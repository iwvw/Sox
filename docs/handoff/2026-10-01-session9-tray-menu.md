# Handoff：托盘右键菜单 WinUI 化（会话九）

日期：2026-10-01
状态：已实现并构建通过（x64），待用户视觉验收

## 本轮目标

用户反馈托盘右键菜单「原生太丑」（初版为 WinForms `NotifyIcon` + `ContextMenuStrip`）。参考 `E:\Code\snow-apps`（Qt `ant_design_qt/AdContextMenu` 自绘菜单）后，选定路线一：保留托盘图标，右键菜单换成 WinUI 原生 Fluent `MenuFlyout`。

## 完成内容

| 项 | 实现 |
|---|---|
| 依赖 | `Sox.App.csproj` 新增 `H.NotifyIcon.WinUI` 2.5.0-beta.3（MIT；初版 2.4.1 有首次宽度缺陷，已升级） |
| 托盘资源 | 新增 `TrayMenu.xaml`（`TaskbarIcon` + `ContextFlyout` 的 `MenuFlyout`，`ContextMenuThemeMode="System"`），`App.xaml` 合并 |
| 控制器 | 重写 `Services/TrayIconService.cs`：`TaskbarIcon` + `ContextMenuMode.SecondWindow`（独立 WinUI 窗渲染 Fluent 菜单）；左键呼出、右键菜单、显隐、主题跟随 |
| 主题 | `MainWindow.ApplyTheme()` 调 `_tray.ApplyTheme(snapshot.Theme)` |
| 菜单项 | 打开搜索框 / 设置 / 关于·检查更新 / 退出；字号 13、图标 14 |
| 文档 | 新增 ADR-0019（含可复用清单与 7 条坑） |

## 关键决策

- 选 `SecondWindow` 而非默认 `PopupMenu`：只有它渲染真 WinUI `MenuFlyout`（圆角 + 材质 + 动效）。`PopupMenu` 只是 Win32 菜单加深浅色。
- **版本从 2.4.1 升到 2.5.0-beta.3**：2.4.1 的 `SecondWindow` 首次弹出宽度偏窄（未预热 Flyout 的 `Measure` 返回 0 尺寸），用户实测「首次有的字显示不全、第二次正常」。2.5.0-beta.1 起上游重写该模式修复。beta 可用 `ContextMenuThemeMode="System"` 让菜单跟随系统深浅色。
- 图标必须多尺寸 `.ico`（Sox 的 `sox-tray.ico` 已含 9 档）。

## 关键坑

- **必须 `-p:Platform=x64 -p:RuntimeIdentifier=win-x64` 构建**：`WindowsAppSDKSelfContained=true` + AnyCPU 报 `requires a supported Windows architecture`（与托盘库无关）。
- **首次宽度偏窄 = 2.4.1 缺陷**：升级到 2.5.0-beta.3 修复（详见 ADR-0019 坑第 2 条）。
- **`SecondWindow` 是库标注的 preview**：多屏 / 高 DPI / 任务栏在侧或顶时可能定位或残影，需实测。
- 菜单项高度被库强制 32，只能改字号微调。
- 2.4.1 点击项后菜单自动关闭；2.5.0-beta.3 起可用 `CloseContextMenuOnItemClick=false` 改。

## 待用户视觉验收

1. 右键托盘菜单是否为 Fluent 观感（圆角/材质/深浅色跟随）
2. 菜单字号是否合适（现 13）
3. 左键呼出是否正常
4. 多显示器 / 高 DPI 下菜单定位是否正确

## 下一步（若验收有问题）

- 定位/残影异常 → 回退 `ContextMenuMode.PopupMenu`（仍是原生菜单，支持深浅色）
- 需要更精细观感 → 路线二：自绘无边框 `Popup` + Win2D（对齐 snow-apps，工作量大）

## 相关文件

- `src/Sox.App/TrayMenu.xaml`、`src/Sox.App/Services/TrayIconService.cs`、`src/Sox.App/App.xaml`、`src/Sox.App/MainWindow.xaml.cs`
- `docs/adr/0019-tray-notifyicon-winui.md`
