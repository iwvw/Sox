# PowerToys 悬浮窗材质实现分析（对标 Sox Spotlight）

调研对象：`microsoft/PowerToys`（本地快照 `E:\Code\PowerToys`，main @ `58d63e42da`）
调研日期：2026-09-30
目的：为 Sox 悬浮搜索窗（F-02 圆角 + 毛玻璃）提供官方实现参考与差异对照。

---

## 0. 结论速览

PowerToys 有两个"Spotlight 式"入口，材质路线完全不同：

| 模块 | 产品 | 框架 | 材质实现 | 圆角实现 |
|---|---|---|---|---|
| `src/modules/launcher` | PowerToys Run（PT Run） | **WPF**（.NET 10-windows）+ Fluent 资源字典 | **无显式材质 API**（WPF-UI 时代用 `WindowBackdropType.Acrylic`，2024-12 已移除），依赖 DWM 系统默认 | `DWMWA_WINDOW_CORNER_PREFERENCE` |
| `src/modules/cmdpal` | Command Palette（CmdPal） | **WinUI 3 / Windows App SDK 2.2** | **WinUI `SystemBackdrop` + `MicaController` / `DesktopAcrylicController`**，只画在卡片元素上 | XAML `CornerRadius` + `SystemBackdropElement` |

Sox 当前走的是 PT Run 同一条路线（WPF + DWM 整窗亚克力），但比官方激进：官方 PT Run 现在**只设置圆角，材质交给系统**（历史上曾用 WPF-UI 的亚克力，后被移除）；Sox 则直接写 `DWMWA_SYSTEMBACKDROP_TYPE` 主动指定整窗材质。CmdPal 的路线（WinUI 3 + `SystemBackdropElement`）才是"材质只填卡片、阴影自绘"的现代做法，Sox 因为技术栈是 WPF，无法直接照搬。

---

## 1. PowerToys Run（launcher）——WPF 路线

### 1.1 技术栈

- `PowerLauncher`：WPF，`net10.0-windows`（见 `src/modules/launcher/PowerLauncher/PowerLauncher.csproj`）
- 主题：`App.xaml` 合并 `pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml`，即 **WPF 的 Fluent 资源字典**（`TextFillColorPrimaryBrush`、`ControlFillColorDefaultBrush`、`DividerStrokeColorDefaultBrush` 等 WinUI 命名体系）
- MVVM：`MainViewModel` + Reactive Extensions（`System.Reactive`）做输入节流
- 无 `Microsoft.UI.Xaml`、无 `SystemBackdropElement`、无 `DesktopAcrylicController`

### 1.2 窗口外观（`MainWindow.xaml`）

关键属性（`src/modules/launcher/PowerLauncher/MainWindow.xaml:21-30`）：

```xml
ResizeMode="NoResize"
ShowInTaskbar="False"
SizeToContent="Height"
Topmost="True"
WindowStyle="None"
```

- 无边框（`WindowStyle="None"`）+ 隐藏任务栏 + 置顶 + 高度自适应
- 窗口根是一个 `Border x:Name="MainBorder"`，本身**不设 Background**，只画边框线；其内的 `Grid x:Name="RootGrid"` 也不设背景（材质若要透出，必须靠窗口层透明，见 1.3）：

```xml
<Border x:Name="MainBorder" BorderBrush="{DynamicResource {x:Static SystemColors.ActiveBorderBrushKey}}">
```

### 1.3 材质与圆角（`MainWindow.xaml.cs`）

官方**没有**调用 `DwmSetWindowAttribute` 设置 `DWMWA_SYSTEMBACKDROP_TYPE`，只做了圆角（当前 `MainWindow.xaml.cs` 中唯一一处 `DwmSetWindowAttribute` 调用就是它）：

```csharp
// MainWindow.xaml.cs:54-76  —— 只声明了圆角相关的 DWM 属性
public enum DWMWINDOWATTRIBUTE { DWMWA_WINDOW_CORNER_PREFERENCE = 33 }
public enum DWM_WINDOW_CORNER_PREFERENCE { DWMWCP_DEFAULT=0, DWMWCP_DONOTROUND=1, DWMWCP_ROUND=2, DWMWCP_ROUNDSMALL=3 }
[DllImport("dwmapi.dll", ...)] internal static extern void DwmSetWindowAttribute(IntPtr hwnd, DWMWINDOWATTRIBUTE attribute, ref DWM_WINDOW_CORNER_PREFERENCE pvAttribute, uint cbAttribute);
```

