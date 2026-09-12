# CrossMacro UI 性能与内存只读审查

日期：2026-09-12  
范围：`src/CrossMacro.UI`、相关 Core/Infrastructure 转换器与 `tests/CrossMacro.UI.Tests`。  
限制：本次只做静态审查和很小的离线计数；没有构建、启动应用、运行桌面宏，也没有声称实际 FPS、RSS 或耗时。

## 结论边界

最高价值的静态风险集中在编辑器动作列表和大宏编辑路径：一个动作属性变更会触发全量列表投影，文本框又按 `PropertyChanged` 推送，因此连续输入 `K` 个字符、动作数为 `N` 时，列表投影至少形成 `O(KN)` 工作；当 `K` 与 `N` 同量级时，累计工作呈 `O(N²)`。撤销状态还会在同一类输入上复制 `N` 个 `EditorAction`。这不是单次 `UpdateActionListPresentation` 自身为 N²。

已有代码保护了若干边界：动作集合批量更新期间会抑制状态刷新；选择同步用 `IsSelectionSyncPending` 和一次 `Dispatcher.Post` 合并；编辑器释放时会取消预览、取消捕获并解除动作/本地化订阅；播放用的图像解码缓存有容量 8 的 LRU。上述保护不能抵消每次属性输入都重新投影和复制的主路径。

## 关键发现

### 1. 编辑器动作列表在每个属性通知上全量重建（高）

**路径与触发。** `EditorViewModel.OnAnyActionPropertyChanged` 对除 `Index` 外的每个动作属性变更调用 `UpdateActionListPresentation`（`src/CrossMacro.UI/ViewModels/EditorViewModel.cs:1279-1284`）。该方法先 `ActionListItems.Clear()`，再扫描 `Actions`、格式化显示名并逐项 `Add`（`src/CrossMacro.UI/ViewModels/EditorViewModel.cs:1360-1447`）；左侧 `ListBox` 直接绑定这个集合（`src/CrossMacro.UI/Views/Tabs/EditorTabView.axaml:66-98`）。动作 `Text` setter 会发出 `PropertyChanged`（`src/CrossMacro.Core/Models/EditorAction.cs:447-467`），而编辑器文本框使用 `UpdateSourceTrigger=PropertyChanged`（`src/CrossMacro.UI/Views/Tabs/EditorTabView.axaml:353-360`）。

**复杂度与分配证据。** 一次重建为 `O(N)`，并为每个显示行创建一个 `EditorActionListItem`（`src/CrossMacro.UI/ViewModels/EditorActionListItem.cs:7-62`），同时由格式化器构造/格式化显示字符串（`src/CrossMacro.UI/Localization/EditorActionDisplayFormatter.cs:8-70`）。连续 `K` 次输入就是 `O(KN)` 扫描、行对象和显示字符串分配；`K≈N` 时累计为 `O(N²)`。仓库测试明确使用 5,000 个动作并断言产生 5,000 个动作行（`tests/CrossMacro.UI.Tests/ViewModels/EditorViewModelTests.PersistenceUndo.cs:407-433`）。

**保护与反证。** `Actions` 批量变更时 `_isBatchUpdatingActions` 可抑制中间状态刷新（`src/CrossMacro.UI/ViewModels/EditorViewModel.cs:1214-1217`），选择行为的 Items 变更回调也用 pending 标志合并到一次 UI 同步（`src/CrossMacro.UI/Behaviors/ListBoxSelectedActionIndices.cs:374-391`）。因此不能把一次 `Clear` 加 `N` 次 `Add` 直接判成必然 N²；风险来自 `UpdateActionListPresentation` 被反复调用，而不是选择同步的每个 Add 都全扫。

**建议。** 把行投影改成增量更新或按动作属性类别刷新；至少对文本编辑做 UI 帧级合并/去抖，并在同一批输入中只替换受影响行。保留全量重建作为结构变化、过滤切换和语言切换的慢路径。

**确认状态。** 触发关系、`O(N)` 和分配类型已由源码确认；实际 UI 线程占用、布局时间和视觉容器数需实测。

### 2. 属性通知扇出会再次扫描全宏，并向绑定层发出大量通知（高）

