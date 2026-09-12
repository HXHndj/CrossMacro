# Stage 4 编辑器性能/内存修复

日期：2026-09-12  
范围：编辑器动作投影、属性编辑撤销快照、大宏转换路径和图像预览取消。

## 实现

- `src/CrossMacro.UI/ViewModels/EditorViewModel.cs`
  - 建立 `EditorAction` 到 `EditorActionListItem` 的索引。
  - 对显示名可局部更新的属性只更新绑定行；结构、过滤和简化变化继续走全量投影，避免改变选择/过滤语义。
  - 保留动作批量更新期间的全量刷新边界。
  - 释放时取消当前图像预览请求。
- `src/CrossMacro.UI/ViewModels/EditorViewModel.Actions.cs`
  - 撤销历史仍保持 50 步和 400 ms 的同动作同属性合并规则。
  - 历史状态持有 detached `EditorAction` 快照；普通字段编辑复制引用数组并只 clone 已变更动作，结构操作仍使用完整 detached clone。
  - 局部状态更新仍分配一个 `N` 长度引用数组，但避免每字符重新创建 `N` 个动作对象；结构快照和 Undo/Redo 恢复仍允许完整 `N` 级路径。
  - 非选中动作的直接属性通知也会更新 immutable known state，保存时不会回退到旧字段。
  - `RestoreStateSnapshot` 对历史动作重新 clone，防止 Undo/Redo 后编辑污染历史。
- `src/CrossMacro.UI/ViewModels/EditorViewModel.CaptureAndFileOps.cs`
  - 编辑器加载的恢复转换移入 `Task.Run`，UI 线程仅提交恢复结果和批量填充。
  - 保存的 clone、验证、转换和图像字典准备移入后台，并传入 ViewModel 生命周期 token；UI 线程只捕获 immutable 引用快照并提交 SkipInitialZeroZero 状态。
  - 保存完成回 UI 时会校验 known-state/SkipInitialZeroZero 仍未变化，避免后台期间的新编辑被旧准备结果覆盖。
- `src/CrossMacro.UI/ViewModels/EditorViewModel.ScreenReadingPresentation.cs`
  - 每次预览建立 linked CTS，启动新预览时等待 `CancelAsync` 取消旧请求；保留版本号检查和 Bitmap Dispose，并捕获取消回调异常。
- `tests/CrossMacro.UI.Tests/ViewModels/EditorViewModelTests.PersistenceUndo.cs`
  - 新增 5,000 行单字段编辑检查：动作行集合不发整表 Reset，只产生绑定行的局部 Replace 更新。
  - 新增不同动作快速编辑的逐步 Undo/Redo 语义检查。
  - 新增 Undo→编辑→Undo/Redo 历史隔离检查。
  - 新增 5,000 动作快照共享检查：连续同字段编辑后 4,999 个未改动作仍与历史快照共享引用，改动作保持 detached。
  - 新增后台保存期间修改 SkipInitialZeroZero 的一致性检查，防止旧准备结果覆盖新值。
- `tests/CrossMacro.UI.Tests/ViewModels/EditorViewModelTests.CaptureScreenReading.cs`
  - 新增阻塞预览 decoder 检查：切换离开图像动作会取消过期解码。
- `tests/CrossMacro.UI.Tests/ViewModels/EditorViewModelPerformanceTests.cs`
  - 新增独立小样本：预热后对 `N=1000/5000、K=20` 记录 elapsed 和 `GC.GetAllocatedBytesForCurrentThread` 到 xUnit/TRX，并执行动作级分配预算断言。

## 静态验证与保留语义

- 过滤和简化开启时仍使用原全量投影；局部路径只在无隐藏/简化过滤时启用，因此不改变隐藏行、压缩 movement 行、缩进或脚本结构处理。
- 局部投影使用同一行索引的 `Replace` 通知，不触发整表 `Reset`；集合/结构变化仍保留原全量刷新。
- 选择同步仍通过既有 pending `Dispatcher.Post` 合并；局部行更新保持底层索引，避免扰动多选索引。
- Undo/Redo 恢复会 clone 历史动作；结构操作仍完整保存状态。50 步上限和 400 ms 同动作同属性 coalescing 常量未降低。
- 5,000 动作测试和新测试已静态加入，但按任务要求本轮没有 build/test。

## 未测与后续验证

统一执行者需运行相关 UI tests，重点确认筛选、简化、脚本结构、Undo/Redo、5,000 动作单字段无 Reset 和过期预览 cancellation。随后用受控 N/K 输入记录 UI dispatcher 时间、`GC.GetAllocatedBytesForCurrentThread`、managed/native image 内存和实际 realized 容器数。本轮没有实际 FPS、RSS、GC 暂停、UI 卡顿或跨平台 Avalonia 行为数据。
