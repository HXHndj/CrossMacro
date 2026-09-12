# 持久化、调度、Profile 生命周期与应用鲁棒性审计

审计范围：`src/CrossMacro.Infrastructure`、`src/CrossMacro.Application`、相关 Core/UI 测试，以及 `global.json`、`Directory.Build.*`、`Directory.Packages.props`。本轮只读审查源代码并执行已有测试；没有修改产品代码、依赖或启动正式应用/服务，也没有执行真实宏或真实设置。

## 结论

当前源代码可以在本机 SDK `10.0.400`（`global.json` 要求 `10.0.100`、`latestFeature`）下用既有 NuGet 资产重新编译。原子写入在单个文件层面成立：`FileBackedJsonStorage` 先写唯一临时文件并 flush，再 `File.Replace`/`File.Move`，最后删除临时文件（`src/CrossMacro.Infrastructure/Helpers/FileBackedJsonStorage.cs:22-63,78-87`）。跨文件设置、Profile 目录与注册表的更新仍可能留下混合状态；Profile 切换和应用层变更的异常/取消边界也需要确认。

## 主审验收修订

主审抽查了 ManageSchedule、ManageTextExpansion、TextExpansionStorageService、RuntimeLifecycle、RunScriptRuntimeValidator、RunScriptScreenReadingStepParser 和 RunScriptScreenReadExecutor 的原始实现。以下项目不能统称为六个已复现 P1：

- 第 1、2 项接受为条件性 P2：单文件已有原子写入，跨文件/目录失败恢复不足仍需要故障注入，尚未证明正常操作丢失数据。
- 第 3 项接受为条件性 P2 并发风险：ManageTextExpansion 自身有 `_operationGate`，非活动 profile 在存在 `_profileStore` 时会走独立路径，这些保护必须保留。问题集中于活动存储与 coordinator 切换没有共享事务边界，而不是所有操作都没有锁。
- 第 4 项接受为优先修复项：Add/Update 在修改操作之后仍可能取消/保存失败，无相应回滚；Remove/SetEnabled 已有部分补偿，不应概括为所有 mutation 都没有补偿。
- 第 5 项降为生命周期契约审计项：编排器不停止抛错的启动步骤是代码事实，但步骤可能负责自清理。本次没有确认一个内置步骤必然在半启动失败后泄漏，故不计为已确认 P1 产品缺陷。
- 第 6 项接受为 P2：命令识别接受 whitespace，后续仅按空格切分，验证器将未识别解析结果作为成功，执行器直接返回；需补同一 tokenizer 和明确错误。

## 候选问题与证据（6 项）

### 1. [P2] 全局设置和 Profile 设置不是一个事务

`SettingsService.SaveCoreAsync` 先写全局文件，再在 Profile generation 仍匹配时写 Profile 文件（`src/CrossMacro.Infrastructure/Services/SettingsService.cs:154-179`）；同步路径也按同样顺序分别写入（`src/CrossMacro.Infrastructure/Services/SettingsService.cs:192-220`）。因此全局写成功、Profile 写失败时，重启后会看到新全局设置配旧 Profile 设置。单文件临时写入只能保护每个文件，不能回滚已经完成的第一步。

触发：让全局路径可写、Profile 设置路径的父级为阻塞文件或在第二次写入时注入 I/O 失败。反证/现有覆盖：`SettingsServiceTests.Save_WhenWriteFails` 与 `SaveAsync_WhenWriteFails` 让配置根本身不可用，失败发生在第一份文件，未覆盖第二份文件成功后的回滚。修复方向：两份临时文件都完成并 flush 后再提交，并保留旧文件备份/恢复日志；或者明确采用可恢复 journal 并在启动时完成恢复。未测：第二文件失败、进程崩溃于两次 replace 之间、磁盘满/权限变化。

### 2. [P2] Profile 创建/改名/删除失败后没有完整回滚

创建先写 Profile 文件，再把条目加入内存注册表并保存注册表（`src/CrossMacro.Infrastructure/Services/ProfileManager.cs:125-137`）；注册表保存失败会留下完整但不可见的目录和内存条目。改名先修改对象再保存注册表（`src/CrossMacro.Infrastructure/Services/ProfileManager.cs:153-168`），保存失败会使内存名称与磁盘名称分叉。删除先从注册表移除并持久化，再递归删除目录（`src/CrossMacro.Infrastructure/Services/ProfileManager.cs:191-204`）；目录删除失败会留下脱离注册表的目录。