**路径与触发。** 选中动作自身订阅 `OnSelectedActionPropertyChanged`（`src/CrossMacro.UI/ViewModels/EditorViewModel.Actions.cs:267-275`）。对文本、变量、脚本输出等属性，它会调用 `RefreshAvailableVariableNames`（`src/CrossMacro.UI/ViewModels/EditorViewModel.Actions.cs:321-346`）；同一属性随后还会经过所有动作订阅路径并再次走 `RefreshAvailableVariableNames`/屏幕读取通知（`src/CrossMacro.UI/ViewModels/EditorViewModel.cs:1279-1335`）。`BuildAvailableVariableNames` 和 `BuildAvailableColorVariableNames` 都按 `Actions` 扫描并排序（`src/CrossMacro.UI/ViewModels/EditorViewModel.VariableSuggestions.cs:6-85`）。

**复杂度与分配证据。** 对一个文本字符变更，静态路径至少包含一次 `UpdateActionListPresentation` 的 `O(N)` 扫描，以及选中动作和通用动作订阅可能各自触发的变量候选扫描；候选数组、`HashSet` 和排序结果每次都是新对象（`src/CrossMacro.UI/ViewModels/EditorViewModel.VariableSuggestions.cs:8-10,54,59-78`）。`NotifyVisibilityChanged` 单次发出 69 个 `OnPropertyChanged` 调用（`src/CrossMacro.UI/ViewModels/EditorViewModel.Actions.cs:391-465`），其中很多属性在当前动作类型下不可见，但仍会通知绑定层。

**保护与反证。** `RefreshAvailableVariableNames` 在集合内容不变时不会替换候选数组（`src/CrossMacro.UI/ViewModels/EditorViewModel.VariableSuggestions.cs:81-103`），动作同步旗标也避免了部分规范化递归。这减少了绑定重建，却没有消除前置扫描和 69 次通知；本次未执行运行时事件计数。

**建议。** 将候选变量维护为按动作增量更新的索引；把可见性通知按动作类型分组，只通知当前模板需要的属性；对文本输入将列表投影、候选扫描和撤销记账合并到一次调度批次。

**确认状态。** 源码路径和 69 次静态调用已确认；每个字符实际触发几次（取决于 Avalonia 订阅顺序和绑定写回）需用 `PropertyChanged` 计数器实测。

### 3. 多个页面的 ItemsControl 没有显式虚拟化，外层 ScrollViewer 放大风险（中高）

**路径与触发。** 触发器、快捷键和计划任务页面都在外层 `ScrollViewer` 中用默认 `ItemsControl` 渲染任务集合：`src/CrossMacro.UI/Views/Tabs/TriggerTabView.axaml:21-50`、`src/CrossMacro.UI/Views/Tabs/ShortcutTabView.axaml:20-49`、`src/CrossMacro.UI/Views/Tabs/ScheduleTabView.axaml:20-49`。文本扩展同样把 `ItemsControl` 放在外层滚动页中（`src/CrossMacro.UI/Views/Tabs/TextExpansionTabView.axaml:21-23,139-140`）。这些控件没有 `ItemsPanel` 或 `VirtualizingStackPanel` 配置；默认 `ItemsControl` 面板不是虚拟化面板。文件页的 `ListBox` 也嵌套在外层 `ScrollViewer`，且只有 `MinHeight` 没有视口高度约束（`src/CrossMacro.UI/Views/Tabs/FilesTabView.axaml:21-30`），这可能让内部列表按无界高度测量而失去虚拟化收益。

**复杂度与分配证据。** 页面集合为 `ObservableCollection`，没有 UI 层的条目上限（例如 `TriggerViewModel.Tasks`、`ShortcutViewModel.Tasks`、`ScheduleViewModel.Tasks` 分别见各 ViewModel 的集合声明）。每次页面布局/集合刷新都有把 `O(M)` 条目模板纳入测量和容器树的风险，`M` 是任务或扩展数量；任务行模板包含多个 `Grid`、`Button`、`TextBlock`，不是轻量纯文本行。

**保护与反证。** 编辑器左侧使用 `ListBox` 并有受约束的 Grid 行（`src/CrossMacro.UI/Views/Tabs/EditorTabView.axaml:29-98`），Avalonia 默认 ListBox 在有界视口下通常可虚拟化；这只限制视觉容器，不限制第 1、2 项的全量投影和集合对象。当前没有应用运行时的已实现容器计数，因此外层滚动导致 ListBox 失去虚拟化仍需确认。

**建议。** 对可增长任务列表使用有界 `ListBox`/`ItemsRepeater` 和明确的虚拟化面板；避免让外层 `ScrollViewer` 为列表提供无界高度。对小型固定枚举（例如星期选项）无需改造。

