# Handoff：前端功能补齐（会话三）

日期：2026-09-29
状态：进行中

## 会话目标

在「Lertaro 核心 + 自研 WPF 前端」的基础上，把 PRD 里的功能逐项补齐。

## 本轮完成

| 项 | 内容 |
|---|---|
| 亚克力 + 圆角 | DWM `SystemBackdropType=Acrylic` + `CornerPreference=Round` + `WindowChrome GlassFrameThickness=-1`（关键：需 app.manifest 声明 Win10/11，否则 OSVersion 判成 Win8 而失效） |
| 不吃 Alt+Tab | `WS_EX_TOOLWINDOW` |
| 搜索栏图标 | 右侧 34px `Assets/search-sparkle.png` |
| 拼音搜索 | 拷贝核心 PinyinAlias 插件到 `src/Plugins/`，改名 `Sox.*`，构建后复制到 App/Service 的 `Plugins/`；**改核心后必须重启 SoxService 并让别名 regen**（否则旧索引快照没拼音） |
| 关键词高亮 | `HighlightTextBlock` 照搬官方 `TextHighlighter`：`FuzzyQuery.Parse` 逐词 + `d:` 盘符 + 路径拆分；Name 和 Path 两列都高亮，前景色 `ResultMatchForeground` |
| 结果不跳动 | keyed diff（按 Path 保留/插入/移除，保留行不重建）+ 新行 `AnimateIn` 一次性淡入；高度**直接绑定** `ListHeight`（曾用动画导致弹簧感，最终选直接绑定） |
| 查询缓存 | LRU 64，相同词回退秒出（状态栏显示「缓存」） |
| 流式渲染 | 结果边到边显示（flush 25ms），防抖 15ms，`maxResults=20` |
| 首搜慢 | 根因：每次启动都发 INITIALIZE → 服务重载缓存 + 20s 监控空窗。改为仅冷启动时初始化 |
| F-06 历史排序 | 打开时 `SearchHistoryStore.Record`；结果用 `SearchResultRankComparer` 历史优先排序 |
| F-08 空输入最近 | 空输入显示收藏（置顶）+ 最近打开 |
| F-14 DebugOverlay | F12 切换，显示内存/结果数/耗时 |
| F-12 右键菜单 | `HKCU\Software\Classes\Directory\shell\SoxSearch`（免提权）+ `--dir` 参数经单实例管道转发 |
| F-13 设置面板 | 托盘右键 → 设置：跟随系统主题 / 热键模式 / 结果条数（存 `%APPDATA%\Sox\ui-settings.json`） |
| F-15 收藏 | `Ctrl+D` 切换收藏，空输入收藏置顶 |

## 未完成

### F-11 对话框内呼出（已挂起，待专门一轮）

按 ADR-0011 的架构接入后，遇到一个**未定位的阻断点**，暂停细调。

**已确认可用（日志实测）**：
- App 启动 `HookIpcClient`，服务自动拉起提权 hook 进程（`Hook starting with arguments: --hook` → `Hooks and ExplorerTracker initialized successfully`）
- App 与 hook 的 IPC 通道连通（`[HookIpcServer] App connected on both pipes`）
- hook 里也加载了 PinyinAlias 插件
- App 侧订阅了 `OnActivated` / `OnExplorerActivated` / `OnPathCaptured`
- 选中结果的路径回填通道已写：`NavigateDialog` + `RestoreDialogFocus`（走 hook 的 `IFileDialogAdapter.NavigateTo/RestoreFocus`）

**阻断点（已定位，2026-09-29 诊断）**：链路三环全部实测正常。
- **手势**：核心窗口 = `100ms < 间隔 < 300ms`（`ModifierDoubleTapDetector.cs`）。写独立最小低级键盘钩子程序复刻同款检测，实测 140-200ms 双击稳定触发 `DOUBLE-CTRL FIRED`
- **hook → IPC**：`hook.log` 出现 `[HookProcess] Double-Ctrl detected, sending ACTIVATE`
- **IPC → App**：`%TEMP%\sox_hook_events.txt` 出现 `hook OnActivated`
- **根因**：之前"没反应"是**双击节奏过慢**。实测用户操作间隔 500-800ms，超出 100-300ms 窗口，因此一次都没触发。用 0.15-0.25s 节奏即命中

**残留待确认**：
1. `OnActivated` 后窗口是否真正显示（`FocusQuery()` 有 `Show()+Activate()+Force` 前台逻辑，但未做窗口可见性实测）
2. 对话框场景是否与普通窗口一致（核心在对话框会早退放行，但双击检测在放行之前，理论上仍触发）
3. **环境干扰**：官方 Lertaro 若同时运行（`LertaroService` + `Lertaro.App`），其 hook 也监听双击 Ctrl，会抢手势/前台，测试前应停掉

**下一步**：
1. 停掉官方 Lertaro，仅保留 Sox，用 0.15-0.25s 节奏验证普通窗口 + 对话框两种场景
2. 若窗口不显示，再排查 `ForegroundHelper.Force`（UIPI/前台权限）

### P2

命令执行/网页搜索/计算器（F-16）、输入历史联想（F-17）。

## 关键坑（下次别再踩）

1. **DWM backdrop 必须非 layered 窗口**：`AllowsTransparency=True` 会失效，用 `WindowStyle=SingleBorderWindow` + `WindowChrome`。
2. **manifest 必须声明 supportedOS**，否则 `Environment.OSVersion` 返回 6.2 导致所有 Win11 分支失效。
3. **改核心后要 `sc stop/start SoxService`** 让服务重载新 dll 并 regen 别名。
4. **图标**：缩略图按完整路径缓存（专辑封面），类型图标按扩展名缓存；提取在 UI 线程。
5. **结果动画**：只有 keyed diff 之后，行淡入才安全（否则虚拟化回收会重播）。

## 调试入口

- 搜索耗时：`%TEMP%\sox_search_timing.txt`（每次搜索追加一行）
- 服务日志：`src/Sox.App/bin/Debug/net10.0-windows/Data/Machine/logs/service.log`
- 索引缓存：同目录 `Data/Machine/indexes/*.idx`

## 下一步

1. 做 F-11 对话框内呼出（单独一轮）
2. 收尾 F-16/F-17
3. 内存实测（NFR-01 < 100MB）与单文件发布（NFR-07）
