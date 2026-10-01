# ADR-0019. 托盘图标与右键菜单：H.NotifyIcon.WinUI + Fluent MenuFlyout

日期：2026-10-01
状态：Accepted

## 背景

R6 托盘的初版是 `TrayIconService`（WinForms `NotifyIcon` + `ContextMenuStrip` + 自写 `ProfessionalColorTable` 深色表）。用户实测反馈「原生右键菜单太丑」：`ContextMenuStrip` 走 `ToolStripProfessionalRenderer`，没有 WinUI 的圆角、亚克力材质与主题过渡，与 `Sox.App` 的卡片材质风格割裂。

调研参考项目 `E:\Code\snow-apps`（Qt/C++）：它的托盘菜单用自研组件库 `ant_design_qt` 的 `AdContextMenu`（继承 `QMenu`）**完全自绘**（`QProxyStyle` + `paintEvent` 画圆角/hover/图标列/分隔线/危险项，配色走 design token）。思路可借鉴（不要用框架自带菜单渲染），但 Qt 的 `QMenu`/`QProxyStyle` 无法移植到 WinUI 3。

本轮目标：托盘图标保留，右键菜单换成 **WinUI 原生 Fluent 观感**，并沉淀成可复用的方案。

## 决策

**托盘图标改用 `H.NotifyIcon.WinUI` 的 `TaskbarIcon`，右键菜单用原生 `MenuFlyout`，通过 `ContextMenuMode.SecondWindow` 在独立 WinUI 窗口里渲染。**

不再使用 WinForms `NotifyIcon`/`ContextMenuStrip`（仅 `System.Drawing` 图标处理仍保留在 `ShellIconProvider`，与本决策无关）。

### 为什么是 H.NotifyIcon.WinUI

- 纯 WinUI 实现，不依赖 WinForms 控件；自带 `Shell_NotifyIcon` 消息窗口、任务栏重建（Explorer 重启）恢复、DPI 变化重载图标、Efficiency Mode。
- `ContextFlyout` 直接吃标准 `MenuFlyout`，因此菜单是**真 WinUI 菜单**（圆角、亚克力、主题、键盘导航、`MenuFlyoutSeparator`、图标），不是自绘也不是 Win32 菜单。
- MIT 许可（`H.NotifyIcon` + `H.NotifyIcon.WinUI`），依赖 `Microsoft.WindowsAppSDK >= 1.6`，与 Sox 的 WindowsAppSDK 2.2 兼容。

### 三种菜单模式

`TaskbarIcon.ContextMenuMode` 有三档：

| 模式 | 渲染 | 观感 | 说明 |
|---|---|---|---|
| `PopupMenu`（库默认） | 库自建的 Win32 `PopupMenu` | 原生 Win32 菜单 | 支持 `ContextMenuThemeMode` 深浅色，但不带 WinUI 圆角/材质 |
| `SecondWindow` | **独立 WinUI 窗口里的 `MenuFlyout`** | **Fluent（圆角 + 材质 + 动效）** | Sox 采用。库标注为 preview |
| `ActiveWindow` | 宿主窗口角落的 `MenuFlyout` | Fluent | 位置不对齐托盘，不适用 |

选 `SecondWindow`：它是唯一能得到「Fluent 菜单」观感的模式。库为它建一个无边框、透明、置顶、圆角的独立窗口，把 `ContextFlyout` 的项搬进去，用 `MenuFlyout` 展示。

### 实现（Sox 版）

**1. 包引用**（`src/Sox.App/Sox.App.csproj`）

```xml
<PackageReference Include="H.NotifyIcon.WinUI" Version="2.5.0-beta.3" />
```

> 版本说明：初版用 2.4.1。2.4.1 的 `SecondWindow` 有「首次弹出宽度偏窄、文字截断」的缺陷（见下「坑」第 2 条），2.5.0-beta.1 起上游重写该模式（预热测量 + 主题属性 + 多屏定位），故升到 2.5.0-beta.3。它是预览版；若介意，可等 2.5.0 正式版。

**2. 托盘资源**（`src/Sox.App/TrayMenu.xaml`，`ResourceDictionary`，在 `App.xaml` 合并）