**确认状态。** “没有显式虚拟化配置”和 ItemsControl 默认非虚拟化是静态确认；实际 realized container 数、测量次数和滚动时 CPU 需实测。

### 4. 大宏加载/保存在 UI 线程完成转换和投影（高）

**路径与触发。** 文件读取本身在 `ConfigureAwait(false)` 后返回，但加载随后把 `LoadMacroSequence` 放入 `RunOnUiThreadAsync`（`src/CrossMacro.UI/ViewModels/EditorViewModel.CaptureAndFileOps.cs:912-947`）。`LoadMacroSequence` 在 UI 回调中执行转换、清空动作和图像集合、逐项加入动作，再刷新完整状态（`src/CrossMacro.UI/ViewModels/EditorViewModel.CaptureAndFileOps.cs:958-1009`）。事件恢复路径按 `sequence.Events` 扫描并创建动作（`src/CrossMacro.Infrastructure/Services/EditorActionConverter.cs:1389-1479`）；允许的资源上限是 32 MiB 文件、100,000 行和 1,000,000 个事件（`src/CrossMacro.Infrastructure/Persistence/Macros/MacroFileLimits.cs:8-12`）。

保存也会在第一次异步切换前克隆、验证和构建序列（`src/CrossMacro.UI/ViewModels/EditorViewModel.CaptureAndFileOps.cs:545-585`）；转换器随后按动作和生成事件循环（`src/CrossMacro.Infrastructure/Services/EditorActionConverter.cs:502-610`）。因此大动作集的 Save 命令也可能长时间占用发起命令的 UI 调用栈。

**复杂度与分配证据。** 加载包含 `O(E)` 事件到动作的转换、`O(N)` 的 Actions 清空/加入和随后 `RefreshActionCollectionState` 的完整扫描；保存包含 `O(N)` 动作克隆和转换，文本输入还可能按字符扩展事件。已有测试用 5,000 动作验证每行都会被加入（`tests/CrossMacro.UI.Tests/ViewModels/EditorViewModelTests.PersistenceUndo.cs:408-432`），并用 5,000 动作验证撤销/重做重建（`tests/CrossMacro.UI.Tests/ViewModels/EditorViewModelTests.PersistenceUndo.cs:481-510`），但测试没有给出耗时或内存预算。

**保护与反证。** `_isBatchUpdatingActions` 确实抑制了加载期间每个 `Actions.Add` 的完整状态刷新；这把状态刷新从 `O(N²)` 降为一次批量刷新，但 UI 回调内仍执行转换、N 次集合通知和最终 O(N) 投影。文件读取、PNG 解码和写盘已异步化，不等于编辑器转换异步化。

**建议。** 先在线程池构造不可变编辑投影和候选索引，回 UI 线程只做一次集合替换；或分块提交并在块之间让出 dispatcher。保存应从不可变编辑快照开始，在后台完成验证/转换，UI 只报告结果。对事件数接近上限的文件增加明确的编辑保护或分页策略。

**确认状态。** UI 回调边界、循环上限和测试规模已确认；未测真实文件加载时间、UI 卡顿时长和峰值 RSS。

### 5. 撤销状态按整宏复制；400 ms 合并只减少历史条目，不减少复制（高）

**路径与触发。** 撤销上限为 50（`src/CrossMacro.UI/ViewModels/EditorViewModel.cs:39-40`）。一个状态包含完整 `List<EditorAction>`，`CloneState` 按动作逐个 `Clone`（`src/CrossMacro.UI/ViewModels/EditorViewModel.Actions.cs:27-36`）；`SaveUndoState` 比较整状态并在入栈时再次 `CloneActions`（`src/CrossMacro.UI/ViewModels/EditorViewModel.Actions.cs:568-584`）。选中动作属性变更在未恢复/未同步时保存旧状态，并在之后 `RememberCurrentState` 再克隆当前状态（`src/CrossMacro.UI/ViewModels/EditorViewModel.Actions.cs:267-275,385-388`）。

**复杂度与分配证据。** `AreStatesEquivalent` 对两个长度为 `N` 的动作列表逐项比较大量字段（`src/CrossMacro.UI/ViewModels/EditorViewModel.Actions.cs:38-143`），为 `O(N·F)`，`F` 是字段比较数。每个快照至少保留 `N` 个新 `EditorAction` 对象；50 个历史快照对应约 `50×N` 个动作对象，外加 `_lastKnownState` 和当前操作的临时克隆。`EditorAction.Clone` 还会为保留的 TextInput 事件列表建立新列表（`src/CrossMacro.Core/Models/EditorAction.cs:1463-1505`）。字符串字段多数共享引用，因此不能据此宣称 `50×N` 个字符串副本或具体字节数。

