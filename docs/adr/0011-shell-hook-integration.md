# ADR-0011. Shell 集成：复用核心 hook 进程与 IPC

日期：2026-09-29
状态：Accepted（实现部分挂起）

## 背景

Listary 的灵魂功能是「在打开/保存对话框里呼出搜索、选中后回填路径」。核心 Lertaro 已把这套做成了完整子系统：一个提权 hook 进程（`Sox.Service --hook`）监听键盘/鼠标/资源管理器/对话框，通过命名管道与 App 通信。

自研前端要在不重写这套子系统前提下接入它。

## 决策

- 复用核心的 `HookIpcClient`（App 侧）与 `HookProcess`（hook 侧），不自行实现键盘钩子/对话框探测
- App 启动时 `new HookIpcClient().Start()`：它经服务 `RequestHookLaunchAsync` 拉起提权 hook 进程，并连接 `HookIpcNames` 的两个管道（event + cmd）
- 事件接入：
  - `OnActivated`（双击 Ctrl 手势）→ 呼出 Soy 窗口
  - `OnExplorerActivated`（活动对话框 hwnd + 路径）→ 记录对话框上下文
  - `OnPathCaptured`（是否对话框）→ 标记对话框模式
- 路径回填：选中结果时若在对话框上下文，发 `IpcMessageId.NavigateDialog`（Hwnd + StringVal1=路径）+ `RestoreDialogFocus`，由 hook 的 `IFileDialogAdapter.NavigateTo/RestoreFocus` 执行

## 后果

- 基础设施零成本复用，App 侧只做「事件→UI」「选中→发消息」的薄接线
- hook 的呼出手势由核心 `UserSettings.Hotkeys.ToggleWindowHotkey`（默认双击 Ctrl，窗口 100-300ms）控制，App 无法直接改其节奏（属核心设置）
- **已知已定位（2026-09-29）**：双击 Ctrl 链路三环实测均正常（手势 100-300ms 窗口、hook `Double-Ctrl detected`、App `OnActivated`）。此前"没反应"的真实原因是**用户操作节奏 500-800ms，超出双击窗口**；用 0.15-0.25s 节奏即命中。残留待确认：`OnActivated` 后窗口显示与对话框场景、以及官方 Lertaro 同时运行时的 hook 抢占
- 对话框内嵌搜索栏的完整窗口定位（geometry 探测/位置计算/键盘路由，官方 31 文件）未做，当前只打通「呼出 + 路径回填」通道

## 关联

- 取代 ADR-0007（原计划自写 CBTProc 钩子 + SendInput 注入）
- 依赖 ADR-0010（核心快照）