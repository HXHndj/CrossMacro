# Windows Release Portable 预查

日期：2026-09-12。本文只记录静态路径和本机资产检查；没有启动 Release publish、Windows portable smoke、真实宏或用户配置。

## 发布入口

- `global.json` 要求 SDK `10.0.100`，`rollForward=latestFeature`；本机已安装 SDK `10.0.400`，因此满足该解析规则。
- 未发现 `*.pubxml` 文件。发布 profile 由根目录 `Directory.Build.targets` 的 `CrossMacroPublishProfile=portable-trimmed` 集中提供。
- `scripts/ci/publish-windows-portable.ps1` 是 Windows portable 入口，目标为 `src/CrossMacro.UI.Windows/CrossMacro.UI.Windows.csproj`，按 `Release`、`win-x64` 或 `win-arm64` 发布，并要求清理后目录只有一个 `.exe`。
- `portable-trimmed` profile 设置 self-contained、trimmed、single-file、非 AOT；因此交付物应是可直接运行的 `CrossMacro.UI.exe`，不是只含 DLL 的目录。
- `scripts/smoke/windows-portable.ps1` 检查可执行文件后调用 `scripts/smoke/cli-smoke.ps1`；后者覆盖 `--help`、隔离配置下的 `settings get --json`、绝对坐标 dry-run 和 mixed 坐标 dry-run。

## 配置隔离方案

Windows `PathHelper` 通过 `%APPDATA%\\crossmacro` 解析配置，`LoggerSetup` 通过 `%LOCALAPPDATA%\\crossmacro\\logs` 解析日志。每次 Release/portable smoke 应先创建唯一临时根目录，并在同一进程树继承以下变量：

```text
APPDATA=<unique-temp-root>
LOCALAPPDATA=<unique-temp-root>
DOTNET_CLI_HOME=<unique-temp-root>
TEMP=<unique-temp-root>\\tmp
TMP=<unique-temp-root>\\tmp
DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
MSBUILDDISABLENODEREUSE=1
```

验证结束后只检查并报告临时根目录和进程树状态；不读取、覆盖或清理用户现有 `%APPDATA%`/`%LOCALAPPDATA%` 数据。只有确认 SpecialFolder 重定向或产品显式 config root 生效后，portable smoke 的 `settings get` 才能在隔离根下初始化默认文件。

实际检查结果：在本机新启动的 PowerShell/.NET 10 进程中设置 `APPDATA=<临时目录>` 后，`Environment.GetFolderPath(SpecialFolder.ApplicationData)` 仍返回 `C:\Users\\W\\AppData\\Roaming`。因此上述变量清单不能单独作为已验证的配置隔离机制；在产品提供显式 config root、隔离用户 profile 或沙箱前，不得用它宣称 `settings get`/GUI 已隔离。真实 `Roaming\\crossmacro` 与 `Local\\crossmacro\\logs` 已存在，本轮没有触碰。

## 本机资产

- .NET runtime：`Microsoft.NETCore.App 10.0.11`、`Microsoft.WindowsDesktop.App 10.0.11`。
- Windows reference/runtime packs：`Microsoft.NETCore.App.Ref 10.0.11`、`Microsoft.WindowsDesktop.App.Ref 10.0.11`、`Microsoft.NETCore.App.Host.win-x64 10.0.11`。
- NuGet 全局缓存位于 `C:\Users\\W\\.nuget\\packages`，已找到当前 `Directory.Packages.props` 使用的 Avalonia 12.1.1、CommunityToolkit.Mvvm 8.4.2、Microsoft.Extensions 10.0.10、ModelContextProtocol 2.2.0、Serilog sinks、测试包和分析器等版本；本轮不下载或改写包版本。
- Node.js 可用：`D:\Program Files\\nodejs\\node.exe` 24.15.0，npm 11.12.1；`website\\node_modules` 不存在，`npm ls --prefix website --depth=0` 报 `astro@^6.4.4` unmet dependency。官网构建需要先获得依赖，当前不属于 Windows portable Release 前置条件，本轮不下载。

## 执行约束

最终执行应由 `scripts/validation/20260912/Invoke-MonitoredProcess.ps1` 串行包住 publish/smoke 命令，保留命令参数、stdout、stderr、TRX（如适用）、PID 树 JSONL、peak WorkingSet/PrivateBytes、预算、超时和退出码。启动前使用 `--disable-build-servers`、`MSBUILDDISABLENODEREUSE=1`；runner 的 session-local mutex 防止同一会话并发执行。只有退出码为 0、产物结构满足入口脚本约束且隔离 smoke 成功，才计算并记录最终 `.exe` SHA-256。
