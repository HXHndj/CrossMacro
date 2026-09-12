# Stage 5 UI/UX 与官网修复

日期：2026-09-12。范围限定为热键控件、回放/编辑器视图文案与布局、文件页忙状态、增长列表视口/虚拟化，以及官网截图呈现。未修改 `Editor*.cs`、`TextBoxClipboardHandler`、依赖、平台/runtime/settings/profile 服务。

## 已实现

- `HotkeyCapture` 显式设置 `IsTabStop`，支持焦点后的 Enter/Space 启动捕获；鼠标启动时主动聚焦控件；捕获中显示本地化 Cancel 按钮。Esc 保留为可选择的热键，Cancel 通过独立入口完成，不把 Esc 重新解释为取消。
- 回放 Precision 分支改用 `Playback_PrecisionMotionRate`，语义为质量上限；编辑器外层底栏从不存在的 `Grid.Row="2"` 调整到已声明的第二行 `Grid.Row="1"`。没有依据旧截图声称历史版本必然遮挡。
- 编辑器多选/多等待操作提示拆为数量与本地化标签/说明，新增的 `Editor_SelectedActionsLabel`、`Editor_SelectedWaitActionsLabel`、`Editor_MultiSelectionPropertiesHint` 在基准资源和全部 8 个语言资源中保持同键集合。
- Files ViewModel 增加原子 Load/Save 进行中门禁、`IsFileOperationInProgress` 通知和加载/保存状态文本；文件页在操作期间禁用交互，loaded-macro 列表拥有界定的视口。操作结束时 busy 属性通过 `RunOnUiThreadAsync` 清除，门禁在 UI 更新 finally 后释放。
- Schedule、Shortcut、Trigger、Text Expansion 的无选择增长列表使用有界 `ScrollViewer`、`ItemsControl` 与 `VirtualizingStackPanel`；Files 保留原有 `SelectedMacroItem` 语义并给 `ListBox` 加有界虚拟化视口，Shortcut 的 Window Rules 也加入同样的无选择虚拟化列表。这样没有引入独立 `ListBox.SelectedItem` 与任务行 `SelectTaskCommand` 的双重选择状态。固定星期选项未改造。
- 官网首屏、图库和品牌图片补充导入资源的 `width`/`height`，图库每项提供可见的 “Open full-size image” 原图入口并保留具体 `aria-label`。

## 精确变更文件

- `src/CrossMacro.UI/Controls/HotkeyCapture.axaml`
- `src/CrossMacro.UI/Controls/HotkeyCapture.axaml.cs`
- `src/CrossMacro.UI/ViewModels/FilesViewModel.cs`
- `src/CrossMacro.UI/Views/Tabs/EditorTabView.axaml`
- `src/CrossMacro.UI/Views/Tabs/PlaybackTabView.axaml`
- `src/CrossMacro.UI/Views/Tabs/FilesTabView.axaml`
- `src/CrossMacro.UI/Views/Tabs/ScheduleTabView.axaml`
- `src/CrossMacro.UI/Views/Tabs/ShortcutTabView.axaml`
- `src/CrossMacro.UI/Views/Tabs/TriggerTabView.axaml`
- `src/CrossMacro.UI/Views/Tabs/TextExpansionTabView.axaml`
- `src/CrossMacro.UI/Localization/Resources.resx`
- `src/CrossMacro.UI/Localization/Resources.ar.resx`
- `src/CrossMacro.UI/Localization/Resources.es.resx`
- `src/CrossMacro.UI/Localization/Resources.fr.resx`
- `src/CrossMacro.UI/Localization/Resources.ja.resx`
- `src/CrossMacro.UI/Localization/Resources.pt.resx`
- `src/CrossMacro.UI/Localization/Resources.ru.resx`
- `src/CrossMacro.UI/Localization/Resources.tr.resx`
- `src/CrossMacro.UI/Localization/Resources.zh.resx`
- `tests/CrossMacro.UI.Tests/Controls/HotkeyCaptureTests.cs`
- `tests/CrossMacro.UI.Tests/ViewModels/FilesViewModelTests.cs`
- `website/src/pages/index.astro`
- `website/src/layouts/BaseLayout.astro`
- `website/src/styles/global.css`
- `docs/fixes/2026-09-12/stage5.md`

## 静态验证

- 逐一 XML 解析 8 个修改后的 AXAML 与 9 个 `.resx`，均通过。
- 以 `Resources.resx` 为基准比较全部语言资源键集合，9 个资源文件均报告 `PARITY`。
- 文本筛选确认热键 Enter/Space、Cancel 入口、Precision 标签、编辑器 `Grid.Row="1"`、文件 busy 状态、各增长列表的有界 `ScrollViewer`/`MaxHeight`/`VirtualizingStackPanel`、官网图片尺寸和原图链接均存在。
- 按用户边界没有运行 build/test、GUI、浏览器、屏幕阅读器、模拟输入或 Git 操作。

## 动态未测与候选处置

- 未验证 Avalonia 在实际窗口中对列表容器的 realized 数量、滚动手感、嵌套 ListBox 的焦点顺序，以及编辑表单在 100%/150%/200% DPI、窄窗口和长本地化文案下是否完整可见。
- 未验证 Enter/Space 是否由所有平台的焦点导航送达 `HotkeyCapture`，Cancel 与全局捕获服务的竞态，或屏幕阅读器实际朗读名称；Esc 作为合法热键的行为保留为产品选择。
- 未验证 Load/Save 对话框在真实桌面上的重入行为、文件系统延迟、状态栏时序和操作取消；代码门禁已覆盖同一 ViewModel 的并发入口。
- 未验证官网 Astro 构建后的资源 URL、CLS、原图实际分辨率和移动端布局；当前只完成源码级尺寸/链接检查。
- 已补充 headless 行为测试 seam：热键 Enter/Space 启动、Cancel 子控件来源不触发父级捕获、取消后延迟结果不回写；Files 测试覆盖延迟对话框的 Load/Save 互斥及取消/成功/异常后的 busy 恢复。测试未执行。
