# CrossMacro 首轮综合审查

日期：2026-09-12。源码基线：`11766a21f4e0c24707991132976f9ec69b66db01`，VERSION `1.4.0`。

## 主审结论与优先级

项目已有较完整的分层、单文件原子写入、测试和若干资源释放保护。第一轮最值得投入的是编辑器热点与异常路径的一致性；目前没有实际帧率、内存基线或证据支持大规模重写架构。

P1 表示建议第一批处理；P2 表示后续修正/受控验证。严重度包含主审取舍，不直接照收子报告原始评级。

| 优先级 | 问题与影响 | 核心源码证据 | 验收方向 |
| --- | --- | --- | --- |
| P1 | 编辑单个字段时全量重建动作列表；连续 K 次输入对 N 个动作产生 O(KN) 工作，影响响应且重复分配 | [EditorViewModel.cs:1279](D:/Desktop/网页设计/Mouse/src/CrossMacro.UI/ViewModels/EditorViewModel.cs:1279)，重建入口 1360 | 局部更新/通知合并；计数证明单字段修改不再重建 N 行，随后测 UI p95 |
| P1 | 撤销保存完整宏；历史上限 50 仍可保留约 50×N 个动作副本，400 ms 合并未消除每次输入的当前状态复制 | [EditorViewModel.Actions.cs:568](D:/Desktop/网页设计/Mouse/src/CrossMacro.UI/ViewModels/EditorViewModel.Actions.cs:568)，CloneState 31，RememberCurrentState 207 | 增量历史或按字节预算；保持撤销语义，测分配与私有提交量 |
| P1 | Add/Update 已修改运行时集合后可取消/保存失败，调用报错却没有恢复原状态 | [ManageSchedule.cs:12](D:/Desktop/网页设计/Mouse/src/CrossMacro.Application/Automation/ManageSchedule.cs:12)，Shortcut/Trigger 同类路径 | 在修改后取消、写失败分别注入故障；断言运行时与磁盘的一致性 |
| P1 | 输入分发在停止时无超时 Join；遇到阻塞订阅者会拖住线程清理，溢出错误通知还在 hook 线程同步执行 | [WindowsInputEventDispatcher.cs:65](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/WindowsInputEventDispatcher.cs:65)、[WindowsInputCapture.cs:322](D:/Desktop/网页设计/Mouse/src/CrossMacro.Platform.Windows/Services/WindowsInputCapture.cs:322) | 隔离错误通知、明确停止预算/回调契约与资源所有权；只用替身注入卡住的消费者 |
| P2 | 异步剪切等待后删除当前选区，等待期间变更选区可删错文本 | [TextBoxClipboardHandler.cs:30](D:/Desktop/网页设计/Mouse/src/CrossMacro.UI/Services/TextBoxClipboardHandler.cs:30) | 延迟完成的 clipboard 替身；等待期间改变文本/选区，断言不误删 |
| P2 | 大宏转换和显示投影仍在 UI 回调完成；图像预览只丢弃过期结果，未按请求取消旧解码 | [EditorViewModel.CaptureAndFileOps.cs:912](D:/Desktop/网页设计/Mouse/src/CrossMacro.UI/ViewModels/EditorViewModel.CaptureAndFileOps.cs:912)、[EditorViewModel.ScreenReadingPresentation.cs:79](D:/Desktop/网页设计/Mouse/src/CrossMacro.UI/ViewModels/EditorViewModel.ScreenReadingPresentation.cs:79) | 后台准备不可变数据、受控提交；预览取消/有界缓存，测 stale 解码数 |
| P2 | 跨文件保存/Profile 目录操作缺少完整失败恢复；活动文本扩展存储与 profile 切换存在不同 gate 的竞态边界 | [SettingsService.cs:154](D:/Desktop/网页设计/Mouse/src/CrossMacro.Infrastructure/Services/SettingsService.cs:154)、[TextExpansionStorageService.cs:121](D:/Desktop/网页设计/Mouse/src/CrossMacro.Infrastructure/Services/TextExpansionStorageService.cs:121) | 第二次写入失败、注册表保存失败、切换中写入的故障/并发测试 |
| P2 | 屏幕脚本命令识别接受 tab 等空白，参数解析仅按空格切分；验证成功但执行可静默返回 | [RunScriptRuntimeValidator.cs:72](D:/Desktop/网页设计/Mouse/src/CrossMacro.Infrastructure/Services/RunScriptRuntimeValidator.cs:72)、[RunScriptScreenReadingStepParser.cs:78](D:/Desktop/网页设计/Mouse/src/CrossMacro.Infrastructure/Services/RunScriptScreenReadingStepParser.cs:78) | 统一 tokenizer；tab、混合空白必须执行正确或明确报错 |
| P2 | 热键控件只有指针激活路径，缺显式取消；编辑器多选提示固定英文 | [HotkeyCapture.axaml:11](D:/Desktop/网页设计/Mouse/src/CrossMacro.UI/Controls/HotkeyCapture.axaml:11)、[EditorTabView.axaml:1077](D:/Desktop/网页设计/Mouse/src/CrossMacro.UI/Views/Tabs/EditorTabView.axaml:1077) | 纯键盘激活/取消、保留旧值；中文及其他语言文案检查 |
| P2/测量 | 任务/扩展列表的虚拟化、零间隔屏幕轮询、native 等待中的取消、GetMessage 错误返回处理 | 见 UI performance 与 runtime 分报告 | 先确认容器数/CPU/取消时间；GetMessage 单独补 -1 分支测试 |

