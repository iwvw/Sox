# ADR-0014. UI 组件库决策：尝试 WPF-UI 后回退自研 WPF 视觉

日期：2026-09-29
状态：Superseded（WPF 栈整体废弃，本决策失去意义；见 ADR-0016）

## 背景

Sox 前端（Sox.App）自研视觉：`Themes/Dark.xaml` / `Light.xaml` 定义画刷，`WindowBackdrop` 手写 DWM 亚克力与圆角，设置页等用原生 WPF 控件。

为提升观感到 Win11 Fluent 风格，曾决策全面迁移到 WPF-UI（`lepoco/wpfui` 4.3.0），并用 ADR-0014 记录。

## 迁移尝试的结论

迁移过程中陆续发现：

1. **主题系统冲突**：`FontIcon`/`SymbolIcon` 构造函数硬依赖 `Application.Current.Resources` 里的 `DefaultIconFontSize` 与 `FluentSystemIcons` 字体键，必须全局挂载 WPF-UI 的 `Variables.xaml` + `Fonts.xaml`；而 `ControlsDictionary` 的全局隐式控件样式会污染主搜索窗（搜索框出现 WPF-UI 边框，变成"显式输入框"）。全局/局部资源作用域难以两全。
2. **图标渲染异常**：`{ui:SymbolIcon}` 标记扩展与 `<ui:SymbolIcon>` 元素在资源作用域收窄后反复抛"类型构造函数/MarkupExtension provide value 异常"（Wpf.Ui.Markup.SymbolIconExtension、Wpf.Ui.Controls.SymbolIcon），调试成本高。
3. **收益不值当**：Sox 只有设置页是"常规表单"场景（ToggleSwitch/NumberBox/Card），主搜索窗和内嵌窗都是高度定制、自绘的 Spotlight 风格，WPF-UI 能贡献的受众面很小。

## 决策

**放弃 WPF-UI 依赖，全部回退到自研 WPF 视觉层。**

- 从 `Sox.App.csproj` 移除 `WPF-UI` 包引用
- `App.xaml` 只保留自研主题字典（`Themes/Dark.xaml` / `Light.xaml`），废除 WPF-UI 的 `ThemesDictionary` / `ControlsDictionary`
- `ThemeService` 去掉 `ApplicationThemeManager` 调用，恢复纯自研字典切换（系统明暗检测逻辑保留）
- 设置页 `SettingsWindow` 从 `FluentWindow` 回退为普通 `Window`，控件回退为原生 WPF（`CheckBox`/`ComboBox` 等）
- 保留迁移期间积累的合理改动：
  - 设置页"跟随系统主题"开关 + "深色主题"开关的联动逻辑（与 WPF-UI 无关）
  - `OnUserPreferenceChanged` 尊重"跟随系统主题"开关（修复了原先无条件跟随系统的 bug）
  - 设置页布局：横向两列（标签 + 控件）对齐方式
  - 应用/托盘/设置窗复用的 `sox.ico`

## 后果

- 视觉回归自研 WPF：观感依赖 `Themes/*` 画刷与 `WindowBackdrop` 的 DWM 亚克力
- 无第三方主题依赖，升级风险归零，构建更快
- 需要后续为设置页补少量自绘控件样式，使其贴近 Fluent 观感（优先级低）

## 关联

- 取代 ADR-0014 原"全面迁移 WPF-UI"方向
- 不影响 ADR-0012（内嵌窗架构）、ADR-0013（排序）