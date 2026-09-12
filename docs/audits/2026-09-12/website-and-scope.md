# 官网补充检查与证据边界

审查日期：2026-09-12。基线：`11766a21f4e0c24707991132976f9ec69b66db01`，VERSION `1.4.0`。

## 范围

主体是 Avalonia/.NET 桌面应用；`website/` 是 Astro 官网，不是桌面 UI。此次未修改产品代码、依赖或用户设置，未启动录制/回放。测试执行与日志由 robustness 审查者单独负责。

仓库 `screenshots/editor-tab.png` 实际可见版本为 1.3.0，而当前 VERSION 为 1.4.0。该截图不能证明当前运行界面的布局、主题、滚动行为。其他截图也未建立与当前提交的生成对应关系。

## 官网观察

- `website/src/pages/index.astro:117` 的首屏图和 `:143` 的图库图片没有显式 width/height；`website/src/styles/global.css:314` 起的首屏样式及 `:408` 起的图库样式使用 width:100%、height:auto，外框没有预留 aspect-ratio。代码确认未预留图片纵向尺寸；慢速冷缓存下有布局位移风险，CLS 数值未实测。建议使用导入资源的原始宽高预留空间，再按首屏/非首屏设置加载优先级。
- `website/src/pages/index.astro:138` 起图库只有 figure/img/figcaption，没有放大入口。桌面三列缩略图不便阅读复杂界面；建议提供键盘可操作的原图链接或可关闭的预览。属于可用性建议，不列为高优先级程序缺陷。
- `website/src/layouts/BaseLayout.astro` 只包含 JSON-LD 脚本；当前三个源码页面文件未发现定时轮询或客户端事件监听逻辑。不能据此推断构建后的所有资源大小，但目前不应把官网 JS 内存作为首要优化对象。
- 官网有语义化导航、图片替代文本、响应式网格和 reduced-motion 样式，先保留这些已有基础。

未运行官网构建、浏览器性能采样、Lighthouse、网络传输测量或屏幕阅读器测试。上述检查不构成线上部署验收。

## 测试边界观察

`tests/CrossMacro.UI.Tests/Views/Tabs/EditorTabViewTests.cs` 主要通过读取 XAML 并断言字符串来保证绑定契约。`Views/MainWindowTests.cs` 验证 Layoutable 失效标志。这些测试有价值，但不能替代真实窗口的高 DPI、焦点、点击响应和大列表滚动测试。不能把测试数量或 CI 工作流存在当作当前测试已通过的证据。
