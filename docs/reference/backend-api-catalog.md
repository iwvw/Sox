# 后端 API 目录（前端对接用）

调研对象：`E:\Code\sox` 后端（`Sox.Core` / `Sox.Service` / `Sox.PluginSdk` / `Plugins`）
日期：2026-09-30
用途：前端（Sox.App，正按 ADR-0016 重写为 WinUI 3）对接后端时，作为 API 参考。

说明：本目录只列**前端会用到**的公开 API。标注「服务端专用」的类型前端不直接调用。所有签名以当前源码为准，重写前端时若发现不符，以代码为准并回来更新本文。

核实状态：已逐项对照源码核实（2026-09-30）。核实中修正了 4 处错误：`IpcMessageId.ClearHookLog=46` 不存在、`LogLevel` 取值应为 `Error/Warn/Info/Debug`、`SearchDir` 请求体字段顺序（`DirectoryFilter` 在 `Query` 前）、`PipeResponse` 实为 public；并补充了 `PluginConfigField.Value`、`SearchResult.RankSortKey`（internal）、`IpcMessage` 字段表、`Logger`/`LogLevel`、`QuickPanel` 静态辅助方法、`EverythingIpcHost` 等遗漏项。

---

## 0. 架构与进程边界

```
┌───────────────────────────────────────────────────────────────┐
│ Sox.App（前端，将重写为 WinUI 3）                              │
│   ├─ 本地盘搜索 ──┐                                            │
│   ├─ 网络盘搜索 ──┼─ SearchService（Sox.Core，进程内引用）      │
│   ├─ hook 事件 ── HookIpcClient                                │
│   ├─ Everything IPC 宿主（EverythingIpcHost）                  │
│   └─ 单实例转发（SoxLaunch_* 管道）                            │
├───────────────────────────────────────────────────────────────┤
│ Sox.Service（Windows 服务，LocalSystem）                       │
│   ├─ SoxPipe 服务端（Wire 二进制协议，版本化）                  │
│   ├─ USN Journal 增量 + MFT 全扫 + 内存索引                     │
│   └─ 拉起 hook 进程                                            │
├───────────────────────────────────────────────────────────────┤
│ hook 进程（Sox.Service --hook，用户会话）                       │
│   ├─ 全局键鼠钩子                                              │
│   ├─ ExplorerTracker + 文件对话框探测                          │
│   └─ Sox_Hook_Events_* / Sox_Hook_Cmds_* 管道                  │
└───────────────────────────────────────────────────────────────┘
```

| 进程 | 启动 | 角色 |
|---|---|---|
| `Sox.Service.exe --service` | SCM 服务 `SoxService`，LocalSystem | 索引引擎 + `SoxPipe` 服务端 + hook 启动代理 + 更新落盘代理 |
| `Sox.Service.exe --hook` | 服务经 `CreateProcessAsUser` 拉进用户会话 | 全局键鼠钩子 + ExplorerTracker + 对话框探测 |
| `Sox.App.exe` | 用户直接运行（单实例 Mutex `Sox.SingleInstance`） | UI + `SoxPipe` 客户端 + hook 客户端 + Everything IPC 宿主 |
| Everything（第三方） | 用户机器已装 | 连入 App 进程内模拟的 Everything IPC 窗口 |

**前端无需管理员权限**：搜索、状态、设置读取、元数据、最近文件都经 `SoxPipe` 与 LocalSystem 服务通信。唯一提权环节是首次安装/启动服务（`runas` UAC）。

---

## 1. 搜索编排层（前端主入口）

### 1.1 SearchService

位置：`src/Sox.Core/Services/Search/SearchService.cs`

前端唯一推荐实例化的搜索门面。现有实现由 `SearchHost` 持有一个单例（`private readonly SearchService _service = new()`），组合三路源：命名管道（本地盘）、进程内网络盘搜索、实时目录扫描。

```csharp
public class SearchService : IDisposable
```

| 方法 | 语义 |
|---|---|
| `Task<bool> PingAsync(CancellationToken token = default)` | 探活。向 `SoxPipe` 发 Ping，`true` = 服务在线。传输异常被吞成 `false`，不抛 |
| `Task<UsnIndexer.IndexerStatus> GetStatusAsync(CancellationToken token = default)` | 拉一次索引状态快照。失败返回 `State = "error"`，不抛 |
| `Task<bool> SearchStreamingAsync(string query, int maxResults, int maxAppResults, string? directoryFilter, Action<SearchResult> onResult, CancellationToken token = default, Action? onLocalSearchFailed = null, bool bypassExclusions = false, string? fileNameFilter = null)` | 流式搜索，前端核心方法 |
| `Task<List<SearchResult>> GetRecentFilesAsync(IReadOnlyList<string> directories, int limit, int maxAgeMinutes, CancellationToken token = default)` | 最近文件（只返回文件，不含目录） |
| `Task<(bool Ok, int Pid, string? Error)> RequestHookLaunchAsync(bool requestElevation, CancellationToken token = default)` | 请服务拉起 hook 进程 |
| `Task<(bool Ok, string? Error)> RequestApplyUpdateAsync(string sourceDir, CancellationToken token = default)` | 应用更新（`Ok` 只表示更新器已启动） |
| `Task ClearPathCachesAsync(CancellationToken token = default)` | 窗口关闭/隐藏时调用，交回逐行全路径缓存 |
| `void Dispose()` | 取消内部实时扫描缓存 |

**SearchStreamingAsync 参数细节**：

- `query`：原样匹配/高亮的查询文本。调用方**必须先**用 `SearchQuerySortParser.StripExclusionBypass` 剥掉 `*` 标记，不得带标记进来。
- `maxResults`：结果上限。内部换算候选上限（预留排除过滤余量）。`int.MaxValue` 合法（全窗口用）。
- `maxAppResults`：写入 `AppLimit` 字段，当前下游未消费，属预留位，传 `0`。
- `directoryFilter`：非空走 `SearchDir`（限定目录）；空走 `Search`（全局）。
- `onResult`：**可能多线程并发触发**（三路源并行，去重锁外回调）。前端必须自行加锁或改派 UI 线程。结果已做隐藏/系统过滤与按 `Path` 去重。
- `onLocalSearchFailed`：本地管道源失败时回调。
- `bypassExclusions`：绕过用户自定义排除规则（**不绕过**隐藏/系统过滤）。路径模式查询会自动置为有效。
- `fileNameFilter`：可选文件名通配过滤。
- 返回：`Task<bool>`，任一路源成功即 `true`。全程等待所有源完成才返回（非分页）。

调用示例（现有 `SearchHost`）：

```csharp
var ok = await _service.SearchStreamingAsync(query, maxResults, 0, directoryFilter,
    r => { onResult(r); }, ct).ConfigureAwait(false);
```

### 1.2 SearchServiceManagementExtensions

位置：`src/Sox.Core/Services/Search/SearchServiceManagementExtensions.cs`（全部为 `this SearchService` 扩展）

| 签名 | 说明 |
|---|---|
| `void RefreshNetworkIndexes()` | 刷新全部网络盘索引并失效覆盖缓存 |
| `void ConfigureNetworkIndexes()` | 按设置重新配置网络索引 |
| `bool RefreshNetworkDriveIndex(string drive)` | 刷新单个网络盘 |
| `bool CancelNetworkDriveIndex(string drive)` | 取消单个网络盘索引 |
| `IReadOnlyList<NetworkIndexStatus> GetNetworkIndexStatuses()` | 网络索引状态列表 |
| `bool HasNetworkDriveCache(string drive)` | 是否有缓存 |
| `IReadOnlyList<string> GetCachedNetworkDrives()` | 已缓存盘列表 |
| `void DeleteNetworkDriveCache(string drive)` | 删除缓存 |
| `Task InitializeOrLoadIndexAsync(bool forceRebuild = false, CancellationToken token = default)` | 初始化或加载索引 |
| `Task<bool> ClearServiceLogAsync(CancellationToken token = default)` | 让服务截断自身 service.log |
| `Task<bool> RebuildDriveIndexAsync(string drive, CancellationToken token = default)` | 重建单盘（**需授权**） |
| `Task<bool> DeleteDriveIndexAsync(string drive, CancellationToken token = default)` | 删除单盘索引（**需授权**） |
| `Task<bool> CancelDriveIndexAsync(string drive, CancellationToken token = default)` | 取消单盘重建 |
| `Task<MachineSettings> GetMachineSettingsAsync(CancellationToken token = default)` | 读机器设置，失败返回默认实例 |
| `Task<bool> SaveMachineSettingsAsync(MachineSettings settings, CancellationToken token = default)` | 保存机器设置（**需授权**） |
| `Task<Dictionary<string, FileMetadataEntry>> GetFileMetadataBatchAsync(IReadOnlyList<string> paths, CancellationToken token = default)` | 索引内批量元数据（无磁盘 IO）。未跟踪路径直接缺席，调用方自行回退 stat |

**授权说明**：`RebuildDriveIndexAsync` / `DeleteDriveIndexAsync` / `SaveMachineSettingsAsync` 在服务端校验调用方是真实 `Sox.App.exe` 进程（同目录），否则返回 `Unauthorized caller.`。

### 1.3 SearchServiceSpaceExtensions

位置：`src/Sox.Core/Services/Search/SearchServiceSpaceExtensions.cs`

```csharp
Task<IReadOnlyList<SpaceIndexEntry>> GetSpaceEntriesAsync(this SearchService service, string? directory, CancellationToken token = default)
```

`directory` 为空 = 各盘根；否则目录路径。返回已按 `Path` 去重、`Size` 降序排序。用途：目录子树大小、目录列举。

### 1.4 流式订阅（各自独占管道连接）

位置：`SearchStatusStream.cs` / `DirectoryChangeStream.cs`

