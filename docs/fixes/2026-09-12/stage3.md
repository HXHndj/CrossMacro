# Stage 3 persistence and Profile safety fixes

本轮按主审授权完成最小范围修复；没有运行 build/test/restore，没有启动正式应用或服务，也没有读写真实用户配置。修改只涉及以下源代码和对应故障注入/并发回归测试：

- `src/CrossMacro.Infrastructure/Helpers/FileBackedJsonStorage.cs`
- `src/CrossMacro.Infrastructure/Services/SettingsService.cs`
- `src/CrossMacro.Infrastructure/Services/ProfileManager.cs`
- `src/CrossMacro.Infrastructure/Services/ProfileRuntimeCoordinator.cs`
- `src/CrossMacro.Infrastructure/Services/TextExpansionStorageService.cs`
- `src/CrossMacro.Application/Automation/ManageTextExpansion.cs`
- `src/CrossMacro.Application/Automation/ITextExpansionStore.cs` (窄 scope capability)
- `tests/CrossMacro.Infrastructure.Tests/Services/SettingsServiceTests.cs`
- `tests/CrossMacro.Infrastructure.Tests/Services/ProfileManagerTests.cs`
- `tests/CrossMacro.Infrastructure.Tests/Services/TextExpansionStorageServiceTests.cs`
- `tests/CrossMacro.Infrastructure.Tests/Services/ManageTextExpansionTests.cs`

## 变更

`FileBackedJsonStorage` 增加精确字节快照与原子恢复 helper。`SettingsService` 在 `_saveGate` 内捕获两份旧文件；任一写入失败时按 Profile、Global 顺序恢复旧字节，恢复失败会抛出带原始保存异常和恢复异常的 `AggregateException`。同步和异步保存路径都覆盖，generation 不匹配时仍只写允许的文件。

`ProfileManager` 对创建、改名和删除保存注册表前保留深快照。创建失败恢复内存并清理新目录；改名失败恢复旧名称并尝试恢复注册表；删除先把目录移动到同一 Profiles 根下的唯一暂存名，注册表提交后再递归删除，任一步失败都恢复注册表并把暂存目录移回。恢复本身失败时保留原始异常和恢复异常。

`TextExpansionStorageService` 为路径/cache 引入 generation。Load/Save 都捕获固定 path + generation，reload 递增 generation；旧路径的异步完成结果不能覆盖新 Profile cache。Load/GetCurrent/Save 使用深拷贝，调用方变更 entry 不会绕过 SaveAsync 修改缓存。新增的 `IProfileTextExpansionOperationScope` 让活动存储的 scope 跨越 `ManageTextExpansion` 的完整 load/mutate/save；coordinator 切换持有同一 scope，非活动 Profile 仍走独立 `profileStore`。

`ProfileRuntimeCoordinator` 将停止结果细分到每个 runtime service。非 scheduler 停止异常会使切换 fail closed；切换中止时只重启明确成功停止且原本运行的服务，停止失败或 scheduler lifetime 未结束的服务不会被再次 Start，从而保持旧 Profile 状态并避免混合监听。

## 计划验证

主审统一串行授权后，建议使用既有资产执行以下筛选；本轮刻意不执行：

```text
dotnet test tests/CrossMacro.Infrastructure.Tests/CrossMacro.Infrastructure.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~SettingsServiceTests|FullyQualifiedName~ProfileManagerTests|FullyQualifiedName~TextExpansionStorageServiceTests" /m:1
```

应覆盖新增的：

- `SaveAsync_WhenProfileWriteFails_RestoresGlobalFile`
- `CreateProfileAsync_WhenRegistrySaveFails_RestoresMemoryAndRemovesDirectory`
- `RenameProfileAsync_WhenRegistrySaveFails_RestoresPreviousName`
- `DeleteProfileAsync_WhenRegistrySaveFails_RestoresRegistryAndDirectory`
- `GetCurrent_ReturnsDeepSnapshot_ThatCannotMutateCache`
- `SaveAndReload_KeepLatestProfileCacheWhenOperationsOverlap`
- `SwitchProfileAsync_WhenHotkeyStopFails_AbortsWithoutRestartingFailedService`
- `ActiveMutation_HoldsProfileScopeUntilSaveCompletesBeforeSwitchReload`
- `SwitchScopeFirst_MakesActiveMutationWaitForReloadBeforeLoading`

## 边界

恢复策略覆盖写入异常、注册表异常、目录暂存/移回异常和路径 generation 竞态；不能保证进程在两个独立文件 replace 之间突然掉电时无需启动恢复日志，也不能恢复已经被外部进程并发改写的文件。目录递归删除若已发生部分删除且暂存目录无法移回，会以聚合异常暴露残余状态。

本轮未测试：实际磁盘满、掉电、ACL 在写入中途变化、跨卷 Profile 根、外部进程同时改写配置、并发 A/B Profile 切换的压力测试，以及 ManageSchedule/ManageShortcut/ManageTrigger 的应用层 mutation rollback（按授权明确不修改这些文件）。RuntimeLifecycle 半启动项保留为契约审计项，未找到需在本轮修复的具体内置泄漏。