```csharp
// MainWindow.xaml.cs:196-209  —— OnSourceInitialized
if (OSVersionHelper.IsGreaterThanWindows11_21H2())
{
    // ResizeMode="NoResize" removes rounded corners. So force them to rounded.
    IntPtr hWnd = new WindowInteropHelper(GetWindow(this)).EnsureHandle();
    DWMWINDOWATTRIBUTE attribute = DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE;
    DWM_WINDOW_CORNER_PREFERENCE preference = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
    DwmSetWindowAttribute(hWnd, attribute, ref preference, sizeof(uint));
}
else
{
    // Win10：无边框窗口没有边框，补一条 0.5px 边框线
    MainBorder.BorderThickness = new System.Windows.Thickness(0.5);
}
```

**亚克力从哪来？（含历史演进，重要）**

PT Run 的材质经历过一次彻底重构：

- **2024-12 之前**：窗口类型是 WPF-UI 的 `ui:FluentWindow`，显式设置 `WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Acrylic`（Win11）/ `None`（Win10），并用 `SystemThemeWatcher.Watch` 跟随系统主题。同时在根 `Grid` 上叠一层 `SolidColorBrush(Opacity=0.8, ApplicationBackgroundColor)` 压暗亚克力。
- **2024-12 之后**（提交 `7c6af6580e`，*Port from WPF-UI to .NET 9 WPF*）：把 WPF-UI 依赖整体移除，`FluentWindow` 换回原生 `Window`，**`WindowBackdropType.Acrylic` 那两行被删除**，根 Grid 上那层 0.8 不透明度的压暗画刷也一并删除。

也就是说，当前 PT Run 源码里**不存在任何亚克力/材质代码**：没有 `SetWindowCompositionAttribute`、没有 `DwmEnableBlurBehind`、没有 `DWMWA_SYSTEMBACKDROP_TYPE`，连 WPF-UI 时代的 `WindowBackdropType` 都没了。窗口只有 `WindowStyle="None"` + 无背景的根元素，材质依赖 **DWM 对无边框窗口的系统默认合成**（Win11 上由系统主题决定表现）。

> 说明：源码中未找到显式材质 API，因此"当前 PT Run 的亚克力具体由哪一层 DWM 行为提供"无法仅凭代码定论；可确定的是**官方刻意把材质控制权交还给了系统**，代码层面只保留圆角一项 DWM 调用。这点与 Sox 的显式 `DWMWA_SYSTEMBACKDROP_TYPE` 路线相反：Sox 是主动指定，PT Run 是放手不管。

**对 Sox 的启示**：PowerToys 官方在 2024 年主动放弃了"在 WPF 里精细控制材质"（WPF-UI 那套），退回到"只设圆角、材质交给系统"。这说明在 WPF 栈上折腾材质控制的维护成本，官方自己也认为不划算——与 Sox ADR-0014 放弃 WPF-UI 的判断方向一致。

### 1.4 材质相关的枚举定义（`Wox.Plugin/Common/Win32/NativeMethods.cs:495-515`）

Wox 层保留了完整的 DWM 属性枚举，但**只用到圆角**，其余是"备而不用"：

```csharp
public enum DwmWindowAttribute {
    NCRenderingEnabled = 1,
    ...
    UseHostbackdropbrush = 17,     // ← 亚克力相关的 host backdrop，未使用
    UseImmersiveDarkMode = 20,     // ← 深色标题栏，未使用
    WindowCornerPreference = 33,   // ← 唯一实际使用的
    BorderColor = 34,
    CaptionColor = 35,
    TextColor = 36,
    VisibleFrameBorderThickness = 37,
}
```

### 1.5 内容层视觉

- 搜索框：`LauncherControl.xaml`，`Background="Transparent"`，占位符是叠在 TextBox 上的 `TextBlock`（`Tag` 绑定 + `DataTrigger` 判断空文本切前景色），不是 WPF 原生 `PlaceholderText`
- 结果列表：`ResultList.xaml`，无装饰 `ScrollViewer`，虚拟化 `VirtualizingStackPanel`
- 列表项选中态：Fluent 的 `SubtleFillColorTertiaryBrush` / `ControlAltFillColorQuarternaryBrush`
- 字体图标：`Segoe Fluent Icons, Segoe MDL2 Assets`（`&#xE094;` 放大镜）

---

## 2. Command Palette（cmdpal）——WinUI 3 路线（真正的"现代 Spotlight"）

