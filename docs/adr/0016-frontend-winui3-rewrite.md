# ADR-0016. 前端重写：WPF → WinUI 3（对齐 CmdPal 的卡片材质架构）

日期：2026-09-30
状态：Accepted

## 背景

ADR-0001 选择 WPF 的核心约束是「常驻内存 < 100MB」。在此约束下，Sox 前端（`Sox.App`，6,397 行）走的是 WPF + 手写 DWM 材质路线：`WindowBackdrop` 直接写 `DWMWA_SYSTEMBACKDROP_TYPE` 做整窗亚克力，圆角靠 DWM + XAML `Clip` 双保险。

这套路线在实测中暴露了三个结构性问题：

1. **材质只能整窗，无法只填卡片**。CmdPal 那种「卡片外透明 + 卡片内毛玻璃 + 自绘阴影」的效果，WPF 拿不到 `SystemBackdropElement`，做不到。
2. **复用窗口材质会残留**。`App.xaml.cs` 里被迫写成「每次呼出新建 `SearchWindow`」，因为 DWM 会缓存复用窗口的合成结果。这直接损害了呼出速度（NFR-03），也让材质参数化（多材质切换）无从谈起。
3. **手搓 DWM 的成本高且脆弱**。`WindowBackdrop` 用 `WM_NCACTIVATE` + `RDW_FRAME` + 定时器重断言对抗 DWM 的 inactive 灰色缓存，还维护 `ConditionalWeakTable` 防闪烁——这些都是 WPF 不暴露 `SystemBackdropConfiguration` 的补偿性代码。

同期调研了 `microsoft/PowerToys` 的两个 spotlight 式实现（见 `docs/reference/powertoys-spotlight-material.md`）：

- **PowerToys Run**（WPF）：2024-12 提交 `7c6af6580e` 移除了 WPF-UI，现在源码里已无任何材质 API，只设圆角，材质交给系统。
- **CmdPal**（WinUI 3 / Windows App SDK 2.2）：`SystemBackdropElement` + 自定义 `TintedControllerBackdrop`（`MicaController` / `DesktopAcrylicController`），材质只填圆角卡片，阴影自绘，5 种材质可参数化，激活态一行代码处理。

CmdPal 证明了 WinUI 3 能做出 Sox 想要的观感，且其实现已工程化验证（含动态切材质的终结器线程、controller→brush 交接等坑的解法）。

用户已明确：**接受全盘推翻前端重写，功能可迁移或重写，可用更现代的 UI 与框架，ADR 与 PRD 可重写，放宽内存限制。**

## 决策

**保留后端，重写前端为 WinUI 3 + Windows App SDK，采用 CmdPal 式的卡片材质架构。**

### 保留（不改动）

| 项目 | 行数 | 说明 |
|---|---|---|
| `Sox.Core` | 36,700 | 零 WPF 依赖（无 `using System.Windows`），USN/MFT 索引、fzf、拼音、Wire、SearchService 全部保留 |
| `Sox.Service` | 432 | Windows 服务宿主，无 UI |
| `Sox.PluginSdk` | 4,847 | 契约与逻辑保留，但**有 13 个文件带 `using System.Windows`**（约 515 行），需处理，见下方「PluginSdk 的 WPF 面」 |
| `Plugins/FileDialog` | 946 | 对话框适配，无 UI |
| `Plugins/PinyinAlias` | 1,288 | 拼音别名，无 UI |

#### PluginSdk 的 WPF 面（需处理，非「仅 300 行」）

`Sox.App` 直接 `ProjectReference` 了 `Sox.PluginSdk`。一旦 `Sox.App` 切到 `UseWinUI=true`，两边引用的 `System.Windows` 命名空间（WPF 版 `ImageSource` / `UIElement` / `ResourceDictionary` / `MessageBox`）会产生编译期类型冲突。带 WPF 依赖的文件（行数）：