**保护与反证。** 两个 Stack 有 50 的历史上限，`TrimUndoStack` 会裁剪旧历史（`src/CrossMacro.UI/ViewModels/EditorViewModel.Actions.cs:586-599`）；400 ms coalescing 能减少入栈次数。但在 coalescing 窗口内，`RememberCurrentState` 仍对每次输入复制完整 `N` 个动作，因此连续 `K` 次输入仍有 `O(KN)` 分配；本次未测 GC/LOH 或本机对象大小。

**建议。** 使用结构化增量命令、copy-on-write 或仅保存受影响动作字段；至少把 `_lastKnownState` 与当前编辑批次缓存起来，避免每个字符重新克隆整宏。保留 50 条上限作为产品行为约束，并单独限制单次快照成本。

**确认状态。** 50×N 对象规模、重复 Clone 和等价比较已由源码确认；实际保留快照总数、分代回收和峰值内存需实测。

### 6. 编辑器图像预览没有按请求取消或解码缓存，快速切换会并发浪费（中高）

**路径与触发。** 选择动作时直接丢弃返回任务并启动 `RefreshSelectedImageAssetPreviewAsync`（`src/CrossMacro.UI/ViewModels/EditorViewModel.cs:226-263`）；动作类型或图像名变化也会再次启动它（`src/CrossMacro.UI/ViewModels/EditorViewModel.Actions.cs:348-351`）。预览方法只递增版本号，旧请求不会被取消；旧结果仅在 UI 回调时因版本不符而丢弃（`src/CrossMacro.UI/ViewModels/EditorViewModel.ScreenReadingPresentation.cs:79-127`）。每次解码还会按预览尺寸分配像素数组，最大 `640×360×4 = 921,600` 字节（`src/CrossMacro.Infrastructure/Services/ScreenCapture/ImageAssetPreviewDecoder.cs:6-48`），生成 Bitmap 前又 `ToArray` 一次（`src/CrossMacro.UI/ViewModels/EditorViewModel.ScreenReadingPresentation.cs:145-163`）。

**复杂度与分配证据。** 解码器先处理完整 Base64 PNG，再逐像素生成预览；策略允许单个解码帧最高 160 MiB，宏图像总编码预算 96 MiB（`src/CrossMacro.Infrastructure/Services/ScreenCapture/ScreenImageAssetPolicy.cs:11-14`）。快速在 `A→B→C` 间选择会保留多个尚未完成的解码任务和中间缓冲，最终只显示最新版本。现有容量 8 的 LRU 解码缓存只注入播放/脚本执行路径（`src/CrossMacro.Infrastructure/Services/ScreenReading/ImageAssetDecodeCache.cs:3-70`；`src/CrossMacro.Infrastructure/Services/MacroPlayer.cs:769,1149`），编辑器预览没有使用它。

**保护与反证。** ViewModel CTS 在释放时取消所有预览，旧 Bitmap 在替换时 Dispose（`src/CrossMacro.UI/ViewModels/EditorViewModel.cs:1007-1029`、`src/CrossMacro.UI/ViewModels/EditorViewModel.ScreenReadingPresentation.cs:131-143`）；版本检查防止旧结果覆盖新选择。因此这是快速操作期间的 CPU/峰值内存浪费风险，不是已确认的最终 Bitmap 泄漏。

**建议。** 为每次预览维护可取消的请求 CTS，并在开始新解码时取消前一个；按 `(assetName, contentHash)` 缓存已生成的预览 Bitmap/像素，限制缓存容量并明确释放所有权。保持版本检查作为并发兜底。

**确认状态。** 无 per-refresh cancellation、编辑器绕过播放缓存和最大缓冲规模已确认；快速选择下的并发数、实际 native Bitmap 内存和 RSS 需实测。

## 订阅、资源释放和主题/图片缓存的补充结论

