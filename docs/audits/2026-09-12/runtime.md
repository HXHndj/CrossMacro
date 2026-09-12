# CrossMacro Windows runtime review

审查日期：2026-09-12。审查基线为 `11766a21f4e0c24707991132976f9ec69b66db01`；依赖/设置未改动（SDK `10.0.100`，Windows 平台目标 `net10.0`）。本次只读检查了 `CrossMacro.Infrastructure/Services`、`CrossMacro.Platform.Windows`、共享 `Core/Platform.Abstractions` 以及对应测试源码；没有构建、运行测试、录制、回放、输入注入、连接服务或启动桌面服务。

严重度：P1 表示需要优先防护的停止/输入错误路径；P2 表示一般正确性或性能风险。这里的具体运行时触发均未动态复现，不应把条件性风险当作已经发生的故障。`global.json` 请求 SDK 10.0.100；本机实际测试 SDK 为 10.0.400。

## 主审验收修订

- 接受溢出同步通知、无界 Join、GetMessage 返回类型和零间隔轮询的代码证据。队列容量为 4096，排空数量有界；无界的是等待订阅者完成的时间。并没有证明正常回调也会卡死。仅给 Join 加超时不能强制中断已卡住的委托，后续修改还必须定义线程退出与资源所有权。
- SendInput 部分成功的恢复缺口按条件性 P2 保留，尚未证实当前桌面会产生 1/2 返回，更不能把 UIPI 等同于部分成功。官方只保证返回成功插入数及串行、不交错插入，不保证事务回滚。[SendInput 官方契约](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
- GetMessage 的 -1/0/正值处理已对照官方契约，当前 bool 声明不能区分错误。[GetMessage 官方契约](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getmessage)
- `SpinOnce(-1)` 禁止的是 Sleep(1)，不是禁止所有 yield。下面的“忙等”应理解为循环式精细等待，不能据此声称 CPU 必然满核；上层粗等待已分为最多 50 ms 一块。需要测等待中取消和 CPU 后才能改变精度策略。[SpinOnce 官方说明](https://learn.microsoft.com/en-us/dotnet/api/system.threading.spinwait.spinonce?view=net-10.0)
- 屏幕帧 ArrayPool 不清零降为设计观察，**不计入已确认安全缺陷或必做优化**：未发现不可信调用者可越权读取、未初始化区域输出或跨进程暴露的路径。无依据地每帧清零可能增加带宽开销。
- GDI 同步工作和零间隔轮询可由代码确认，但不代表当前 UI 调用一定在主线程；线程上下文与实际吞吐仍待测。`GetEffectiveDelay` 有指数退避，但当初始 pollInterval 为零，乘法结果仍为零。

## 发现

### P1 — 队列溢出路径在 Windows hook 回调中同步调用外部错误处理器

**触发与根因（代码确认）**：事件消费者跟不上而使容量 4096 的队列满时，`TryEnqueue` 返回 `false`，随后 `HandleDispatchOverflow` 在调用方线程直接执行 `CaptureError?.Invoke`。[WindowsInputCapture.cs:313-337](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/WindowsInputCapture.cs:313) 由鼠标/键盘低级 hook 回调直接调用。[WindowsInputCapture.cs:339-356](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/WindowsInputCapture.cs:339) [WindowsInputCapture.cs:576-588](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/WindowsInputCapture.cs:576) 这绕过了专用 dispatch 线程；订阅者可阻塞、重入或抛异常。共享协调器还会按顺序同步扇出各 session 订阅者。[InputCaptureSessionCoordinator.cs:305-333](D:/Desktop/网页设计/Mouse/src/CrossMacro.Infrastructure/Services/InputCaptureSessionCoordinator.cs:305)

**保护与反证**：正常事件只入队，且 dispatcher 对 `InputReceived` 订阅者异常有隔离；这只保护正常消费路径。溢出通知没有同样的 try/catch，也没有独立的非阻塞错误队列，因此不能反证 hook 回调安全。

**可重现方案（未运行）**：以容量为 1 的 dispatcher 让第一个订阅者永久等待，再触发第二个 hook 事件；将 `CaptureError` 订阅器设置为等待或抛异常即可观察回调停滞/异常外逸。真实运行应使用测试替身或 API seam，不应注入真实输入。

**建议**：溢出时只做一次原子状态切换并返回；将错误通知投递到独立的有界/丢弃队列或线程池安全通知器，并隔离错误订阅者。停止请求也应通过受控消息路径完成，避免任何用户代码位于 hook 回调栈上。

### P1 — 停止时 dispatcher 无界 drain + Join，可永久滞留线程和 native 缓冲

**触发与根因（代码确认）**：`WindowsInputEventDispatcher.Dispose` 完成入队后无超时地 `Join` 消费线程；消费线程会继续调用订阅者直到队列排空。[WindowsInputEventDispatcher.cs:65-107](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/WindowsInputEventDispatcher.cs:65) `WindowsInputCapture` 先卸载 hook，再等待 dispatcher，最后才释放 raw-input buffer 和清理线程状态。[WindowsInputCapture.cs:128-137](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/WindowsInputCapture.cs:128)

**影响**：任一 `InputReceived` 订阅者在停止时卡住会令 dispatcher 线程、capture message-pump 线程和 `_rawInputBuffer` 长期存活；直接 `WindowsInputCapture.Dispose` 只发停止消息，不等待这两个线程，重复启动可能累积悬挂线程。协调器的停止过程也只等待已在启动阶段完成的 `StartAsync` 任务。[InputCaptureSessionCoordinator.cs:205-267](D:/Desktop/网页设计/Mouse/src/CrossMacro.Infrastructure/Services/InputCaptureSessionCoordinator.cs:205)

**保护与反证**：正常的有限回调会按顺序 drain，且 hook 在 Join 前已卸载；测试明确要求活动回调结束后 Dispose 才完成。[WindowsInputEventDispatcherTests.cs:130-151](D:/Desktop/网页设计/Mouse/tests/CrossMacro.Platform.Windows.Tests/Services/WindowsInputEventDispatcherTests.cs:130) 这说明 drain 是有意策略，但没有卡死/超时分支。

**可重现方案（未运行）**：令一个输入订阅者等待永不完成，调用 `StopCapture`/`Dispose`；用线程转储或 dispatcher 完成状态确认后台线程不退出。不要在真实全局 hook 上做此实验。

**建议**：提供取消/超时的 `DisposeAsync`；达到预算后丢弃剩余队列并标记 capture 已停止，等待线程使用有界 Join，报告未 drain 数量。订阅者契约应要求不在输入回调中做无限等待。

### P2（条件性）— `SendInput` 部分成功时，成对 click 可能留下按下状态

**触发与根因（代码确认，部分成功触发待 Windows 实测）**：`MouseButtonClick` 将按下和释放放入同一批次；`SendInputBatch` 接受少于请求数时立即抛出。[WindowsInputSimulator.cs:102-113](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/WindowsInputSimulator.cs:102) [WindowsInputSimulator.cs:324-359](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/WindowsInputSimulator.cs:324) 如果返回 1/2，按下可能已经进入系统而释放没有进入。`MacroEventExecutor` 的 `Click` 分支不经过 `EmitButton`，所以没有把该 click 记入状态跟踪器。[MacroEventExecutor.cs:157-188](D:/Desktop/网页设计/Mouse/src/CrossMacro.Infrastructure/Services/Playback/MacroEventExecutor.cs:157)

**影响**：`ButtonStateTracker.ReleaseAll` 的 failsafe 只补发左/右/中键。[ButtonStateTracker.cs:48-100](D:/Desktop/网页设计/Mouse/src/CrossMacro.Infrastructure/Services/Playback/ButtonStateTracker.cs:48) Side1/Side2 click 的部分成功没有对应补偿，可能把扩展鼠标键留在按下状态；异常发生在清理失败或进程中断时，普通 click 也没有批次级回滚。

**保护与反证**：正常全成功返回会按序插入完整 down/up 批次；单独的 ButtonPress/ButtonRelease 会进入 tracker。现有测试只覆盖映射和人为调用 `EnsureInputWasAccepted`，没有覆盖批次返回 1/2。[WindowsInputSimulatorTests.cs:130-153](D:/Desktop/网页设计/Mouse/tests/CrossMacro.Platform.Windows.Tests/Services/WindowsInputSimulatorTests.cs:130)

**可重现方案（未运行）**：在 `SendInput` seam/native shim 返回 `1`（请求 2）并执行 Side1 click，检查补偿调用是否出现；不连接真实桌面、不发送真实输入。

**建议**：把批次部分成功建模为“可能已按下”的状态，先尝试发送对应释放（并记录结果），或改为可恢复的 click 事务；扩展键也纳入最终 failsafe。需要在 Windows/UIPI/不同权限级别实测 `SendInput` 的部分返回模式。

### P2 — `GetMessageW` 错误返回值被错误声明为 `bool`

**触发与根因（代码确认）**：Win32 `GetMessage` 返回 `-1`（错误）、`0`（WM_QUIT）或正数；共享 P/Invoke 却声明为 `bool`。[User32.cs:23-25](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Native/User32.cs:23) [WindowsMessagePump.cs:9-27](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/WindowsMessagePump.cs:9) `-1` 会被 BOOL marshaller 当作 `true`，代码继续用无效消息调用 `TranslateMessage/DispatchMessage`，也没有读取错误或退出。相邻的 STA 实现已用 `int` 并显式处理 `-1`，表明这是边界不一致。[StaMessageThread.cs:144-163](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/StaMessageThread.cs:144)

**保护与反证**：普通 WM_QUIT 的 0 会映射为 false，因此常规停止看似有效；问题只在 message queue/API 错误路径。没有 WindowsMessagePump 的 `-1` 测试。

**可重现方案（未运行）**：为 `GetMessage` 增加返回 `-1` 的测试 seam，执行一次 pump 迭代即可看到它进入 true 分支；不需真实 hook。

**建议**：把返回类型改为 `int`，按 `-1/0/>0` 分支；`-1` 记录 `GetLastError` 并进入统一清理。

### P2 — 高精度等待的取消不可唤醒，且每个短延迟都可能忙等

**触发与根因（代码确认，时延/CPU 数字待测）**：高分辨率策略在 native `WaitForSingleObject` 内等待，取消只在进入等待前检查；等待句柄没有与 cancellation 注册关联。[WindowsWaitableTimerDelayStrategy.cs:45-87](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/WindowsWaitableTimerDelayStrategy.cs:45) 还把超时设为请求值加 100 ms。播放和轨迹的最后 0.5 ms 使用 `SpinWait.SpinOnce(sleep1Threshold: -1)` 忙等。[SystemPlaybackTimingService.cs:48-84](D:/Desktop/网页设计/Mouse/src/CrossMacro.Infrastructure/Services/Playback/SystemPlaybackTimingService.cs:48) [WindowsPrecisionDelay.cs:21-52](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/WindowsPrecisionDelay.cs:21)

**影响**：停止/取消可能要等 native 等待返回（通常至少一个 coarse chunk，timer 异常路径还受 slack 约束）；高事件率的短延迟会持续占用执行核。当前测试覆盖了“等待前已取消”和正常精度，但没有等待中取消或 CPU/尾延迟测量。[WindowsWaitableTimerDelayStrategyTests.cs:28-36](D:/Desktop/网页设计/Mouse/tests/CrossMacro.Platform.Windows.Tests/Services/WindowsWaitableTimerDelayStrategyTests.cs:28)

**可重现方案（未运行）**：Windows 上开始 10–50 ms wait 后立即取消，测量取消到任务完成；再用大量小于 0.5 ms 间隔的轨迹采样记录 CPU 和 p95/p99 延迟。仅允许无输入的 timing harness。

**建议**：用可被取消的 wait handle/注册回调唤醒 native wait，或明确有界取消预算；对尾段采用自适应 yield/spin 策略并把 CPU/延迟作为基准指标，不能只验证平均时长。

### 设计观察（不计缺陷）— 屏幕帧的 ArrayPool 租借默认不清零

**触发与根因（代码确认）**：GDI backend 以默认 `clearOnReturn:false` 创建 `PooledBufferMemoryManager`。[GdiWindowsScreenCaptureBackend.cs:62-72](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/ScreenReading/GdiWindowsScreenCaptureBackend.cs:62) 管理器只有显式开启该选项时才在归还池前清空。[PooledBufferMemoryManager.cs:15-20](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Abstractions/PooledBufferMemoryManager.cs:15) [PooledBufferMemoryManager.cs:43-54](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Abstractions/PooledBufferMemoryManager.cs:43)

**影响**：`ScreenFrame.Dispose` 归还后，同一进程后续租得相同 ArrayPool buffer 的代码可以读取上一帧屏幕像素，造成跨功能的数据残留；这不是跨进程泄露，且新租借者若完整覆盖后再读则无残留。

**保护与反证**：frame 通过 owner 正确归还 buffer，且 `Interlocked.Exchange` 防止同一 manager 重复归还；这些只保证生命周期，不保证内容擦除。现有 GDI 测试验证复用和尺寸重建，没有验证归还内容。[WindowsScreenFrameProviderTests.cs:8-29](D:/Desktop/网页设计/Mouse/tests/CrossMacro.Platform.Windows.Tests/Services/ScreenReading/WindowsScreenFrameProviderTests.cs:8)

**可重现方案（未运行）**：在受控单元测试中租借固定大小 buffer 写入标记、Dispose，再重复租借并检查标记（允许用多次租借提高命中率）；不做真实屏幕采集。

**建议**：先核实是否存在需要隔离的同进程调用者或未覆盖区域输出；只有明确的数据清除要求成立时再决定使用 clearOnReturn 或独立池，并测量额外带宽成本。当前不建议仅据池复用一项就改成每帧清零。

### P2 — Windows GDI 屏幕读取在 async 外观下同步执行整块 BitBlt/拷贝，零 poll 间隔还可形成紧循环

**触发与根因（代码确认，实际吞吐待测）**：`WindowsScreenFrameProvider.CaptureFrameAsync` 没有任何 await；它直接调用 backend 的 `GetDC`、`BitBlt`、`GdiFlush` 和整帧内存复制。[WindowsScreenFrameProvider.cs:25-65](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/ScreenReading/WindowsScreenFrameProvider.cs:25) [GdiWindowsScreenCaptureBackend.cs:37-90](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/ScreenReading/GdiWindowsScreenCaptureBackend.cs:37) 对全虚拟屏幕或高频 image search，会在调用线程串行占用 GDI/内存带宽。`ScreenReadOptions` 允许零 poll interval，[ScreenReadOptions.cs:24-37](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Abstractions/ScreenReadOptions.cs:24) 而 polling 会以零 delay 反复 capture/match。[ScreenReadPolling.cs:94-145](D:/Desktop/网页设计/Mouse/src/CrossMacro.Infrastructure/Services/ScreenReading/ScreenReadPolling.cs:94)

**影响**：调用方看到的 async API 不能避免 UI/playback 线程被一次大帧 capture 阻塞；`pollInterval=0` 且目标不存在时会在超时期间持续抓屏和匹配，CPU/内存带宽可被打满。取消只在 BitBlt/复制前后检查，无法中断进行中的 native copy。[GdiWindowsScreenCaptureBackend.cs:37-90](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/ScreenReading/GdiWindowsScreenCaptureBackend.cs:37)

**保护与反证**：backend 用锁串行保护 GDI 对象、缓存 DC/DIB，provider 校验虚拟屏幕边界；这些避免句柄竞态和越界，不能证明大帧延迟或零间隔成本可接受。现有测试是小区域/功能测试，没有 4K、全屏、零间隔或取消中拷贝基准。

**可重现方案（未运行）**：使用假的 capture backend 延迟 `Capture`，验证 provider 在调用线程同步阻塞；再用受控 fake reader 跑非命中零间隔 polling，统计每秒调用数。Windows 性能验收再用小范围、只读、无注入的 screen capture harness。

**建议**：对可配置 region/最大像素数设硬上限；将大帧 capture 放到专用有界 worker，明确取消语义；禁止或规范化零 poll interval（至少采用最小退避），并记录 capture/match 次数与耗时。

## 未完成的运行时验证

- 未在 Windows desktop session 安装真实 low-level hooks，也未验证 `LowLevelHooksTimeout`、session unlock 重启、Raw Input 注册的真实行为。
- 未执行 `SendInput`、屏幕采集、回放、录制、CPU/延迟基准或 handle 计数；上文涉及“部分成功返回”“取消时延”“吞吐/CPU”的数值均为待测项。
- 未运行任何 test/build；对应测试仅作为静态反证范围读取，不能作为设备/Windows 运行时验收。

