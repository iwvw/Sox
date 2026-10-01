# ADR-0015. 前端查询 Provider 架构（即时结果源）

日期：2026-09-30
状态：Accepted

## 背景

Sox 目前只有「文件/目录搜索」一条结果来源。相比 Spotlight/Listary，缺少应用启动、计算器、打开网址、文件操作、窗口切换、剪贴板历史、自定义命令等能力。Lertaro 的这些功能都以「插件 + `IInstantResultProvider` 等接口」实现，由 `PluginManager` 收集、`SearchResultMapper` 逐键调度合并。Sox.App 没有插件系统，照搬插件框架过重。

## 决策

在 Sox.App 内建一个**精简的前端查询 Provider 框架**，不引入插件加载，Provider 直接以代码注册。

1. **Provider 契约**：`IQueryProvider`，方法 `IEnumerable<InstantResult> Query(string query)`，同步、快、无阻塞（文件 IO 走缓存）。每个 Provider 用 `try/catch` 隔离，单个失败不影响其他。
2. **即时结果模型**：`InstantResult`（Id / Title / Description / Glyph 或图片 / Execute / TabCompletion）。与核心的文件结果在 VM 层合并。
3. **合并顺序**：即时结果**置顶**（pin 在文件结果之前），按 Provider 注册顺序排列；文件结果按既有历史权重排序。空查询不跑 Provider。
4. **统一结果对象**：`ResultItem` 扩展为可承载「文件结果」或「即时结果」两种；`Key` 作为 keyed diff 的稳定标识（即时结果用 `instant:{id}`）。
5. **行为隔离**：即时结果不参与历史学习/收藏/过路惩罚；其 `Execute` 是自包含动作。
6. **触发方式**：Provider 自行判断 query 是否命中（如算式、URL、触发词），无需前端预先剥离触发词（首版不做 token 剥离；应用搜索为「纯前缀匹配」）。

## 首批 Provider

- 计算器（移植 Lertaro `ScientificMathParser`）：算式求值、进制转换，回车复制
- 打开网址 / 网页搜索：URL 直开；`g`/`b`/`bd` 等触发词走搜索引擎
- 应用搜索：开始菜单 + 桌面快捷方式（`.lnk`/`.exe`/`.url`），回车启动
- 自定义命令：用户配置关键词 → 命令，回车运行
- 窗口切换：枚举可见顶层窗口，回车切换

## 后续 Provider

- 文件操作动作（删除到回收站/重命名/属性）→ 挂在结果动作菜单
- 剪贴板历史（自研，Lertaro 无此功能）
- 文件预览面板（先做图片/文本/缩略图，Shell 预览处理器暂缓）
- Everything 集成（开关转发到核心 `EnableEverythingIpc`）

## 后果

- 前端新增查询逻辑集中在 `Services/QueryProviders/`，与核心搜索解耦
- 每个 Provider 独立文件，便于增删
- `ResultItem` 从「只包 SearchResult」变为双形态，`SearchViewModel` 的合并/去重需相应调整
- 不引入插件动态加载，能力全部编译在 App 内

## 关联

- 复用核心 `StartMenuShortcutResolver`（来自 Lertaro PluginSdk）
- 与 ADR-0005（实时查询）、ADR-0013（排序权重）配合：即时结果优先于文件结果