```csharp
// 索引状态推送（先推一次当前状态，之后每次变化推送）
Task SearchStatusStream.SubscribeAsync(Action<UsnIndexer.IndexerStatus> onStatus, CancellationToken token)

// 目录变化推送（订阅列表一次性发送；变更列表需重新订阅）
Task DirectoryChangeStream.SubscribeAsync(IReadOnlyList<string> watched, Action<IReadOnlyList<string>> onChanged, CancellationToken token)
```

两者都会阻塞在该 Task 上直到 token 取消或管道断开，调用方需放到后台任务。**不能与搜索共用连接**。

### 1.5 其它编排入口

| 类型 | 位置 | 用途 |
|---|---|---|
| `IndexedDirectoryEnumerator` | `Services/Search/` | `Task EnumerateAsync(string directoryPath, bool recursive, string filterPattern, Action<SearchResult> onResult, int limit = 0, CancellationToken token = default)`：纯索引目录枚举，带就绪等待，不落盘扫描 |
| `SearchScopeCoverage` | `Services/Search/` | `bool IsIndexed(string directoryPath)` / `void Invalidate()`：目录是否被索引覆盖，派发前剔除无效 scope。60s TTL 缓存 |
| `LiveDirectorySearcher` | `Services/Search/` | `static (string DirectoryToScan, string FilterQuery) ResolvePathModeSearch(string exactPathLower)`：路径模式拆解，前端可复用 |

### 1.6 模糊匹配与高亮（前端打分/高亮用）

位置：`src/Sox.Core/SearchIndex/FuzzyQuery.cs` / `FuzzyMatcher.cs` / `MatchRank.cs`

```csharp
// 每次击键新建一个，勿进程级缓存（避免拿到陈旧解析）
readonly struct FuzzyQuery
{
    static FuzzyQuery Parse(string? query);
    bool IsEmpty;
    string Text;
    bool IsMatch(string? text);
    MatchRank Rank(string? text);
    MatchRank BestMatch(string? primaryText, IEnumerable<string>? alternateTexts = null);
    bool[] HighlightMask(string? text);
}

static class FuzzyMatcher
{
    bool IsMatch(string pattern, string text);
    bool[] ComputeHighlightMask(string text, string query);
    double ComputeMatchWeight(string text, string query);
    MatchRank ComputeMatchRank(string text, string query);
    MatchRank ComputeBestMatch(string query, string primaryText, IEnumerable<string>? alternateTexts = null);
}

readonly record struct MatchRank  // int Tier; int Start; double Weight
// 常量 TierName=0 / TierInitials=1 / TierFull=2；NoMatch（Start=int.MaxValue）
```

已有用例：`Sox.App/Controls/HighlightTextBlock.cs`、`Sox.App/ViewModels/SearchViewModel.cs`。

### 1.7 查询解析

位置：`src/Sox.Core/SearchIndex/Query/`

```csharp
static ParsedSearchQuery SearchQueryParser.Parse(string query);
static string SearchQueryParser.NormalizeExactPath(string pathLower);

// 剥离尾部 :a,b,c 或 ::"hello world" 语法
static string SearchQuerySortParser.Strip(string query, out IReadOnlyList<string> tokens, char prefixChar = ':');
// 剥离首字符 *，必须在查询用于搜索与高亮之前调用
static string SearchQuerySortParser.StripExclusionBypass(string query, out bool bypassExclusions);
```

`ParsedSearchQuery`：`bool IsPathMode`、`string? TargetDrive`、`string? PathPatternLower`、`string? ExactPathLower`、`bool PathEndsWithSeparator`。

### 1.8 进程级搜索偏好

位置：`src/Sox.Core/SearchContext.cs`

```csharp
static class SearchContext
{
    HashSet<byte>? DisabledAliasIds;         // AsyncLocal
    bool FuzzyMatchEnabled;                  // AsyncLocal，回退 DefaultFuzzyMatchEnabled
    bool DefaultFuzzyMatchEnabled;           // 进程级 volatile，默认 true
    bool AndFirstPrecedence;                 // AsyncLocal，回退 DefaultAndFirstPrecedence
    bool DefaultAndFirstPrecedence;          // 进程级 volatile，默认 true
}
```

前端启动/保存设置时推 `DefaultFuzzyMatchEnabled` / `DefaultAndFirstPrecedence`（供插件目录、收藏、高亮等非请求路径使用）。跨进程的本地盘路径靠消息字段传递。

---

## 2. 数据模型

### 2.1 SearchResult

位置：`src/Sox.Core/SearchResult.cs`（实现 `ISearchResult`）

| 字段 | 类型 | 说明 |
|---|---|---|
| `Name` | `string` | 文件/文件夹名 |
| `Path` | `string` | 完整路径 |
| `FullPath` | `string`（只读） | `Path` 别名（插件契约） |
| `ContextDirectory` | `string`（只读） | 所在目录；目录项为其自身 |
| `IsDir` | `bool` | 是否目录 |
| `Drive` | `string` | 盘/源键 |
| `IsApplication` | `bool`（只读，恒 false） | 契约占位 |
| `Attributes` | `FileAttributes` | 供隐藏/系统过滤 |
| `Metadata` | `FileMetadata` | Size/Created/Modified/Accessed（本地时间），非索引结果可能 default |
| `RankSortKey` | `ulong`（**internal**） | 排序键（高位 start，低位 span/length）。前端不可访问，排序请用 `SearchResultRankComparer` |

`SearchResultRankComparer`：`static readonly Instance`、构造 `(behaviorScores)` / `(behaviorScores, penalties)`、`string? ContextDirectory { get; init; }`、`int Compare(...)`。排序键：`RankSortKey` 高位 start -> 行为分降序 -> `RankSortKey` -> 路径长度 -> Drive -> Path。

### 2.2 IndexerStatus（索引状态）

位置：`src/Sox.Core/Indexer/Usn/UsnIndexerStatus.cs`

```
State: string        // idle / pending / indexing / loading-cache / ready / cached / error
Progress: int        // 0-100
TotalFiles / TotalDirs: int
ElapsedTime: double  // 秒
IsMaintenanceBusy: bool
ActiveDrives: List<string>
Drives: List<DriveIndexStatus>
```

`DriveIndexStatus`：`Drive`、`Enabled`、`Kind`（如 `LocalNtfs`）、`State`（`pending/indexing/ready/cached/disabled/failed/unavailable/unknown`）、`Files`、`Dirs`、`CachePath`。

### 2.3 其它 DTO

| 类型 | 字段 | 位置 |
|---|---|---|
| `FileMetadata` | `long Size, DateTime Created, DateTime Modified, DateTime Accessed`（本地时间；`default` 表示未知） | `Sox.PluginSdk/Abstractions/FileMetadata.cs` |
| `FileMetadataEntry` | `long Size, uint CreationTimeUnixSeconds, uint LastWriteTimeUnixSeconds, uint LastAccessTimeUnixSeconds`（UTC 整秒） | `Sox.Core/FileMetadataEntry.cs` |
| `SpaceIndexEntry` | `string Path, string Name, long Size, bool IsDirectory, bool IsHardLinkDuplicate` | `Sox.Core/IndexV2/Space/SpaceIndexEntry.cs` |
| `NetworkIndexStatus` | `Drive, State, Items, Skipped, Errors, EnumerateErrors, AttributeErrors, ReparseSkipped, SlowDirectories, CachePath, DateTime? LastUpdated, Error` | `Sox.Core/Indexer/NetworkDrive/NetworkIndexStatus.cs` |
| `MachineSettings` | `List<string> LocalDrives, bool LocalDriveSelectionConfigured, string ServiceLogLevel` + `IsLocalDriveEnabled(volumeId)` / `ResolveServiceLogLevel()` / `Load()` / `Save()` | `Sox.Core/Settings/MachineSettings.cs` |
| `MatchRank` | `int Tier, int Start, double Weight` | `Sox.Core/SearchIndex/MatchRank.cs` |
| `PipeResponse` | `PipeResponseKind Kind`、`string Message`、`bool IsTransportError`、`IndexerStatus? Status`、`List<string>? ChangedDirectories`、`MachineSettings? MachineSettings`、`Dictionary<string,FileMetadataEntry>? FileMetadata`、`List<SearchResult>? RecentFiles`、`IReadOnlyList<SpaceIndexEntry>? SpaceEntries`、`int Pid`、`bool IsOk`（**public**，但通常由 `SearchService` 内部封装） | `Sox.Core/Wire/PipeResponseBinarySerializer.cs` |

---

## 3. 设置 / 历史 / 收藏 / 最近文件

### 3.1 UserSettings

位置：`src/Sox.Core/Settings/UserSettings.cs`（`public class`，JSON 序列化名与属性名一致）

| 方法 | 语义 |
|---|---|
| `static string SettingsPath { get; }` | 设置文件绝对路径 |
| `static UserSettings Load()` | 读缓存或磁盘；主文件失败回退 `.bak.N`；均失败返回默认 |
| `static UserSettings ForceReload()` | 绕过缓存重新加载 |
| `bool Save()` | 原子写入；内容无变化直接 `true`；成功后失效排除规则缓存 |
| `static void RestoreFrom(string sourcePath)` | 外部 JSON 覆盖当前设置并写盘 |
| `T GetPluginSetting<T>(string pluginId, string key, T defaultValue)` | 插件设置读取 |
| `void SetPluginSetting(string pluginId, string key, object? value)` | 插件设置写入（null 删除键） |

**顶层字段**（前端设置面板读写）：