| 文件 | 行数 | WPF 依赖 | 处置 |
|---|---|---|---|
| `Abstractions/ITheme.cs` | 10 | `ResourceDictionary` | 改中立抽象或 WinUI 版 |
| `Abstractions/ISearchResultAction.cs` | 36 | `ImageSource` | 改中立图标抽象 |
| `Abstractions/Plugins/Preview/IFilePreviewProvider.cs` | 25 | `UIElement` | 改 WinUI `UIElement` |
| `Abstractions/Plugins/Preview/IThumbnailProvider.cs` | 18 | `ImageSource` | 同上 |
| `Helpers/VectorIconHelper.cs` | 44 | `ImageSource` / `DrawingImage` / `Geometry` | 改 `PathIcon` / `Geometry` |
| `Services/IconService.cs` | 42 | `ImageSource` | 同上 |
| `Services/PluginMessageBoxService.cs` | 23 | `MessageBox` 系列 | 改 ContentDialog |
| `Services/PluginPreviewCache.cs` | 83 | `UserControl` / `UIElement` | 改 WinUI 元素 |
| `Services/ThemeService.cs` | 36 | `Application.Current.Resources` | 仅回退路径，赋 `IsDarkThemeFunc` 可绕开 |
| `Shell/FileOperations/ShellOperationStaWorker.cs` | 51 | WPF `Dispatcher` | 改裸 `Thread` + `GetMessage` |
| `Windows/PluginWindow.xaml.cs` | 55 | `Window` / XAML | 插件窗口壳，重写 |
| `Windows/PluginWindowClip.cs` | 32 | `DependencyProperty` / `RectangleGeometry` | 改 `CornerRadius` |
| `Windows/PluginWindowNativeBehavior.cs` | 60 | `HwndSource` / `WindowInteropHelper` | Win32 部分留，WPF 部分换 |

**处理策略（已实测验证，见下）**：

- A. `Sox.PluginSdk` 拆成 `PluginSdk`（中立契约）+ `PluginSdk.Wpf`（WPF 实现）两个项目；WinUI 前端只引前者。
- B. 把这 13 个文件改为中立抽象（图标用 `byte[]`/SVG path 字符串，预览用接口，消息框走服务注入）。
- C. 保持 PluginSdk 原样（含 WPF），WinUI 前端正常引用它，但不使用其中的 WPF 类型。

**实测结论（2026-09-30，M1 验证）**：**选 C 即可，无需改造 PluginSdk。**

M1 骨架构建时实测：WinUI 3 项目（`UseWinUI=true`）引用含 `UseWPF=true` 的 `Sox.PluginSdk`，并实际调用其无 WPF 类型（`StartMenuShortcutResolver`、`HistoryEntryKind`），`dotnet build` **编译通过、零错误**。

原因：`System.Windows` 命名空间的类型冲突只在**同一编译单元内同时引用两套同名类型**时发生；程序集引用本身不冲突。只要 `Sox.App` 不实际使用 `ImageSource` / `UIElement` / `ResourceDictionary` / `MessageBox` 这些 WPF 类型，引用 `Sox.PluginSdk` 就没有问题。

现有 `Sox.App` 实际用到的 PluginSdk 类型只有 `ShellDeleteHelper`、`ShellRenameHelper`、`StartMenuShortcutResolver`、`HistoryEntryKind`——**全部无 WPF 依赖**。

**唯一注意点**：将来若 WinUI 前端要实现插件预览/图标（`IFilePreviewProvider` 返回 `UIElement`、`IThumbnailProvider` 返回 `ImageSource`），那些接口签名里的 WPF 类型就无法在 WinUI 侧实现——届时按需对这 2-3 个接口做中立化（策略 B 的局部应用），不必整体改造。

### 重写（Sox.App，约 6,400 行）

