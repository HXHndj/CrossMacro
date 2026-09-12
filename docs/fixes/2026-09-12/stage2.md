# Stage 2 Windows runtime and screen polling fixes

## 主审验收

2026-09-12：Windows 定向测试在 `ContinuousIntegrationBuild=true` 下重新编译并运行，75/75 通过；生产代码无 error/warning。最终运行日志为 `validation/stage2/stage2-windows-ci-rerun1-20260912-165607-829-ab5bf56d/`，TRX 为 `validation/stage2/stage2-windows-ci-rerun1.trx`。此前的测试编译错误和错误取消时序已修正，失败日志保留供追溯。屏幕轮询测试亦包含在 stage1-infrastructure 的 278 项通过结果中。

750 ms 间隔采样的进程树峰值：Private Bytes 261,324,800，Working Set 459,677,696；跟踪到 9 个进程，未超 2 GiB 预算。这是构建/测试的采样峰值，不是应用长期运行内存。

主审复核了线程预留、超时后的队列所有权、部分输入补偿和句柄生命周期。S3869 只在 raw-handle 数组的必要局部范围抑制，相关生命周期理由就在代码旁，未关闭全局分析器。阶段实现和 targeted 验证已完成；最终提交后的整体验证另列交付记录。

本轮按主审授权完成实现，未执行 build/test、真实录制/回放、真实 hook、SendInput、屏幕采集或服务连接；没有执行 git 操作。修改基于当前工作树，保留平台公开格式、正常 capture drain 语义和精度等待尾段策略。

## 已修改文件

- `src/CrossMacro.Platform.Windows/Native/User32.cs`：`GetMessageW` 返回 `int` 并启用 last-error，使 `-1`、`0`、正数可区分。
- `src/CrossMacro.Platform.Windows/Native/Kernel32.cs`：增加 `WaitForMultipleObjects` native seam。
- `src/CrossMacro.Platform.Windows/Services/WindowsMessagePump.cs`：处理 `GetMessageW == -1`，普通消息与 WM_QUIT 分支明确。
- `src/CrossMacro.Platform.Windows/Services/WindowsInputEventDispatcher.cs`：正常停止仍 drain；超过 1 s 的停止等待后设置 stop flag、清空剩余队列并返回，当前用户委托不被强杀，队列仍由 worker 在最终退出时释放。
- `src/CrossMacro.Platform.Windows/Services/WindowsInputCapture.cs`：溢出错误改为线程池隔离且安全捕获订阅者异常；同一实例增加停止/旧 dispatcher 生命周期门禁；线程预留与 `Thread.Start` 在同一锁内完成，启动失败会释放预留；早期停止会在消息队列初始化后补发 WM_QUIT。
- `src/CrossMacro.Platform.Windows/Services/WindowsWaitableTimerDelayStrategy.cs`：native timer 等待同时等待短生命周期取消事件；等待期间取消可唤醒；timer handle 关闭与 gate 受控，保留精度尾段。
- `src/CrossMacro.Platform.Windows/Services/WindowsInputSimulator.cs`：增加仅供测试的 SendInput delegate seam；批次部分成功时仅对已接受且可精确识别的鼠标 button-down 尝试对应 release 补偿，保留原始失败诊断。
- `src/CrossMacro.Infrastructure/Services/ScreenReading/ScreenReadPolling.cs`：`pollInterval <= 0` 的重复 polling 使用 1 ms 最小退避，并保持 deadline 截断。

## 对应测试改动与建议筛选

- `tests/CrossMacro.Platform.Windows.Tests/Services/WindowsInputCaptureTests.cs`：溢出通知不会阻塞 capture path；并发启动被拒绝，线程启动失败后预留可重试。
- `tests/CrossMacro.Platform.Windows.Tests/Services/WindowsInputEventDispatcherTests.cs`：阻塞订阅者超过预算时停止派发剩余队列并返回。
- `tests/CrossMacro.Platform.Windows.Tests/Services/WindowsInputSimulatorTests.cs`：SendInput 只接受 click 的 down 时验证 XBUTTON release 补偿。
- `tests/CrossMacro.Platform.Windows.Tests/Services/WindowsWaitableTimerDelayStrategyTests.cs`：通过内部 wait-start seam 在 worker 中确定进入等待后再取消，验证 native wait 期间应及时完成。
- `tests/CrossMacro.Infrastructure.Tests/Services/ScreenReading/ScreenReadPollingTests.cs`：零 poll interval 使用 1 ms。

主审执行时可优先筛选：

```text
dotnet test tests/CrossMacro.Platform.Windows.Tests/CrossMacro.Platform.Windows.Tests.csproj --filter "FullyQualifiedName~WindowsInputCaptureTests|FullyQualifiedName~WindowsInputEventDispatcherTests|FullyQualifiedName~WindowsInputSimulatorTests|FullyQualifiedName~WindowsWaitableTimerDelayStrategyTests"
dotnet test tests/CrossMacro.Infrastructure.Tests/CrossMacro.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ScreenReadPollingTests"
```

本轮没有运行上述命令。

## 边界和未测项

- dispatcher 的有界停止不能终止正在执行的 managed 委托；超时后只阻止后续排队事件派发并让 worker 自己退出/释放 queue。若委托永久阻塞，实例会保持“不可重启”门禁，这是安全边界而不是强杀机制。
- `WindowsInputCapture` 的同实例重启只有在旧 dispatcher `IsCompleted` 后才允许；生产上若旧委托永不返回，应由外层创建新 capture 实例并记录泄漏/阻塞告警。
- `SendInput` 补偿只处理批次前缀中明确的鼠标按下记录，未猜测键盘、滚轮、移动或混合批次；真实 Windows/UIPI 是否返回部分计数仍需受控 native 测试。
- 未改变 ArrayPool 每帧清零策略，也未删除高精度等待的忙等尾段；RSS/PrivateBytes、CPU、p95/p99 延迟、hook timeout、session unlock、raw input 重启均待主审发执行令后测量。
- 未改动 `RunScript*` parser/executor、Settings/Profile、UI 或 Core 模型；未进行真实输入注入、录制、回放和屏幕读取。
