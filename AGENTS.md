# AGENTS.md

面向 Agent 的协作约定。任何 Agent 接手本仓库前，先读本文件。

## 项目简介

Sox 是一款 Windows 原生文件搜索工具（Listary + Everything 结合体）：Spotlight 式悬浮搜索框、全盘文件名秒搜、历史学习智能排序。技术路线：**后端复用 Lertaro 核心（MIT，已改名 Sox.\*）+ 前端重写为 WinUI 3**（见 ADR-0016）。单人自用项目，开发方式为「用户观察 + Agent 实时修改」循环。

## 技术栈

- .NET 10 LTS
- 后端核心（源自 Lertaro MIT 快照，见 docs/adr/0010）：
  - `Sox.Core`：USN/MFT 索引引擎、搜索、IPC Wire 协议、SearchService 多源编排（**零 UI 依赖**，原样复用）
  - `Sox.Service`：Windows 服务宿主（服务名 `SoxService`，命名管道 `SoxPipe`）
  - `Sox.PluginSdk`：插件契约
- 前端：`Sox.App`（**WinUI 3 + Windows App SDK 2.x**，卡片材质架构，见 ADR-0016）
  - 目标框架 `net10.0-windows10.0.26100.0`，`UseWinUI=true`
  - 打包：非打包 exe 与 MSIX 两种形态
  - 依赖：WinUIEx、CommunityToolkit.WinUI.*、Microsoft.Graphics.Win2D

## 关键参考文档

- `docs/reference/backend-api-catalog.md`：后端全部对外 API（前端对接必读）
- `docs/reference/powertoys-spotlight-material.md`：CmdPal 材质与架构参考

## 文档体系（改代码前必读）

```
AGENTS.md            本文件：协作约定
CONTEXT.md           领域术语与项目心智模型
docs/PRD.md          产品需求与验收边界
docs/adr/            架构决策记录（决策先行）
docs/handoff/        会话交接文档（会话结束时更新）
docs/reference/      后端 API 目录、CmdPal 材质调研等参考
```

协作规则：

1. 每个会话开始：读 AGENTS.md → CONTEXT.md → 最新 handoff → 相关 ADR
2. 每次拍板新决策：先写 ADR 再动代码
3. 每次会话结束：更新 handoff（进度 + 阻塞 + 下一步）
4. 引入新术语：同步更新 CONTEXT.md 术语表
5. PRD 范围外的东西不做，做了要改 PRD

## 开发循环

```
用户观察窗口 → 反馈问题 → Agent 改代码保存 → 生效 → 用户再观察
```

- **VSCode 调试为主**：按 F5 启动 `Debug Sox.App`。build 任务会自动先停掉 SoxService 服务和 Sox.App 进程（避免 dll 被锁定），再全量构建
- 备选：`dotnet watch`（见 ADR-0008；`dev.ps1` 封装脚本尚未实现）
- 服务由 Sox.App 启动时自动拉起（ping 失败则 sc start / UAC 安装），无需手动管理
- 开发中若手动改了核心（Sox.Core 等），需停服务后 rebuild 才生效（服务加载旧 dll）
- 用户按 Esc 隐藏、Alt+Space 呼出；底部状态栏显示结果数/耗时
- **窗口常驻 + DWM cloak**：呼出时只做 cloak/uncloak，不重建窗口（ADR-0016）

## 命令速查

```powershell
dotnet build                      # 全量构建（VSCode build 任务已内置停服务步骤）
dotnet test                       # 单测
sc stop SoxService                # 手动停服务（build 前可停避免锁）
dotnet publish -c Release         # 发布（非打包 exe）
# MSIX 打包见 ADR-0016 / 项目 Release 配置
```

## 代码风格

- 不写注释，代码自解释
- 后端核心（Sox.Core/Service/PluginSdk）是 Lertaro MIT 快照，保持上游代码结构，改核心前先对照官方仓库 E:\Code\Lertaro
- 前端（Sox.App）重写为 WinUI 3，结构与后端解耦，通过 SearchService（命名管道）通信
- 新代码必须通过 `dotnet build` 且尽量补单测
