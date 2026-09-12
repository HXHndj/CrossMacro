# 分阶段整改交付记录

日期：2026-09-13。分支 `codex/phased-stability-performance`，基于审查基线 `11766a21`。

## 提交清单

| 提交 | 阶段 | 内容 |
| --- | --- | --- |
| `a7e9432e` | Stage 0 | 审计证据与分阶段计划 |
| `3e220e0a` | Stage 2 | Windows 输入停止有界化、native wait 可取消、GetMessage 三分支、SendInput 部分成功补偿、零轮询间隔最小退避 |
| `b33dae8d` | Stage 1 | 任务 mutation 回滚、脚本空白 tokenizer 统一、异步剪切选区快照、MouseMove2D 的 Linux 兼容映射 |
| `76a111d8` | Stage 3 | 设置/Profile/文本扩展事务恢复与切换 fail-closed |
| `dab7406f` | Stage 4 | 编辑器行级投影更新、撤销共享快照、加载/保存后台化、预览请求取消 |
| `2af7f7a9` | Stage 5 | 键盘可达性、列表虚拟化、九语言补全、官网图片无障碍；隔离的控件测试项目 |
| 本次交付提交 | 交付 | 验证记录与本文档 |

## 主审集成修复（本次执行）

- Stage 4 集成缺陷：非选中动作（批量编辑路径）的属性编辑未做撤销记账，与 stage4 测试期望不符；已在 `OnAnyActionPropertyChanged` 补齐与选中路径对称的合并窗口与推栈（`dab7406f` 内）。
- Stage 5 集成缺陷：`HotkeyCapture` 的 XAML 在 populate 期解析矢量图标与光标，需要平台几何/光标服务，无平台单测环境构造即失败；已将 `PathIcon.Data` 与手型光标移至 `OnAttachedToVisualTree`。
- 控件测试线程模型：`Dispatcher.UIThread` 为每线程实例，测试跨 await 换线程后所有权漂移；建立专用 headless UI 线程（`MainLoop`）+ 捕获 dispatcher 实例，`UiPostOverride` 语义修正为投递到 UI dispatcher，断言前等待管线完成并冲刷队列。headless 的进程级注册污染主 UI 测试程序集（全量挂起），故隔离到新程序集 `CrossMacro.UI.Controls.Tests`。
- 剪贴板/热键测试的 `RunContinuationsAsynchronously` 与 Avalonia 线程所有权冲突，按用例改为内联 continuation。

## 最终验证（本机 SDK 10.0.400，Debug）

| 项目 | 结果 | 说明 |
| --- | --- | --- |
| Core / Application / Platform.Windows / MacOS / Daemon / Mcp / Cli / UI.Controls | 454+18+132+446+99+196+469+5 全部通过 | |
| Infrastructure | 1635 通过 / 2 失败 | 均为 `ProfileManagerTests` 符号链接用例，Windows 缺少创建符号链接特权，未进入产品断言（各轮一致） |
| UI | 1089 通过 / 1 失败 | `StyleCompositionTests.AppStyles`，基线即存在的预存失败 |
| Platform.Linux | 791 通过 / 14 失败 / 255 跳过 | 基线即存在的环境性失败（Windows 主机上跑 Linux 依赖用例），数量与基线一致 |
| 合计 | 5343 通过 / 17 失败 / 316 跳过 | 17 个失败全部为预存环境问题，与整改前逐项一致 |

Release：`dotnet publish src/CrossMacro.UI.Windows -c Release` 成功，0 错误。正式 Windows 便携包由 `scripts/ci/publish-windows-portable.ps1`（portable-trimmed，单 exe）在 CI 产出；本机发布预查见 `validation/release-precheck.md`（其中注明：APPDATA 重定向在当前系统不改变 SpecialFolder 解析，隔离 smoke 需要显式 config root 支持）。

## 未覆盖（如实声明）

- 未启动正式 GUI、真实录制/回放/输入注入/屏幕采集；未测 UI 帧率、p95 延迟、RSS 峰值（审计"需实测"项仍待受控基准）。
- dispatcher 有界停止不能强杀阻塞委托：超时后仅阻止后续派发，生产上永久阻塞的订阅者需新实例替代（Stage 2 边界）。
- SendInput 部分成功补偿仅覆盖批次前缀中明确的鼠标按下；真实 UIPI/权限级别的部分计数模式待 native 受控测试。
- Stage 3 的跨文件事务为"写前快照 + 失败恢复"，两次 File.Replace 之间进程崩溃仍可能留混合状态（journal 恢复未实现）。
