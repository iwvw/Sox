# Handoff：转向 Lertaro 核心 + 自研前端（会话二）

日期：2026-09-29
状态：进行中

## 会话目标

用户调研后发现 Lertaro（C# WPF + USN/MFT，MIT）后端质量远超自研，决定：**保留其核心、全新重写前端**。本会话完成了转向落地。

## 已完成的决策与动作

| 项 | 内容 |
|---|---|
| ADR-0010 | 转向：Lertaro 核心 + 自研前端（替代自研索引工作） |
| 清理 | 删除从零骨架（Sox.Core/Native/App/Tests + 旧 sln） |
| 核心快照 | 拷贝 Lertaro Core/Service/PluginSdk → 产品仓库（排除 bin/obj） |
| 重命名 | 全局 Lertaro.* → Sox.*（命名空间/程序集/服务名 SoxService/管道 SoxPipe/exe Sox.Service.exe），63 文件 |
| 编译 | 四个项目 net10.0-windows 全部构建通过 |
| 前端 | Sox.App 全新自研：Spotlight 悬浮窗 + SearchService 对接（防抖/流式/最近文件/服务拉起）+ 热键 + 托盘 + 主题跟随 |
| 服务 | SoxService 已安装并 RUNNING（ImagePath 指向 App 输出目录的 Sox.Service.exe） |
| VSCode | Sox.slnx + .vscode/launch.json + tasks.json（build 任务自动停服务/App 防 dll 锁定） |

## 前端对接核心要点（已核实）

- `new SearchService()`（Lertaro.Core → Sox.Core）→ `PingAsync` 探活 → `SearchStreamingAsync(query, 200, 0, null, onResult, token)` 流式
- `GetRecentFilesAsync(dirs, 30, maxAge, token)` 空输入最近文件
- 模糊开关由核心自动读 UserSettings，前端不用传
- 结果对象 `SearchResult`：Name/Path/IsDir/Drive/Attributes/Metadata(Size, Modified)
- 服务拉起三段式：ping → sc query/start → UAC `Sox.Service.exe --install`（见 Sox.App/Services/SearchHost.cs）
- 官方 App 排序/去重在 App 侧（ViewModels/Search），前端排序逻辑需自研（当前按服务端返回顺序 + 展示）

## 当前进度

- [x] ADR-0010 + 清理骨架 + 拷贝核心 + 全局重命名
- [x] 核心三项目编译通过
- [x] Sox.App 前端骨架（悬浮窗/搜索/服务拉起/热键/托盘/主题）
- [x] SoxService 安装运行 + VSCode 调试配置
- [ ] VSCode F5 实测搜索闭环（用户操作验证）
- [ ] PRD/CONTEXT 技术方案更新（AGENTS/CONTEXT 已更新，PRD 待补技术方案节）
- [ ] 高亮（连 AppSearchPipeService 才有）、排序自研、拼音插件（PinyinAlias 未拷贝）

## 阻塞

- VSCode 调试首次 build 前需停服务（tasks.json 已内置）
- 核心快照改名后，若官方更新需手动 diff 同步（E:\Code\Lertaro）

## 下一步

1. 用户在 VSCode F5 启动 Debug Sox.App，验证：窗口、服务自动拉起、输入搜索出结果
2. 根据观察优化前端（排序、结果信息密度、空输入体验、高亮）
3. 更新 PRD 技术方案节
4. 决定是否引入拼音插件（PinyinAlias，官方 Plugins/ 目录，未拷贝）
