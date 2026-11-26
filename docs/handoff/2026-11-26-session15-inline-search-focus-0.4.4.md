# Session 15 · 0.4.4:内联搜索抢焦点 + 快照头重复读取

日期:2026-11-26

## 起因

用户反馈:文件对话框(「打开」/「选择文件夹」)里出现 Sox 内联搜索面板后,**对话框自身控件全部不可用**——下拉框一展开就收起,「打开 / 选择文件夹」要点很多次才生效,有时永远点不动。

## 排查路径(关键证据)

1. `%TEMP%` 下的探针脚本采样(HWND / `GetGUIThreadInfo` / `WindowFromPoint`):
   - 「打开」按钮 `enabled=True`、按钮中心点**命中按钮本身**、对话框曾是前台 → **排除遮挡、排除按钮禁用、排除鼠标捕获**。
   - 长时间采样显示:**前台持续被 `Sox.App` 占着**,对话框线程 `hwndFocus=none` → 面板霸占前台。
2. 应用日志(`Inline-diag`)显示 Hook 高频重报同一对话框(`ShowForDialog` 重复 20+ 次);一次 `HidePanel` 后 `_dialogHwnd` 归零,下一次上报就走完整显示路径 → `FocusBox()` → `Activate()` → **再次抢前台**。
3. 结论:面板在「显示路径」上无条件抢焦点;而 Hook 的重报 + 隐藏/重显把这条路径变成**抢焦点循环**。对话框一旦不是活动窗口,其下拉框/按钮立即失效。

## 修复

| 问题 | 改动 |
|---|---|
| 面板反复抢焦点 | `InlineSearchWindow` 新增 `_focusedDialog`:**同一对话框仅首次显示抢焦点**,重复显示不再 `Activate` |
| 跳转后焦点没交还对话框 | `Navigate` 先 `AllowSetForegroundWindow(hookPid)` → `ForceForeground(对话框)` → 发 `RestoreDialogFocus` → 再 `NavigateDialog`(对齐 Lertaro 的两步交接) |
| 跳转历史从不写入 | `Navigate` 里 `SearchBox.Text` 的读取移回 UI 线程(原在 `Task.Run` 内,每次抛 `COMException 0x8001010E`) |
| `magic mismatch` 每轮询刷 3 条 + 磁盘缓存发现失效 | `SnapshotFormat.TryReadHeaderFromFile` 先读 magic/version 又交给会重读二者的 `ReadHeader` → 有效文件必报错、无效文件被静默接受。改为只读一次 + 3 个测试 |
| 设置页「浏览…」文件夹选择框无法确认 | 拾取器原来在已打开的 `ContentDialog` 内弹出,两个模态叠加 → 拾取器 OK 不生效。改为先关对话框→拾取→带草稿重开;取消编辑不再触发保存 |

## 发布

- 版本 `0.4.3` → **`0.4.4`**;CI 与 Release 工作流均 success。
- 4 个提交:`e5b9419`(内联焦点)、`16a5b7a`(快照头)、`10e40e5`(设置拾取器)、`6a6b05e`(chore release)。
- tag `v0.4.4` 已推送,六份产物齐全。
- 测试 80 → **83 通过**(`TryReadHeaderFromFile` 可达性 / 错版本返回 null / 过短文件返回 null)。
- 本机已覆盖安装 0.4.4(本地构建),用户实测通过。

## 排查过程中踩过的坑(供下次参考)

- **`dotnet publish` 增量陷阱**:只改 `.xaml.cs` 时 `Sox.App.csproj` 可能被判 up-to-date,`Sox.App.dll` 不会重编——第一次安装因此装了个旧 App。**发布前必须核对 `Sox.App.dll` 的时间戳**,必要时删 `obj/bin`。
- **不能只替换安装目录里的单个 dll**:安装版的 `Sox.App.pri` 与本机构建的布局不同(126KB vs 1.46MB),热替换会导致 App 启动即崩。要验证就整包覆盖安装。
- **Debug 版 App 无法拉起 Hook**:服务端 `HookLaunchRequestHandler.IsGenuineAppProcess` 要求 App 目录 == 服务目录或其父目录;`src\Sox.App\bin\Debug` 与 `D:\APP\Sox\Service` 不匹配,会被判 `Unauthorized caller`。内联搜索类问题只能在安装版复现。
- **日志位置**:安装版 App 用 `%LocalAppData%\Sox\logs`,Debug 版走 portable 模式写到 `bin\Debug\Data\Users\<hash>\logs`;Hook 日志在 `%LocalAppData%\Sox\logs\hook.log`(不是 ProgramData)。

## 未做 / 后续

- `release.yml` 的测试门禁已在 0.4.3 周期补上(commit `8f45be5`),本次发布已由它把关。
- 若后续再遇「面板与对话框争前台」类问题,优先看 `ShowForDialog` 的完整显示路径与 `HidePanel` 的触发频率。