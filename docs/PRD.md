# PRD：Sox 文件搜索工具

> 版本：v2（2026-09-30 重写）
> 变更摘要：前端由 WPF 重写为 WinUI 3（ADR-0016），放宽内存与发布形态限制，补齐后端已具备但前端未暴露的能力。v1 见 git 历史。

## 背景

现有 Windows 搜索工具的两难：

- Listary：交互智能（全局呼出、历史学习、对话框集成）但常驻内存大且收费
- Everything：索引速度天花板、界面丑陋

目标：做一款自用工具，兼得 Listary 的交互与 Everything 的速度，界面走 Fluent / Spotlight 风格。

## 用户与模式

- 用户：作者本人，单人自用
- 开发模式：用户观察 + Agent 实时修改（构建闭环）
- 平台：Windows 10 / 11，NTFS 卷

## 核心场景

1. Alt+Space 呼出悬浮框，输入即出结果，Enter 打开
2. 中文拼音缩写匹配（`wxg` → 微信）
3. 保存/打开对话框内快速选取文件
4. 资源管理器右键菜单集成
5. 历史学习：越用越准的排序

## 技术基线（v2）

| 项 | 内容 |
|---|---|
| 运行时 | .NET 10 LTS |
| 后端 | `Sox.Core`（零 UI 依赖，36,700 行，原样复用）+ `Sox.Service`（Windows 服务） |
| 前端 | `Sox.App` 重写为 **WinUI 3 + Windows App SDK 2.x** |
| 打包 | 非打包 exe 与 MSIX 两种形态 |
| 进程 | 三进程：`SoxService`（索引/搜索）· `Sox.App`（UI）· hook（`Sox.Service --hook`，全局键盘/对话框） |

详见 ADR-0016、`docs/reference/backend-api-catalog.md`、`docs/reference/powertoys-spotlight-material.md`。

## 功能需求

### P0（必须）

| ID | 需求 | 后端支撑 |
|---|---|---|
| F-01 | 全局热键 Alt+Space 呼出 / 隐藏悬浮窗（可配置） | hook / `SummonHotkey` |
| F-02 | Spotlight 式居中悬浮框，**卡片圆角 + 卡片内材质（Acrylic/Mica 可切换）+ 自绘阴影**，跟随系统主题 | WinUI 3 `SystemBackdropElement` |
| F-03 | 实时搜索：输入即触发，防抖 + 取消 + 后台线程，虚拟化渲染 | `SearchService.SearchStreamingAsync` |
| F-04 | 全盘 NTFS 文件名索引：USN Journal 增量 + 首次 MFT 全扫 | `Sox.Core` 索引引擎（已具备） |
| F-05 | 拼音匹配：首字母 + 全拼前缀 | `PinyinAlias` 插件（已具备） |
| F-06 | 智能排序：相关度硬门槛 + 历史行为加权 + 时间衰减 | `SearchResultRankComparer` + `SearchHistoryStore` |
| F-07 | 键盘导航：↑↓ 选择，Enter 打开，Esc 关闭，失焦自动隐藏 | 前端 |
| F-08 | 空输入显示最近打开 / 收藏 / 常用目录 | `GetRecentFilesAsync` + `Favorites` |
| F-09 | 托盘常驻 + 单实例（Mutex）；崩溃守护重启（**当前未实现，待补**） | `SingleInstanceForwarder` |
| F-10 | 索引缓存落盘，重启快速恢复 | `Sox.Core`（已具备） |
| F-11 | **窗口常驻 + DWM cloak 显隐**：呼出到可见 < 100ms（不再每次新建窗口） | 前端架构 |
| F-12 | 材质可配置：Acrylic / AcrylicThin / Mica / MicaAlt / 纯色，可调透明度 | 前端（抄 CmdPal `BackdropStyles`） |

### P1（重要）