| 字段 | 类型 | 默认值 | 含义 |
|---|---|---|---|
| `NetworkDrives` | `List<NetworkDriveSetting>` | 空 | 已配置网络盘 |
| `WslSettings` | `List<WslSetting>` | 空 | 已配置 WSL 发行版 |
| `FolderIndexes` | `List<FolderIndexSetting>` | 空 | 已配置文件夹索引 |
| `DefaultFileManager` | `DefaultFileManagerSetting` | 见下 | 第三方文件管理器重定向 |
| `Favorites` | `List<FavoriteItemSetting>` | 空 | 收藏项 |
| `ExcludedPaths` | `List<string>` | `Windows.old` / `ProgramData` / `SystemRoot` / `ProgramW6432` / `AppData` / `ProgramFiles(x86)` | 排除根路径（支持环境变量） |
| `IgnoredPathGlobs` | `List<string>` | `.*`、`~*`、`\$*`、`node_modules` | 忽略 glob |
| `IgnoredPathRegexes` | `List<string>` | 空 | 忽略正则 |
| `BlacklistedProcesses` | `List<string>` | 空 | 全局进程黑名单（不弹窗） |
| `EnableHistory` | `bool` | `true` | 启用搜索历史 |
| `EnableKeywordHistory` | `bool` | `true` | 启用关键词历史 |
| `StartWithWindows` | `bool` | `true` | 开机启动 |
| `AutoCheckUpdates` | `bool` | `true` | 自动检查更新 |
| `AutoSilentUpdate` | `bool` | `false` | 静默自动更新 |
| `EnableHardwareAcceleration` | `bool` | `true` | 快速窗硬件加速（重启生效） |
| `EnableFuzzyMatch` | `bool` | `true` | 模糊匹配（关闭即子串精确） |
| `OrFirstPrecedence` | `bool` | `false` | `\|` 优先于空格 |
| `HideTrayIcon` | `bool` | `false` | 隐藏托盘图标 |
| `EnableEverythingIpc` | `bool` | `false` | 启用 Everything IPC |
| `ShowOpenedFoldersInInlineSearch` | `bool` | `true` | 内联搜索显示已打开文件夹 |
| `GlobalTokenPrefix` | `string` | `":"` | 全局令牌前缀 |
| `LogLevel` | `string` | `"Info"` | 用户进程日志级别 |
| `PreferredLanguage` | `string` | 系统 UI 文化 | 界面语言 |
| `Theme` | `string` | `"Light"` | 主题（Light/Dark） |
| `ThemeFollowSystem` | `bool` | `false` | 跟随系统主题 |
| `LightThemeId` / `DarkThemeId` | `string` | `""` | 主题插件 id |
| `Hotkeys` | `HotkeyPageSettings` | new | 热键页全部设置 |
| `SearchWindow` | `SearchWindowSettings` | new | 快速窗布局 |
| `EnableQuickSearchClipboardAutoFill` | `bool` | `false` | 剪贴板自动填充 |
| `PreviewWindow` | `PreviewWindowSettings` | new | 预览窗尺寸 |
| `MainWindow` | `MainWindowSettings` | new | 主窗尺寸/单实例 |
| `QuickPanel` | `QuickPanelSettings` | new | 快速面板 |
| `QuickLaunch` | `QuickLaunchSettings` | new | 快速启动源 |
| `LocalSend` | `LocalSendSettingsModel` | new | LocalSend 传输 |
| `DisabledPluginComponents` | `List<string>` | 空 | 禁用组件 id，格式 `{Dll名}::{类型}::{组件名}` |
| `QuickNavigationProviderOrder` | `List<string>` | 空 | 快速导航提供者排序 |
| `SidebarGroupOrder` | `List<string>` | 空 | 侧栏过滤组排序 |
| `ColumnOrder` | `List<string>` | 空 | 结果列顺序 |
| `ResultTypeOrder` | `List<string>` | 空 | 结果类型优先级 |
| `ActionMenuGroupOrder` | `List<string>` | 空 | 动作菜单分组排序 |
| `FilePreviewProviderOrder` / `ThumbnailProviderOrder` | `List<string>` | 空 | 预览/缩略图提供者优先级 |
| `ResultTypeTriggers` | `Dictionary<string,string>` | 空 | 结果类型触发字符 |
| `PluginSettings` | `Dictionary<string, Dictionary<string, object>>` | 空（OrdinalIgnoreCase） | 插件设置，外层键为插件 id |

**子模型默认值**：

- `HotkeyPageSettings`：`ToggleWindowHotkey`=`"Ctrl"`、`QuickSwitchHotkey`=`"Ctrl+G"`、`NextItemHotkey`=`"Ctrl+N"`、`PreviousItemHotkey`=`"Ctrl+P"`、`ActionsMenuHotkey`=`"Ctrl+O"`、`QuickLookHotkey`=`"Alt+P"`、`QuickPanelHotkey`=`"Ctrl+F2"`、`OpenFullWindowHotkey`=`"Ctrl+F"`、`StayOpenHotkey`=`"Ctrl+T"`、`LocalSendSendWindowHotkey`=`"Ctrl+S"`、`SelectJumpModifier`=`"Ctrl"`、`CompleteFromSelectionHotkey`=`"Ctrl+Tab"`、`KeywordHistoryPreviousHotkey`=`"Alt+Up"`、`KeywordHistoryNextHotkey`=`"Alt+Down"`、`KeywordHistoryDeleteHotkey`=`"Ctrl+Delete"`、`AllowHotkeysInFullscreen`=false、`QuickNavTriggerOnMiddleClick`=true、`PluginActionHotkeys`=空字典
- `SearchWindowSettings`：`SearchBarWidth`=570、`SearchBarHeight`=60、`RelativeLeft`/`RelativeTop`=null、`ShowClock`=false、`LockPosition`=false
- `PreviewWindowSettings`：`Width`=400、`Height`=529
- `MainWindowSettings`：`Width`=854、`Height`=480、`SingleInstance`=false、`CloseOnRepeatHotkey`=false
- `FavoriteItemSetting`：`Name`/`Path`/`Hotkey`（扁平热键串，空=未分配）
- `DefaultFileManagerSetting`：`Enabled`=false、`Path`=`""`、`Parameter`=`""`（`%s`/`{}` 展开为加引号的文件夹路径）
- `NetworkDriveSetting` / `WslSetting`：`Id`=`""`、`RefreshMode`=`"Manual"`
- `FolderIndexSetting`：`Path`（路径即身份）、`RefreshMode`=`"Manual"`

### 3.2 QuickPanelSettings

| 字段 | 类型 | 默认值 |
|---|---|---|
| `Enabled` | `bool` | `true` |
| `Tabs` | `List<QuickPanelTab>` | 含一个默认标签 |
| `ActiveTabId` | `string` | `""` |
| `ClosedPluginTabIds` / `ListViewPluginTabIds` / `TabOrder` | `List<string>` | 空 |
| `BlacklistedProcesses` | `List<string>` | 空（叠加全局黑名单） |

`QuickPanelTab`：`Id`、`Name`、`Enabled`（默认 true）、`Processes`、`Folders`、`GroupOrder`、`DisabledGroupIds`、`GroupPreferences`（`Dictionary<string, QuickPanelGroupPreference>`，OrdinalIgnoreCase）。
`QuickPanelFolderSource`：`Id`、`Path`、`Kind`（默认 `RecentFiles`）、`SortByModified`、`Recursive`、`FilterPattern`、`MaxItems`=20、`MaxAgeMinutes`=0、`AcceptsDrops`。
`QuickPanelGroupPreference`：`DisplayName`、`Sort`（默认 `ModifiedDescending`）、`ThumbnailView`、`Expanded`。

**静态辅助方法**：

```csharp
static QuickPanelTab QuickPanelTab.CreateDefault();          // 默认含 Desktop/Downloads/Personal 三个 RecentFiles 源
static string QuickPanelTab.NewId();                         // 8 位稳定短 id
QuickPanelTab QuickPanelTab.Clone();                         // 深拷贝（设置页编辑用，避免直接改进程内对象）
static QuickPanelFolderSource QuickPanelFolderSource.For(string path, QuickPanelSourceKind kind = RecentFiles);
static string QuickPanelFolderSource.DefaultName(string path);
static QuickPanelSortMode QuickPanelGroupPreference.DefaultSortFor(QuickPanelSourceKind kind);
static QuickPanelSortMode QuickPanelGroupPreference.DefaultSortFor(QuickPanelFolderSource source);
```

`QuickPanelSortMode`：`ModifiedDescending` / `NameAscending`。`QuickPanelSourceKind`：`RecentFiles` / `AllByModified` 等（含内置源与文件夹源）。

纯函数（`QuickPanelGroupOrdering` / `QuickPanelTabSelection`）：

```csharp
List<string> QuickPanelGroupOrdering.Resolve(IEnumerable<string> available, IEnumerable<string>? order, IEnumerable<string>? disabled);
string? QuickPanelTabSelection.SelectTabId(string? processName, IEnumerable<QuickPanelTab>? tabs);
bool QuickPanelTabSelection.IsBlocked(string? processName, UserSettings? settings);
```

### 3.3 热键字符串工具

位置：`src/Sox.Core/Settings/HotkeyStringFormat.cs`

```csharp
bool IsReservedWindowsShortcut(string? value);
bool IsBareModifier(string? value, out string modifier);   // 双击模式判定，输出 Control/Alt/Shift/Win
void ParseCombo(string? value, out string modifier, out string key);
string ToDisplayText(string value);                        // Oem 键转显示符号
```

### 3.4 SearchHistoryStore（搜索历史）

位置：`src/Sox.Core/SearchHistoryStore.cs`（`public static`）

