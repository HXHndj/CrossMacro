# 第一轮修复记录

日期：2026-09-12

本轮按 `docs/audits/2026-09-12/robustness.md` 主审修订和 `ui-ux.md` 第 1 项执行，范围限定为任务 mutation 回滚、屏幕脚本空白解析和异步剪切时序。没有执行 Git 提交、暂存或其他写操作，也没有执行 build 或 test；构建与测试由主审统一串行执行。

## 改动

- `ManageSchedule`、`ManageShortcut`、`ManageTrigger` 在 Add/Update 的操作完成后检查到取消或 Save 失败时执行补偿。Add 通过 RemoveTask 回滚，Update 使用操作前的深拷贝恢复任务字段；调度任务额外恢复操作适配器可能重算的 `NextRunTime`。操作进入适配器前标记为待回滚，以覆盖同步适配器部分修改后抛错；恢复失败时用 `AggregateException` 保留原异常和恢复异常。
- Add 在进入 mutation 前拒绝已有相同 ID，避免按 ID Remove 时误删原任务。
- `RunScriptScreenReadingStepParser.SplitStep` 改用 .NET 全部 Unicode whitespace 规则，与 `RunScriptSyntax` 的 `char.IsWhiteSpace` 边界一致。运行时验证器对识别到的 screen-reading 前缀但无法解析的步骤返回明确语法错误；执行器对所有 screen-reading 命令统一验证，解析失败不再静默返回。
- `TextBoxClipboardHandler.TryCutAsync` 保存剪切开始时的完整文本、选区边界和选中文本。异步 clipboard 写入完成后仅在三者仍完全一致时清除选区；期间文本或选区变化则保留当前内容并返回失败。

## 精确文件列表

- `src/CrossMacro.Application/Automation/ManageSchedule.cs`
- `src/CrossMacro.Application/Automation/ManageShortcut.cs`
- `src/CrossMacro.Application/Automation/ManageTrigger.cs`
- `tests/CrossMacro.Application.Tests/Automation/ManageTaskMutationRollbackTests.cs`
- `src/CrossMacro.Infrastructure/Services/RunScriptScreenReadingStepParser.cs`
- `src/CrossMacro.Infrastructure/Services/RunScriptRuntimeValidator.cs`
- `src/CrossMacro.Infrastructure/Services/Playback/RunScriptScreenReadExecutor.cs`
- `tests/CrossMacro.Infrastructure.Tests/Services/RunScriptScreenReadingWhitespaceTests.cs`
- `src/CrossMacro.UI/Services/TextBoxClipboardHandler.cs`
- `tests/CrossMacro.UI.Tests/Services/TextBoxClipboardHandlerTests.cs`
- `docs/fixes/2026-09-12/stage1.md`

## 新增和建议执行的筛选

主审应在单一串行窗口执行以下筛选，并保留其 TRX/控制台输出：

- `dotnet test tests\CrossMacro.Application.Tests\CrossMacro.Application.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~ManageTaskMutationRollbackTests|FullyQualifiedName~ManageTaskWorkflowCancellationTests|FullyQualifiedName~ManageTaskSnapshotTests"`
- `dotnet test tests\CrossMacro.Infrastructure.Tests\CrossMacro.Infrastructure.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~RunScriptScreenReadingWhitespaceTests|FullyQualifiedName~RunScriptScreenReadRuntimeTests|FullyQualifiedName~RunScriptCompilerTests"`
- `dotnet test tests\CrossMacro.UI.Tests\CrossMacro.UI.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~TextBoxClipboardHandlerTests"`

Application 替身测试会真实增删/更新内存集合，覆盖 Add 取消、Update 保存失败、调度时间恢复，以及保存和恢复同时失败时保留两项异常。Infrastructure 测试覆盖 tab、CR/LF、NBSP 混合空白的分词、编译语法错误、执行坐标读取和 malformed screen command 的明确错误。UI 测试使用延迟 clipboard 替身分别改变选区和文本，断言不误删。

## 风险与未测

补偿回滚恢复的是任务运行时对象状态，不构成跨进程事务，也不能撤销适配器已向外部观察者发出的集合事件或其它外部副作用。它依赖既有 `SaveAsync` 的原子文件语义；若 Save 在失败前已产生不可见的持久化部分提交，仍需存储层 journal/恢复机制。应用层没有新增跨调用共享锁，并发管理调用仍不在本轮范围内。

调度 `NextRunTime` 的精确恢复依赖 store 返回的对象就是 operations 更新的运行时对象；适配器若替换对象或跨集合复制，仍需更高层事务端口。真实 Avalonia UI、真实系统 clipboard、Windows runtime、磁盘故障注入、build/test、真实宏与用户配置均未在本轮运行。