```xml
<tb:TaskbarIcon x:Key="SoxTrayIcon"
    ContextMenuThemeMode="System"
    IconSource="ms-appx:///Assets/sox-tray.ico"
    NoLeftClickDelay="True" ToolTipText="Sox" Visibility="Collapsed">
    <tb:TaskbarIcon.ContextFlyout>
        <MenuFlyout AreOpenCloseAnimationsEnabled="False">
            <MenuFlyoutItem FontSize="13" Text="打开搜索框">
                <MenuFlyoutItem.Icon><FontIcon FontSize="14" Glyph="&#xE721;" /></MenuFlyoutItem.Icon>
            </MenuFlyoutItem>
            <MenuFlyoutSeparator />
            ...
        </MenuFlyout>
    </tb:TaskbarIcon.ContextFlyout>
</tb:TaskbarIcon>
```

要点：
- 初始 `Visibility="Collapsed"`：由 `TrayIconService.Show()` 按 `UserSettings.HideTrayIcon` 决定，避免启动瞬间闪现。
- `FontSize="13"`：`MenuFlyoutItem` 默认 14，偏大；图标同步 14。
- `AreOpenCloseAnimationsEnabled="False"`：`SecondWindow` 模式下该值会改变关闭时序，置 false 最稳。
- `ContextMenuThemeMode="System"`：菜单跟随系统深浅色（2.5.0-beta.3 起可用）。
- 图标用**多尺寸 `.ico`**（Sox 的 `sox-tray.ico` 含 16/20/24/32/40/48/64/128/256），库按托盘 DPI 选尺寸，避免模糊。

**3. 控制器**（`src/Sox.App/Services/TrayIconService.cs`）

```csharp
var icon = (TaskbarIcon)Application.Current.Resources["SoxTrayIcon"];
icon.RequestedTheme = _theme;                       // 主题跟随
icon.ContextMenuMode = ContextMenuMode.SecondWindow; // Fluent 菜单
icon.LeftClickCommand = new RelayCommand(() => OpenRequested?.Invoke());
icon.NoLeftClickDelay = true;                        // 左键立即响应，不等双击判定
// 按索引给 MenuFlyoutItem 挂 Click
icon.ForceCreate(enablesEfficiencyMode: false);      // 立即注册图标
```

- 显隐：`icon.Visibility = Visible/Collapsed`。
- 主题：`ApplyTheme(ElementTheme)` 由 `MainWindow.ApplyTheme()` 在主题变化时调用（`TaskbarIcon.RequestedTheme` 会传导到 `SecondWindow` 的菜单窗口）。
- 退出：`Visibility=Collapsed` + `Dispose()`。
- `ForceCreate(enablesEfficiencyMode: false)`：Sox 常驻且需要即时呼出，不启用 Efficiency Mode（省电但会降低调度优先级）。

### 复用到其它应用的清单

1. 引 `H.NotifyIcon.WinUI`（≥2.4.1，需 WindowsAppSDK ≥1.6）。
2. 准备多尺寸 `.ico` 放 `Assets/`。
3. 新建 `TrayMenu.xaml`（`ResourceDictionary`），声明 `TaskbarIcon` + `ContextFlyout`；在 `App.xaml` 的 `MergedDictionaries` 合并。
4. 写一个 `TrayIconService`：`Show(bool)`、`ApplyTheme(ElementTheme)`、事件、`Dispose`；`ContextMenuMode=SecondWindow`、`ForceCreate(false)`。
5. 左键呼出用 `LeftClickCommand` + `NoLeftClickDelay=true`；右键菜单项用 `Click` 事件或 `Command`。
6. 主题变化时把 `RequestedTheme` 赋给 `TaskbarIcon`。

## 坑与注意