CmdPal 是 PowerToys 正在主推的新一代命令面板，架构与材质处理都远比 PT Run 现代，是本次调研的重点。

### 2.1 技术栈

- `Microsoft.CmdPal.UI`：**WinUI 3 / Windows App SDK**（`UseWinUI=true`，`Microsoft.WindowsAppSDK` **2.2.0**），`net10.0-windows10.0.26100.0`
- `WindowsAppSDKSelfContained=true`，可选 AOT（`EnableCmdPalAOT`）
- 关键依赖（`Microsoft.CmdPal.UI.csproj`）：
  - `WinUIEx`（`WindowEx` 基类，负责窗口边框/标题栏/置顶等原生扩展）
  - `CommunityToolkit.WinUI.*`（SettingsControls、Animations、Converters）
  - `Microsoft.Graphics.Win2D`（`BlurImageControl` 的合成特效）
  - `Microsoft.Windows.CsWin32`（源生成的 Win32 P/Invoke，如 `PInvoke.DwmSetWindowAttribute`）
  - `Microsoft.Extensions.Hosting` + DI
- 原生辅助：`CmdPalKeyboardService`（C++/WinRT 低层键盘钩子）、`Microsoft.Terminal.UI`

### 2.2 分层窗口模型（核心设计）

CmdPal 把窗口拆成两层（`MainWindow.xaml` / `MainWindow.xaml.cs`）：

```
WindowEx（HWND，无边框 + 全透明 + 被 DWM cloak 隐藏）
└── CmdPalMainControl（可见的"卡片"）
    └── Border x:Name="CardBorder"     ← 画圆角、1px 描边、ThemeShadow 投影
        └── Grid x:Name="CardContent"
            ├── SystemBackdropElement   ← 材质只画在这里
            ├── BackgroundLayerPresenter（可选背景图，BlurImageControl）
            └── MainContentPresenter（ShellPage 主内容）
```

```xml
<!-- MainWindow.xaml:19-24 注释原文 -->
<!--
    The whole window is borderless and transparent (see MainWindow.xaml.cs).
    CmdPalMainControl is the visible "card" — it draws the rounded corners,
    border, drop shadow, and hosts the SystemBackdropElement that paints
    Mica / Acrylic / etc. behind the content.
-->
```

```csharp
// MainWindow.xaml.cs:741-748
private void InitializeBackdropSupport()
{
    // The window itself paints nothing (it's transparent). All actual backdrop
    // rendering lives on the SystemBackdropElement inside CmdPalMainControl, so the
    // mica/acrylic only fills the rounded card instead of the whole HWND. The empty
    // tint here keeps the HWND fully transparent.
    SystemBackdrop = new TransparentTintBackdrop { TintColor = Colors.Transparent };
}
```

**关键点**：HWND 自己是 `TransparentTintBackdrop`（全透明），材质画在卡片元素上，所以毛玻璃只填圆角卡片，卡片外的阴影区保持透明。这是与 PT Run / Sox "整窗材质"路线的本质区别。

> `SystemBackdropElement` 是 **Windows App SDK 内置控件**（1.4+，命名空间 `Microsoft.UI.Xaml.Controls`），它本身就是一个可放任意 `SystemBackdrop` 的 XAML 元素。PowerToys 在 `src/common/Common.UI.Controls` 里把它封装成了可复用的 `TransientSurface`（含 `AlwaysActiveDesktopAcrylicBackdrop`），CmdPal、ColorPicker、ShortcutGuide、PowerOCR 都复用这套。WPF 没有对应控件，这是 Sox 无法照搬的直接原因。

### 2.3 卡片视觉（`CmdPalMainControl.xaml`）

```xml
<Grid x:Name="ShadowHost" Padding="{x:Bind ShadowPadding, Mode=OneWay}">   <!-- 透明留白给阴影 -->
  <Border x:Name="CardBorder"
          VerticalAlignment="Top"
          Background="Transparent"
          BorderBrush="{ThemeResource SurfaceStrokeColorDefaultBrush}"
          BorderThickness="1"
          CornerRadius="{x:Bind CardCornerRadius, Mode=OneWay}"
          Shadow="{StaticResource CardShadow}"          <!-- ThemeShadow -->
          Translation="0,0,32">                          <!-- Z 轴抬升投影 -->
    <Grid x:Name="CardContent">
      <controls:SystemBackdropElement x:Name="BackdropElement" CornerRadius="{x:Bind CardCornerRadius, Mode=OneWay}" />
      <ContentPresenter x:Name="BackgroundLayerPresenter" ... />   <!-- 背景图 -->
      <ContentPresenter x:Name="MainContentPresenter" ... />
    </Grid>
  </Border>
</Grid>
```

