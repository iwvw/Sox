# Handoff：全模块审查 + 批量修复（会话十四）

日期：2026-11-24
状态：P0–P4 全部落地；`dotnet build Sox.slnx -c Debug` 0 warning / 0 error；`dotnet test` 72 passed / 0 failed。待用户视觉验收。

## 本轮起因

16 个子代理并行审查各模块（上限 20），产出约 170 条候选问题。逐条人工核对后**剔除/修正了 6 条不成立的**（例如「别名 scratch 跨轮残留」「VerifyPath 匹配文件名」「ReFS ref 参数各自别名」），其余按严重度修复。

## 关键修正（与子代理原始结论不同）

| 子代理说法 | 实际 |
|---|---|
| 别名 `decodedLength` 跨轮残留 | 循环内声明，每轮重置；真实问题在 **ASCII 分支路径 decode 缺失**（`SearchMatcherAliasExtensions`），已修 |
| `PathGate.VerifyPath` 整串模糊匹配 | 逐段匹配，机制不成立；实际问题是 `pIdx--` 跳过未匹配段导致段错位，本次未改（低优先） |
| `ReFsScanner` 的 `ref files` 各 worker 各自别名 | 闭包捕获后提升为字段，计数安全，撤回 |

## 已修复（按优先级）

### P0
- `LiveIndex.Compact`：写盘失败不再丢弃 pending delta（新增 `DeltaOverlay(snapshot, carried)` 迁移构造）
- `SnapshotFormat.TryReadHeaderFromFile`：版本/魔数不匹配返回 null 且**不再删除**缓存文件
- `UserSettingsPersistence.Save`：写前比对磁盘现状，跨进程覆盖时告警；6 个设置页改为保存前取最新实例
- USN：只清待重建盘的索引；重建失败 `TryRestoreFromCache` 回填
- 单盘失败状态置 `failed`（新增 `IndexBuilder.onDriveFailed`）
- 网络盘：多数目录枚举失败时不再标记 `IsComplete`
- 图标缓存键改为完整路径；两个缓存改 LRU 而非 `Clear()`
- `ScientificMathParser` 补 `%` 除零
- 应用/窗口标题匹配改用 `FuzzyMatcher.IsMatchFreeText`（新增，不剥离盘符）

### P1
- 进程名解析失败结果负缓存（10s），避免钩子线程反复枚举进程
- `ReloadSettings` 改为 `ApplySettings` 原地更新，不再换 detector 实例
- `GetPreviousStore` 锁外取 store；`TreeBuilder` 计数与入队共用 `_enqueueGate`
- 内嵌面板 `ApplyResults` 加 CTS 代次校验
- `MainWindow` 抽 `SubscribeStatus()`，`Interlocked.Exchange` + Cancel/Dispose
- 托盘图标挂 `ProcessExit` 清理
- `UpdateRelaunchMarker.FreshFor` 5 分钟 → 1 小时
- 自更新脚本改 `-EncodedCommand`（Base64）
- 单实例互斥体加 SessionId
- `SearchHistoryStore`：`_pathIndex` O(1) 判存在、写盘 500ms 去抖、全局 500 条上限、`Task.Run` 异常兜底

### P2
- 拼音别名生成加 memo（`AliasGenerationUtf8.TryGetCached`，含 mask）
- `DirectoryFilterResolver.FindRootRow` 优先 `FirstRowForId(RootId)`
- `PathSearchFuzzy` 传 token + 并行度上限
- `SearchHost` 启动改指数退避（50ms→500ms）
- `ResizeToContent` 尺寸未变直接 return
- `PinyinAliasProvider.ConvertEnabled` 加 volatile 槽
- 自更新解压移出 UI 线程

### P3
- 别名 ASCII 慢路径强制 decode
- `SyncVisibleWindow` 改为先裁尾再按位对齐
- `SearchStreamPump.TryWrite` 统一捕获取消/关闭
- `ExplorerPathCollector` 释放 Document/Folder/Self
- `ShellPasteHelper` 检测 `GetAnyOperationsAborted` 记 Warn
- `PluginPreviewCache` 键分隔符 `:` → `|` + `%3A` 转义
- `Logger.RollOver` 改为重命名保留 `.1`，失败才回落截断
- Wire `SearchResultFrameCodec.ReadPayload` 截断/损坏返回 empty 而非抛