- `EditorViewModel` 的动作订阅、`Actions.CollectionChanged`、选择索引、警告集合、本地化事件、捕获和预览资源都有显式 Dispose 路径（`src/CrossMacro.UI/ViewModels/EditorViewModel.cs:994-1029`）。
- `MainWindowViewModel.Dispose` 只显式释放 Recording、Schedule、Shortcuts、Triggers、Settings（`src/CrossMacro.UI/ViewModels/MainWindowViewModel.cs:979-985`），没有释放 Playback、Files、TextExpansion、Editor；其中 Playback 自己有计时器、会话和本地化解除逻辑（`src/CrossMacro.UI/ViewModels/PlaybackViewModel.cs:842-866`），Files 订阅本地化和 loaded-session 事件但没有 `IDisposable`（`src/CrossMacro.UI/ViewModels/FilesViewModel.cs:7,65-70`）。当前这些 ViewModel 注册为 singleton（`src/CrossMacro.UI/DependencyInjection/ViewModelServiceRegistration.cs:7-17`），应用退出随后还会 Dispose service provider（`src/CrossMacro.UI/App.axaml.cs:150-169`），所以当前单窗口进程不等同于已证明泄漏；若窗口/容器提前销毁、测试重复构造或未来改为 transient，这个释放缺口会保留事件引用。建议补齐统一 child-lifetime contract，并用弱引用测试确认窗口关闭后可回收。
- 主题服务有按名称的 `_themeCache`，`TryCreateThemeDictionary` 会复用，刷新目录时清空缓存（`src/CrossMacro.UI/Themes/ThemeService.cs:7,84-125,192-209`）；主题字典仅创建颜色和固定刷子映射（`src/CrossMacro.UI/Themes/ThemeResourceDictionaryFactory.cs:49-69`）。当前没有足够静态证据把主题缓存判为持续内存瓶颈。主题应用和外部主题刷新是同步 UI 路径（`src/CrossMacro.UI/Themes/ThemeService.cs:21-82`），动态资源替换可能造成一次性重绑定/重绘，需实测确认。
- `ScreenImageAssetPolicy`、Bitmap Dispose 和播放 LRU 是有效保护，但没有证明编辑器快速预览的峰值 RSS；本审计不填入 FPS/RSS 实测数。

## 可复现实测方案

1. **编辑器投影/通知计数。** 生成 `N={250,1000,5000}` 的 `EditorAction`，在 `ActionListItems.CollectionChanged`、`EditorViewModel.PropertyChanged`、动作 `PropertyChanged` 上计数；对一个 `TextInput` 连续输入 `K=1,10,100` 个字符，记录 `UpdateActionListPresentation` 次数、候选扫描次数、通知总数和 `GC.GetAllocatedBytesForCurrentThread`。分别测试每字符间隔小于/大于 400 ms，验证 coalescing 只影响历史条目。
2. **大宏加载/保存。** 用相同动作类型生成 5,000、20,000、100,000 条输入，分别测 `LoadMacroSequence`、`BuildValidMacroSequenceAsync` 的 UI 线程墙钟时间、UI dispatcher 延迟、托管分配和进程峰值内存；另测接近文件事件上限的输入并确认保护行为。使用 `dotnet-trace`/PerfView 或等效 UI 线程采样，不把静态上限当成实际性能结果。
3. **虚拟化。** 对任务/扩展/loaded-macro 页面记录 `Items.Count` 与实际 `ListBoxItem`/模板视觉节点数，滚动到首尾并观察容器是否随视口复用；比较去除外层 `ScrollViewer`、改成有界虚拟化面板后的布局耗时。编辑器列表单独记录投影集合数与 realized 容器数。
4. **预览并发。** 在 3 个有效大图像资产间以固定间隔快速切换，给解码器包一层只读计数器，记录 started/completed/cancelled/stale 数、并发峰值、解码缓冲和 Bitmap native 内存；重复切换同一资产验证缓存命中。关闭编辑器后用弱引用和 GC 检查旧 Bitmap、ViewModel CTS、解码任务是否可回收。
5. **主题和生命周期。** 反复切换内置主题、刷新外部主题目录，记录一次切换的 UI dispatcher 阻塞、动态资源更新耗时和 managed/native 内存；创建/关闭多个 ViewModel 或窗口后以弱引用检查未显式 Dispose 的 child 是否仍被事件源持有。

## 未测项

本次未启动应用、未构建测试、未运行桌面宏，未取得实际 FPS、输入延迟、UI dispatcher 排队时间、GC 暂停、native Bitmap 分配、RSS、虚拟化 realized 数量或主题切换耗时。上述数值只能由后续受控实测确认。
