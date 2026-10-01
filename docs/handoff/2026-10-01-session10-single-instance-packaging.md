# Handoff：单实例激活修复 + 打包脚本（会话十）

日期：2026-10-01
状态：单实例激活已实现并客观验证；打包脚本已实现，便携 zip 与安装包本地跑通；待用户视觉验收

## 本轮完成

### 1. 单实例激活链路补全

**问题**：`Program` 有命名 Mutex 门禁（`Sox.SingleInstance`），但 `SingleInstanceForwarder` 只有发送端，全仓没有管道 `Sox.App.Activation` 的服务端。后果：首实例常驻时重复启动 exe（桌面图标/开始菜单/任务栏固定项）会被静默挡掉，**唤不出窗口**；日常只能靠 Alt+Space 与托盘左键。

**修复**：
- 新增 `src/Sox.App/SingleInstanceActivationServer.cs`：首实例启动时循环监听命名管道 `Sox.App.Activation`，收到连接即回调。
- `MainWindow` 构造里创建监听，回调经 `DispatcherQueue.TryEnqueue(ShowWindow)` 唤出窗口；`MainWindow_Closed` 里 `Dispose`。
- 管道一次性连接、accept 后重建；读取 payload 再关闭，避免客户端写入拿到 broken pipe。

**客观验证**（检测 `DWMWA_CLOAKED`）：
- 重复启动后进程数保持 1
- 首实例主窗口 cloaked 从 `1`（隐藏）变为 `0`（显示），连续采样稳定
- app.log 出现 `Single-instance activation received; summoning window`（该诊断日志已撤除）

### 2. 打包脚本（便携版 + 安装版）

此前仓库**无任何打包脚本**。新增（参考 `E:\Code\momomi`）：

| 文件 | 作用 |
|---|---|
| `Directory.Build.props` | 产品版本单一来源 `SoxVersion`（CI 用 `-p:Version` 覆盖） |
| `build/installer.iss` | Inno Setup 6 安装脚本；AppId 对齐 `InstallationDetector`；管理员权限；安装注册服务、卸载删除服务；升级前停服务 |
| `build/build-release.ps1` | publish（self-contained）→ 便携 zip → ISCC 编译安装包 |
| `build/languages/ChineseSimplified.isl` | 简体中文界面（入库） |
| `.github/workflows/release.yml` | `v*` tag 触发，x64 出 zip+setup、arm64 只出 zip，创建 Release |
| `docs/adr/0020-packaging-portable-and-installer.md` | 打包形态决策 |

**关键约束**：
- Inno AppId 必须是 `{D37D0B75-B5E3-40D9-92EE-429C7D4D7F2A}`，与 `InstallationDetector` 的卸载键一致。
- 数据目录：安装版 `%ProgramData%\Sox` / `%LocalAppData%\Sox`；便携版 exe 旁 `Data\`。
- Service 放进 publish 的 `Service\` 子目录（`ServiceBootstrapper`、`ServicePluginLoader` 都按此找）。

**csproj 修的两处 publish 缺陷**：
- `BuildAndCopyService` 内层构建显式 pin `SelfContained=false;Platform=AnyCPU`：否则 publish 的全局属性泄漏进去，空 RID + `SelfContained=true` 触发 NETSDK1191。
- 新增 `PublishServiceToPublishDir`：把 self-contained 的 Service publish 进 `$(PublishDir)Service\`；`Service.csproj` 补 `RuntimeIdentifiers` 否则 restore 缺 `win-x64` 目标报 NETSDK1047。

**本地验证结果**：
- 便携 zip：`Sox-0.1.0-x64-portable.zip`，约 182MB
- 安装包：`Sox-0.1.0-x64-setup.exe`，约 126MB
- 单架构 publish 目录约 440MB

## 关键坑

- PowerShell 读 `[xml]` 里带 `Condition` 属性的元素会拿到 `XmlElement`（`ToString` 是类型名），要读 `.InnerText`。
- Inno Setup 用 winget 装在 `%LOCALAPPDATA%\Programs\Inno Setup 6`，不是 Program Files；脚本候选路径已含。
- `publish` 会触发 App csproj 的内层 Service 构建，全局属性泄漏是 NETSDK1191 的根因。

### 3. 版本号系统与发行说明

- **版本唯一来源**：`Directory.Build.props` 的 `<SoxVersion>`（当前 `0.1.0`）；tag 用 `v0.1.0`。
- `docs/release-and-versioning.md`：版本号规则、发版流程、发行说明写法。
- 发行说明两套模板，生成脚本按补丁位自动选：
  - `release-notes-template-formal.md`（`x.y.0`，完整下载表 + 注意事项）
  - `release-notes-template-patch.md`（`x.y.z`, z>0，精简）
- `.github/scripts/gen-release-notes.ps1`：填 `{{CHANGES}}`/`{{VERSION}}`/大小占位；变更正文优先取 `.github/release-notes/<version>.md`，缺失则回退 compare 链接。已本地验证两种模板。
- `.github/workflows/ci.yml`：push/PR 到 main 的构建校验。
- `release.yml` 补：tag 与 `SoxVersion` 一致性校验（不一致直接失败）、发行说明生成并作为 Release 正文。
- 首个版本变更条目：`.github/release-notes/0.1.0.md`。

## 待用户视觉验收

1. 首实例常驻时重复双击 exe / 开始菜单启动，搜索窗是否被唤出
2. 便携 zip 解压后可直接运行（数据落在 `Data\`）
3. 安装包安装到 `Program Files\Sox`，注册 SoxService，重装/升级不残留旧服务

## 下一步

- 插件目录（PinyinAlias / FileDialog）当前不在解决方案构建，未进包；等 F-27 前端插件加载器落地后补进打包脚本
- CI 首次跑通后按约定（见全局 instructions）回传安装包链接
- 打包脚本未加数字签名（ECDSA 更新签名机制见 `UpdatePackage`，与本轮分发无关）

## 相关文件

- `src/Sox.App/SingleInstanceActivationServer.cs`、`Program.cs`、`SingleInstanceForwarder.cs`、`MainWindow.xaml.cs`
- `build/installer.iss`、`build/build-release.ps1`、`Directory.Build.props`
- `.github/workflows/release.yml`、`.github/workflows/ci.yml`
- `.github/scripts/gen-release-notes.ps1`、`.github/release-notes-template-formal.md`、`.github/release-notes-template-patch.md`、`.github/release-notes/0.1.0.md`
- `docs/adr/0020-packaging-portable-and-installer.md`、`docs/release-and-versioning.md`