- 圆角由 XAML `CornerRadius` 完成，`SystemBackdropElement` 也带同样的 `CornerRadius`，保证材质被裁到圆角内
- 阴影用 WinUI `ThemeShadow` + `Translation="0,0,32"`，不是 DWM 阴影（因为 HWND 无边框，DWM 不会给投影）
- 默认 `CardCornerRadius = 8`，`ShadowPadding = 16`（`CmdPalMainControl.xaml.cs` 的 DependencyProperty 默认值）

### 2.4 材质引擎：`TintedControllerBackdrop`

自定义 `SystemBackdrop` 子类（`Controls/TintedBackdrops.cs`，620 行），是整个材质系统的核心。设计目标：**在不替换 `SystemBackdrop` 的前提下动态切换材质与色调**。

```csharp
internal sealed partial class TintedControllerBackdrop : SystemBackdrop, IDisposable
{
    private readonly Dictionary<ICompositionSupportsSystemBackdrop, BackdropTarget?> _targets = [];
    private BackdropSettings? _settings;
    private bool _isBackdropAttached;
    private bool _isInputActive = true;
    ...
    public void Update(BackdropParameters backdrop, BackdropControllerKind kind, bool isImageMode, bool hasColorization)
```

内部按 `BackdropControllerKind` 分派到三种 WinUI 控制器（`TintedBackdrops.cs:480-528`）：

```csharp
private void AttachMicaController(ICompositionSupportsSystemBackdrop target, BackdropSettings settings)
{
    if (!MicaController.IsSupported()) return;
    var controller = new MicaController { Kind = settings.Kind == BackdropControllerKind.MicaAlt ? MicaKind.BaseAlt : MicaKind.Base };
    _micaController = controller;
    if (settings.ApplyTint) { controller.TintColor = ...; controller.TintOpacity = ...; controller.FallbackColor = ...; controller.LuminosityOpacity = ...; }
    controller.SetSystemBackdropConfiguration(_configuration);
    controller.AddSystemBackdropTarget(target);
}

private void AttachAcrylicController(ICompositionSupportsSystemBackdrop target, BackdropSettings settings)
{
    if (!DesktopAcrylicController.IsSupported()) return;
    var controller = new DesktopAcrylicController {
        Kind = settings.Kind == BackdropControllerKind.AcrylicThin ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Default,
        TintColor = ..., TintOpacity = ..., FallbackColor = ..., LuminosityOpacity = ...,
    };
    ...
    controller.AddSystemBackdropTarget(target);
}

// Solid：不用控制器，直接挂一个 Windows.UI.Composition 的 CompositionColorBrush
private void AttachSolidColorBrush(ICompositionSupportsSystemBackdrop target, BackdropSettings settings)
{
    var compositor = new WindowsCompositionCompositor();
    var brush = compositor.CreateColorBrush(settings.SolidColor);
    target.SystemBackdrop = brush;
}
```

**激活态处理**：材质有 active / inactive 两副面孔，通过 `SystemBackdropConfiguration.IsInputActive` 驱动（`TintedBackdrops.cs:239-245`），窗口激活状态由 `MainWindow_Activated` 转发（`MainWindow.xaml.cs:1521-1524`）：

```csharp
RootElement.SetIsInputActive(args.WindowActivationState != WindowActivationState.Deactivated);
```

**工程难点（官方注释记录）**：投影目标 `ICompositionSupportsSystemBackdrop` 持有线程亲和的 native `ContentExternalBackdropLink`，切换材质时若被 C#/WinRT 在终结器线程释放会崩。因此 `_targets` 字典在整个连接生命周期内保持目标为根对象，并且 controller → solid brush 的交接通过 dispatcher 延迟（`QueueApply` + `SolidAttachRetryCount = 2`），失败时回退到卡片背景色。这段是 WinUI SystemBackdrop 动态切换的真实坑，值得记录。

### 2.5 主题与材质的参数化（ViewModels 层）

材质参数被抽象成配置表，与 UI 解耦：

**`BackdropStyle` 枚举**（`BackdropStyle.cs`）：`Acrylic` / `Clear` / `Mica` / `AcrylicThin` / `MicaAlt`

**`BackdropControllerKind` 枚举**（`BackdropControllerKind.cs`）：`Solid` / `Acrylic` / `AcrylicThin` / `Mica` / `MicaAlt` / `Custom`