触发：在 `SaveRegistryAsync` 或 `Directory.Delete` 注入失败。反证/现有覆盖：Profile 测试覆盖干净创建、迁移、路径穿越和符号链接边界，但没有注册表写失败、目录删除失败或重启后恢复检查。修复方向：对注册表和目录操作使用可恢复的事务步骤；保存失败恢复内存快照，创建失败清理新目录，删除使用可恢复移动并在清理失败时保留可见状态。未测：注册表 replace 之后进程崩溃、创建文件序列中任一步失败、目录包含锁定文件/重解析点。

### 3. [P2，条件性] Profile 切换的串行门没有覆盖所有存储调用，并且部分停止失败仍继续切换

`ProfileRuntimeCoordinator` 的 `_gate` 只串行化 coordinator 自身的切换请求（`src/CrossMacro.Infrastructure/Services/ProfileRuntimeCoordinator.cs:18,80-153`）。`ManageTextExpansion` 可以在另一个调用中临时执行 `_store.ReloadAsync(profileDirectory)`（`src/CrossMacro.Application/Automation/ManageTextExpansion.cs:83-119`），而 `TextExpansionStorageService.SaveAsync` 在未持有同一把锁时读取可变 `FilePath` 并写入（`src/CrossMacro.Infrastructure/Services/TextExpansionStorageService.cs:121-149,164-177`）。切换同时发生时，一次保存可能落到新 Profile，或在 reload 后把旧列表重新写入缓存。

此外，停止文本扩展、快捷键、触发器和热键时的异常被记录后忽略（`src/CrossMacro.Infrastructure/Services/ProfileRuntimeCoordinator.cs:178-216`），只有 scheduler 生命周期未结束才中止。旧监听器停止失败时继续加载并启动新 Profile，会造成旧/新配置并存或重复监听。

触发：并发执行针对 Profile 的文本扩展写入与 `SwitchProfileAsync`，或让任一非 scheduler runtime service 的 Stop 抛错。反证/现有覆盖：Profile 测试只按顺序验证 scheduler 停止顺序、participant reload 和 participant reload 失败恢复；没有并发写入、停止失败后状态检查。修复方向：把所有 Profile-scoped mutation 纳入 coordinator 的共享 gate/participant 生命周期，保存时捕获路径和 generation；停止失败应 fail closed，报告完整状态并阻止 commit/restart，或有明确补偿。未测：并发 switch A/B、写入期间 switch、旧服务停止异常后新服务是否仍可用。

### 4. [P1] 应用层 Add/Update 在取消或保存失败时会留下内存变更

`ManageSchedule`、`ManageShortcut`、`ManageTrigger` 都先调用 operations，再检查取消令牌并保存；Add/Update 没有异常回滚（`src/CrossMacro.Application/Automation/ManageSchedule.cs:12-29`、`ManageShortcut.cs:26-40`、`ManageTrigger.cs:12-29`）。取消发生在 mutation 与 save 之间，或 save 抛错时，运行时集合已经改变但磁盘仍是旧版本。Remove/SetEnabled 的补偿也不完全等价于原快照：Remove 依赖重新 Load，SetEnabled 重新计算的调度时间可能不同。

触发：operations 回调中取消令牌，或让 store.SaveAsync 抛出。反证/现有覆盖：`ManageTaskWorkflowCancellationTests` 只在 LoadAsync 阶段取消，证明尚未 mutation 时不保存；没有 mutation 后取消、保存失败或恢复后集合断言。修复方向：在 operations 前获取不可变快照，保存失败/取消时恢复快照；或让 store 提供带事务语义的 mutate-and-save 操作，并在成功提交后再观察取消。未测：四种 mutation 的中途取消、三类 store 的写失败、并发管理调用。

### 5. [待核实契约] 生命周期启动步骤在“部分成功后抛错”时不会被编排器回滚

`RuntimeLifecycle` 只有在 `step.StartAsync` 完成返回后才把步骤加入 `_startedSteps`（`src/CrossMacro.Application/Runtime/RuntimeLifecycle.cs:20-34`）。若步骤已注册监听/资源但在返回前抛出异常或取消，该步骤没有进入清理列表；启动回滚只会停止之前已记录的步骤（`RuntimeLifecycle.cs:35-43,91-99`）。

触发：一个 Start delegate 先产生副作用，再抛 `OperationCanceledException`/`IOException`。反证/现有覆盖：`RuntimeLifecycleTests.CancelledStartRollsBackCompletedStepsWithoutStartingLaterSteps` 在第二步完成返回后才取消（测试 `tests/CrossMacro.Application.Tests/Runtime/RuntimeLifecycleTests.cs:47-72`），因此没有覆盖部分启动。修复方向：定义可补偿的 start contract（例如步骤先登记、返回启动句柄，或显式 `StartAsync` 失败清理自身），并为部分启动添加测试。未测：每个内置生命周期步骤的半启动异常、取消与清理异常叠加。