| 模块 | 新方案 |
|---|---|
| 框架 | WinUI 3 + Windows App SDK 2.x（`net10.0-windows10.0.26100.0`） |
| 主搜索窗 | `WindowEx`（WinUIEx）无边框透明窗 + `CmdPalMainControl` 式卡片（`SystemBackdropElement` 材质 + `ThemeShadow` 阴影） |
| 窗口生命周期 | **常驻 + DWM cloak**（不再每次新建窗口），呼出仅 cloak/uncloak + 抢前台 |
| 材质 | `BackdropStyles` 配置表 + `TintedControllerBackdrop` 式控制器（Acrylic / AcrylicThin / Mica / MicaAlt / Clear） |
| 主题 | Provider + 资源字典 + 快照原子替换（抄 CmdPal `ThemeService` 结构） |
| 内嵌 mini 窗 | 同架构的第二个 WinUI 3 窗口（ADR-0012 交互不变，实现重写） |
| 设置窗 | WinUI 3 `SettingsCard`（CommunityToolkit.WinUI.Controls.SettingsControls） |
| 托盘 / 单实例 / 热键 | WinUI 3 等价实现（WinUIEx `TrayIcon` 或 Shell_NotifyIcon P/Invoke） |

### 打包

两种形态都提供：

1. **非打包 exe**（`WindowsPackageType=None`）：保持现有「服务 + 裸 exe」部署，自管 Windows App SDK 运行时。
2. **MSIX**（CmdPal 同款）：安装/更新干净，自动处理运行时依赖。

### 复用 CmdPal 的部分

- **可移植**：`BackdropStyles` / `BackdropStyleConfig` / `BackdropParameters` 参数化抽象（纯数据，无 WinUI 耦合）、设计 token（圆角 8、行高 48、基色 `#202020`/`#F3F3F3`）。
- **可参考不可移植**：`TintedControllerBackdrop`（依赖 PowerToys 宿主 DI 与 `ICompositionSupportsSystemBackdrop`）、`MainWindow` 的 cloak/命中测试逻辑。
- **不引入**：CmdPal 的 extension SDK、DI 容器、AdaptiveCards、遥测体系——Sox 不需要。

## 后果

### 放宽的约束

- **NFR-01 内存**：从「< 100MB」放宽到「前端常驻 < 300MB」。依据：CmdPal UI 进程实测 200.8MB（含扩展宿主 50.8MB）；Sox 前端预计 200-280MB。核心索引内存预算不变。
- **NFR-07 发布**：从「自包含单文件 exe」改为「非打包 exe 或 MSIX」，不再强求单文件。

### 新增约束

- **冷启动**：WinUI 3 冷启动比 WPF 慢（CmdPal 实测 2.84s）。由「常驻 + cloak」抵消，用户日常感知的呼出延迟目标是 < 100ms。
- **构建**：需 Windows App SDK，VSCode 调试配置需调整。
- **UI 全部重写**：前端 6,400 行归零，按功能清单逐项迁移。

### 不变的部分

- 三进程架构（SoxService / Sox.App / hook）不变
- Wire 协议、命名管道、SearchService 调用方式不变
- 核心搜索与排序行为不变
- 插件契约（`IPlugin` 等）不变

## 关联

- **取代 ADR-0001**（C# + WPF 的技术栈选择）
- **取代 ADR-0012**（内嵌窗：交互保留，WPF 实现作废）
- **取代 ADR-0014**（WPF-UI 回退决策：WPF 栈整体废弃，该决策失去意义）
- 不影响 ADR-0002 / 0003 / 0005 / 0006 / 0009 / 0013（核心索引、搜索、排序）
- 依据调研：`docs/reference/powertoys-spotlight-material.md`
- PRD 需同步重写（前端相关功能项与非功能需求）

## 待定（实施阶段再定，不影响本决策）

1. WinUI 3 的托盘图标方案（WinUIEx vs 自写 `Shell_NotifyIcon`）
2. 插件窗口壳（`PluginWindow`）在 WinUI 3 下的形态
3. hook 进程与 WinUI 3 前端的 IPC 是否需调整（预期不变）
4. 非打包模式下的 Windows App SDK 运行时分发方式