| 成员 | 语义 |
|---|---|
| `static string HistoryPath { get; }` | `search-history.json` 路径 |
| `static event Action? Changed` | 存储变更事件 |
| `static void Record(string keyword, string path, HistoryEntryKind kind)` | 记录打开项（后台线程，受 `EnableHistory` 约束） |
| `static double GetPriority(string path)` | 单路径行为分，未知返回 0 |
| `static IReadOnlyList<HistoryEntry> GetEntries()` | 全量条目，最近优先 |
| `static void SaveEntries(IEnumerable<HistoryEntry> entries)` | 整体替换（设置页编辑/删除） |
| `static IReadOnlyDictionary<string,double> Snapshot()` | 路径 -> 行为分快照 |
| `static IReadOnlyDictionary<string,int> PenaltySnapshot()` | 路径 -> 被跳过次数 |
| `static void RecordPassover(IEnumerable<string> paths)` | 记录被跳过项（后台线程） |
| `static void ClearPenalty(string path)` | 清除跳过惩罚 |

**注意**：`Record` / `RecordPassover` 在后台线程执行（`File.Exists` 在慢速网络共享上可能阻塞数秒），**绝不能在 UI 线程同步调用**。

### 3.5 KeywordHistoryStore（关键词历史）

位置：`src/Sox.Core/KeywordHistoryStore.cs`

```csharp
static string HistoryPath { get; }
static event Action? Changed;
static void Record(string? keyword);                       // 去重前移并累加计数
static void Delete(string keyword);
static IReadOnlyList<string> GetEntries();
static IReadOnlyList<KeywordHistoryEntry> GetCountedEntries();
static void SaveEntries(IEnumerable<KeywordHistoryEntry> entries);   // 上限 2000
```

`KeywordHistoryEntry`：`string Keyword, int Count`。

`HistoryEntry`（`Sox.PluginSdk.Services`）：`string Keyword, string Path, HistoryEntryKind Kind, long Time（Unix 秒）, int Count`。`HistoryEntryKind`：`File / Folder / Application`。

### 3.6 排除规则与过滤

位置：`src/Sox.Core/ExclusionRuleSet.cs` / `FileSystemItemFilter.cs` / `ProcessNameFilter.cs` / `NaturalNameComparer.cs`

```csharp
sealed class ExclusionRuleSet
{
    static ExclusionRuleSet Empty { get; }
    static void InvalidateCache();
    static ExclusionRuleSet From(UserSettings settings);
    static ExclusionRuleSet From(UserSettings settings, string root);
    bool IsExcluded(SearchResult result, string? exemptRoot = null);
    bool IsExcludedPath(string path, bool isDirectory, string? exemptRoot = null);
}

static class FileSystemItemFilter
{
    bool IsHiddenOrSystem(FileAttributes) / (SearchResult) / (string path);   // 不触发磁盘 IO
}

static class ProcessNameFilter
{
    bool Matches(string? processName, IEnumerable<string>? names);   // 忽略 .exe 后缀与大小写
}

sealed class NaturalNameComparer : IComparer<string>   // Explorer 风格自然排序（StrCmpLogicalW）
{
    static NaturalNameComparer Instance { get; }
}
```

### 3.7 别名注册

位置：`src/Sox.Core/SearchIndex/AliasProviderRegistry.cs`（`public static`）

```csharp
Func<IAliasProvider,bool> FilterFunc { get; set; }    // 启用过滤唯一入口
void Register(IAliasProvider provider);
char GetSyllableSeparator(byte providerId);
byte GetProviderId(IAliasProvider provider);
byte GetProviderIdByComponentId(string componentId);   // 未找到返回 255
IEnumerable<IAliasProvider> GetActiveProviders();
IEnumerable<IAliasProvider> GetAllProviders();         // 含禁用，供设置页
string ComputeProvidersFingerprint();                  // SHA256 hex
bool HasNonAscii(string text);
bool HasInvalidUtf16(string text);
```

### 3.8 持久化位置与格式

目录由 `DataDirectoryResolver` 决定。安装版：用户 `%LocalAppData%\Sox`，机器 `%ProgramData%\Sox`；便携版：`Data\Users\<SID hash>` 与 `Data\Machine`。

| 数据 | 路径 | 格式 |
|---|---|---|
| 用户设置 | `UserDataDir\user-settings.json` | JSON |
| 用户设置备份 | `user-settings.json.bak.1` ~ `.bak.5` | JSON |
| 机器设置 | `SharedDataDir\machine-settings.json` | JSON（前端应经 pipe 读写） |
| 搜索历史 | `UserDataDir\search-history.json` | JSON |
| 跳过惩罚 | `UserDataDir\search-history-penalties.json` | JSON |
| 关键词历史 | `UserDataDir\keyword-history.txt` | 每行 `关键词<TAB>次数` |
| 本地盘索引缓存 | `SharedDataDir\indexes` | 二进制 |
| 网络盘索引缓存 | `UserDataDir\indexes` | 二进制 |
| 旧 UI 设置（App 侧，非 Core） | `%AppData%\Sox\ui-settings.json` | JSON（重写时需迁移或废弃） |

所有写入经 `AtomicFileStore.Write`：写临时文件 + `File.Replace` 原子替换，IOException 重试 5 次。

### 3.9 日志与其它实用工具

**Logger**（`src/Sox.Core/Logger.cs`，`public static`）：

```csharp
static readonly string SharedDataDir;        // 机器级数据目录
static readonly string UserDataDir;          // 用户级数据目录
static string LogDir { get; }
static LogLevel MinimumLevel { get; set; }
static void Initialize(string logFileName, string? baseDirectory = null, bool overwrite = true);
static void Log(string message, LogLevel level = LogLevel.Info);
static bool IsEnabled(LogLevel level);
static IReadOnlyList<string> ReadLogLines(string path);
static bool ClearCurrentLog();
```

`LogLevel`（enum）：`Sox.Core.LogLevel`，取值 `Error = 0 / Warn = 1 / Info = 2 / Debug = 3`（`UserSettings.LogLevel` 与 `MachineSettings.ServiceLogLevel` 存字符串，未知一律回退 `Info`）。

**其它可能用到的工具类**：

| 类型 | 位置 | 用途 |
|---|---|---|
| `NaturalNameComparer` | `Sox.Core/` 根 | Explorer 风格自然排序（`StrCmpLogicalW`） |
| `WslPath` | `Sox.Core/WslPath.cs` | WSL 路径转换 |
| `VolumeHelper` | `Sox.Core/Indexer/VolumeHelper.cs` | 卷信息 |
| `GlobToRegex` | `Sox.Core/Indexer/GlobToRegex.cs` | glob 转正则 |
| `FileTimeHelper` | `Sox.Core/` | 文件时间转换（Unix 秒 ↔ DateTime） |
| `StartMenuShortcutResolver` | `Sox.PluginSdk/Helpers/` | 开始菜单快捷方式解析（应用搜索用） |
| `ShellOpenHelper` / `ShellInvokeHelper` / `ShellPathHelper` | `Sox.PluginSdk/Helpers/` | Shell 打开/调用/路径 |
| `ShellDeleteHelper` / `ShellRenameHelper` / `ShellPasteHelper` | `Sox.PluginSdk/Shell/FileOperations/` | 删除到回收站/重命名/粘贴 |
| `UserPathResolver` / `UserProfileHelper` / `PathAvailability` / `PathExistenceCache` | `Sox.PluginSdk/Helpers/` | 用户路径解析与存在性缓存 |
| `TriggerWord` | `Sox.PluginSdk/Services/` | 触发词判定（`Normalize` / `TryMatch` / `TryMatchInvoked` / `TryMatchAny` / `IsTypedPrefixOf`） |

---

## 4. Sox.PluginSdk 契约

位置：`src/Sox.PluginSdk/`（`net10.0-windows`，`UseWPF=true`，版本 2.0.0）

**三条全局事实**：

1. **无 DI、无实例容器**。所有 Service 是 `static class` + `public static` 委托字段，宿主进程启动时赋值，插件只调静态方法。未赋值时退化为默认行为（多为返回空/null）。
2. **SDK 内无 `IPlugin` 实现，无 `PluginManager`**。App 当前完全不加载插件（ADR-0015 用内建 `IQueryProvider`）。插件发现只在 Service/Hook 进程的 `ServicePluginLoader`，且只识别 5 种组件接口。
3. 引用关系：`Sox.Core` / `Sox.App` / `Sox.Service` / `Plugins/*` 全部引用 SDK；SDK 不引用任何项目。

### 4.1 插件基础契约

