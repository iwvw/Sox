# ADR-0012. 对话框内嵌 mini 搜索窗（独立窗口 + 自动出现）

日期：2026-09-29
状态：Superseded（实现部分由 ADR-0016 取代：内嵌窗交互设计保留，WPF 实现重写为 WinUI 3）

## 背景

ADR-0011 复用了核心 hook 打通了「双击 Ctrl → 呼出 → 选中回填」，但 UI 复用的是居中的 **Spotlight 主窗口**：它悬在屏幕顶部中央、与对话框完全分离，交互上不像 Listary。

用户要求：**体检贴合文件对话框底边的独立 mini 搜索窗**，Listary 风格，**对话框一出现就自动出现**，不依赖手动双击 Ctrl。

现状（已验证可用）：
- `Sox.Plugins.FileDialog` 插件（3 个适配器）已让 hook 能识别文件对话框
- hook 已发 `OnExplorerActivated`（对话框 hwnd + 标题）、`OnPathCaptured`（当前目录 + dialog 标志）、`OnExplorerDeactivated`、`OnActiveWindowMoved`
- App 已订这些事件，主窗口能贴合定位（`SearchWindow.PositionForDialog`）
- `NavigateDialog` + `RestoreDialogFocus` 回填通道已通

## 决策

新建**独立的 `InlineSearchWindow`**（不复用 `SearchWindow`），并改变出现方式：

1. **独立窗口**：紧凑、无边框、无标题（`WindowStyle=None`、`ResizeMode=NoResize`、`ShowInTaskbar=False`、`Topmost=True`、`WindowStartupLocation=Manual`），只含搜索框 + 少量结果行（复用 `SearchViewModel`，行数上限收敛）。
2. **自动出现**：`OnExplorerActivated`（识别到文件对话框）即 `Show()` 并贴合对话框底边定位；不要求用户先按双击 Ctrl。
   - 双击 Ctrl（`OnActivated`）保留为「聚焦已出现的 mini 窗」的补充手势。
3. **自动消失**：`OnExplorerDeactivated` 或自身失焦 / Esc 即 `Hide()`。
4. **贴合定位**：读对话框 `DWM 扩展边框`（`DialogGeometry`），宽取其 `2/3`，水平居中对齐；优先挂在对话框下沿，下方无空间则压在上沿。`OnActiveWindowMoved` 时重定位。
5. **不抢焦点（首版）**：`ShowActivated=False` + 不调用 `Activate()`，避免打断用户在对话框里的正常操作；用户可点击 mini 窗或按双击 Ctrl 聚焦后再输入。
6. **回填**：mini 窗选中结果 → 复用已有的 `NavigateDialog`（经 `DialogNavigator` 委托）→ 回填对话框 → `Hide()`。

## 后果

- UI 与手势解耦：对话框出现即出现，贴合、紧凑
- 主 `SearchWindow`（Spotlight）保持不变，仍由 Alt+Space 呼出；对话框场景不再借用它
- 首版不抢焦点，属于保守选择；若体验上希望「出现即可直接打字」，下一轮改为出现即聚焦（`ShowActivated=True` + `Activate()`），并观察是否打断对话框正常流程
- 键盘路由仍沿用核心 hook 的对话框语义（对话框场景 hook 不吞键），最终输入归属由「焦点是否在 mini 窗」决定

## 关联

- 延续 ADR-0011（复用 hook 进程与 IPC）
- 依赖 `Sox.Plugins.FileDialog`（对话框识别）
- 取代 ADR-0011 中「复用主窗口做对话框呼出」的形态
- 被 **ADR-0016** 取代（实现层）：交互设计（自动出现、贴合定位、回填通道）保留，WPF 窗口实现重写为 WinUI 3
