# Handoff：初始需求讨论 → 文档体系建立

日期：2026-09-29
状态：进行中

## 会话目标

新项目 Sox 的初始调研阶段：确认需求、选定技术栈、建立 Agent 文档体系、搭建工程骨架。

## 已确认的需求（对应 PRD）

- 产品形态：Listary + Everything 结合体，Spotlight 式悬浮搜索框
- 全盘 NTFS 文件名秒搜，不搜文件内容
- 拼音匹配（首字母 + 全拼前缀）
- 历史学习智能排序
- 资源管理器深度集成（对话框 Ctrl 呼出 + 右键菜单）
- 常驻内存 < 100MB，跟随系统主题
- 实时搜索（跟随输入）
- 单进程 + 管理员权限，托盘常驻

## 已拍板的决策（对应 docs/adr/）

| ADR | 决策 |
|---|---|
| 0001 | C# + WPF (.NET 10 LTS)，否决 Electron/Tauri/WinUI3/C++ |
| 0002 | USN Journal 增量 + MFT 全扫，全盘纯文件名，全内存驻留 |
| 0003 | 拼音索引：首字母 + 全拼前缀 |
| 0004 | 单进程 + requireAdministrator，托盘守护重启 |
| 0005 | 查询实时化：防抖 + 取消 + 后台线程 + top30 |
| 0006 | 排序：相关度硬门槛 + 行为加权 + 时间衰减 |
| 0007 | Shell 集成：CBTProc 对话框 Hook + 注册表右键菜单 |
| 0008 | 开发工作流：dotnet watch + DebugOverlay + 索引缓存落盘 |

## 当前进度

- [x] 需求讨论完成
- [x] AGENTS.md / CONTEXT.md / PRD.md 建立
- [x] 8 个 ADR 建立
- [x] 初始 handoff 生成
- [ ] 解决方案骨架（Sox.sln + 4 项目）
- [ ] Sox.Core / Sox.Native / Sox.App / Sox.Tests 骨架
- [ ] dev.ps1 + dotnet watch 工作流
- [ ] 构建验证 + 启动 watch

## 工程规划（已画好，待落地）

```
Sox.sln
├─ src/Sox.Core/     Index(FileEntry/Trie/Pinyin/IndexEngine/IUsnSource)
│                    Query(QueryParser/QueryExecutor)  Rank(RankEngine/RankPersist)
├─ src/Sox.Native/   UsnApi / HotKey / ShellDialogHook / ShellContextMenu / SendKeys
├─ src/Sox.App/      SearchWindow / SettingsWindow / TrayIcon / ThemeService / DebugOverlay
└─ src/Sox.Tests/    QueryExecutorTests / RankEngineTests
```

依赖方向：Sox.App → Sox.Core ← 接口 ← Sox.Native（依赖倒置）。

## 阻塞

无。

## 下一步

1. 创建解决方案骨架 + 4 个项目（先让 `dotnet build` 通过空壳）
2. 实现 SearchWindow（毛玻璃 + 无边框 + 空输入最近列表）
3. 实现 IndexEngine 最小可用版（先做全量扫描，再做 USN 增量）
4. dev.ps1 起 watch，进入「用户观察 + Agent 修改」循环

## 提醒下一位 Agent

- 先读 AGENTS.md → CONTEXT.md → PRD.md → 相关 ADR
- 本机 dotnet SDK 为 9/10，项目框架 `net10.0-windows`
- 工作目录 E:\Code\sox 当前为空目录（非 git repo，可考虑初始化）
