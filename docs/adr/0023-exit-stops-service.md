# ADR-0023. 退出即停服务:合并托盘的两个退出项

日期:2026-11-24
状态:Accepted

## 背景

托盘右键菜单从 ADR-0019 起有两个退出项:

| 菜单项 | 行为 |
|---|---|
| 退出 | 只关 UI(`Application.Current.Exit()`),服务继续跑 |
| 退出并停止服务 | 先 `sc stop SoxService`,再关 UI |

服务是独立注册的 Windows 服务(`StartType=auto`,`ServiceInstaller.cs`),所以「退出」之后 `Sox.Service.exe` 仍在后台运行、继续索引。

用户实测后认为这是缺陷:点了「退出」,界面上"程序已经关了",但进程列表里服务还在,理解成"没有同步退出"。两个含义接近的退出项并列,本身也让人在选择时困惑。

## 决策

**合并为一个「退出」:点它 = 关 UI + 停服务。删掉「退出并停止服务」这一项。**

即:App 不再是"关掉界面后后台还留一个常驻服务"的模式;用户主动退出的语义统一为"整个 Sox 都停下"。

### 为什么

- **语义一致性**:用户视角里 Sox 是一个应用,不是"界面 + 后台服务"两件东西。给了两个退出项,等于把内部进程架构暴露给了用户,而这两项对用户的差别(停不停索引服务)多数人并不关心,只会选错。
- **用户明确要求**:本轮由用户拍板选择「合并」而非保留双项。
- **代价可接受**:服务停掉后索引不再常驻,下次启动 App 要重新拉起服务并加载索引(`SearchEngineInitializer` 走缓存路径,日志显示 3/3 盘秒级载入)。这是"退出"这个动作本来的预期成本。

### 保留不退服务的情形

- **自更新**(`ExitForUpdate` → `Environment.Exit(0)`)**不停服务**。更新脚本自己负责 `sc stop` → `robocopy` → `sc start`(见 ADR-0021),App 提前停服务会和脚本时序冲突。
- **崩溃 / 任务管理器强杀 / 会话注销**:不经过菜单,自然也不会停服务;服务由 SCM 的恢复策略兜底。

### 行为细节

- 停服务沿用 `ServiceBootstrapper.TryStop()`:`sc stop` 后轮询 SCM 直到 `STOPPED`(最长 20 × 250ms = 5s),在**后台线程**执行,避免阻塞 UI 线程。
- 停服务失败(超时/异常)不阻止退出:记 Warn 日志后照常关闭 App,不把用户卡在退不掉的界面上。

## 影响

- `TrayMenu.xaml`:删除「退出并停止服务」项。
- `TrayIconService`:`ExitAndStopServiceRequested` 事件移除,`ExitRequested` 保留。
- `MainWindow`:删掉 `ExitAppAndStopService`,`ExitApp` 改为"停服务 + 退出"。

## 相关

- ADR-0019(托盘菜单)
- ADR-0021(设置/特性/自更新,含更新脚本的服务停启时序)
- `docs/reference/backend-api-catalog.md`(服务拉起/停止)