| 接口 | 用途 | 关键成员 |
|---|---|---|
| `IPluginComponent` | 所有组件根契约 | `string Name => GetType().Name;`、`string Description => "";` |
| `IPlugin : IPluginComponent` | 插件包顶层接口 | `string? WebsiteUrl`、`string? WebsiteLabel`（**无 Initialize/Shutdown**） |
| `IInstantResultProvider` | 实时查询输出即时结果 | `IEnumerable<InstantResultItem> GetInstantResults(string query)`、`bool[]? GetHighlightMask(...)`、`IReadOnlyList<string> QueryTriggerKeywords` |
| `ISearchableItemProvider` | 返回可被索引的静态条目 | `IEnumerable<SearchableItem> GetSearchableItems()`、`bool EnableAlias`、`event Action? ItemsChanged` |
| `IQueryTokenProvider` | 认领尾部 token 并变换结果 | `bool CanHandle(string)`、`Task<IReadOnlyList<ISearchResult>> ApplyAsync(...)`、`string? GetHighlightText(...)` |
| `IActionProvider` | 声明动作与动态动作提供器 | `IEnumerable<ISearchResultAction> GetActions()`、`IEnumerable<IDynamicActionProvider> GetDynamicActionProviders()` |
| `IDynamicActionProvider` | 运行时生成菜单项 | `string GroupName`、`void Init()`、`bool CanProvide(...)`、`IEnumerable<DynamicMenuItem> GetMenuItems(..., IntPtr hMenu)`、`void ExecuteCommand(..., uint commandId, IntPtr ownerHwnd)`、`void ClearSession()` |
| `IAliasProvider` | 为非 ASCII 生成别名 | `bool CanHandle(string)`、`IEnumerable<string> GetAliases(string)`、`int Version`、`IEnumerable<string> GetQueryForms(string)`、`int[]? MapAliasToSourceIndices(...)`、`void GetAliasesUtf8(string, AliasByteSink)` |
| `IResultColumnProvider` | 注册结果列 | `IEnumerable<ResultColumnDefinition> GetColumns()`、`string GetCellValue(...)` |
| `ISearchScopeProvider` | 关键词前缀 + 目录集合 | `IReadOnlyList<SearchScope> GetSearchScopes()` |
| `ISidebarFilterProvider` | 侧栏过滤分组 | `IEnumerable<SidebarFilterGroup> GetFilterGroups()`、`int SortOrder` |
| `IQuickPanelTabProvider` | Quick Panel 贡献 Tab | `Task<IReadOnlyList<ISearchResult>> GetEntriesAsync(CancellationToken)` |
| `IThemeProvider` | 提供主题 | `IEnumerable<ITheme> GetThemes()` |
| `ITranslationProvider` | i18n | `IReadOnlyList<string> SupportedCultures`、`IReadOnlyDictionary<string,string> GetTranslations(string)` |
| `IFullSearchFileResultProvider` | 为文件网格贡献结果 | `IReadOnlyList<InstantResultItem> GetFileResults(string query, int limit)` |
| `IConfigurable` | 暴露设置 schema | `PluginConfigSchema GetConfigSchema()` |
| `IPluginSearchWindow` | 暴露给插件的搜索窗最小接口 | `LocateInExplorerExternal` / `OpenFileOrFolderExternal` / `OpenFileOrFolderAsAdminExternal` / `HideWindow` |
| `ISearchResult` | 只读结果数据 | `Name` / `FullPath` / `ContextDirectory` / `IsDir` / `IsApplication` / `GetHighlightMask` / `Metadata` / `InstantActionArgument` |
| `ISearchResultAction : IPluginComponent` | 结果动作 | `GroupName` / `DisplayName` / `Hotkey` / `CanExecute` / `Execute(...)` |

`SearchWindowType`：`Main | Quick | Inline`。

### 4.2 预览契约

| 接口 | 成员 |
|---|---|
| `IFilePreviewProvider` | `int Priority`、`bool CanPreview(string path, bool isDir)`、`UIElement CreatePreview(...)`、`bool RendersExternally` |
| `IPreviewSessionAware` | `void EndPreviewSession()` |
| `IReceivesPreviewPanelBounds` | `void OnPreviewPanelBoundsAvailable(int left, int top, int width, int height)` |
| `IReusablePreview` | `bool TrySetTarget(string path, bool isDir)` |
| `IThumbnailProvider` | `int Priority`、`bool CanProvideThumbnail(...)`、`ImageSource? GetThumbnail(string path, int size)` |

### 4.3 窗口适配契约

| 接口 | 成员 |
|---|---|
| `IOpenedFolderCollector` | `IReadOnlyList<OpenedFolder> GetOpenedFolders()` |
| `IActivePathCollector : IOpenedFolderCollector` | `string TargetName`、`bool CanHandle(...)`、`string? TryGetPath(...)` |
| `IFileDialogAdapter` | `bool CanHandle(...)`、`string? GetCurrentPath(IntPtr)`、`bool NavigateTo(IntPtr, string)`、`bool TargetIsFolderOnly`、`bool GetDockBounds(...)`、`bool TryGetTargetFieldBounds(...)`、`bool TryGetFileListBounds(...)`、`bool RestoreFocus(IntPtr)` |
| `IInlineSearchAdapter` | `CanHandle` / `CanRecognizeHost` / `CanTrigger` / `GetSearchScope` / `ExecuteItem` / `GetDockBounds` / `CanEnterActionsMode` / `IsFileExplorer` / `CanShowQuickNav` / `GetListItems` / `OnSelectionChanged` / `OnSearchFinished` |
| `IQuickNavigationProvider` | `string GroupName`、`bool CanProvide(ISearchResult)`、`IEnumerable<DynamicMenuItem> GetMenuItems(...)`、`void ExecuteCommand(...)` |

`OpenedFolder`：`(string Path, IntPtr WindowHandle)`。`AdapterRect`：`int Left, Top, Right, Bottom`。`MouseTriggerType`：`DoubleClick | MiddleClick`。

### 4.4 数据模型

| 类型 | 字段 |
|---|---|
| `InstantResultItem` | `Title`、`Description`、`IconData`（SVG path 串）、`IconColor`（hex）、`ActionType`（默认 `"Copy"`）、`ActionArgument`、`TabCompletion`、`HBitmapIcon`、`OnExecute`、`OnExecuteFunc` |
| `SearchableItem` | 同上 + `ResultKind`（默认 `"InstantResult"`） |
| `DynamicMenuItem` | `Text`、`CommandId`、`IsSeparator`、`HasSubMenu`、`SubMenuHandle`、`IsDisabled`、`IsActionable`、`HBitmapItem`、`OnExecute`、`ShortcutHint`、`IsContinuation`、`IsHeader` |
| `ResultColumnDefinition` | `ColumnId`、`HeaderText`、`Width`=120、`VisibilityPredicate`、`SortComparer`、`OnDoubleClick` |
| `SearchScope` | `Keyword`、`IReadOnlyList<string> Folders`、`FilterPattern`（默认 `"*"`） |
| `SidebarFilterGroup` | `Id`、`Header`、`Items`、`AllowMultiSelect` |
| `SidebarFilterItem` | `Id`、`DisplayName`、`IconData`、`IconKey`、`MatchPredicate` |
| `PluginConfigField` | `Key`、`GroupKey`、`LabelKey`、`DescriptionKey`、`FieldType`、`DefaultValue`、`Choices`、`ChoiceOptions`、`SubFields`、`RequireModifier`、`RequireNonEmpty`、`IsTriggerWord`、`MaxLength`、`SelectionStart`、`SelectionLength`、`CustomControl`、`OnClick`、`GetValue`、`SetValue`、`Value` |
| `PluginConfigSchema` | `List<PluginConfigField> Fields`、`Action? OnSave`、`Action? OnRollback` |
| `ConfigFieldType` | `Boolean, Text, Integer, Choice, Array, Object, Group, StringList, Hotkey, FilePath, FolderPath, CustomControl, Button` |
| `FavoriteItem` | `Name`、`Path`、`Hotkey` |

### 4.5 SDK 服务（静态委托注入）

**当前有宿主实现的**：

| 服务 | 关键方法 |
|---|---|
| `DirectoryIndexerService` | `RegisterDirectory(pluginId, path, recursive, filterPattern)`、`UnregisterDirectories(pluginId)`、`Task<List<ISearchResult>> SearchDirectoriesAsync(...)`、`IAsyncEnumerable<ISearchResult> EnumerateDirectoryAsync(...)`、`IDisposable WatchDirectories(...)` |
| `PluginSettingsService` | `T GetSetting<T>(pluginId, key, defaultValue)`、`SetSetting(...)`、`bool IsComponentEnabled(dllName, componentType, componentName)`、事件 `SettingChanged` / `ComponentEnablementChanged` |
| `TranslationService` | `string Get(key)`、`TryGet(key, out result)`、`string Format(key, params args)`、`GetCurrentCulture()`、事件 `CultureChanged` |
| `ToolRunService` | `Func<string,string,Task<string?>>? RunDopusPathsFunc` |

**SDK 已定义但当前宿主未赋值（重写时需接线）**：`AppLifecycleService`、`ExplorerPathService`、`ExplorerService`、`FavoritesService`、`FileMetadataService`、`FuzzyMatchService`、`HistoryService`、`IconService`、`LocalSendTransferService`、`MemoryMaintenanceService`、`PluginMessageBoxService`、`PluginPreviewCache`、`PluginPromptService`、`PreviewActivationSignal`、`PreviewDialogSignal`、`RecentFilesService`、`SearchQueryService`、`SearchRefreshService`、`SearchWindowService`、`SettingsSearchService`、`SettingsWindowService`、`ThemeService`、`UserDataService`。

### 4.6 注册表

| 注册表 | 成员 |
|---|---|
| `ActivePathCollectorRegistry` | `FilterFunc`、`Register(IActivePathCollector)`、`GetCollectors()`、`GetAllCollectors()` |
| `FileDialogAdapterRegistry` | `FilterFunc`、`Register(IFileDialogAdapter)`、`IFileDialogAdapter? GetMatchingAdapter(IntPtr, string, string)`、`GetAdapters()`、`GetAllAdapters()` |
| `InlineSearchAdapterRegistry` | `FilterFunc`、`Register(IInlineSearchAdapter)`、`GetMatchingAdapter(...)`、`GetAdapters()`、`GetAllAdapters()` |
| `OpenedFolderCollectorRegistry` | `IReadOnlyList<OpenedFolder> GetOpenedFolders()` |

### 4.7 插件生命周期

```
ServicePluginLoader.LoadForService()   // SoxService 启动，只加载 IAliasProvider + ITranslationProvider
ServicePluginLoader.LoadForHook()      // hook 进程启动，加载全部组件类型
```

流程：扫 `{BaseDirectory}/Plugins/**/*.dll`（递归，按路径序排序以保证别名 id 稳定）→ `Assembly.LoadFrom` → 遍历类型（跳过 interface/abstract）→ 按 `IsAssignableFrom` 判定组件种类 → `Activator.CreateInstance` 无参实例化 → 注册。

识别接口：`IAliasProvider`、`ITranslationProvider`、`IActivePathCollector`、`IFileDialogAdapter`、`IInlineSearchAdapter`。

