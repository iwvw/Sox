# ADR-0022. 文件对话框集成：底部吸附搜索面板 + Quick Switch

日期：2026-10-02
状态：Accepted

## 背景

Listary 的核心卖点之一是文件对话框集成：在任意「打开 / 保存」对话框里输入路径片段，直接跳转目录；以及在对话框内按 Ctrl+G 一键跳回刚才在资源管理器里浏览的目录（Quick Switch）。

Sox 的后端（Lertaro 快照）已经带了一套 `ExplorerTracker` + 文件对话框适配器（`#32770` 识别、面包屑取当前路径、`NavigateTo` 回填路径）和 hook 进程，但前端 WinUI 3 一侧此前完全没有接入，Quick Switch 与内嵌面板都不可用。

## 决策

**前端接入 hook，实现两个能力：Quick Switch（Ctrl+G）与底部吸附搜索面板。**

### 1. hook 集成

`MainWindow` 构造时创建 `HookIpcClient` 并 `Start()`：连接命名管道，按需拉起提权 hook 进程（`Sox.Service.exe --hook`）。订阅 `OnExplorerActivated` / `OnPathCaptured` / `OnExplorerDeactivated`。

hook 进程的调用方校验（`HookLaunchRequestHandler.IsGenuineAppProcess`）必须同时接受两种布局：开发构建（App 与服务同目录）与正式安装（App 在根目录、服务在 `Service\` 子目录，即 App 目录是服务目录的父目录）。旧代码只接受同目录，导致**所有正式安装版都无法拉起 hook**。

### 2. Quick Switch

Ctrl+G 的手势识别与导航完全在 hook 进程内完成（`GlobalHotkeyDetector.TryHandleQuickSwitchNavigation`），前端只要把 hook 拉起来即可，无需额外代码。

### 3. 底部吸附搜索面板（`InlineSearchWindow`）

- 一个独立的 WinUI 3 窗口，`#32770` 对话框激活且路径捕获后显示，吸附在对话框下方、宽度约为对话框的 62% 并居中。
- 窗口不是置顶（WS_EX_TOPMOST 会盖住无关应用），而是设为对话框的 **owned window**（`GWLP_HWNDPARENT`），只跟随并浮于该对话框之上；隐藏时解除归属。
- 窗口就是卡片：`CardControl` `ShadowPadding=0`、`ShowShadow=False`、默认 `CardCornerRadius=8` 自绘抗锯齿圆角；DWM 用 `DWMWCP_DONOTROUND` 避免二次圆角。透明 margin 会参与命中测试、吞掉对话框边缘的点击，所以不留 padding。
- 定位用**对话框所在显示器的 DPI** 换算物理像素后 `SetWindowPos`，而不是 `MoveAndResize`（后者按面板窗口当前 DPI 换算，跨不同缩放显示器时用旧值算错）。竖直位置用 DWM `DWMWA_EXTENDED_FRAME_BOUNDS`（真实可见边缘）而非 `GetWindowRect`（含不可见阴影边框），消除与对话框之间的空隙。
- 空查询显示其它已打开的资源管理器目录（Quick Switch 列表）；有查询走全局搜索，最多 60 条、列表最高 7 行并可滚动，选中目录跳转该目录、选中文件跳到其所在目录。
- 搜索在 `Task.Run` 中执行（`SearchStreamingAsync` 首个 await 前会同步读设置与历史库），并用按路径复用的 `ResultItem` 缓存避免每按键重载 shell 图标。

### 4. 跳转提交

适配器 `NavigateTo` 把路径写入对话框文件名框后，通过 `PostMessage` 回车提交。提交**无条件执行**：早期的「仅当对话框是前台窗口才提交」门控与面板抢焦点形成竞态，表现为要反复点击才生效。`PostMessage` 投递到对话框自己的线程，不受前台窗口影响。

## 结果

- 正式安装版与开发版都能拉起 hook。
- 面板可显示、定位正确、跨 DPI 正确、可滚动、可跳转。
- Quick Switch 由 hook 独立完成。

## 相关文件

- `src/Sox.App/MainWindow.xaml.cs`（`StartHookIntegration` / `OnExplorerActivated` / `OnPathCaptured`）
- `src/Sox.App/InlineSearchWindow.xaml(.cs)`
- `src/Sox.Core/Services/HookLaunch/HookLaunchRequestHandler.cs`
- `src/Plugins/FileDialog/{Standard,Classic}FileDialogAdapter.cs`
- `src/Sox.Service/Service.csproj`、`src/Plugins/FileDialog/FileDialog.csproj`（插件打包）