### 6. [P2] 屏幕脚本的混合空白会被验证为合法并在运行时静默 no-op

`RunScriptSyntax.IsScreenReadingStep`/`StartsWithCommandToken` 用 `char.IsWhiteSpace` 识别命令边界（`src/CrossMacro.Core/Services/RunScriptSyntax.cs:221-230,308-315`），但 `RunScriptScreenReadingStepParser.SplitStep` 只按 ASCII 空格切分（`src/CrossMacro.Infrastructure/Services/RunScriptScreenReadingStepParser.cs:26-36,78-80`）。当输入含制表符时，`RunScriptRuntimeValidator` 看见命令前缀却把 `TryValidateStep == false` 当作无错误成功（`src/CrossMacro.Infrastructure/Services/RunScriptRuntimeValidator.cs:72-76`）；运行执行器随后因解析失败直接返回（`src/CrossMacro.Infrastructure/Services/Playback/RunScriptScreenReadExecutor.cs:55-58`）。例如 `pixelcolor\t1\t2\tcolor` 会被接受但不读取屏幕，也不报错。

触发：从文件、剪贴板或编辑器输入使用 tab/混合空白的 screen-reading step。反证/现有覆盖：运行时和 parity 测试使用普通空格，没有 tab、CR/LF 或混合空白边界。修复方向：使用同一个按 `char.IsWhiteSpace` 的 tokenizer，并把“命令前缀已识别但形状无效”转换为明确 syntax error；对所有 screen-reading 命令增加混合空白测试。未测：tab、非 ASCII 空白、命令与参数相邻、引号路径和错误 step 编号。

## 测试基线与证据

所有命令均在 `D:\Desktop\网页设计\Mouse` 执行，使用已有 `obj/project.assets.json`，没有 restore；`/m:1` 串行构建。三个命令都重新输出了当前源码对应的项目 DLL，因而“测试编译当前代码”成立。

| 项目/筛选 | 退出码 | 结果 | 说明 |
|---|---:|---|---|
| `dotnet test tests\\CrossMacro.Infrastructure.Tests\\CrossMacro.Infrastructure.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~DebouncedSaveCoordinatorTests|FullyQualifiedName~JsonScheduledTaskRepositoryTests|FullyQualifiedName~ProfileManagerTests|FullyQualifiedName~SchedulerServiceTests|FullyQualifiedName~MacroScheduledTaskExecutorTests|FullyQualifiedName~SettingsServiceTests|FullyQualifiedName~ProfileLoadedMacroSessionStoreTests|FullyQualifiedName~ProfileSwitchRequestBridgeTests" --logger "trx;LogFileName=infra-robustness.trx" --results-directory docs\\audits\\2026-09-12\\validation /m:1 --verbosity minimal` | 1 | 83 总计，81 通过，2 失败，0 跳过 | 两项 `ProfileManagerTests` 符号链接测试在 `Directory.CreateSymbolicLink` 处失败，Windows 环境缺少创建符号链接特权；堆栈为测试行 171/190，未进入产品断言。 |
| `dotnet test tests\\CrossMacro.Application.Tests\\CrossMacro.Application.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~ManageTaskWorkflowCancellationTests|FullyQualifiedName~ManageTaskSnapshotTests|FullyQualifiedName~RuntimeLifecycleTests" --logger "trx;LogFileName=application-robustness.trx" --results-directory docs\\audits\\2026-09-12\\validation /m:1 --verbosity minimal` | 0 | 11/11 通过，0 跳过 | 覆盖加载取消、快照、生命周期启动/停止回滚。 |
| `dotnet test tests\\CrossMacro.UI.Tests\\CrossMacro.UI.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~TextBoxClipboardHandlerTests|FullyQualifiedName~LoadedMacroSessionTests|FullyQualifiedName~ProfileLoadedMacroSessionPersistenceServiceTests|FullyQualifiedName~RuntimeLifecycleTests" --logger "trx;LogFileName=ui-robustness.trx" --results-directory docs\\audits\\2026-09-12\\validation /m:1 --verbosity minimal` | 0 | 20/20 通过，0 跳过 | 包含剪贴板成功/失败基础测试；其替身立即完成或失败，**不覆盖等待期间选区变化**。 |

保留的原始 TRX：

- `docs/audits/2026-09-12/validation/infra-robustness.trx`
- `docs/audits/2026-09-12/validation/application-robustness.trx`
- `docs/audits/2026-09-12/validation/ui-robustness.trx`

构建期间有现有编译器/分析器 warning，但没有编译错误；本轮结束时未发现残留 `dotnet`、`vstest` 或 `testhost` 进程。没有对测试进程峰值 RSS/PrivateBytes 做采样，因此不能把 1.5 GB 作为已测上限。