**关键缺口**：`ServicePluginLoader` 不识别 `IPlugin`，也不加载 `IInstantResultProvider` / `ISearchableItemProvider` / `IQueryTokenProvider` / `IActionProvider` / `IDynamicActionProvider` / `IResultColumnProvider` / `ISearchScopeProvider` / `ISidebarFilterProvider` / `IQuickPanelTabProvider` / `IThemeProvider` / `IFilePreviewProvider` / `IThumbnailProvider` / `IQuickNavigationProvider` / `IFullSearchFileResultProvider` / `IConfigurable`。**这些接口当前无任何宿主加载器，重写前端时必须补上。**

启用/禁用：`PluginComponentEnablement.IsComponentEnabled`，读 `UserSettings.DisabledPluginComponents`，通过 `FilterFunc` 注入 4 个 Registry。

### 4.8 WPF 专属（重写必须替换）

| 文件 | WPF 依赖 | 替换方向 |
|---|---|---|
| `Abstractions/ITheme.cs` | `ResourceDictionary GetResources()` | 改 WinUI `ResourceDictionary` 或中立抽象 |
| `Abstractions/ISearchResultAction.cs` | `ImageSource? Icon` | 改 WinUI `ImageSource` 或图标抽象 |
| `Abstractions/Plugins/Preview/IFilePreviewProvider.cs` | `UIElement CreatePreview(...)` | 改 WinUI `UIElement` |
| `Abstractions/Plugins/Preview/IThumbnailProvider.cs` | `ImageSource? GetThumbnail(...)` | 同上 |
| `Services/IconService.cs` | `ImageSource` | 同上 |
| `Services/PluginMessageBoxService.cs` | `MessageBox` 系列 | 改 ContentDialog |
| `Services/PluginPreviewCache.cs` | `UserControl` / `UIElement` | 改 WinUI 元素 |
| `Services/ThemeService.cs` | `Application.Current.Resources` | 仅回退路径，赋值 `IsDarkThemeFunc` 即绕开 |
| `Helpers/VectorIconHelper.cs` | `ImageSource` / `DrawingImage` / `Geometry.Parse` | 改 `PathIcon` / `Geometry` |
| `Windows/PluginWindow.xaml(.cs)` | `Window` / XAML / `DragMove` | 插件窗口壳，明确重写 |
| `Windows/PluginWindowClip.cs` | `DependencyProperty` / `RectangleGeometry` | 改 `CornerRadius` 或 Composition |
| `Windows/PluginWindowNativeBehavior.cs` | `HwndSource` / `WindowInteropHelper` | Win32 部分可留，WPF 部分换 |
| `Shell/FileOperations/ShellOperationStaWorker.cs` | `System.Windows.Threading.Dispatcher` | 改裸 `Thread` + `GetMessage` 循环 |

**完全可复用**（与 UI 无关）：全部 WindowAdapters 契约、Registries、`IPluginComponent` / `IPlugin` / `IAliasProvider` / `AliasByteSink` / `ISearchResult` / `FileMetadata` / `SearchWindowType`、除上表外的全部 Services、除 `VectorIconHelper` 外的 Helpers、除 `ShellOperationStaWorker` 外的 Shell 操作。

---

## 5. 跨进程协议（前端必须原样兼容）

### 5.1 管道总览

| 通道名 | 协议 | 服务端 | 客户端 | 方向 |
|---|---|---|---|---|
| `SoxPipe` | Wire 二进制（SLPQ / SLPR / SLRS） | Service | App | 双工 |
| `Sox_Hook_Events_{sessionHash}` | `PipeRequestBinarySerializer` | hook（elevated） | App | Hook → App 单向事件 |
| `Sox_Hook_Cmds_{sessionHash}` | 同上 | hook | App | App → Hook 单向命令 |
| `SoxLaunch_{UserName}` | 纯文本，`\u001f` 分隔，UTF-8 | App 首实例 | 第二份 App | 单向 |
| Everything IPC 窗口 | Win32 `WM_USER` + `WM_COPYDATA` | App 进程内 | Everything 客户端 | 双向 |
| LocalSend | UDP 多播 + HTTP(S) TCP 53317 | App/服务进程内 | LocalSend 对端 | 双向 |

`HookIpcNames`：`EventPipeName = "Sox_Hook_Events_" + SessionHash`、`CmdPipeName = "Sox_Hook_Cmds_" + SessionHash`。`SessionHash = SHA256(Sid + '\0' + Process.SessionId)` 十六进制小写。

### 5.2 Wire 版本化

| 序列化器 | Magic | 版本 |
|---|---|---|
| `SearchRequestBinarySerializer` | `SLPQ` | `VersionSearchRequest = 10` |
| `PipeResponseBinarySerializer` | `SLPR` | `Version = 6` |
| `PipeRequestBinarySerializer`（hook） | `SLPQ` | `VersionIpc = 3` / `VersionString = 1` |
| `SearchResponseBinarySerializer` | `SLRS` | `Version = 6` |
| `SearchResultWithHighlightBinarySerializer` | `HLRS` | `Version = 2`（**当前仓库无调用方**，属预留/历史遗留，新前端可不实现） |

**精确匹配，无协商无降级**。版本不一致时双方首个字节即抛 `InvalidDataException`。新增请求 ID 也算契约变更，必须递增版本号。字符串统一 UTF-8 + 7-bit LEB128 varint 长度前缀。

### 5.3 SearchRequestId（App → Service）

| ID | 值 | 请求体 | 响应 | 流式 | 需授权 |
|---|---|---|---|---|---|
| `Ping` | 0 | 无 | `Ok` | 否 | |
| `Status` | 1 | 无 | `Status` | 否 | |
| `Rebuild` | 2 | 无 | `Ok`/`Error` | 否 | 是 |
| `GetMachineSettings` | 3 | 无 | `MachineSettings` | 否 | |
| `SetMachineSettings` | 4 | `LocalDrives` | `Ok` | 否 | 是 |
| `Search` | 5 | `Limit(i32), AppLimit(i32), Query(str), DisabledAliasComponents(str[]), FileNameFilter(str), ExactMatch(1B), OrFirstPrecedence(1B)` | 流 | 是 | |
| `SearchDir` | 6 | `Limit(i32), AppLimit(i32), DirectoryFilter(str), Query(str), DisabledAliasComponents(str[]), FileNameFilter(str), ExactMatch(1B), OrFirstPrecedence(1B)` | 流 | 是 | |
| `RebuildDrive` | 7 | `Drive` | `Ok`/`Error` | 否 | 是 |
| `DeleteDriveIndex` | 8 | `Drive` | `Ok`/`Error` | 否 | 是 |
| `SubscribeStatus` | 9 | 无 | 持续 `Status` 帧 | 是 | |
| `Initialize` | 10 | 无 | `Ok` | 否 | |
| `GetFileMetadata` | 11 | `FilePaths` | `FileMetadata` dict | 否 | |
| `ClearServiceLog` | 12 | 无 | `Ok`/`Error` | 否 | |
| `GetRecentFiles` | 13 | `Limit, MaxAgeMinutes, Directories` | `RecentFiles` | 否 | |
| `ClearPathCaches` | 14 | 无 | `Ok` | 否 | |
| `LaunchHook` | 15 | `RequestElevation` | `HookLaunched(pid)`/`Error` | 否 | 是 |
| `CancelDriveIndex` | 16 | `Drive` | `Ok`/`Error` | 否 | |
| `EnumerateDir` | 17 | `Limit, DirectoryFilter, Query, Recursive` | 流 | 是 | |
| `SubscribeDirectoryChanges` | 18 | `Directories` | 持续 `DirectoriesChanged` 帧 | 是 | |
| `GetSpaceEntries` | 19 | `Drive` | `SpaceEntries` | 否 | |
| `ApplyUpdate` | 20 | `UpdateSourceDir` | `Ok`/`Error` | 否 | 是 |

### 5.4 PipeResponseKind（Service → App）

| Kind | 值 | Payload |
|---|---|---|
| `Ok` | 1 | 无 |
| `Error` | 2 | `Message` |
| `Status` | 3 | `State, Progress, TotalFiles, TotalDirs, ElapsedTime, IsMaintenanceBusy, ActiveDrives, Drives[]` |
| `MachineSettings` | 4 | `LocalDrives` |
| `FileMetadata` | 5 | `[{path, Size, CreationTimeUnixSeconds, LastWriteTimeUnixSeconds, LastAccessTimeUnixSeconds}]` |
| `RecentFiles` | 6 | `[{Name, Path, IsDir, Drive, ModifiedUtc}]` |
| `HookLaunched` | 7 | `Pid` |
| `DirectoriesChanged` | 8 | `[str]` |
| `SpaceEntries` | 9 | `[{Path, Name, Size, IsDirectory, IsHardLinkDuplicate}]` |

搜索结果帧（`SearchResultFrameCodec`）：`Name, Path, IsDir, Drive, RankSortKey(u64), Metadata.Size, Created/Modified/Accessed(u32 unix 秒), Attributes(i32)`。帧类型：`0=End, 1=FileResult, 2=AppResult, 3=NotIndexed, 255=Header`。

### 5.5 Hook IPC

**HookIpcClient（App 侧）public 事件**：

```csharp
event Action? OnActivated;                              // 双击 Ctrl 呼出
event Action? OnQuickPanelHotkey;
event Action? OnQuickNavigationHotkey;
event Action<char>? OnCharacterTyped;
event Action? OnBackspacePressed / OnEscapePressed / OnEnterPressed / OnUpPressed / OnDownPressed / OnLeftPressed / OnRightPressed;
event Action<int>? OnCtrlNumberPressed;
event Action? OnFocusInlineSearchRequested;
event Action<int,int>? OnMouseClick / OnMouseDoubleClick / OnMouseMiddleClick;
event Action<IntPtr,string,string,bool>? OnExplorerActivated;   // hwnd, title, className, isDesktop
event Action? OnExplorerDeactivated;
event Action<string,bool,bool>? OnPathCaptured;                 // path, isDesktop, isDialog
event Action<IReadOnlyList<string>>? OnOpenedFoldersCaptured;
event Action? OnActiveWindowMoved;
event Action<string>? OnError;
```

