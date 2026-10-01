# Handoff：PowerToys 材质调研（会话五）

日期：2026-09-30
状态：完成

## 会话目标

Clone/更新 microsoft/PowerToys，调研官方 spotlight 式悬浮窗的材质实现、技术栈与框架，产出分析文档，并与 Sox 现状对比。

## 本轮完成

| 项 | 内容 |
|---|---|
| 仓库维护 | `E:\Code\PowerToys` 已存在（origin = microsoft/PowerToys）。丢弃本地 1 处行尾差异、`git reset --hard origin/main` 更新到最新 main @ `58d63e42da`，工作区干净 |
| 调研范围 | `src/modules/launcher`（PowerToys Run，WPF）+ `src/modules/cmdpal`（Command Palette，WinUI 3）+ `src/common/Common.UI.Controls` |
| 产出 | `docs/reference/powertoys-spotlight-material.md`（约 480 行） |

## 核心结论（详见文档）

1. **两个入口两条路线**：
   - PowerToys Run：WPF + Fluent 资源字典，**当前源码已无任何材质 API**，只设圆角（`DWMWA_WINDOW_CORNER_PREFERENCE`）。历史上（2024-12 前）用 WPF-UI 的 `FluentWindow` + `WindowBackdropType.Acrylic`，提交 `7c6af6580e`（Port from WPF-UI to .NET 9 WPF）整体移除，材质交还系统。
   - CmdPal：WinUI 3 / Windows App SDK 2.2，用 `SystemBackdropElement` + 自定义 `TintedControllerBackdrop`（`MicaController` / `DesktopAcrylicController` / CompositionColorBrush），**材质只画在圆角卡片元素上**，阴影自绘（`ThemeShadow` + `Translation Z=32`）。
2. **Sox 与官方 PT Run 同路线**（WPF + DWM 整窗材质），但 Sox 主动写 `DWMWA_SYSTEMBACKDROP_TYPE`，官方现在不写。
3. **CmdPal 的参数化抽象可直接借鉴**：`BackdropStyle` / `BackdropStyleConfig` / `BackdropStyles` 注册表 / `BackdropParameters` 全是纯数据，与 WinUI 无耦合，可作为 F-13 设置面板"材质选择"的模板。
4. **CmdPal 的材质代码不能照搬**：深度依赖 WinUI `ICompositionSupportsSystemBackdrop`，WPF 无法引用。

## 未做（文档已列为建议）

- 未改任何 Sox 代码（本次仅调研 + 出文档）
- 材质参数化 / F-13 材质下拉 / 卡片外透明路线，均只在文档第 5 节给出建议，未落 ADR

## 下一步建议

1. 若要推进材质参数化，先写 ADR（Sox DWM 路线可支持的组合有限，需界定范围）
2. 文档中"PT Run 当前亚克力具体由哪层 DWM 行为提供"无法仅凭源码定论，如需要可在真机验证
3. 回到主线：会话四遗留的回填跳转已修，下一步 F-16 / F-17、内存实测、单文件发布

## 关键坑（本次新增）

- PowerToys 仓库 `core.autocrlf=true`，工作区常年有行尾差异；更新前 `git reset --hard` 即可，不要试图 stash（stash 后 pull 仍会被行尾差异挡下）。
