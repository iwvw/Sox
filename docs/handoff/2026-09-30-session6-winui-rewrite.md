# Handoff：前端重写决策与文档重整（会话六）

日期：2026-09-30
状态：决策阶段完成，待开工实施

## 会话目标

用户决定**接受全盘推翻重构前端**（可用更现代的 UI 与框架，功能可迁移或重写，ADR/PRD 可重写，放宽限制）。本轮完成决策文档化与后端 API 梳理，不动代码。

## 本轮拍板的决策

**ADR-0016：前端 WPF → WinUI 3**（已 Accepted）

| 项 | 决定 |
|---|---|
| 前端框架 | WinUI 3 + Windows App SDK 2.x |
| 架构 | 对齐 CmdPal 的卡片材质架构（`SystemBackdropElement` + `ThemeShadow`） |
| 窗口生命周期 | 常驻 + DWM cloak（不再每次新建窗口） |
| 打包 | 非打包 exe 与 MSIX 两种形态 |
| 迁移节奏 | 一次性重写（不做渐进双轨） |
| 内存 | NFR-01 从 < 100MB 放宽到 < 300MB |
| 发布 | NFR-07 从「自包含单文件」改为「exe 或 MSIX」 |

## 重写范围实测（关键结论）

**后端几乎零成本保留**：

| 项目 | 行数 | WPF 耦合 | 处置 |
|---|---|---|---|
| `Sox.Core` | 36,700 | **零** | 原样复用 |
| `Sox.Service` | 432 | 无 UI | 原样复用 |
| `Sox.PluginSdk` | 4,847 | 仅 `Windows/PluginWindow.xaml(.cs)` 约 300 行 | 换窗口壳 |
| `Plugins/*` | 2,234 | 无 UI | 原样复用 |
| **`Sox.App`** | **6,397** | **100% WPF** | **全部重写** |

即：索引引擎、搜索、拼音、IPC、服务、插件契约全部保留；真正重写的只有 `Sox.App` 那 6,400 行前端 + 300 行插件窗口壳。

## 本轮产出的文档

| 文件 | 内容 |
|---|---|
| `docs/adr/0016-frontend-winui3-rewrite.md` | 新 ADR（前端重写决策） |
| `docs/adr/0001-csharp-wpf-net10.md` | 标记 Superseded（前端部分被 0016 取代） |
| `docs/adr/0012-inline-search-window.md` | 标记 Superseded（实现被 0016 取代，交互保留） |
| `docs/adr/0014-wpfui-migration.md` | 标记 Superseded（WPF 栈废弃，决策失去意义） |
| `docs/PRD.md` | v2 重写（技术基线、功能项扩到 F-27、NFR 放宽、新里程碑） |
| `docs/reference/backend-api-catalog.md` | **后端 API 全目录**（约 950 行，前端对接必读） |
| `docs/reference/powertoys-spotlight-material.md` | CmdPal 材质调研（上一轮产出） |
| `AGENTS.md` | 技术栈、开发循环、命令速查更新 |
| `CONTEXT.md` | 术语表新增卡片/材质/cloak/Provider 等 |

## 后端 API 目录的核实结果

已逐项对照源码核实，修正 4 处错误 + 补 7 处遗漏：

**修正**：
1. `IpcMessageId.ClearHookLog=46` 不存在（枚举最大 45）
2. `LogLevel` 是 `Error/Warn/Info/Debug`（非 `Warning`，顺序相反）
3. `SearchDir` 请求体字段顺序：`DirectoryFilter` 在 `Query` 前
4. `PipeResponse` 实为 public（原标 internal）

**补充**：`PluginConfigField.Value`、`SearchResult.RankSortKey`（internal）、`IpcMessage` 字段表、`Logger`/`LogLevel`、`QuickPanel` 静态辅助、`EverythingIpcHost`、其它工具类。

**顺带确认**：`ServicePluginLoader` 只识别 5 个接口；SDK 的 18 个服务委托全仓库无赋值点。

## 实施时必须注意的硬约束（来自 API 目录第 7 节）

1. **Wire 协议精确匹配、无协商**：前端与 Service 必须同版本；改任何字段都要同时递增版本号（请求 v10 / 响应 v6）。
2. **`onResult` 多线程并发回调**：前端必须自行同步或改派 UI 线程。
3. **`SearchHistoryStore.Record` 绝不能在 UI 线程同步调用**（慢速网络共享上 `File.Exists` 可能阻塞数秒）。
4. **hook 管道名含 `SessionHash`**，不能改固定名。
5. **两个缺口**：SDK 的 15+ 个 `I*Provider` 接口除 5 个外无宿主加载器；18 个 SDK 服务委托无人赋值。若要支持插件，需新建调度层。

## 下一步（实施）

按 PRD v2 里程碑：

1. **M1**：WinUI 3 骨架 —— 三进程打通、常驻 cloak 窗口、卡片材质（Acrylic/Mica）、基础搜索与结果列表
2. **M2**：交互打磨 —— 拼音、排序、键盘导航、空输入最近列表、主题与材质设置
3. **M3**：集成 —— 对话框 mini 窗、Shell 右键菜单、设置面板、索引管理
4. **M4**：能力补齐 —— 预览、历史管理、Provider、Everything IPC、Quick Panel
5. **M5**：稳定性 + 内存 + 双形态打包

**开工前需确认**（ADR-0016 待定项）：
1. WinUI 3 托盘图标方案（WinUIEx vs 自写 `Shell_NotifyIcon`）
2. 插件窗口壳（`PluginWindow`）在 WinUI 3 下的形态
3. hook 进程与 WinUI 3 前端的 IPC 是否需调整（预期不变）
4. 非打包模式下的 Windows App SDK 运行时分发方式
5. 是否新建 `Sox.App.WinUI` 项目保留旧 `Sox.App`，还是一次性替换（用户选了一次性重写）

## 关键坑（本次新增）

- **PowerToys 仓库 `core.autocrlf=true`**：工作区常年有行尾差异，更新前 `git reset --hard` 即可，别 stash。
- **文档里的 `dev.ps1` 实际不存在**（ADR-0008 规划但未实现），AGENTS.md 已注明。
- **WinUI 3 冷启动比 WPF 慢**（CmdPal 实测 2.84s），靠常驻 cloak 抵消；不要为冷启动速度纠结。
- **CmdPal 内存实测**：UI 进程 200.8MB + 扩展宿主 50.8MB，是 NFR-01 放宽到 300MB 的依据。
