# Handoff：设置功能补全 + 自更新（会话十一）

日期：2026-10-02
状态：全部实现，构建通过（0 warning / 0 error），核心逻辑已客观验证；待用户视觉验收

## 本轮完成（对应 ADR-0021）

| 需求 | 实现 |
|---|---|
| 开机自启 + 自启最小化 | `Services/StartupService.cs`（HKCU Run 值 + `--minimized`）；「常规」页开关；启动带 `--minimized` 则驻留托盘 |
| 快捷键设置 | `Services/HotkeyParser.cs` + `Services/HotkeyService.cs`；`UserSettings.SummonHotkey`（默认 Alt+Space）；「热键」页录制控件 |
| 优先级设置 | `PathPriorityRuleSetting`（高/正常/不常用/不索引）+ `PathPriorityResolver`；「索引」页优先级分组；加成叠在行为分上，Excluded 硬过滤 |
| 网络搜索可配置 | `WebSearchEngineSetting` + `WebSearchDefaults`；`WebSearchQueryProvider` 改读设置；「网络搜索」页表格增删改；图标支持本地文件或内置字形 |
| 高亮颜色 | 结果行高亮改用主题化 `Sox.HighlightBrush`（暗 `#FFD166` / 亮 `#C2410C`） |
| 纯名称结果放大居中 | `ResultItem.IsNameOnly` → `HighlightTextBlock.Emphasized`（16 半粗，隐藏空副标题行） |
| 自更新与版本检测 | `Services/AppUpdateService.cs`（查 Releases、下载、cmd 脚本覆盖重启）；启动静默检查 + 「关于」页一键更新 |

## 新增设置字段（UserSettings）

`StartWithWindows`（默认改为 false）、`MinimizeToTrayOnStart`、`SummonHotkey`、`WebSearchEngines`、`PathPriorities`、`GitHubToken`。

## 客观验证（已通过）

- 构建：`dotnet build -r win-x64 -c Debug` 0 warning 0 error
- 启动：进程 Responding，日志无异常
- 更新检测端到端：`Update check: current 0.1.1, latest 0.1.1, hasUpdate=False`
- `--minimized` 启动：窗口 `cloaked=1`（隐藏）
- 优先级解析（无头探针）：High/Excluded/Uncommon/边界（`D:\APPLE` 不匹配 `D:\APP`）/加成 ±400 全部正确
- 自启注册表值格式：`"<exe>" --minimized`

## 关键决策 / 坑

- **私有仓库 + 自更新**：Sox 仓库是 private，匿名 GitHub Releases API 返回 404。新增可选 `GitHubToken`；填了 token 只直连（镜像会剥离 Authorization），留空走镜像链（公开仓库用）。**当前默认留空，私有仓库下自动检查会失败，需在「常规」页填 token**。
- **自启用注册表而非计划任务**：Sox.App 不提权（只有服务要管理员），注册表 Run 足够；momomi 因主程序提权才用计划任务。
- **不复用 Core 的 ECDSA 更新通道**：那套依赖 `portable-updater.bat` + 提权服务；Sox 安装版由 Inno 自身覆盖、便携版 robocopy 用户目录，无需提权。
- `HighlightTextBlock` 绑 `FontWeight` 会让 XAML 编译在 pass2 崩（`Unknown type`）；改为控件自己的 `Emphasized` 依赖属性。
- public 页面构造用到的服务类型必须 public（`AppUpdateService`/`AppUpdateInfo`）。

## 待用户视觉验收

1. 「常规」页：自启开关写入注册表、最小化到托盘
2. 「热键」页：录制新组合键并即时生效（旧组合键释放）
3. 「网络搜索」页：增删改引擎、图标、关键字
4. 「索引」页优先级：高/不常用影响排序、不索引真正排除
5. 高亮颜色可辨识度、应用类结果名称放大
6. 「关于」页：检查更新 / 一键更新（需先填 token）

## 下一步

- 若要公开仓库：去掉 `GitHubToken` 依赖，或改为构建期注入
- 自更新尚未做哈希/签名校验（momomi 同样只校验 Content-Length）；如介意可接回 Core 的 ECDSA 通道
- 发布 v0.1.2（版本号在 `Directory.Build.props`）

## 相关文件

- `src/Sox.App/Services/StartupService.cs`、`HotkeyParser.cs`、`HotkeyService.cs`、`AppUpdateService.cs`
- `src/Sox.Core/Settings/FeatureSettings.cs`（优先级 + 网络搜索模型）
- `src/Sox.Core/SearchResult.cs`（PathPriorityBonus）、`src/Sox.App/Services/SearchHost.cs`（加成 + 排除过滤）
- `src/Sox.App/Settings/GeneralPage.*`、`HotkeyPage.*`、`WebSearchPage.*`、`IndexPage.*`、`AboutPage.*`
- `docs/adr/0021-settings-features-and-self-update.md`
