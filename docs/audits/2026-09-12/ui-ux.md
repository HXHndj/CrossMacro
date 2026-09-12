# CrossMacro UI/UX 只读审查

日期：2026-09-12。源码基线：当前工作树，`VERSION` 为 `1.4.0`。

本审查覆盖主窗口导航，以及录制、文件、编辑、回放、设置路径；重点查看 `Views`、`Styles`、相关 ViewModel/Service 和 `tests/CrossMacro.UI.Tests`。没有启动 GUI、模拟输入、构建或运行测试，也没有改产品代码、依赖或用户设置。以下“确认”表示问题由源码调用链或静态布局直接可见；真实时序、排版和辅助功能表现仍需实测。

仓库 `screenshots/*.png` 属于快照，显示 `CrossMacro v1.3.0`，不能作为当前 `1.4.0` 的视觉验收证据。本次没有把截图用于确认当前布局。

## 主审验收修订

- 接受第 1 项剪切时序缺口、第 2 项缺少键盘激活/显式取消路径、第 5 项硬编码英文。第 2 项中 Esc 作为可选热键未必应禁止：建议提供明确的取消按钮或取消语义，而不是未经产品决定一律占用 Esc。
- 第 3 项降为字段语义待确认：两模式共用标签是代码事实，但仅凭资源键名无法证明实际显示错误；先对照两个参数的算法语义和说明文字。
- 第 4 项仅为结构整理项：越界 Grid.Row 不等于已证实重叠/不可见，框架可能将其限制在最后一行。未取得当前渲染证据，不计为阻断保存的确认缺陷。
- 第 6 项按“进行中反馈及重入防护待测”保留：方法内部没有 busy gate 已确认，但还要验证 Avalonia 方法命令包装和平台模态对话框行为，不能直接宣称双击必然并行打开对话框或重复加载。
- 仅直接查看过 editor-tab.png 的 1.3.0 版本标记；不能把这一标记外推为每张截图都已经核验版本。

## 发现与候选

### 1. Ctrl+X 的异步剪切可能删除用户后来选中的内容（高，代码确认；时序复现需实测）

- 触发路径：在任意编辑器 `TextBox` 选中文本，触发系统 Cut；窗口在 `CuttingToClipboard` 事件中立即 `e.Handled = true`，随后 fire-and-forget 启动异步剪切。
- 证据：`D:\Desktop\网页设计\Mouse\src\CrossMacro.UI\Views\MainWindow.axaml.cs:107-117,140-157`；`D:\Desktop\网页设计\Mouse\src\CrossMacro.UI\Services\TextBoxClipboardHandler.cs:30-47`。处理器先保存 `selectedText`，等待 `setTextAsync`，再对“当前” `textBox.SelectedText` 写入空字符串。
- 根因：等待剪贴板完成期间没有保存并校验选择范围、文本版本或控件焦点。用户若在等待中改选区或继续输入，清空操作指向新选区。
- 影响：可能静默删除错误的输入内容；剪贴板写入成功后无法恢复原选区。建议以起始/结束索引和文本版本删除原范围，仅在控件内容未改变时提交；变化时保留文本并报告剪切未完成。
- 保护与反证：剪贴板异常路径会保留文本；`tests\CrossMacro.UI.Tests\Services\TextBoxClipboardHandlerTests.cs:56-87` 只覆盖同步成功和异常，没有覆盖异步期间选区/文本变化，也没有覆盖窗口事件的 fire-and-forget 时序。

### 2. 设置热键控件对键盘用户没有启动捕获路径，Esc 也会被当作新热键（中，代码确认；焦点/OS 行为需实测）

- 触发路径：设置页中的 `HotkeyCapture` 声明 `Focusable="True"`，但捕获只挂在内部 `Border.PointerPressed`；控件没有 `KeyDown`/Enter/Space 处理。鼠标启动捕获后按 Esc，底层服务会把任意按下的键直接完成捕获。
- 证据：`D:\Desktop\网页设计\Mouse\src\CrossMacro.UI\Controls\HotkeyCapture.axaml:11-16`；`...\HotkeyCapture.axaml.cs:180-212`；`D:\Desktop\网页设计\Mouse\src\CrossMacro.Infrastructure\Services\GlobalHotkeyService.cs:275-331`。服务的捕获分支没有 Escape 特判或取消结果。
- 根因：控件的可聚焦状态与交互实现不一致，且没有显式取消动作。
- 影响：纯键盘用户无法从 Tab 焦点开始修改全局热键；鼠标用户按 Esc 取消时会意外把 Esc 写入该槽位。建议提供真正的 Button/键盘激活语义，并让 Esc 取消且保留旧值，另提供可见取消反馈。
- 保护与反证：控件在卸载时会取消待处理捕获；`tests\CrossMacro.UI.Tests\Controls\HotkeyCaptureTests.cs:5-12` 只检查 `IDisposable`，没有键盘激活、Escape 或辅助功能测试。

### 3. 回放两模式共用速率标签（字段语义待确认）