### P4
- 新增 `WireCodecTests`（varint/7-bit/frame 往返 + 截断）、`FuzzyMatcherDriveSpecTests`
- CI 改跑 `dotnet test Sox.slnx`
- `Directory.Build.props` 统一 `Nullable` / `ImplicitUsings`
- `.vscode/tasks.json` 加 `test` 任务、build 补 `-p:Platform=x64`

## 未做 / 后续

- `ShellPasteHelper` 取消后回滚（IFileOperation 无回滚能力，只能提示——已加 Warn 日志）

## 第二轮（本次续做）

上轮遗留项已完成：

| 项 | 做法 |
|---|---|
| `PathGate.VerifyPath` 段错位 | **经复核此条不成立**：原代码 `pIdx--` 本就无条件执行，每段最多消费一个 query 段，与 `Verify()` 一致。已撤回改动，仅保留注释 |
| 快照同长损坏 | 新增 `SnapshotCrc32`（CRC32C），header 加 `SectionsCrc32` 字段，版本 10 → **11**；`Snapshot.Open(path, verifyChecksum)` 可选校验；缓存打开失败时才跑 CRC 以区分「结构错」与「字节坏」 |
| `GetFullPath` 热路径 | `List`/`StringBuilder` 改为 `[ThreadStatic]` 复用 |
| 路径模式取消传播 | `MatchUniquesForPath(..., token)` + `ParallelOptions.CancellationToken` |
| 压缩内存峰值 | `Capacity` 按可见行估算（扣除删除/改名），返回前 `TrimExcess` |
| STA 图标线程 COM | 线程入口 `OleInitialize` / `OleUninitialize` |
| 图标队列无上限等待 | `Run` 加 10s 超时 |

新增测试：`SnapshotCrc32Tests`（含标准向量 `0xE3069283`、增量一致性、位翻转检测）。
测试总数 72 → **77**。第五轮修复后为 80。

**注意**：快照版本升到 11，旧的 `.idx` 会因版本不匹配被忽略并重建（**不会删除文件**，这是第一轮修的行为）。首次启动会全盘重扫一次。

## 第三轮（退出语义，ADR-0023）

用户拍板：托盘两个退出项合并为一个。原「退出」只关 UI、服务继续跑，用户理解成"没同步退出"。

- `TrayMenu.xaml`：删掉「退出并停止服务」项，只留「退出」
- `TrayIconService`：移除 `ExitAndStopServiceRequested` 事件
- `MainWindow`：`ExitApp` 改为「停服务（后台线程，`TryStop` 最长 5s）→ 退出」；删除 `ExitAppAndStopService`
- 保留：`ExitForUpdate` 不停服务（更新脚本自己 `sc stop`/`sc start`，见 ADR-0021）
- 停服务会连带 `HookProcessBroker.KillAll()`，不留 hook 孤儿

详见 `docs/adr/0023-exit-stops-service.md`。

## 第四轮(0.4.2 回归修复 → 0.4.3)

0.4.2 发布后本机安装验证时,服务日志报 `Stream does not support reading`,索引初始化失败。**这是第二轮 CRC 改动引入的回归,已随 0.4.2 发出**。

两个叠加错误:

1. `SnapshotWriter.Write` 的 FileStream 用 `FileAccess.Write` 打开,而 CRC 需要回读同一流 → 抛异常。改为 `FileAccess.ReadWrite` + 回读前 `Flush()`。
2. CRC 字段偏移手算错误(9 个 int 数成 10 个),把校验值写到 RootId 上、读回为 0,校验形同虚设。改为由 `WriteHeader` 直接返回写入位置,不再重复布局算术。

根因:只有布局算术单测,没有真正写过快照文件。补齐 `SnapshotRoundTripTests`(真实写入 → 带校验读回、5000 条多缓冲区、翻转字节必须被拒绝)。测试 77 → **80**。

另注:第四轮表里 `PathGate.VerifyPath` 那条经复核不成立,已撤回。

## 相关文件

`src/Sox.Core/IndexV2/LiveIndex.cs`、`Delta/DeltaOverlay.cs`、`Persistence/SnapshotFormat.cs`、`Alias/AliasGenerationUtf8.cs`、`SearchIndex/FuzzyMatcher.cs`、`Wire/SearchResultFrameCodec.cs`、`SearchHistoryStore.cs`、`Logger.cs`、`Hook/**`、`Indexer/**`；`src/Sox.App/Settings/*`、`MainWindow.xaml.cs`、`Services/*`；`src/Plugins/*`；`tests/Sox.Core.Tests/*`
