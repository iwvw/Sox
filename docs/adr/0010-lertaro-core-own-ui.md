# ADR-0010. 转向：Lertaro 核心 + 自研前端

日期：2026-09-29
状态：Accepted（前端技术选型部分被 ADR-0016 取代）

## 背景

从零自研的骨架（ADR-0001~0009）暴露两个现实问题：

1. 索引性能与内存远不及成熟实现：全盘 200 万文件索引耗时数分钟（目录枚举），Trie 前缀树曾导致 12GB 内存爆炸（ADR-0009 改线性扫描才压回几百 MB）。
2. 用户调研后认为同类开源项目 Lertaro（C# WPF + .NET 10 + USN/MFT 索引，MIT）后端质量远超自研，但其前端缺乏工业设计美感。

结论：不重复造轮子，保留成熟后端，重写前端。

## 决策

- 产品仓库：E:\Code\sox（现有文档体系 AGENTS/PRD/ADR/handoff 延续）
- 后端：采用 Lertaro 核心快照（MIT，来源 E:\Code\Lertaro）
  - Lertaro.Core：索引引擎（USN/MFT）、搜索、IPC Wire 协议、SearchService 多源编排
  - Lertaro.Service：Windows 服务宿主（USN 索引后台，命名管道 LertaroPipe）
  - Lertaro.PluginSdk：插件契约
- 前端：全新编写，Spotlight 风格 WPF，替换 Lertaro 官方 App 的 UI 层
- 核心引入方式：直接拷贝为仓库内快照（不 submodule、不 fork），保留 MIT 来源声明
- 自研骨架（Sox.Core/Sox.Native/Sox.App/Sox.Tests）废弃清理

## 后果

- 秒级索引、USN 实时增量、版本化 IPC 协议全部免费获得；UI 侧自由重写，满足工业设计要求
- 前端与核心通过 Supervised 命名管道通信（SearchService 编排门面 + Wire 协议），重写不影响后端
- 不 fork / 不 submodule：与官方上游同步需手动拷贝比对核心快照（代价可接受，核心 API 稳定）
- 排序逻辑有部分在官方 App 侧，新前端须自带排序（可参考核心的 MatchRank，样式自定义）

## 关联

- 取代 ADR-0001（自研 WPF 栈）的「自研」部分，保留 WPF 技术选择
- 取代 ADR-0002 / ADR-0009 的索引引擎自研工作，改用 Lertaro 实现
- 取代 ADR-0004（单进程 + requireAdministrator）：改用三进程模型（服务 + App + hook）
- **后续更新**：本 ADR 中「前端用 WPF」的选择已被 **ADR-0016** 取代（前端重写为 WinUI 3）；「后端复用 Lertaro 核心」的核心决策仍然有效