- 触发路径：回放页选择 `Precision (recommended)`，显示的速率输入标签仍来自 `Playback_StrictSpeedMotionRate`，和下方 Strict speed 分支相同。
- 证据：`D:\Desktop\网页设计\Mouse\src\CrossMacro.UI\Views\Tabs\PlaybackTabView.axaml:70-85` 使用 `Playback_StrictSpeedMotionRate`；`...\PlaybackTabView.axaml:88-104` 再次使用同一键。资源中该键值为“Report limit/报告上限”，而精确模式只有 `Playback_PrecisionMotionRateHint`，见 `D:\Desktop\网页设计\Mouse\src\CrossMacro.UI\Localization\Resources.resx:307-313` 和 `Resources.zh.resx:304-313`。
- 根因：Precision 分支复制了 Strict speed 的标签绑定，资源键没有对应到模式语义。
- 影响：用户会把“保留点、可能降速”的 Precision 选项误解为“报告上限”，难以判断输入值实际控制什么。建议增加 Precision 专用标签资源并绑定到该分支；补充每个模式的视图绑定断言。
- 保护与反证：模式的 ViewModel 状态和回放行为有测试，但当前 UI 测试没有检查这两个字段标签是否对应。

### 4. 编辑器底部状态/操作栏使用了未声明的 Grid 行（结构整理项；不计已确认布局故障）

- 触发路径：进入编辑页并查看底部状态、撤销/重做、运行测试、加载和保存区域，尤其在窗口缩放或内容较高时。
- 证据：`D:\Desktop\网页设计\Mouse\src\CrossMacro.UI\Views\Tabs\EditorTabView.axaml:29` 外层 Grid 只声明 `RowDefinitions="*,Auto"`；同一外层 Grid 的底部区域在 `...\EditorTabView.axaml:1128-1129` 使用 `Grid.Row="2"`。当前源码没有对应的第三行定义。
- 根因：容器行定义与子项行索引不同步；应明确让底部区域落在第二行，或补齐并定义第三行。
- 影响：依赖 Avalonia 对越界行索引的处理，状态/操作栏可能被压到错误区域、与主内容重叠或在特定尺寸不可见；这会直接阻断编辑器保存、加载和测试回放的可发现性。建议修正行索引/定义，并在 100%、150%、200% DPI 与窄窗口检查底栏可见和可操作。
- 保护与反证：旧 `screenshots/editor-tab.png` 看见底栏，但它是 `v1.3.0` 快照，不能反证当前源码；没有执行动态布局检查。

### 5. 编辑器多选提示绕过了语言设置，固定显示英文（中，代码确认）

- 触发路径：在编辑器动作列表中选择多个非纯等待动作，右侧多选提示显示；选择中文或其他语言后仍会显示英文句子。
- 证据：`D:\Desktop\网页设计\Mouse\src\CrossMacro.UI\Views\Tabs\EditorTabView.axaml:1077-1080` 的 `"{0} wait actions selected"`，以及 `...\EditorTabView.axaml:1115-1117` 的 `"{0} actions selected. Properties are shown only for one action or multiple wait actions."` 都是字面量。`ShowMultiSelectionPropertiesHint` 条件见 `D:\Desktop\网页设计\Mouse\src\CrossMacro.UI\ViewModels\EditorViewModel.cs:356-359`。
- 根因：用户可见的多选文案写在 AXAML `StringFormat` 中，没有经过本地化服务，也没有复数规则。
- 影响：设置页允许切换语言，但核心编辑反馈混用英文；长句在右侧窄面板还可能换行或被截断。建议将数量和说明拆成带复数处理的本地化资源，并补充非英文视图文案检查。
- 保护与反证：资源奇偶/键集合测试不能发现 AXAML 中不存在的字面量；现有编辑器测试覆盖状态属性，没有覆盖该文案。

### 6. 文件页 Load/Save 的进行中反馈与重入防护（方法级缺少 gate；命令和对话框行为待测）

- 触发路径：文件页已有宏时快速重复点击 Save，或连续点击 Load；按钮绑定方法命令，且仍由 `CanLoadMacro`/`CanSaveMacro` 保持启用。
- 证据：`D:\Desktop\网页设计\Mouse\src\CrossMacro.UI\Views\Tabs\FilesTabView.axaml:77-100`；`D:\Desktop\网页设计\Mouse\src\CrossMacro.UI\ViewModels\FilesViewModel.cs:144-149,265-352`。`CanLoadMacro` 和 `CanSaveMacro` 只受外部播放状态/是否有宏影响；Load/Save 方法在文件对话框和文件管理器 await 期间没有 in-flight 标志或互斥门禁。
- 根因：异步文件操作没有共享 busy 状态，界面也没有“正在加载/保存”的反馈。
- 影响：重复 Load 可能将同一文件加入会话多次；重复 Save 可能并行弹出对话框并以最后完成者覆盖状态/源路径反馈。建议引入独立的 Load/Save busy 状态，命令执行期间禁用对应操作，并在状态栏给出进行中提示。
- 保护与反证：删除加载宏有确认对话框且有对应测试；现有文件测试覆盖取消、错误、成功及保存期间切换选择，但没有并行 Load/Save 或双击重入测试。

## 覆盖与未测范围

已读：`src/CrossMacro.UI/Views/MainWindow.axaml(.cs)`、录制/文件/回放/编辑/设置视图及相关 ViewModel、`Styles` 中输入/选择/按钮/列表样式、剪贴板与全局热键调用链，以及对应 UI/ViewModel 测试。

未测：真实 Avalonia 排版对越界 `Grid.Row` 的处理；纯键盘 Tab 顺序、焦点可见性、屏幕阅读器名称与按钮自动化树；高 DPI/系统文本缩放、窄窗口、RTL 和所有语言的实际换行；剪贴板延迟下选区竞态；文件对话框是否允许重入及重复操作的真实结果；录制/回放真实桌面输入、托盘和平台窗口装饰行为。截图仅为 `v1.3.0` 仓库快照，未用于当前视觉验收。
