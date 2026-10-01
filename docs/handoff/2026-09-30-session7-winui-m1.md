# Handoff：WinUI 3 前端 M1 骨架（会话七）

日期：2026-09-30
状态：M1 骨架完成，可构建

## 会话目标

清掉旧 WPF 前端，按 ADR-0016 从零重建 `Sox.App` 为 WinUI 3，实现 M1 骨架（常驻 cloak 窗口 + 卡片材质 + 基础搜索）。

## 本轮完成

### 清目录

- 停掉 Sox.App / SoxService 进程（避免文件锁）
- 旧 `src/Sox.App` 全部删除（用户确认不保留）
- 源文件备份在 `%TEMP%\opencode\sox-app-backup`（56 个文件，仅源文件，非 git 追踪）

### 新建 WinUI 3 项目

| 文件 | 内容 |
|---|---|
| `Sox.App.csproj` | TFM `net10.0-windows10.0.26100.0`，`UseWinUI=true`，`WindowsPackageType=None`（非打包），`WindowsAppSDKSelfContained=true`，`DISABLE_XAML_GENERATED_MAIN` |
| `Program.cs` | 自定义入口 + 单实例 Mutex |
| `App.xaml(.cs)` | 应用入口，`OnLaunched` 建 MainWindow |
| `MainWindow.xaml(.cs)` | `WindowEx` 无边框透明 + 常驻 cloak + 防抖搜索 |
| `Controls/CardControl.xaml(.cs)` | 卡片：`SystemBackdropElement` + `ThemeShadow` + 圆角 8 |
| `Controls/TintedControllerBackdrop.cs` | 材质引擎（抄 CmdPal，简化） |
| `Services/BackdropStyles.cs` | 5 种材质配置表（Acrylic/AcrylicThin/Mica/MicaAlt/Clear） |
| `Services/BackdropParameters.cs` | 材质参数记录 |
| `Services/ThemeService.cs` | 明暗解析 + 材质参数生成 |
| `Services/SearchHost.cs` | 服务拉起 + SearchService 持有 + 搜索 |
| `app.manifest` | PerMonitorV2 DPI |
| `.vscode/tasks.json` + `launch.json` | 更新为 `-r win-x64` 与 RID 输出路径 |

### 依赖版本（实测可用）

- `Microsoft.WindowsAppSDK` 2.2.0
- `Microsoft.Windows.SDK.BuildTools` 10.0.26100.4654（2.2.0 要求 ≥ 此版本）
- `WinUIEx` 2.8.0
- `CommunityToolkit.WinUI.Controls.SettingsControls` / `Animations` 8.2.250402
- `Microsoft.Graphics.Win2D` 1.3.2

### 构建结果

`dotnet build src/Sox.App/Sox.App.csproj -r win-x64` → **成功，0 warning 0 error**。

## 本轮重要发现（推翻 ADR-0016 的一个担忧）

**WinUI 3 项目可以直接引用含 WPF 的 `Sox.PluginSdk`，无需改造。**

实测：WinUI 3 项目（`UseWinUI=true`）引用 `UseWPF=true` 的 `Sox.PluginSdk`，并实际调用其无 WPF 类型（`StartMenuShortcutResolver`、`HistoryEntryKind`），编译零错误。

原因：`System.Windows` 类型冲突只在**同一编译单元同时引用两套同名类型**时发生；程序集引用本身不冲突。

ADR-0016 的「PluginSdk WPF 面」小节已更新为「选 C 即可，无需改造」。唯一注意点：将来实现插件预览/图标（`IFilePreviewProvider` 返回 WPF `UIElement`、`IThumbnailProvider` 返回 WPF `ImageSource`）时，那 2-3 个接口需局部中立化。

## 当前状态

M1 骨架已可构建、可运行。窗口逻辑：启动即 cloak（隐藏），Alt+Space 呼出；搜索框防抖 150ms 接 `SearchService.SearchStreamingAsync`；结果列表结构化（图标 + 名称 + 路径 + 高亮）；↑↓/Enter/Esc 键盘导航；失焦自动隐藏。

## 本轮后续补充（M1 收尾）

