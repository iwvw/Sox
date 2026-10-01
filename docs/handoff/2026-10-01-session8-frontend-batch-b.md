# Handoff：前端体验批次 B（会话八）

日期：2026-10-01
状态：R1–R7 全部实现 + 后端能力盘点优化，构建通过，待用户视觉验收

## 本轮完成（两轮）

### 第一轮（R1–R7）见 session8 初版

### 第二轮（Review + 后端能力接入 + 设置覆盖补全）

| 项 | 实现 |
|---|---|
| 审查修复：SVG 预览 | `ShowPreviewSvg` 用 `SvgImageSource`（BitmapImage 解不了 SVG） |
| 审查修复：材质持久化 | `UserSettings` 新增 `BackdropStyle`/`BackdropOpacity`；`ThemeService.SaveMaterial()`；设置窗口改动即写盘 |
| 最近文件（F-08） | 空输入时 `LoadRecentAsync()`（此前 `GetRecentAsync` 已写好但断链） |
| 索引状态（SearchStatusStream） | MainWindow 订阅状态流，索引进度时显示 28px 提示条（去重日志） |
| 索引管理 UI（F-17） | 设置面板"索引"分组：本地盘列表 + 重建/取消 + 清服务日志（SearchServiceManagementExtensions 接线） |
| Everything IPC（F-23） | 新增 `EverythingIpcHost`，App 进程内 `EverythingIpcServer` + `EverythingSearchDataProvider`，随设置开关启停 |
| 设置覆盖扩展 | 由 9 字段扩到：关键词历史、最大结果数（新 UserSettings.MaxResults）、忽略 globs/regexes、窗口宽度、预览宽度、全局令牌前缀、界面语言 |
| 索引管理后端 | SearchHost 新增 GetMachineSettingsAsync / SaveMachineSettingsAsync / RebuildDriveIndexAsync / DeleteDriveIndexAsync / CancelDriveIndexAsync / ClearServiceLogAsync |

## 后端已具备但本轮未接（预算/合理边界）

- LocalSend（F-24）、QuickPanel（F-25）、QuickLaunch、插件加载器（F-27）、StartMenuShortcutResolver 应用搜索
- 热键子项配置（Hotkeys 全字段）、DefaultFileManagerSetting、BlacklistedProcesses、ShowOpenedFoldersInInlineSearch、EnableHardwareAcceleration
- NetworkDrives/WslSettings/FolderIndexes 配置 UI（需插件目录索引支持）

## 关键坑（本轮新增）

- **`SvgImageSource` 无 `SetSource`**：用 `UriSource` 指向临时 svg 文件
- **`Debug` 标识符冲突**：类内新增 `Log.Debug` 方法后 `Debug.WriteLine` 解析为方法名 → 必须 `System.Diagnostics.Debug.WriteLine`
- **SearchStatusStream 服务端周期性推 ready 状态**：每 ~5s 一次，日志需按 signature 去重
- **`SearchHost` 从 internal 改 public**：设置窗口构造参数需要它（XAML 类型须 public）

## 本轮完成（对照 docs/requirements/batch-b-frontend.md）