| ID | 需求 | 后端支撑 |
|---|---|---|
| F-13 | 保存/打开对话框内自动出现贴合对话框的 mini 搜索窗，选中后注入路径；Quick Switch 列出已打开目录一键跳转 | hook + `IFileDialogAdapter`（已具备） |
| F-14 | 资源管理器右键菜单集成（**现有实现用 `HKCU\Software\Classes`，免提权**；ADR-0007 原方案的 HKCR 需提权，重写时沿用 HKCU 方案） | `ShellIntegration`（已具备） |
| F-15 | 设置面板：热键 / 目录排除 / 主题 / 材质 / 索引盘 / 插件 | `UserSettings`（全字段已具备） |
| F-16 | DebugOverlay 性能面板（F12） | 前端 |
| F-17 | 索引管理 UI：单盘重建/删除/取消、索引状态展示、网络盘与 WSL 配置 | `SearchServiceManagementExtensions` + `MachineSettings` |
| F-18 | 结果预览面板：图片 / 文本 / 缩略图 | `IThumbnailProvider` 契约 |
| F-19 | 历史与关键词历史管理 UI（查看/删除） | `SearchHistoryStore` / `KeywordHistoryStore` |

### P2（可选）

| ID | 需求 | 后端支撑 |
|---|---|---|
| F-20 | 收藏/固定（Ctrl+数字） | `FavoriteItemSetting` |
| F-21 | 命令执行 / 网页搜索 / 计算器 / 应用搜索 / 窗口切换 / 剪贴板历史 | ADR-0015 Provider（已具备） |
| F-22 | 输入历史联想（同词优先上次选择） | `KeywordHistoryStore` |
| F-23 | Everything IPC 兼容（第三方客户端可连入查询） | `EverythingIpcServer`（已具备） |
| F-24 | LocalSend 局域网传输 | `LocalSendServiceManager`（已具备） |
| F-25 | Quick Panel（工作区标签 + 文件夹源） | `QuickPanelSettings`（已具备） |
| F-26 | 自动更新（检查 + 下载 + 应用） | `UpdatePackage` / `RequestApplyUpdateAsync`（落盘具备，下载端需补） |
| F-27 | 插件系统：加载 `Plugins/` 下的扩展，暴露 15+ `I*Provider` 能力 | `Sox.PluginSdk`（契约已具备，**前端加载器需新建**） |

## 非功能需求（v2 放宽）

| ID | 需求 | v1 | v2 |
|---|---|---|---|
| NFR-01 | 常驻内存 | < 100MB | **前端 < 300MB**（后端索引预算不变） |
| NFR-02 | 全盘匹配查询 | < 50ms | < 50ms（不变） |
| NFR-03 | 呼出到可见 | < 100ms | < 100ms（靠常驻 cloak 达成） |
| NFR-04 | 索引增量实时，零后台轮询 CPU | 不变 | 不变 |
| NFR-05 | 重启恢复 | < 2s | < 2s（不变） |
| NFR-06 | 权限 | 管理员 | 不变（USN 读取需管理员；`Sox.App` 日常搜索无需管理员，仅安装/启动服务时 UAC） |
| NFR-07 | 发布 | 自包含单文件 exe | **非打包 exe 或 MSIX**（不再强求单文件） |
| NFR-08 | 冷启动 | — | 无硬性要求（常驻后用户不感知） |

## 范围外（不做）

- 不搜文件内容全文
- 不支持 FAT / exFAT 卷索引（可用目录扫描降级）
- 不做移动端 / 云端同步 / 多用户

## 里程碑（v2：前端重写）

| 阶段 | 内容 |
|---|---|
| M1 | WinUI 3 骨架：三进程打通、常驻 cloak 窗口、卡片材质（Acrylic/Mica）、基础搜索与结果列表 |
| M2 | 交互打磨：拼音匹配、智能排序、键盘导航、空输入最近列表、主题与材质设置 |
| M3 | 集成：对话框 mini 窗（F-13）、Shell 右键菜单（F-14）、设置面板（F-15）、索引管理（F-17） |
| M4 | 能力补齐：预览面板、历史管理、Provider（F-21）、Everything IPC、Quick Panel |
| M5 | 稳定性 + 内存优化 + 双形态打包（exe / MSIX） |

## 与 v1 的差异

1. **前端技术栈**：WPF → WinUI 3（ADR-0016）
2. **材质**：整窗 DWM 亚克力 → 卡片内 `SystemBackdropElement`，5 种材质可配置
3. **窗口生命周期**：每次新建 → 常驻 + cloak
4. **内存/发布**：放宽（NFR-01 / NFR-07）
5. **功能面扩大**：把后端已具备但前端未暴露的能力（索引管理、预览、历史管理、Everything IPC、Quick Panel、LocalSend、插件系统）纳入需求