**public 属性与方法**：

```csharp
int  ServiceProcessId { get; }         // 实为 hook 进程 PID
bool IsConnected { get; }
bool IsHotkeysDisabled { get; set; }   // set 即发 SetHotkeysDisabled
void Start();                          // 幂等
void Stop();
void SendMessage(IpcMessage msg);      // fire-and-forget
Task<bool> TrySendMessageAsync(IpcMessage msg);
void Dispose();
```

连上后先 `SetAppProcessId` + `SetHotkeysDisabled`，并重放 5 个粘性状态。校验对端 PID，不匹配抛 `UnauthorizedAccessException`。

**IpcMessageId 全量**：

App → Hook：`Stop=1`、`SetAppProcessId=2`、`SetQuickSearchVisible=3`、`SetInlineSearchVisible=4`、`NavigateDialog=5`（Hwnd, 路径）、`RestoreDialogFocus=6`、`ReloadSettings=7`、`SetHotkeysDisabled=8`、`ForceForeground=13`、`KillProcess=14`、`ExecuteInlineItem=15`、`InlineSelectionChanged=16`、`InlineSearchFinished=17`、`SetInlineWindowOnScreen=18`、`RequestOpenedFolders=19`、`ToolResult=44`。

Hook → App：`Activate=20`、`ExplorerDeactivated=21`、`ActiveWindowMoved=22`、`KeyBackspace=23`、`KeyEscape=24`、`KeyEnter=25`、`KeyUp=26`、`KeyDown=27`、`KeyLeft=28`、`KeyRight=29`、`KeyChar=30`、`KeyCtrlNumber=31`、`MouseClick=32`、`ExplorerActivated=33`、`PathCaptured=34`、`Error=35`、`QuickPanelHotkey=36`、`MouseDoubleClick=38`、`MouseMiddleClick=39`、`ExecuteInlineItemResponse=40`、`OpenedFoldersCaptured=41`、`QuickNavigationHotkey=42`、`RunTool=43`、`FocusInlineSearch=45`。

> 注：枚举值不连续（9-12、37 空缺）。早期调研误列 `ClearHookLog=46`，**源码中不存在该值**（`IpcMessageId` 最大为 45），已删除。

**`IpcMessage` 结构字段**（`Sox.Core/Wire/IpcMessage.cs`，`struct`）：

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | `IpcMessageId` | 消息类型 |
| `ProcessId` | `uint` | 进程 id（`SetAppProcessId` / `KillProcess` 用） |
| `BoolVal` | `bool` | 布尔载荷（`SetQuickSearchVisible` / `SetInlineSearchVisible` / `SetHotkeysDisabled` / `ForceForeground` / `SetInlineWindowOnScreen` / `InlineSearchFinished` 等） |
| `CharVal` | `char` | 字符载荷（`KeyChar`） |
| `IntVal` | `int` | 整数载荷（`KeyCtrlNumber` / `ExecuteInlineItem` 的 requestId / `ExecuteInlineItemResponse` 的 requestId） |
| `MouseX` / `MouseY` | `int` | 鼠标坐标（`MouseClick` / `MouseDoubleClick` / `MouseMiddleClick`） |
| `Hwnd` | `long` | 窗口句柄（`NavigateDialog` / `RestoreDialogFocus` / `ForceForeground` / `ExecuteInlineItem` / `InlineSelectionChanged` / `InlineSearchFinished` / `ExplorerActivated`） |
| `StringVal1` | `string?` | 主字符串（路径 / 标题 / 输出文件 / 失败原因，随消息类型而定） |
| `StringVal2` | `string?` | 次字符串（类名 / 工具路径 / 搜索输入） |
| `StringList` | `IReadOnlyList<string>?` | 字符串列表（`OpenedFoldersCaptured`） |
| `IsDesktop` | `bool` | 是否桌面（`ExplorerActivated` / `PathCaptured`） |
| `IsDialog` | `bool` | 是否文件对话框（`PathCaptured`） |

### 5.6 Everything IPC

- 窗口类 `EVERYTHING_TASKBAR_NOTIFICATION`，隐藏窗口，创建后广播 `EVERYTHING_IPC_CREATED`。
- `WM_USER` 命令码：版本类回 `1.4.1` / build `1300`；`IpcGetTargetMachine`(5)；`IpcIsAdmin`(403) → 实际管理员状态；能力探测回固定值。
- `WM_COPYDATA` action code：`0` 命令行、`1/2` Query v1、`17/18` Query2 v1、`19/20` GetRunCount、`21/22` SetRunCount、`23/24` IncRunCount。
- 查询语法：`parent:` / `path:` / `ext:` / `folder:` / `file:` / `root:` / `drive:` / `nopath:` / `name:` / `exact:` / `nocase:` / `nowholeword:` / `noregex:` / `case:` / `wholeword:` / `regex:`，前导路径 `"C:\..." rest`。
- 数据源 `EverythingSearchDataProvider`：`ExecuteQueryAsync` / `GetRunCount` / `SetRunCount` / `IncrementRunCount`。运行历史内存字典上限 10000。
- **前端侧宿主**：`Sox.App/Services/EverythingIpcHost.cs`（`public sealed class EverythingIpcHost : IDisposable`）：`EverythingIpcHost(SearchHost searchHost)` 构造、`bool IsRunning`、`void Start()` / `Stop()` / `Apply(bool enabled)` / `Dispose()`。前端按 `UserSettings.EnableEverythingIpc` 调 `Apply`。**重写时这个宿主类需要重新实现**（它在 App 侧，不在 Core）。

### 5.7 服务管理

前端拉起流程（现有 `SearchHost.EnsureReadyAsync`）：

1. `SearchService.PingAsync(ct)` 探测。
2. 失败则 `StartServiceAsync`：找 `Sox.Service.exe` → `sc query SoxService` → 匹配则 `sc start`，否则 `--uninstall` + `--install`（runas）+ `sc start`。
3. 20 次 × 500ms 轮询 `PingAsync`。
4. 冷启动额外 `InitializeOrLoadIndexAsync(false, ct)`。

`Sox.Service.exe` CLI：`--service` / `--install`(`-i`) / `--uninstall`(`-u`) / `--hook` / 无参数（控制台调试）。

### 5.8 更新与安装检测

| 类型 | public 入口 |
|---|---|
| `UpdatePackage` | `CreateStagingDirectory()`、`TryGetStagedPackage(...)`、`Verify(...)`、`TryVerifyAndExtract(...)`、`ResolvePayloadRoot(...)`；常量 `ZipFileName="latest.zip"`、`SignatureFileName="latest.zip.sig"`、`StagingDirPrefix="SoxUpdate-"` |
| `UpdateRelaunchMarker` | `Write(sessionId, appExePath, now)`、`Clear()`、`TryTake(out sessionId, out appExePath, now)`（5 分钟新鲜度） |
| `InstallationDetector` | `Detect()` → `InstallationMode`（`Portable` / `Installed`） |
| `DataDirectoryResolver` | `ResolveShared(...)`、`ResolveUser(...)` |
| `CurrentUserIdentity` | `SidHash`、`SessionHash`、`Hash(string)` |
| `ElevationManager` | `IsRunningAsAdmin()`、`TryElevateProcess(exePath, args)` |
| `SessionProcessLauncher` | `TryLaunch(sessionId, exePath, arguments, requestElevation, detachFromConsole, out pid, out error)` |
| `HookProcessBroker` | `TryLaunch(...)`，每会话最多一个 hook |

更新签名：ECDSA P-256 + SHA256，公钥硬编码。落盘由 `portable-updater.bat` 提权执行。**当前仓库无下载/检查更新代码**（`UserSettings` 有相关字段但未实现）。

---

## 6. 驱动监控与索引

### 6.1 本地盘（经管道）

用 `SearchService.GetStatusAsync` 拉状态，`SearchStatusStream.SubscribeAsync` 订阅变化。重建/删除/取消单盘走 `SearchServiceManagementExtensions`。

### 6.2 网络盘 / WSL / 文件夹索引（App 进程内）

位置：`src/Sox.Core/Services/Network/UserNetworkDriveSearch.cs`（`public static`）

| 成员 | 说明 |
|---|---|
| `event Action<IReadOnlyList<NetworkIndexStatus>> StatusesChanged` | 状态变化 |
| `event Action<string, IReadOnlyCollection<string>?> DirectoriesChanged` | 目录内容变化 |
| `void Configure()` / `void Refresh()` | 配置/强制刷新（读 `UserSettings`） |
| `bool RefreshDrive(string)` / `bool CancelDrive(string)` | 单盘刷新/取消 |
| `IReadOnlyList<NetworkIndexStatus> GetStatuses()` | 状态列表 |
| `bool HasCache(string)` / `IReadOnlyList<string> GetCachedDrives()` / `void DeleteCache(string)` / `void ClearAllCaches()` | 缓存管理 |
| `SearchStreaming(...)` / `bool EnumerateDirectory(...)` / `List<SearchResult> GetRecentFiles(...)` | 搜索/列举/最近文件 |

网络盘解析工具：`NetworkDriveResolver.GetNetworkDrives()` / `GetUncPath(letter)` / `GetNetworkId(letter)`；`IndexedPathSpelling.IndexSpellings(path)`（映射盘 ↔ UNC、`\\wsl$` ↔ `\\wsl.localhost`）。

### 6.3 盘符增删（热插拔）