| 项 | 实现 |
|---|---|
| R4 真流式上屏 | `SearchHost.SearchStreamingAsync` 改为节流快照（40ms）反复回调；`MainWindow.OnSearchUpdate` 增量消费，结果边到边显 |
| R4 图标异步 | `ShellIconProvider` 新增专用 STA 线程（`IShellItemImageFactory` 是 STA-only，线程池会 `RPC_E_WRONG_THREAD`）；`ResultItem.RequestIcon` 后台出 PNG、UI 线程建 `BitmapImage` |
| R4 增量排序 | 每次快照 `List.Sort` + 截断到 200 条；不再等三源全完成 |
| R1 多屏呼出 | `GetTargetDisplay` 优先 `GetCursorPos` + `DisplayArea.GetFromPoint`，跟随鼠标所在显示器 |
| R2 左右键 | 列表 `←` 打开、`→` 开动作菜单；菜单 `←`/`Esc` 返回；`↑↓` 选择；`Enter` 执行；`Ctrl+数字` 在两种视图下分别打开结果/执行动作 |
| R2 动作菜单 | 结果区替换视图（非 Flyout）：打开、打开所在文件夹、以管理员运行、复制完整路径、复制文件名、固定到收藏 |
| R3 预览面板 | 主卡片右侧合并列（`PreviewColumn`），图片（含 SVG）/文本代码；无预览时整列隐藏，窗口宽度自适应 |
| R3 语法高亮 | `CodeHighlighter`（自研轻量，无大依赖）；`Theme.Normal.xaml` 增 `Sox.Syntax.*` 三主题画刷 |
| R6 托盘 | `TrayIconService`（WinForms `NotifyIcon` + 主题化 `ContextMenuStrip`）；左键呼出；菜单：打开搜索框 / 设置 / 关于·检查更新 / 退出 |
| R5 设置面板 | `SettingsWindow`（`SettingsCard`/`SettingsExpander`）：外观（主题/材质/透明度）、搜索（模糊/或优先/历史/排除目录）、系统（开机/托盘/Everything IPC）、关于 |
| R7 发布裁剪 | `PublishTrimmed/ReadyToRun=false`（XAML 反射会崩）；`TrimPublishLanguages` 目标保留 en-us/zh-CN |

## 关键实现文件

- `Services/SearchHost.cs`：流式快照（`Snapshot` + 脏标志 + 40ms flush 循环）
- `Services/ShellIconProvider.cs`：`StaIconWorker` 专用 STA 线程 + `GetIconPng`/`CreateImage` 分离
- `Services/PreviewService.cs`：类型判定（图片/SVG/文本代码）+ 大小上限（文本 512KB / 20 万字符）
- `Services/CodeHighlighter.cs`：C#/JS/Py/JSON/XML/YAML/MD/CSS/Shell 分词
- `Services/TrayIconService.cs`：托盘 + 深色 `ProfessionalColorTable`
- `SettingsWindow.xaml(.cs)`：设置面板
- `ViewModels/ActionItem.cs`：动作项

## 已验证（客观）

- `dotnet build -r win-x64` 0 warning 0 error
- 流式实测：`toml` 首屏 40ms 出 200 条，112ms 补全
- 图标错误从 58 降到 0（STA 修复）
- 进程常驻约 160MB（NFR-01 < 300MB）

## 待用户视觉验收

1. 左右键交互、动作菜单替换列表
2. 预览面板（图片/代码）与无预览时隐藏
3. 托盘图标与右键菜单主题
4. 设置面板各分组
5. 多显示器呼出跟随鼠标

## 已知简化 / 后续

- 动作菜单暂未含重命名/删除/属性（需交互式输入或额外 COM，本轮先不做）
- 预览暂无音视频元数据、文件夹信息（用户未要求）
- 设置面板未含热键录制、索引管理（可后续补，参考 WSLCC `SettingsPage`）
- 托盘"关于/检查更新"目前打开设置页
- 插件加载器、Everything IPC 宿主、LocalSend、QuickPanel 仍未接入（PRD F-23/F-24/F-25/F-27）

## 关键坑（本轮新增）

- **`IShellItemImageFactory` 是 STA-only**：线程池（MTA）调用抛 `RPC_E_WRONG_THREAD (0x8001010E)`，必须专用 STA 线程
- **`BitmapImage` 必须 UI 线程创建**：COM 出 PNG 字节 → 回 UI 线程 `SetSource`
- **WinUI 3 不能用 `PublishTrimmed`**：XAML 反射依赖，会 `XamlParseException`
- **`ItemsSource=null` 重建列表会闪**：改用 `ObservableCollection` 原位替换
- **`DisplayArea` 是 `IReadOnlyList`，无 `IndexOf`**