**`BackdropStyles` 注册表**（`BackdropStyles.cs`）：

| Style | ControllerKind | BaseTintOpacity | BaseLuminosityOpacity | 说明 |
|---|---|---|---|---|
| Acrylic | Acrylic | 0.5 | 0.9 | 默认 |
| AcrylicThin | AcrylicThin | 0.0 | 0.85 | 更透 |
| Mica | Mica | 0.0 | 1.0 | FixedOpacity 0.96，不支持透明度调节 |
| MicaAlt | MicaAlt | 0.0 | 1.0 | FixedOpacity 0.98 |
| Clear | Solid | 1.0 | 1.0 | 纯色 + alpha |

**`BackdropStyleConfig.ComputeEffectiveOpacity`**（`BackdropStyleConfig.cs:59-76`）：

```csharp
// Mica 等不支持透明度：直接用 FixedOpacity
if (!SupportsOpacity && FixedOpacity > 0) return FixedOpacity;
// Solid：透明度就是用户值（控制 alpha）
if (ControllerKind == BackdropControllerKind.Solid) return userOpacity;
// 模糊类：base tint × 用户透明度
var baseTint = baseTintOpacityOverride ?? BaseTintOpacity;
return baseTint * userOpacity;
```

**`BackdropParameters` 记录**（`Services/BackdropParameters.cs`）：`TintColor` / `FallbackColor` / `EffectiveOpacity` / `EffectiveLuminosityOpacity` / `Style`

### 2.6 主题服务（`ThemeService.cs`）

`ThemeService` 把「用户设置 + 系统偏好」翻译成具体材质参数与资源字典，是整个外观的枢纽：

1. 选择 provider：`NormalThemeProvider`（无色化）或 `ColorfulThemeProvider`（强调色/图片着色）
2. 计算 tint：`ColorizationMode` = `CustomColor` / `WindowsAccentColor` / `Image`，默认透明
3. 解析明暗：`GetElementTheme`（跟随系统时用 `UISettings.GetColorValue(UIColorType.Background)` 亮度判断）
4. 调 `provider.GetBackdropParameters(context)` 得到 `BackdropParameters`
5. 组装 `ThemeSnapshot`，`Interlocked.Exchange` 原子替换，触发 `ThemeChanged`
6. `ResourceSwapper.TryActivateTheme(provider.ThemeKey)` 切换 `Theme.Normal.xaml` / `Theme.Colorful.xaml`
7. 变更去抖 500ms（`ReloadDebounceInterval`），订阅 `UISettings.ColorValuesChanged` 跟随系统强调色变化

**`NormalThemeProvider`**（`NormalThemeProvider.cs:32-55`）：基色深 `#202020` / 浅 `#F3F3F3`，浅色主题下调低 luminosity，返回 `BackdropParameters`。

**`ColorfulThemeProvider`**（`ColorfulThemeProvider.cs`）：把强调色经 hue 变形（`WindowsAccentHueWarpTransform`，一张 36 点 hue LUT，模拟 Windows 强调色曲线）+ 亮度分级（`AccentShades`）后与基色 alpha 合成；彩色主题下把 tint 透明度抬到至少 0.8 让颜色透出来。

### 2.7 主题资源字典

`Styles/Theme.Normal.xaml` 与 `Theme.Colorful.xaml` 用 `ThemeDictionaries` 提供 Dark / Light / HighContrast 三套：

```xml
<ResourceDictionary x:Key="Dark">
    <SolidColorBrush x:Key="LayerOnAcrylicPrimaryBackgroundBrush" Color="#50202020" />
    <SolidColorBrush x:Key="LayerOnAcrylicSecondaryBackgroundBrush" Color="Transparent" />
    ...
</ResourceDictionary>
<ResourceDictionary x:Key="Light">
    <!-- 注释：TextBox 光标是反色渲染，需要明确定义背景 -->
    <SolidColorBrush x:Key="LayerOnAcrylicPrimaryBackgroundBrush" Color="#A0FFFFFF" />
    ...
</ResourceDictionary>
```

### 2.8 激活态 / 显隐的 DWM 技巧