| 项 | 实现 |
|---|---|
| 全局热键 | `Interop/SummonHotkey.cs`（低层键盘钩子，Alt+Space，吞键，回调 marshal 到 DispatcherQueue） |
| 抢前台 | `Interop/ForegroundHelper.cs`（`AllowSetForegroundWindow` + `AttachThreadInput`，避开 IME 崩溃） |
| 呼出/隐藏 | `SetCloaked` 开关 + 居中定位（工作区 22% 高度） |
| 失焦隐藏 | `Window_Activated` 的 `Deactivated` 分支 |
| 结果列表 | `ViewModels/ResultItem.cs`（结构化），`ListView` 虚拟化，图标 + 高亮 |
| Shell 图标 | `Services/ShellIconProvider.cs`（`SHGetFileInfo` + `IShellItemImageFactory` 缩略图，按扩展名/路径缓存，用 `System.Drawing` → PNG → `BitmapImage`） |
| 匹配高亮 | `Controls/TextHighlighter.cs`（附加属性方案，WinUI `TextBlock` 是 sealed 不能继承；复用核心 `FuzzyQuery`，与排序口径一致） |
| 托盘 | `Interop/TrayIcon.cs`（纯 P/Invoke `Shell_NotifyIcon` + 自建消息窗口 + `TrackPopupMenu`；避免 WinForms 框架冲突） |
| 主题 | `Themes/SoxTheme.xaml`（Dark/Light `ThemeDictionaries`），跟随系统 |
| 窗口图标 | `AppWindow.SetIcon` |

**注意**：WinUIEx 的 `IsVisibleInTray` 语义是"最小化到托盘"，与 Sox 的 cloak 常驻不匹配，故未采用，改自写 `TrayIcon`。

## 下一步（M2）

1. **材质切换验证**：确认 5 种材质实际渲染效果（需真机观察）
2. **设置面板**：热键 / 目录排除 / 主题 / 材质 / 索引盘（接 `UserSettings` 全字段）
3. **内嵌 mini 窗**（F-13）：同架构第二个 WinUI 3 窗口 + hook 集成
4. **Provider 迁移**（F-21）：把旧 `QueryProviders/`（计算器/网页/应用/命令/窗口/剪贴板）迁到 WinUI 3
5. **索引管理 UI**（F-17）：接 `SearchServiceManagementExtensions`
6. **Everything IPC 宿主**：重实现 `EverythingIpcHost`
7. **验证待定项**：非打包模式下 Windows App SDK 运行时分发（当前自包含，已可运行）

## 关键坑（本次新增）

- **`DISABLE_XAML_GENERATED_MAIN`**：自定义 `Program.cs` 时必须加，否则与自动生成入口冲突（`CS0101`）
- **XAML 根元素必须与 code-behind 基类一致**：`WindowEx` 的 XAML 根要写 `<winuiex:WindowEx>`，否则 `CS0263`
- **WinUI 3 的 `TextBlock` 是 sealed**：不能继承做自定义控件，高亮改用附加属性
- **`DispatcherQueueTimer` 命名冲突**：`Microsoft.UI.Dispatching` 与 `Windows.System` 都有，需完全限定
- **`WindowsAppSDKSelfContained=true`**：非打包 exe 需要，否则运行时找不到 Windows App SDK
- **`-r win-x64`**：WinUI 3 项目必须指定 RID，输出路径含 RID 段
- **运行中无法构建**：Sox.App 锁定自己的 exe，build 前必须停进程（VSCode 的 `stop-sox` 任务已覆盖）
- **服务 `ImagePath` 指向旧路径**：换输出目录后需 `--uninstall` + `--install` 重装服务
- **数据目录是便携模式**：`Data\` 在 exe 目录下，不在 `%ProgramData%`
- **`SearchResult` 在 `Sox.Core` 命名空间**，不在 `Sox.Core.Services.Search`
- **PowerShell 不支持 heredoc**：写文件用 write 工具，别用 `cat > ... << EOF`
- **旧崩溃日志 `%TEMP%\sox_crash.txt` 会误导**：可能残留旧 WPF 版本的堆栈，排查前先删