Service 内部处理：`DriveDeviceRemovalMonitor`（CM_Register_Notification）、`DriveReattachWaiter.Start(drive, token, onReattached)`、`DriveIndexRemovalScope`、`DriveMonitorFactory.EnsureMonitor(...)`。前端只需监听 `IndexerStatus.Drives[].State`。

### 6.4 UsnIndexer（服务端专用）

位置：`src/Sox.Core/Indexer/Usn/UsnIndexer.cs`。前端不直接调用，列出供参考：`SearchStreaming` / `EnumerateDirectory` / `SnapshotStatus` / `Status` / 事件 `StatusChanged` / `DirectoriesChanged` / `SetDriveStatuses` / `SetDriveState` / `UpdateDriveProgress` / `CompactMemory` / `ClearCaches` / `UnloadRuntime`。

### 6.5 插件本体行为

**FileDialog**（`src/Plugins/FileDialog/`）：4 个组件，由 `ServicePluginLoader.LoadForHook()` 注册。

| 类 | 接口 | 触发条件 |
|---|---|---|
| `ClassicFileDialogAdapter` | `IFileDialogAdapter` | 窗口类 `#32770`，无 `Breadcrumb Parent`，有文件名编辑框（1152/1148）与 ComboBox |
| `StandardFileDialogAdapter` | `IFileDialogAdapter` | 窗口类 `#32770`，含 `Breadcrumb Parent`；`TargetIsFolderOnly` 由 `FOS_PICKFOLDERS` 特征决定 |
| `FolderBrowserDialogAdapter` | `IFileDialogAdapter` | 窗口类 `#32770`，无面包屑，含 `SysTreeView32`（控件 ID 14145 或 100）；`TargetIsFolderOnly` 恒 true；`GetCurrentPath` 返回 null |
| `ExplorerPathCollector` | `IActivePathCollector` | 窗口类 `CabinetWClass` / `Progman` / `WorkerW`；取资源管理器当前标签页路径 |

**PinyinAlias**（`src/Plugins/PinyinAlias/`）：`PinyinAliasProvider` 实现 `IAliasProvider` + `ITranslationProvider` + `IConfigurable`。

- `Version => 4`（版本变化触发全部索引重新烘焙别名）
- `InputRanges = PinyinEngine.TableRange`（CJK 表范围）
- `SyllableSeparator = (char)2`（U+0002）
- `GetAliases`：首字母别名 + 全拼别名（多读音 `|` 连接），可选附加简体拼写，两层缓存上限 4096
- 配置项：`ConvertTraditionalToSimplified`（Boolean，默认 true），存 `UserSettings.PluginSettings["Sox.Plugins.PinyinAlias"]["ConvertTraditionalToSimplified"]`

**拼音 API（前端可引用 `Sox.Plugins.PinyinAlias.dll`）**：`PinyinEngine.IsChinese(char)` / `TryGetPinyins(char, out string[])` / `TryGetPinyinIds(char, out ushort[])` / `GetSyllableUtf8(int)` / `AllSyllables` / `MayContainChinese(ReadOnlySpan<char>)` / `TableRange`。

---

## 7. 前端重写对接要点

### 7.1 必须原样兼容（逐字节，不可变）

1. **SoxPipe Wire 协议**：全部序列化器、版本号、帧类型、字段顺序。
2. **hook IPC 协议**：`IpcMessageId` 全部值、`IpcMessage` 字段映射、`HookIpcNames` 命名规则、管道方向与单实例语义。
3. **hook 事件/命令语义**：`HookIpcClient` 事件集合、`NavigateDialog` + `RestoreDialogFocus`、粘性状态重放、`ExecuteInlineItem` requestId 关联、`RunTool`/`ToolResult` 往返。
4. **Everything IPC**：窗口类名、`WM_USER` 命令码、`WM_COPYDATA` action code、v1/v2 列表二进制布局、查询语法。
5. **LocalSend 协议**：UDP 多播地址/端口、v1/v2 HTTP 路由、TLS 指纹与 PIN。
6. **服务 CLI 契约**：`--service/--install/--uninstall/--hook`、服务名 `SoxService`、`Sox.Service.exe` 文件名与同目录授权判据。
7. **更新包契约**：`SoxUpdate-*` 前缀、`latest.zip` / `.sig` 文件名、ECDSA 公钥、`update-payload` 子目录、`portable-updater.bat` 约定、`update-relaunch` 标记格式。

### 7.2 可自由调整（前端内部）

1. `SoxLaunch_{UserName}` 单实例转发管道（命名、编码、监听线程均可重写）。
2. 托盘 / 热键 / 主题 / 窗口生命周期（ADR-0016 明确全部重写）。
3. `SearchHost` / `EverythingIpcHost` 的组织方式（可换 WinUI 3 DI）。
4. 更新下载与检查逻辑（当前未实现）。
5. `HookIpcClient` 的封装层（事件语义稳定，可包 Observable/Command）。

### 7.3 已知陷阱

- **Wire 版本精确匹配**，无协商。前端与 Service 必须同版本同装；任何请求 ID 增删、字段顺序/宽度变化都要同时递增版本号。
- **`SearchRequestMessage` 是 struct**，默认值是历史行为（`ExactMatch=false` → 模糊；`OrFirstPrecedence=false` → AND 优先；`Recursive=false` → 单层）。漏设字段是「旧版语义」而非报错。
- **hook 管道名含 `SessionHash`**，跨会话/多用户互不干扰，不能改固定名。
- **`onResult` 多线程并发**，前端必须自行同步或改派 UI 线程。
- **`SearchHistoryStore.Record` 绝不能在 UI 线程同步调用**（慢速网络共享上 `File.Exists` 可能阻塞数秒）。
- **搜索取消**：`SearchEngine` 按 `directoryFilter` 分槽取消，同一 filter 的新搜索只取消同 filter 的旧搜索。取消表现为 `OperationCanceledException`，`SearchStreamingAsync` 会向上抛。
- **超时**：管道连接 2s；`IndexedDirectoryEnumerator` 服务连接 30s、就绪 2min、轮询 250ms；冷启动窗口 120s。
- **权限**：搜索/状态/设置读取/元数据/最近文件都不需管理员；只有安装/启动服务走 runas。

### 7.4 重写时的两个明确缺口

1. **前端无插件加载器**：SDK 提供 15+ 个 `I*Provider` 接口，除 5 个（Alias/Translation/ActivePath/FileDialog/InlineSearch）外无加载与调度代码。WinUI 3 前端若要真支持插件，需新建等价于上游 `PluginManager` 的组件发现 + 逐键调度 + 结果合并层。
2. **大量 SDK 服务委托无人赋值**：`IconService`、`SearchWindowService`、`SearchQueryService`、`FavoritesService`、`HistoryService`、`FileMetadataService`、`FuzzyMatchService`、`UserDataService`、`ExplorerPathService`、`RecentFilesService`、`SettingsWindowService`、`SettingsSearchService`、`PluginPromptService`、`PluginMessageBoxService`、`AppLifecycleService`、`MemoryMaintenanceService`、`LocalSendTransferService`、`SearchRefreshService` 在当前 App 都无赋值点。重写时需逐个接线。

---

## 附录：类型位置速查

| 类型 | 位置 |
|---|---|
| `SearchService` + 扩展 | `Sox.Core/Services/Search/` |
| `SearchEngine`（服务端） | `Sox.Core/SearchEngine.cs` |
| `SearchResult` / `SearchResultRankComparer` | `Sox.Core/SearchResult.cs` |
| `SearchContext` | `Sox.Core/SearchContext.cs` |
| `UsnIndexer` / `IndexerStatus` | `Sox.Core/Indexer/Usn/` |
| `FuzzyQuery` / `FuzzyMatcher` / `MatchRank` | `Sox.Core/SearchIndex/` |
| `SearchQueryParser` / `SearchQuerySortParser` | `Sox.Core/SearchIndex/Query/` |
| `UserSettings` + 子模型 | `Sox.Core/Settings/` |
| `SearchHistoryStore` / `KeywordHistoryStore` | `Sox.Core/` 根 |
| `ExclusionRuleSet` / `FileSystemItemFilter` / `ProcessNameFilter` / `NaturalNameComparer` | `Sox.Core/` 根 |
| `AliasProviderRegistry` | `Sox.Core/SearchIndex/` |
| `MachineSettings` | `Sox.Core/Settings/MachineSettings.cs` |
| `SpaceIndexEntry` | `Sox.Core/IndexV2/Space/` |
| `NetworkIndexStatus` / `NetworkIndexer` | `Sox.Core/Indexer/NetworkDrive/` |
| `UserNetworkDriveSearch` / `NetworkDriveResolver` | `Sox.Core/Services/Network/` |
| `HookIpcClient` / `HookIpcNames` | `Sox.Core/Hook/Ipc/` |
| Wire 序列化器 | `Sox.Core/Wire/` |
| `EverythingIpcServer` / `EverythingSearchDataProvider` | `Sox.Core/Services/Everything/` |
| `LocalSend*` | `Sox.Core/Services/LocalSend/` |
| `UpdatePackage` / `UpdateRelaunchMarker` | `Sox.Core/Services/Update/` |
| `InstallationDetector` / `DataDirectoryResolver` / `CurrentUserIdentity` | `Sox.Core/Services/Installation/` |
| `ElevationManager` | `Sox.Core/Services/ElevationManager.cs` |
| `ServicePluginLoader` | `Sox.Core/Services/Plugin/Loading/` |
| PluginSdk 全部 | `Sox.PluginSdk/` |
| FileDialog / PinyinAlias 插件 | `Plugins/FileDialog/` / `Plugins/PinyinAlias/` |

## 关联

- ADR-0016（前端重写为 WinUI 3）
- `docs/reference/powertoys-spotlight-material.md`（CmdPal 材质与架构参考）
- ADR-0015（前端查询 Provider 架构）
