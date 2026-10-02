# Handoff：文件对话框集成 + 应用搜索修复（会话十二）

日期：2026-10-02
状态：全部实现并构建通过（0 warning / 0 error）；核心行为已用日志客观验证；待用户视觉验收

## 本轮完成（对应 ADR-0022）

| 需求 | 实现 |
|---|---|
| hook 集成 | `MainWindow.StartHookIntegration` 创建并启动 `HookIpcClient`，订阅 `OnExplorerActivated` / `OnPathCaptured` / `OnExplorerDeactivated` |
| 正式安装版无法拉起 hook | 修 `HookLaunchRequestHandler.IsGenuineAppProcess`：同时接受同目录（dev）与「App 是服务父目录」（安装版 `App\` + `Service\`） |
| 插件打包缺失 | `Service.csproj` 加 `FileDialog` / `PinyinAlias` 的 `ProjectReference` + `CopyPluginsToOutput/Publish`；从两插件 csproj 移除错误 PostBuild |
| Quick Switch | Ctrl+G 手势与导航全在 hook 内完成（`GlobalHotkeyDetector`），前端只需拉起 hook |
| 底部吸附面板 | 新增 `InlineSearchWindow`（独立 WinUI 窗，owned window，DWM 圆角，按对话框 DPI 定位，空查询列已打开目录，有查询走全局搜索，可滚动，选中跳转） |
| 全屏禁用呼出 | `FullscreenDetector` + `UserSettings.DisableHotkeyInFullscreen` +「快捷键」页开关（默认关） |
| 托盘退出服务 | 托盘「退出并停止服务」+ `ServiceBootstrapper.TryStop` |
| 打包应用搜索 | `AppsFolderEnumerator`（STA 枚举 shell:AppsFolder）+ `ApplicationQueryProvider.AppendAppsFolderApps`；`ShellIconProvider` 支持虚拟 shell 路径取图标 |
| 拼音别名接入 App | `CoreAliasBootstrap`（App 进程注册 `PinyinAliasProvider`，让 jsb 命中记事本） |
| 应用历史误显示 AUMID | `SearchHost.InjectHistoryMatches` 跳过 `HistoryEntryKind.Application` |
| 跳转需反复点击 | 适配器 `NavigateTo` 回车提交改无条件（去掉前台窗口门控竞态） |
| 面板跨 DPI 尺寸错乱 | 定位改用对话框显示器 DPI 算物理像素 + `SetWindowPos` |
| 面板结果截断不可滚动 | 上限 60 条、列表 `MaxHeight=336` 并启用滚动 |
| 面板搜索慢 | 搜索移入 `Task.Run` + 按路径复用 `ResultItem` 缓存 |
| GitHub Token 位置 | 从「常规」页移到「关于」页更新区 |

## 关键坑（本轮踩到并解决）

1. **stop/start 竞态把服务卡在 STOP_PENDING**：`sc stop` 未等到 STOPPED 就 `sc start`，`UsnService.OnStop` 被中断挂死，LocalSystem 进程非管理员杀不掉（需 `RunAs` 提权 taskkill）。教训：每次 `sc stop` 后轮询到 STOPPED 再继续。
2. **改插件代码必须停服务**：`Sox.Service.exe` 持有 `Service\Plugins\*.dll`，不停服务会 MSB3027 文件锁。
3. **面板透明 margin 会吞点击**：`ShadowPadding>0` 时窗口比卡片大，透明区仍参与命中测试。面板窗口必须等于卡片。
4. **owned window 取代 TOPMOST**：`IsAlwaysOnTop=true` 会让面板盖住所有应用；改用 `GWLP_HWNDPARENT` 设为对话框的 owned window。
5. **导航提交竞态**：适配器的回车提交曾被 `GetForegroundWindow()==对话框` 门控，与面板抢焦点冲突；`PostMessage` 本身不受前台影响，无条件提交即可。
6. **应用历史注入**：历史里 `Kind=Application` 的项 Path 是 `shell:AppsFolder\{AUMID}`，注入成文件行后标题是原始 AUMID、副标题是 `shell:AppsFolder`，还会被无关查询误召回（"wows" 是 "Windows" 子序列）。已跳过。

## 客观验证（已通过）

- `dotnet build Sox.slnx -c Debug`：0 warning / 0 error
- hook 日志：`Hooks and ExplorerTracker initialized successfully`
- App 日志（加诊断后实测）：`Inline: dialog=(822,250,1760,865) scale=1 set=(1000,865,582,81) vis=True`
- 适配器提交日志证实修复前 `allowed=False` 反复出现（竞态），修复后无条件提交

## 待用户视觉验收

1. 打开文件对话框：底部面板出现、宽约对话框 62% 居中、圆角/边框正常、与对话框间距极小
2. 空查询列已打开目录、输入关键字搜索结果可滚动（>7 条）、点条目/回车跳转
3. 在对话框内按 Ctrl+G 跳回上一个资源管理器目录
4. 面板不置顶（切到其它应用时不应盖住它们）
5. 搜 "wows" 首行应为应用「记事本/Notepad」而非 AUMID；搜 "jsb" 能命中记事本
6. 全屏开关、托盘「退出并停止服务」、关于页 Token

## 下一步

- 面板右上角「收藏 / 历史 / 工具」三个按钮目前是装饰，无事件处理；如需要应接功能或移除
- 面板跳转使用的是插件既有 `NavigateTo`；Classic 适配器同步做了无条件提交，但未在真实老式对话框上验证
- 发布 v0.3.0（版本号已 bump，notes 已写），打 tag 推送触发 release.yml

## 相关文件

- `src/Sox.App/InlineSearchWindow.xaml(.cs)`
- `src/Sox.App/MainWindow.xaml.cs`、`src/Sox.App/Services/{CoreAliasBootstrap,FullscreenDetector,ServiceBootstrapper,ShellIconProvider}.cs`
- `src/Sox.App/Services/QueryProviders/{ApplicationQueryProvider,AppsFolderEnumerator}.cs`
- `src/Sox.App/Services/SearchHost.cs`、`src/Sox.App/Controls/CardControl.xaml(.cs)`
- `src/Sox.Core/Services/HookLaunch/HookLaunchRequestHandler.cs`、`src/Sox.Core/Settings/UserSettings.cs`
- `src/Plugins/FileDialog/{Standard,Classic}FileDialogAdapter.cs`、`src/Sox.Service/Service.csproj`
- `docs/adr/0022-file-dialog-integration.md`
