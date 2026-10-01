# ADR-0004. 进程模型：单进程 + requireAdministrator

日期：2026-09-29
状态：Superseded（已被 ADR-0010 转向实际推翻：改用 Lertaro 核心的三进程模型，App 不再 requireAdministrator，改为按需 UAC）

> 现状（2026-09-30 核实）：Sox 实为**三进程**（`SoxService` 服务 + `Sox.App` + hook），`Sox.App` 的 `app.manifest` 无 `requireAdministrator`，安装/启动服务时才 `runas` 提权。本 ADR 记录的是自研骨架时期的决策，已不适用，保留供追溯。

## 背景

USN 读取需要管理员权限。候选：单进程提权 / 双进程（UI + 提权索引服务）+ IPC。

- 双进程：UI 崩索引还在，但共享内存 +30MB、IPC（named pipe）通信复杂度上升
- 单进程：内存最省、无 IPC，UI 崩则全崩，靠守护重启兜底

## 决策

- 单进程 + `requireAdministrator` 清单
- 崩溃守护：托盘监控主窗口存活，异常退出自动重启
- 保留双进程拆分作为后期优化项（仅当发现单进程不够用时再动）

## 后果

- 内存最省（一份进程 + 共享索引）
- 进程崩溃（含守护）期间搜索不可用；以管理员身份常驻（自用工具可接受）
- 热重载 / 调试体验更简单：单进程无需跨进程重连