- **Cloak 隐藏**：`DWMWA_CLOAK = TRUE` 隐藏窗口但保留 XAML 渲染（避免 WinUI3 首帧闪烁），显示时 uncloak（`MainWindow.xaml.cs:1158-1183`）
- **边框颜色**：`DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE (0xFFFFFFFE)` 去掉 `WS_THICKFRAME` 留下的 1px 系统描边；调试模式切回 `DWMWA_COLOR_DEFAULT`（`MainWindow.xaml.cs:716-739`）
- **圆角**：无边框调试模式下 `DWMWCP_DEFAULT`，正常 `DWMWCP_DONOTROUND`（因为卡片自己画圆角，避免叠加）
- **自定义 resize**：`ResizeBorderThicknessDip = 8`，`WM_NCHITTEST` 返回 `HTLEFT` 等码，配合 `InputNonClientPointerSource` 注册区域

### 2.9 背景图特效（`BlurImageControl`）

`Controls/BlurImageControl.cs` 用 Win2D 合成特效链实现背景图：`Brightness` → `Blur` → `TintBlend` / `Tint`，各参数（BlurAmount、ImageBrightness、ImageOpacity、TintColor、TintIntensity）都是可动画的 `DependencyProperty`。

---

## 3. Sox 当前实现（对照基线）

### 3.1 技术栈

- WPF，`net10.0-windows`（ADR-0001）
- 自研视觉：`Themes/Dark.xaml` / `Light.xaml` 纯画刷字典，**无第三方 UI 库**（ADR-0014 记录了尝试 WPF-UI 后回退的决策）
- `WindowBackdrop`（`src/Sox.App/Interop/WindowBackdrop.cs`，217 行）手写 DWM 调用

### 3.2 窗口模型（`SearchWindow.xaml`）

```xml
<Window WindowStyle="SingleBorderWindow" ResizeMode="NoResize" Background="Transparent" Topmost="True" ...>
  <Border x:Name="RootCard" CornerRadius="14">
    <Border.Clip><RectangleGeometry x:Name="RootClip" RadiusX="14" RadiusY="14"/></Border.Clip>
    <Grid>
      <Grid x:Name="SearchPane" Background="{DynamicResource WindowBackground}"> ... </Grid>
      <Border x:Name="PreviewPane" ... />   <!-- 预览侧栏 -->
    </Grid>
  </Border>
</Window>
```

- 用 `SingleBorderWindow` + `WindowChrome`（`CaptionHeight=0`、`GlassFrameThickness=-1`）把玻璃框铺满客户区
- 内容层 `SearchPane` 自己填 `WindowBackground`（深色 `#001B1B1F`、浅色 `#00FAFAFC`，都是近乎全透明的 alpha）

### 3.3 材质实现（`WindowBackdrop.cs`）

三个入口：

| 方法 | 职责 |
|---|---|
| `Prepare(window, dark)` | 一次性：隐藏 Alt+Tab、去标题栏按钮、设 `WindowChrome`、丢弃合成背景、设深色/圆角/标题栏颜色 |
| `ApplyMaterial(window, dark, acrylic)` | 写 `DWMWA_SYSTEMBACKDROP_TYPE`，`acrylic ? 3 (TransientWindow) : 2 (MainWindow)`；清/写之间有 `FrameChanged` + `WM_NCACTIVATE` + `RedrawWindow` |
| `ClearBackdrop` / `ForceTopmost` | 清材质 / 强制置顶 |

核心代码：

```csharp
// WindowBackdrop.cs:157-193
public static void ApplyMaterial(Window window, bool dark, bool acrylic)
{
    DropCompositedBackground(window, hwnd);
    SetDarkMode(hwnd, dark);
    var unchanged = MaterialApplied.TryGetValue(window, out var applied)
                    && applied.Dark == dark && applied.Acrylic == acrylic;
    if (!unchanged)
    {
        var none = 0;
        DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref none, sizeof(int));
        FrameChanged(hwnd);
        var backdrop = acrylic ? DwmsbtTransientWindow : DwmsbtMainWindow;   // 3 / 2
        DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        FrameChanged(hwnd);
        ...
    }
    SendMessage(hwnd, WmNcActivate, new IntPtr(1), IntPtr.Zero);   // 强制 NC 帧进激活态
    RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RdwFrame | RdwInvalidate);
}
```

**官方没有、Sox 独有的工程处理**（都是 DWM 整窗材质的实际坑）：

