# ADR-0017. 前端实现：直接移植 CmdPal 的卡片/材质/窗口架构

日期：2026-09-30
状态：Accepted

## 背景

ADR-0016 决定前端 WPF → WinUI 3，并「对齐 CmdPal 的卡片材质架构」，但当时是「参考其参数化抽象与状态机思路，不照搬代码」。会话七据此从零写了 M1 骨架（`CardControl` + 手搓 `TintedControllerBackdrop`），但骨架随后被清空，`src/Sox.App` 仅剩 `Assets`。

用户本轮明确指示：**直接把 CmdPal 前端拿过来用，做删减和适配**，做出 Spotlight 悬浮搜索框。

## 决策

**把 PowerToys CmdPal（`Microsoft.CmdPal.UI`，MIT）中「窗口 + 卡片 + 材质 + 主题」四层代码移植进 `Sox.App`，删掉 PowerToys 特有依赖，接上 Sox 后端 `SearchService`。**

这与 ADR-0016 的「参考不移植」相比是一次升级：直接移植经过工程验证的材质引擎，避免重复踩 `SystemBackdropElement` 动态切材质的坑。

### 移植范围（逐文件）

| CmdPal 文件 | 处置 | 说明 |
|---|---|---|
| `Controls/CmdPalMainControl.xaml(.cs)` | 移植 | 卡片：圆角 + 描边 + `ThemeShadow` + `SystemBackdropElement` |
| `Controls/TintedBackdrops.cs` | 移植 | 材质引擎（`MicaController`/`DesktopAcrylicController`/CompositionColorBrush 动态切换） |
| `ViewModels/BackdropStyle.cs` / `BackdropControllerKind.cs` / `BackdropStyleConfig.cs` / `BackdropStyles.cs` / `PreviewBrushKind.cs` / `Services/BackdropParameters.cs` | 移植 | 纯数据配置表，零 WinUI 耦合 |
| `Services/NormalThemeProvider.cs` / `ThemeContext.cs` / `IThemeProvider.cs` | 移植 | 基色 `#202020`/`#F3F3F3`，材质参数计算 |
| `Styles/Theme.Normal.xaml` | 移植 | `LayerOnAcrylic*` 画刷 + 参数控件画刷 |
| `MainWindow` 的 cloak / 无边框 / `ThemeShadow` / 激活态转发 | 借鉴重写 | 去掉 telemetry / dock / 多显示器 summon / 自定义 resize，保留 cloak 显隐与激活态 |
| `Controls/SearchBar.xaml` | 不移植 | CmdPal 的 SearchBar 依赖 `PageType`/参数运行 VM，Sox 用单行 `TextBox` |
| `App.xaml.cs` 的 DI / 扩展宿主 / AdaptiveCards / WinGet / telemetry | 不移植 | PowerToys 特有，Sox 不需要 |
| 全部 `ViewModels`（数百个）/ `Pages` / `Dock` / `ExtViews` | 不移植 | 与命令面板领域强耦合 |

### 不引入

CmdPal 的 extension SDK、DI 容器（`Microsoft.Extensions.Hosting`）、AdaptiveCards、遥测、Dock、多显示器 summon、自定义 resize（`WM_NCHITTEST`）。

### 窗口生命周期（Sox 版）

CmdPal 的 cloak 技巧完整保留，去掉无关分支：

1. 构造时 `HideWindow()`：`DWM cloaking` + `SW_HIDE` + `SW_SHOWNA`（XAML 预渲染，避免 WinUI 3 首帧闪烁）
2. 呼出：`SW_SHOW` + 取消 cloak + `SetForegroundWindow`（`AllowSetForegroundWindow` + `AttachThreadInput`）+ `HWND_TOPMOST`
3. 失焦：重新 cloak 隐藏
4. 无边框：`OverlappedPresenter.SetBorderAndTitleBar(false, false)` + `DWMWA_BORDER_COLOR=COLOR_NONE` + `DWMWA_WINDOW_CORNER_PREFERENCE=DONOTROUND`（卡片自画圆角）

### 搜索接入（替换 CmdPal 的 ShellPage/ViewModel）

新增 `Services/SearchHost.cs`：持 `SearchService` 单例，`SearchStreamingAsync` 流式回调 marshal 到 UI 线程，`SearchResultRankComparer` 叠加历史分排序。结果列表用 `ListView` 虚拟化 + `ShellIconProvider` 图标 + `TextHighlighter` 高亮（复用核心 `FuzzyQuery`）。

## 后果

- 材质/卡片/主题是「已工程验证的实现」，省去重写与调试成本（对齐 ADR-0016 的意图，执行力更强）。
- `Sox.App` 的 UI 层仍是从零组装的（搜索/列表/热键/托盘为新写），仅视觉与窗口架构来自 CmdPal。
- CmdPal 上游更新不会自动同步，属有意取舍（MIT 快照，Sox 只取所需）。
- 不再手搓材质：删除会话七的 `Controls/CardControl` + 自写 `TintedControllerBackdrop` 思路。

## 关联

- 细化并替代 ADR-0016 中「复用 CmdPal 的部分」（该节「可参考不可移植」的表述由本 ADR 更新为「窗口/卡片/材质/主题直接移植」）
- 依据调研：`docs/reference/powertoys-spotlight-material.md`
- 上游：`E:\Code\PowerToys`（main @ `58d63e42da`，MIT）
