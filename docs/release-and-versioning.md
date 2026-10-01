# 版本与发布

面向维护者。约定版本号、发行说明与自动构建的规则。

## 版本号

采用语义化版本 `主.次.补丁`（`x.y.z`），git tag 统一加 `v` 前缀。

| 位 | 何时递增 | 例子 |
|---|---|---|
| 主（x） | 架构性变更、不兼容改动（如前端重写） | `1.0.0` |
| 次（y） | 新增功能，向后兼容 | `0.2.0` |
| 补丁（z） | 修 bug、仅细节调整 | `0.1.1` |

**唯一来源**：`Directory.Build.props` 的 `<SoxVersion>`。改版本只改这一处，其余全部自动派生：

- App / 安装包显示版本：`-p:Version=$SoxVersion`（由 `build/build-release.ps1` 传入）
- `Core` / `Service` / `PluginSdk` 是 Lertaro MIT 快照，各自保留自己的 `Version`，不随 `SoxVersion` 变

## 发版流程

```powershell
# 1. 改版本号（唯一来源）
#    编辑 Directory.Build.props 的 <SoxVersion>

# 2. 写变更条目（可选，但正式版建议写）
#    新建 .github/release-notes/<version>.md，见「发行说明」

# 3. 提交并打 tag（tag 必须与 SoxVersion 一致，CI 会校验）
git commit -am "chore: release v0.2.0"
git tag v0.2.0
git push origin main --tags
```

打 tag 后 `Release` 工作流自动：构建 x64（zip + setup）与 arm64（zip）→ 生成发行说明 → 创建 GitHub Release。

## 发行说明

两类模板，生成脚本按版本号的补丁位自动选择（`build` 无需手工挑）：

| 模板 | 适用 | 特征 |
|---|---|---|
| `release-notes-template-formal.md` | 正式版，补丁位为 0（`x.y.0`） | 完整下载表 + 注意事项 |
| `release-notes-template-patch.md` | 小版本，补丁位 > 0（`x.y.z`, z>0） | 精简下载表 |

变更正文优先取 `.github/release-notes/<version>.md`；没有则回退到与上一个 tag 的 compare 链接。

变更条目的写法（简单平实，分节列出）：

```markdown
## 新增

- 一句话说清做了什么、对用户意味着什么。

## 变更

- ...

## 修复

- ...
```

生成脚本：`.github/scripts/gen-release-notes.ps1`。本地手动生成：

```powershell
pwsh -File .github/scripts/gen-release-notes.ps1 `
  -Version 0.2.0 -ArtifactsDir artifacts `
  -OutputPath release-notes.md -PreviousTag v0.1.0
```

## 工作流

| 文件 | 触发 | 作用 |
|---|---|---|
| `.github/workflows/ci.yml` | push / PR 到 `main` | 构建校验（Release / x64） |
| `.github/workflows/release.yml` | 推 `v*` tag / 手动 | 出产物并创建 Release |

打包细节见 ADR-0020。