GetMessage 的三值返回和 SendInput 的成功插入计数已与微软官方契约核对；详细链接与适用边界在 runtime 报告。精细等待不能直接判定为满核忙等，ArrayPool 不清零没有成立的越权路径，半启动步骤也可能自行清理，均未直接纳入确认缺陷。Grid.Row 越界、Precision 标签以及文件对话框重入同样保持待验证状态。

## 本次验证结果

| 筛选测试组 | 总数 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: | ---: |
| Infrastructure（持久化/Profile/调度） | 83 | 81 | 2 | 0 |
| Application（取消/快照/生命周期） | 11 | 11 | 0 | 0 |
| UI（剪贴板/会话持久化/生命周期） | 20 | 20 | 0 | 0 |
| 合计 | 114 | 112 | 2 | 0 |

主审直接读取三份 TRX 核验计数及两项失败堆栈。失败均发生在测试创建符号链接时，Windows 缺少所需特权，尚未进入产品断言，不算通过也不算已复现产品缺陷。三个筛选命令采用本机 SDK 10.0.400、`--no-restore`、`/m:1`，执行者报告重新编译当前源码后运行；完整命令和退出码见 robustness.md。保留了 TRX，未保留完整构建控制台日志，因此源码编译过程的详细 warning 不能由独立日志复核。

**资源证据限制：执行者没有记录测试峰值 RSS/Private Bytes，因此不能宣称已验证 1.5 GB 预算。** 结束时主审检查未发现 dotnet/testhost/CrossMacro 进程。114 项不是全套测试，也不是性能或真实 GUI 验收；现有剪切测试使用立即完成的替身，不覆盖等待期间的选区变化。

## 审查方式与边界

主审 Astra 拆分问题、抽查原始源码与 TRX、排除反证并确定优先级；四个 Luna/max 子审查者分别负责 UI/UX、UI 性能与内存、录制/回放和 Windows 内核、持久化/生命周期与测试。没有递归委派或另建 Codex task。

