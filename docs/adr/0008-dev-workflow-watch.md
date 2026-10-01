# ADR-0008. 开发工作流：dotnet watch + DebugOverlay + 索引缓存落盘

日期：2026-09-29
状态：Accepted（前端热重载部分受 ADR-0016 影响，见文末注记）

> 注记（2026-09-30）：本 ADR 的 `dotnet watch` 热重载假设基于 WPF。前端重写为 WinUI 3 后（ADR-0016），`dotnet watch` 对 WinUI 3 的 XAML 热重载支持有限（WinUI 3 更依赖 VS 的 XAML 热重载 / `Debug` 重部署），需在实施时重新评估。DebugOverlay 与索引缓存落盘的决策不受影响。

## 背景

开发模式要求「用户观察 + Agent 实时修改 + 实时生效」的闭环，且每次改动不能打断观察流（重启全盘重扫不可接受）。

## 决策

- 用 `dotnet watch` 作开发主循环：改 XAML / 方法体热重载，改类型结构自动重启
- `dev.ps1` 封装启动：watch + 日志重定向（`%TEMP%\sox\logs\`）
- DebugOverlay（F12）：窗口角落实时显示内存 / 索引条数 / 查询耗时，供量化观察
- 索引缓存落盘：启动加载缓存 + USN 增量比对，重启 1-2s 恢复（生产开机启动同样受益）
- Agent 协作规则：改完代码必回读日志确认编译与运行无异常

## 后果

- 用户全程无操作，窗口永远跟得上最新代码
- 热重载失败的改动以秒级重启补偿，观察流几乎不断
- 日志与 DebugOverlay 成为 Agent 与用户之间的「量化观察通道」
