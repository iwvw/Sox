# ADR-0021. 设置功能补全：自启、热键、优先级、可配置网络搜索、自更新

日期：2026-10-02
状态：Accepted

## 背景

前端设置面板此前只有外观/搜索/索引/关于四页，缺 Listary 那套日常能力：

- `StartWithWindows` 字段存在但从未落盘，无 UI，无注册表/计划任务实现。
- 呼出热键硬编码 Alt+Space，未接 `UserSettings.Hotkeys`。
- 排除规则只有「排除目录」一段文本框，没有 Listary 那种「高/正常/不常用/不索引」的优先级分层。
- 网络搜索的关键词、URL 模板、图标全部硬编码在 `WebSearchQueryProvider.Sources`。
- 「检查更新」按钮只打日志；Core 有签名校验与提权落盘半程，但没有检查/下载半程，也没有 `portable-updater.bat`。

## 决策

### 1. 开机自启

`StartupService` 写 HKCU `Run` 值 `Sox = "<exe>" --minimized`。Sox.App 不需要提权（只有后台服务要管理员），所以用注册表 Run 而非计划任务（momomi 因主程序提权才用计划任务）。启动时若命令行含 `--minimized` 则驻留托盘不弹窗。默认关闭，由「常规」页开关控制。

### 2. 热键

`HotkeyParser`（解析/格式化 `Ctrl+Shift+D` 这类扁平文本）+ `HotkeyService`（`RegisterHotKey` 生命周期）。呼出热键来自 `UserSettings.SummonHotkey`（默认 `Alt+Space`），设置页改动后经 `App.SettingsChanged` 实时重注册，无需重启。要求至少一个修饰键 + 一个主键。

### 3. 优先级

`PathPriorityRuleSetting { Path, Priority }`，Priority 为 `High / Normal / Uncommon / Excluded`。`PathPriorityResolver` 做前缀匹配（边界感知，`C:\Foo` 不匹配 `C:\Foobar`），最具体规则胜出。High/Uncommon 转成行为分加成（±400，`SearchResultRankComparer.PathPriorityBonus`），Excluded 在 `SearchHost.Snapshot` 里硬过滤。管理 UI 放在「索引」页的「优先级」分组。

### 4. 可配置网络搜索

`WebSearchEngineSetting { Enabled, Keyword, Name, UrlTemplate, SuggestUrl, IconPath, Glyph }`，默认一组内置引擎。`WebSearchQueryProvider` 改为每次读 `UserSettings.WebSearchEngines`，图标支持本地文件（`IconPath`）或内置字形（`Glyph`）。「网络搜索」页提供表格 + 增删改（双击行编辑）。

### 5. 自更新

`AppUpdateService`（App 侧，参考 momomi `AppUpdateService`）：GitHub Releases `latest` API 查版本 → `System.Version` 比较 → 按形态（安装版 `unins000.exe` 判定）/架构选产物 → `HttpClient` 流式下载 → 生成 cmd 脚本等待 Sox 退出后静默安装（安装版）或 robocopy 覆盖（便携版，排除 `Data\`）并重启。启动时延迟 8 秒静默检查（受 `AutoCheckUpdates` 控制），「关于」页显示结果并支持一键更新。

与 momomi 的差异：
- **发布仓库已转为公开**（2026-10-03），自更新走匿名 GitHub Releases API + 镜像链，不再需要 token。此前私有仓库时曾有可选的 `UserSettings.GitHubToken` 与「关于」页输入框，仓库公开后已移除。
- 不复用 Core 的 ECDSA 签名更新通道：那套依赖 `portable-updater.bat` 与提权服务，而 Sox 的安装版由 Inno 自身完成覆盖、便携版是用户目录内 robocopy，无需提权，直接用 cmd 脚本更简单。

### 6. UI 细节

- 高亮色：结果行的查询词高亮从 `SystemAccentColor` 改为主题化的 `Sox.HighlightBrush`（暗色 `#FFD166`、亮色 `#C2410C`），在亚克力卡片上更易辨识。
- 纯名称结果：`ResultItem.IsNameOnly`（副标题为空，典型是应用类）→ `HighlightTextBlock.Emphasized`，名称放大到 16 加半粗，副标题行隐藏。

## 后果

- 设置面板新增「常规」「热键」「网络搜索」三页，「索引」页加「优先级」分组。
- `UserSettings` 新增 `StartWithWindows`（默认改为 false）、`MinimizeToTrayOnStart`、`SummonHotkey`、`WebSearchEngines`、`PathPriorities`。
- 自更新对公开仓库开箱即用（无需任何凭据）。

## 关联

- 参考 `E:\Code\momomi`（`AppUpdateService`、热键录制页）
- ADR-0018（历史排序，优先级加成叠在其上）、ADR-0015（前端 Provider）
- `docs/reference/backend-api-catalog.md`（更新签名与落盘契约）