1. **激活态竞争**：材质在非激活窗口上合成会得到灰色 fallback，且重复写同值不重绘 → Sox 清空 + 重写 + `WM_NCACTIVATE` + `RDW_FRAME` 强制重合成
2. **淡显防闪**：`MaterialApplied`（`ConditionalWeakTable`）记录已应用材质，材质没变就不走"清空→重写"（那中间一帧无材质就是闪烁源）
3. **显隐后重断言**：`ReassertBackdrop`（`SearchWindow.xaml.cs:264-279`）用 60ms 定时器连做 3 次 `ReapplyBackdrop` + `ForceTopmost`，让"落在 DWM 稳定后"的那次生效
4. **置顶重断言**：WPF `Topmost` 在隐藏/显示或失去前台后不可靠，`ForceTopmost` 每次显示都重设 z 序

### 3.4 主题

`ThemeService`（`src/Sox.App/Services/ThemeService.cs`，52 行）：读注册表 `AppsUseLightTheme`，切换 `Dark.xaml` / `Light.xaml`，`IsDark` 供 DWM 材质配色。**没有**材质样式枚举、没有色调参数化、没有强调色 / 背景图。

---

## 4. 差异对照

| 维度 | PowerToys Run | CmdPal | Sox 现状 |
|---|---|---|---|
| 框架 | WPF（Fluent 资源） | WinUI 3 / WinAppSDK 2.2 | WPF（自研画刷） |
| 材质 API | 无（WPF-UI 时代用 `WindowBackdropType.Acrylic`，已移除；现依赖 DWM 系统默认） | `MicaController` / `DesktopAcrylicController` / `CompositionColorBrush` | `DWMWA_SYSTEMBACKDROP_TYPE` 显式指定 |
| 材质作用范围 | 整窗 | **仅圆角卡片元素** | 整窗 |
| 圆角 | `DWMWA_WINDOW_CORNER_PREFERENCE` | XAML `CornerRadius` + `SystemBackdropElement.CornerRadius` | DWM 圆角 + XAML `RootCard.Clip` 双保险 |
| 阴影 | DWM 默认 | `ThemeShadow` + `Translation Z=32` | DWM 默认（无强度参数） |
| 材质样式可选 | 无 | 5 种（Acrylic / AcrylicThin / Mica / MicaAlt / Clear） | 无（写死 `DWMSBT_TRANSIENTWINDOW` 亚克力） |
| 色调 / 透明度 | 无 | 用户可调 opacity、tint、强调色、背景图 | 无 |
| 激活态处理 | 未处理（依赖系统） | `SystemBackdropConfiguration.IsInputActive` | `WM_NCACTIVATE` + `RDW_FRAME` 手搓 |
| 主题系统 | Fluent 资源字典 | Provider + 资源字典 + 快照原子替换 + 500ms 去抖 | 两个画刷字典 + 注册表明暗判断 |
| 显隐防闪 | 无 | DWM `DWMWA_CLOAK` | `ConditionalWeakTable` 防重复 + 定时器重断言 |

### 关键差异解读

1. **"整窗材质 vs 卡片材质"是最大分歧。**
   Sox 走 PT Run 的整窗路线，优点是代码量小、与 DWM 原生阴影/圆角天然一致；代价是**材质无法只填卡片**，预览侧栏、阴影区都受同一材质影响，也无法做"卡片外透明 + 卡片内毛玻璃"的效果（CmdPal 正是这么做的）。
   PT Run 也没解决这个问题，而且它在 2024-12 直接**放弃了材质控制**，退回到"整窗、材质交给系统"，把窗口做得刚好贴合内容。

2. **材质可控性差一个量级。**
   CmdPal 有完整的 `BackdropStyle` → `BackdropStyleConfig` → `BackdropParameters` → Controller 的参数链，用户能选 5 种材质、调透明度和色调；Sox 只有 `acrylic: bool` 一个开关。若要 F-13 设置面板支持"材质选择"，CmdPal 的 `BackdropStyles` 注册表是可以直接照搬的抽象（纯数据，与 WinUI 无耦合）。

3. **激活态问题是 WPF + DWM 整窗路线的固有成本。**
   Sox 用 `WM_NCACTIVATE` + `RDW_FRAME` + 定时器重断言来对抗 DWM 的 inactive 灰色缓存；CmdPal 用 `SystemBackdropConfiguration.IsInputActive` 一行解决。这不是 Sox 代码差，是 API 层级差异——WPF 没有暴露 WinUI 那套 `SystemBackdropConfiguration`。

4. **CmdPal 的 `TintedControllerBackdrop` 那些"坑"注释，对 Sox 有借鉴意义。**
   动态切换材质时目标投影的终结器线程释放问题、controller → brush 交接的 dispatcher 延迟，这些在 WinUI 里存在，在 WPF 的 DWM 属性写路径里表现为另一种形式（就是 Sox 已经在处理的闪烁/灰色缓存）。说明"动态改材质"本身就是难事，不是路线选错。