本次是第一轮横向审查，不是逐行覆盖全部代码，也不是已完成优化。范围涉及 UI、共享 Infrastructure/Application/Core、Windows 适配和 Astro 官网；Linux/macOS 真机行为、完整 MCP/CLI 安全审计没有完成。没有启动正式 GUI、录制/回放、输入注入或后台服务，没有改产品源文件、依赖或用户配置。仅增加审查材料，测试可能更新常规 bin/obj。原有 `.zcode/` 未跟踪内容保留。

源码路径扫描计数为 1,567 个 C#/AXAML 文件、504 个测试 C# 文件；这些是项目规模，不是已逐个审阅的数量。

证据等级：

- **代码确认**：具体调用链与状态转换可见；不等于已在真实桌面复现。
- **测试证据**：本次执行的结果，以 TRX 和命令日志为准；不外推到未执行测试。
- **需实测**：帧时间、操作延迟、RSS/私有提交量、DPI/焦点/辅助功能等，不能从源码或历史截图直接给出实际数值。

## 已有基础与反证

- 有模块分层、针对行为的测试和多平台 CI；CI 配置存在不代表本次全套通过。
- 编辑器撤销栈上限为 50，不能称为无限历史泄漏；但仍存在随宏长度放大的完整快照成本。
- `ListBoxSelectedActionIndices.RequestListBoxSelectionSync` 有 pending 标志及 Dispatcher.Post 合并，且有 detach 解绑。没有证据支持“每次 Add 必然立刻全量扫描，导致这条选择同步路径 O(N²)”或“该行为必然泄漏页面”。
- 历史截图显示 1.3.0；当前源码为 1.4.0，因此未以旧截图宣称当前布局已经视觉验收。

## 分项证据

- [UI/UX](ui-ux.md)
- [UI 性能与内存](ui-performance.md)
- [录制、回放与 Windows](runtime.md)
- [持久化、生命周期与测试](robustness.md)
- [官网补充与范围说明](website-and-scope.md)
- [原始测试结果目录](validation/)

## 后续性能验收建议

先建立同一 Release 构建、同一机器和同一数据集的基线，然后每次只改一类根因。以下是建议测试条件，不是本次测得的性能或已经获得验收的门槛。

| 场景 | 数据规模与动作 | 采样指标与正确性要求 |
| --- | --- | --- |
| 编辑器 | 1千/1万动作起步，单字段连续编辑、筛选、批量删除、撤销；前两档达标后才考虑更大规模 | 输入到可见反馈的 p50/p95、UI 线程最长停顿、分配量；动作顺序/选区/撤销结果一致 |
| 页面生命周期 | 编辑/设置/文件页切换 100 次，比较多个循环后的存活量 | RSS、Private Bytes、GC heap、事件订阅数；区分缓存平台与逐轮上升，不以一次未降 RSS 判断泄漏 |
| 录制 | 先用隔离假事件源测试，再在受控目标窗口验证真实输入 | 入队/消费/丢弃数、峰值队列、停止耗时；键盘与按钮按下/释放成对 |
| 回放取消 | 倒计时、长等待、屏幕匹配、任务切换、窗口锁定期间取消 | 从取消请求到停止的 p95、停止后无残留执行/按键、状态与界面一致 |
| 保存与切换 | 故障注入：文件锁、写入失败、切换并发、无效文件 | 失败后原文件可用、错误可见、内存和磁盘所属 profile 一致；不触及真实用户库 |
| 屏幕读取 | 先单张 1080p，再单张 4K；连续批次逐一执行 | RSS、Private Bytes、CPU、取消时间；完成一批释放帧与缓冲，不重复并发运行 |
| 视觉交互 | 当前版本 100%/150%/200% DPI、窄窗口、中英文、纯键盘 | 按钮可达、无裁切遮挡、焦点可辨、错误可恢复、辅助功能名称可理解 |

探索性基准应同时设置内存和时间上限，保存日志后终止自己的测试进程；不得从小样本直接启动巨大宏或重复并发采样。优化顺序以数据正确性/停止可靠性、编辑器热点、录制与屏幕读取资源预算、布局与辅助功能为主。


