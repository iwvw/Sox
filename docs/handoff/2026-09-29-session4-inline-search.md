# Handoff：内嵌搜索窗与对话框集成（会话四）

日期：2026-09-29
状态：进行中

## 会话目标

把 F-11 从「复用主窗口」升级为 Listary 式「贴合文件对话框的独立 mini 搜索窗，自动出现」，并接入 Quick Switch（已打开目录快速跳转）。

## 本轮完成

### F-11 对话框集成本体

| 项 | 内容 |
|---|---|
| 对话框识别 | 新建 `src/Plugins/FileDialog` 插件（`Sox.Plugins.FileDialog.dll`），移植官方 3 个适配器：`StandardFileDialogAdapter` / `ClassicFileDialogAdapter` / `FolderBrowserDialogAdapter`。加载器扫描 `Plugins/` 自动注册，hook 已能识别 `#32770` 对话框 |
| 内嵌 mini 窗 | 新建 `InlineSearchWindow`（独立无边框窗，非复用主窗）。`OnExplorerActivated`（对话框出现）即自动 `Show`，不依赖手动双击 Ctrl |
| 贴合定位 | `Interop/DialogGeometry`：读对话框 DWM 扩展边框 + 工作区 + DPI，宽度取对话框 2/3，水平居中，优先挂对话框下沿（无空间则压上沿），`OnActiveWindowMoved` 时重定位 |
| 回填通道 | 选中结果 → `NavigateDialog` + `RestoreDialogFocus`（经 hook 适配器写回对话框）。通道通，**细节见遗留问题** |

### Quick Switch

| 项 | 内容 |
|---|---|
| 目录采集 | `ExplorerPathCollector`（`IActivePathCollector`，放 FileDialog 插件内）：经 Shell COM `ShellWindows` 枚举已打开的 Explorer 目录 |
| IPC | App 发 `RequestOpenedFolders`，订阅 `OnOpenedFoldersCaptured` |
| 展示 | 内嵌窗下方列出已打开目录（空输入时），点击跳转；hover 直角铺满整行 |

### 视觉与资源

- 新图标：`Assets/sox.ico`（深色圆角底放大镜星标，程序图标）、`Assets/sox-tray.ico`（`search-sparkle` 无边框版，托盘）
- 内嵌窗：实色背景（`WindowSurface`）、无圆角、贴底
- 结果栏重写（见下方「结果栏口径统一」）

### 结果栏口径统一（解决多轮视觉问题）

根因：`ListBox` 默认模板给内部 `ScrollViewer` 加 2px 边框 + 1px 偏移，`540px` 只装 9 项 54px 条目 → 底部空白 + 可多滚一行 + 快捷键比结果多一行。

修法：
- 新增 `ViewModels/ResultListMetrics`（`ItemHeight=54`、`VisibleRows=10`、`ListMaxHeight`、`ListEdgeThickness`）作为**唯一度量源**，XAML 用 `x:Static` 引用
- `ResultList` 换无装饰模板（ScrollViewer 去 padding/border）→ `svH=540 svViewport=10 c0Y=0`（实测）
- 首尾边距补齐：列表上下各补 7px，使首行/末行到顶/底边距离 = 左侧 10px

### Bug 修复

- 空结果不清屏：`Flush` 从不 apply 空快照 → 显式清空
- 空 Path 结果渲染成空白行 → `ApplySnapshot` 过滤
- 虚拟化容器回收残留淡入动画致行不可见 → `ResultItem_Loaded` 非动画分支复位 opacity
- 空状态：有查询但无结果显示「没有匹配的结果」（`ShowEmptyState`，等搜索真正结束才显示）

## 未完成

### 遗留问题（优先）

1. ~~回填落点在文件名框、未跳转~~ **已修（2026-09-29）**：根因是 App 发 `NavigateDialog` 前缺 `AllowSetForegroundWindow(hookPid)`，导致 hook 在 `Task.Delay(300)` 后的 `SetForegroundWindow` 受前台锁阻拦，回车确认落不到对话框。补上授权后生效（实测 `allowed=True`，跳转正常）。
2. **主窗阴影过重**：DWM 阴影无强度参数，唯一开关 `DWMWA_NCRENDERING_POLICY=DISABLED` 会连亚克力一起关（已实测失效并回退）。要减只能换窗口方案（放弃 `SingleBorderWindow` 改自绘无边框），代价大，待定。

### P2

命令执行/网页搜索/计算器（F-16）、输入历史联想（F-17）。

### 收尾

内存实测（NFR-01 < 100MB）、单文件发布（NFR-07）、git 首次提交。

## 关键坑（下次别再踩）

1. **改核心或插件后必须 `sc stop SoxService`**，否则 dll 被服务/hook 进程锁定，构建拷贝失败（`MSB3027 file locked`）。
2. **ListBox 默认模板带装饰**：算高度必须用无装饰模板，否则视口比设定高度少一截。
3. **DWM backdrop 需非 layered 窗口**（`AllowsTransparency=True` 会失效亚克力）。
4. **DWM 非客户区渲染**：关阴影 = 关亚克力，同一个开关。
5. **双击 Ctrl 节奏**：核心窗口 100-300ms，实测用户常按 500-800ms 不触发。
6. **官方 Lertaro 同跑会抢手势/前台**，测 Sox hook 前先停掉。
7. **回收容器动画残留**：`Loaded` 非动画分支要复位 opacity/transform。

## 调试入口

- hook 事件：`%TEMP%\sox_hook_events.txt`
- hook 日志：`src/Sox.App/bin/Debug/net10.0-windows/Data/Users/<hash>/logs/hook.log`
- 服务日志：同目录 `Data/Machine/logs/service.log`
- 搜索耗时：`%TEMP%\sox_search_timing.txt`

## 下一步

1. 修回填跳转（遗留 1）
2. F-16 / F-17
3. 内存实测 + 单文件发布 + git 提交