1. **必须按平台/RID 构建**。`WindowsAppSDKSelfContained=true` + `AnyCPU` 会报 `WindowsAppSDKSelfContained requires a supported Windows architecture`。用 `-p:Platform=x64 -p:RuntimeIdentifier=win-x64`（或 arm64）。此坑与托盘库无关，是 Sox 的 WinUI 自包含约束。
2. **首次弹出宽度偏窄（2.4.1 的缺陷，已由升级修复）**。2.4.1 的 `SecondWindow` 在未预热的 Flyout 上 `Measure`：`item.Measure` 对从未加载的 `MenuFlyoutItem` 返回 0 尺寸，窗口按偏窄尺寸定位；项被宿主后才加载并撑开，所以是「首次窄、之后正常」。2.5.0-beta.1 起上游重写该模式（先 `EnsureSecondWindowContextMenuLoaded` 让 Flyout 加载并测一次，再算窗口尺寸，并加了 tray 排除矩形与 `Top`/`Bottom` 定位），此缺陷消失。**这是本项目从 2.4.1 升到 2.5.0-beta.3 的直接原因。**
2. **`ContextMenuThemeMode` 需 2.5.0-beta 起**。2.4.1 没有该属性；2.4.1 下只能给 `TaskbarIcon.RequestedTheme` 赋 `ElementTheme` 让菜单跟随主题（`SecondWindow` 里 `frame.RequestedTheme = ActualTheme`）。2.5.0-beta.3 起可用 `ContextMenuThemeMode="System|Light|Dark"` 直接控制。
3. **`SecondWindow` 是 preview**。库注释明确「need full testing on various systems, including Windows 10」。多屏 / 高 DPI / 任务栏在侧边或顶部时可能出现定位或残影，需实测（2.5.0-beta.3 已补 tray 排除矩形与上下定位，但仍属 preview）。
4. **菜单项高度由库强制 32**。`SecondWindow` 里对每个非分隔项 `Height=32, Padding=(11,0,11,0)`；要更紧凑只能改字号或换模式。
5. **点击项后菜单自动关闭**（库默认；2.5.0-beta.3 起可用 `CloseContextMenuOnItemClick=false` 关闭该行为）。
6. **图标资源 URI**：`IconSource` 用 `ms-appx:///Assets/xxx.ico`；库内部转 `System.Drawing.Icon` 需要 `System.Drawing.Common`（WinUI 项目里由 WindowsDesktop 框架提供，Sox 已 `FrameworkReference Microsoft.WindowsDesktop.App.WindowsForms`）。
7. **资源键复用**：`TaskbarIcon` 从 `Application.Current.Resources` 取，是单例对象；多次 `Show(true)` 不要重复 `ForceCreate`。

## 备选（未采用）

- **WinForms `NotifyIcon` + 自绘 `ToolStripRenderer`**：无法做出 Fluent 圆角/材质，且与 WinUI 视觉割裂，即被替换的初版。
- **`PopupMenu` 模式**：仍是 Win32 菜单观感，只是支持深浅色。作为 `SecondWindow` 出问题时的回退。
- **路线二：自绘无边框 `Popup`/独立窗 + Win2D**（对齐 snow-apps 的 `AdContextMenu`）：观感最接近，但需自己实现 hover/键盘/定位/多屏，工作量大。当前 `SecondWindow` 已满足需求，暂不做。

## 后果

- 托盘右键菜单获得 WinUI Fluent 观感，与 `Sox.App` 卡片材质一致。
- 移除 WinForms 托盘渲染代码；`Sox.App.csproj` 新增一个 MIT 依赖。
- 菜单外观由 XAML `MenuFlyout` 声明，后续改菜单项只需改 `TrayMenu.xaml`。
- 该方案与业务无关，可直接复用到其它 WinUI 3 应用（见上方复用清单）。

## 关联

- 细化 ADR-0016「托盘 / 单实例 / 热键 → WinUI 3 等价实现」中托盘一项（选型落定为 `H.NotifyIcon.WinUI`）
- 相关代码：`src/Sox.App/TrayMenu.xaml`、`src/Sox.App/Services/TrayIconService.cs`、`src/Sox.App/App.xaml`、`src/Sox.App/MainWindow.xaml.cs`（`ApplyTheme`）
- 参考调研：`E:\Code\snow-apps`（Qt `ant_design_qt/AdContextMenu`，自绘思路，不可移植）
- 上游：`HavenDV/H.NotifyIcon`（MIT），本 ADR 对应 2.5.0-beta.3（2.4.1 因 `SecondWindow` 首次宽度缺陷被替换）