---

## 5. 对 Sox 的可执行建议

按投入产出排序：

1. **短期（低风险，纯参数化）**：把材质抽象成 `BackdropStyles` 那样的配置表（`Style → DWM backdrop type + tint + opacity`），给 F-13 设置面板加"材质"下拉。Sox 的 DWM 路线能支持的组合有限（`DWMSBT_AUTO`/`NONE`/`MAINWINDOW`/`TRANSIENTWINDOW`/`TABBEDWINDOW`），但"亚克力 / Mica / 纯色"三选一是现实的。参考 `BackdropStyles.cs` 的写法。

2. **中期（中等风险）**：显式加 `DWMWA_USE_IMMERSIVE_DARK_MODE`（Sox 已有）+ `DWMWA_CAPTION_COLOR` / `DWMWA_BORDER_COLOR` 的调优，减少 Win11 上系统描边的干扰。CmdPal 的 `DWMWA_COLOR_NONE` 处理已验证有效。

3. **长期（高风险，需 ADR）**：若追求"卡片外透明 + 卡片内材质 + 自绘阴影"的 CmdPal 效果，WPF 路线上可行但代价大——需要放弃 `SingleBorderWindow`，改无边框 + `AllowsTransparency` + 自绘阴影，并自行用 Win2D/D3D 合成材质（因为 WPF 拿不到 `SystemBackdropElement`）。ADR-0014 已经因为类似原因否掉了 WPF-UI，这条路线应在 ADR 中重新论证，不要默认走。

4. **不建议照搬 CmdPal 的材质代码**：`TintedBackdrops.cs` 深度依赖 WinUI `ICompositionSupportsSystemBackdrop` / `MicaController`，WPF 无法引用。可借鉴的是**它的参数化抽象与状态机思路**，不是代码。

---

## 附录：关键文件索引

PowerToys Run（WPF 路线）：
- `src/modules/launcher/PowerLauncher/MainWindow.xaml` / `.xaml.cs`（圆角、无边框）
- `src/modules/launcher/PowerLauncher/LauncherControl.xaml`（搜索框）
- `src/modules/launcher/PowerLauncher/App.xaml`（Fluent 资源）
- `src/modules/launcher/Wox.Plugin/Common/Win32/NativeMethods.cs`（DWM 枚举）

通用 UI 控件（WinUI，CmdPal 与其它模块复用）：
- `src/common/Common.UI.Controls/Controls/TransientSurface/TransientSurface.xaml`（卡片材质模板：`SystemBackdropElement` + `AlwaysActiveDesktopAcrylicBackdrop` + `ThemeShadow`）

CmdPal（WinUI 3 路线）：
- `src/modules/cmdpal/Microsoft.CmdPal.UI/MainWindow.xaml` / `.xaml.cs`（分层窗口、cloak、激活态）
- `src/modules/cmdpal/Microsoft.CmdPal.UI/Controls/CmdPalMainControl.xaml` / `.xaml.cs`（卡片、圆角、阴影）
- `src/modules/cmdpal/Microsoft.CmdPal.UI/Controls/TintedBackdrops.cs`（材质引擎，620 行）
- `src/modules/cmdpal/Microsoft.CmdPal.UI/Controls/BlurImageControl.cs`（背景图特效）
- `src/modules/cmdpal/Microsoft.CmdPal.UI/Services/ThemeService.cs`（主题枢纽）
- `src/modules/cmdpal/Microsoft.CmdPal.UI/Services/NormalThemeProvider.cs` / `ColorfulThemeProvider.cs`
- `src/modules/cmdpal/Microsoft.CmdPal.UI.ViewModels/BackdropStyles.cs` / `BackdropStyleConfig.cs` / `BackdropStyle.cs` / `BackdropControllerKind.cs`
- `src/modules/cmdpal/Microsoft.CmdPal.UI.ViewModels/Services/BackdropParameters.cs`
- `src/modules/cmdpal/Microsoft.CmdPal.UI/Styles/Theme.Normal.xaml` / `Theme.Colorful.xaml`

Sox（对照）：
- `src/Sox.App/Interop/WindowBackdrop.cs`
- `src/Sox.App/Windows/SearchWindow.xaml` / `.xaml.cs`
- `src/Sox.App/Services/ThemeService.cs`
- `src/Sox.App/Themes/Dark.xaml` / `Light.xaml`
- `docs/adr/0014-wpfui-migration.md`
