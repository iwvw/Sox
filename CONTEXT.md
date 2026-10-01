# CONTEXT.md

领域语言与项目心智模型。让所有 Agent 用统一术语沟通。

## 项目一句话

Sox：Windows 原生文件搜索工具。后端复用 Lertaro 核心（USN/MFT 秒级索引），前端 WinUI 3 Spotlight 风格悬浮窗。

## 架构心智模型

```
┌──────────────────────────────────────────────┐
│ 前端 Sox.App（WinUI 3）：常驻卡片窗 + 托盘       │
│   输入防抖 → SearchService → 流式结果 → 虚拟化渲染 │
│   卡片材质：SystemBackdropElement + ThemeShadow │
├──────────────────────────────────────────────┤
│ 编排层 SearchService（Sox.Core）               │
│   多源合并：管道搜索 + 网络盘 + 实时目录扫描      │
├──────────────────────────────────────────────┤
│ 后台服务 SoxService（Windows 服务，LocalSystem）│
│   命名管道 SoxPipe（Wire 协议，版本化）          │
│   USN Journal 增量 + MFT 全扫 + 内存索引        │
└──────────────────────────────────────────────┘
```

三进程：SoxService（后台索引/搜索）· Sox.App（前端 UI）· hook（`Sox.Service --hook`，全局键盘/对话框监听，已接入）。

## 术语表

| 术语 | 含义 |
|---|---|
| 核心（Core） | 源自 Lertaro MIT 快照的后端，已改名 Sox.Core / Sox.Service / Sox.PluginSdk |
| 前端（App） | Sox.App，WinUI 3 悬浮窗，通过 SearchService 调核心（ADR-0016） |
| 卡片（Card） | 前端可见的圆角主体；材质画在卡片上，卡片外的阴影区透明 |
| 卡片材质 | WinUI `SystemBackdropElement` 上的 Acrylic/Mica 等；与「整窗材质」相对 |
| BackdropStyle | 材质样式枚举：Acrylic / AcrylicThin / Mica / MicaAlt / Clear（抄 CmdPal） |
| BackdropParameters | 材质参数记录：TintColor / FallbackColor / EffectiveOpacity / EffectiveLuminosityOpacity / Style |
| 常驻 + cloak | 窗口常驻不销毁，显隐只做 DWM `DWMWA_CLOAK` 开关（呼出秒开的关键） |
| SearchService | Sox.Core 的多源编排门面：ping 探活、SearchStreamingAsync 流式搜索、GetRecentFiles |
| Wire 协议 | 命名管道字节序列化协议（SearchRequestId 枚举，版本化，精确匹配无协商） |
| SoxPipe | 主搜索命名管道（服务端 SoxService，客户端 App） |
| SoxService | Windows 服务名（LocalSystem，自动安装/拉起） |
| hook 进程 | `Sox.Service --hook` 提权进程：全局键盘钩子 + ExplorerTracker + 对话框探测；经 HookIpcClient 与 App 通信 |
| HookIpcClient | App 侧 hook 客户端：Start 即拉起 hook 进程并连管道，事件 OnActivated/OnExplorerActivated/OnPathCaptured |
| ToggleWindowHotkey | hook 的呼出手势（核心 UserSettings，默认双击 Ctrl，窗口 100-300ms）；**注意与主窗的 Alt+Space 是两个独立机制**（Alt+Space 由前端 `SummonHotkey` 低层钩子实现） |
| NavigateDialog | App→Hook 消息：让活动文件对话框导航到指定路径（路径回填） |
| SearchResult | 结果对象：Name / Path / IsDir / Drive / Attributes / Metadata(Size, Modified) |
| 服务拉起 | App 启动时 ping 失败 → sc start / UAC --install 的三段式 |
| 结果帧 | 流式搜索结果，每命中回调一次（多线程并发，前端需自行同步） |
| keyed diff | 结果按 Path 保留/插入/移除，保留行不重建（防跳动） |
| 历史排序 | 打开记录（SearchHistoryStore）叠加到核心 rank，用 SearchResultRankComparer |
| Provider | 前端查询源（ADR-0015）：计算器/网页/应用/命令/窗口/剪贴板，走 IQueryProvider |
| 内嵌 mini 窗 | 贴合文件对话框的独立小搜索窗（ADR-0012），选中回填路径 |

## 关键数值（设计目标）

| 指标 | 目标 |
|---|---|
| 前端常驻内存 | < 300MB（v2 放宽，原 < 100MB） |
| 全盘索引 | 秒级（MFT 直读 + USN 增量，由核心保证） |
| 查询延迟 | < 50ms |
| 呼出到可见 | < 100ms（靠常驻 cloak） |

## 前端对接要点

- 搜索：`new SearchService()` → `SearchStreamingAsync(query, fileLimit, appLimit, dirFilter, onResult, token)`
- 空输入：`GetRecentFilesAsync(dirs, limit, maxAgeMinutes, token)` 显示最近文件
- 状态：`GetStatusAsync()` + `SearchStatusStream.SubscribeAsync(onStatus, token)`
- 服务拉起：PingAsync 失败 → sc query/start → 未安装则 UAC `Sox.Service.exe --install`
- 模糊匹配开关由核心自动读 UserSettings，前端无需传
- 材质：抄 CmdPal 的 `BackdropStyles` 配置表，实现走 WinUI `SystemBackdropElement`
- **完整 API 见 `docs/reference/backend-api-catalog.md`**

## 项目结构

```
Sox.slnx
├─ src/Sox.Core/        核心：索引/搜索/Wire/SearchService（Lertaro 快照改名）
├─ src/Sox.Service/     Windows 服务宿主（SoxService）
├─ src/Sox.PluginSdk/   插件契约
├─ src/Sox.App/         前端（WinUI 3，ADR-0016 重写中）
├─ src/Plugins/         插件本体（FileDialog / PinyinAlias）
└─ .vscode/             VSCode 调试（build 任务自动停服务）
```
