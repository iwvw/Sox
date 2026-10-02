# Handoff：面板按钮补全 + 仓库转公开 + 移除 Token（会话十三）

日期：2026-10-03
状态：全部实现并构建通过（0 warning / 0 error）；待用户视觉验收

## 本轮完成

| 需求 | 实现 |
|---|---|
| 面板右侧三按钮补全 | 收藏=显示收藏列表切换；历史=已打开目录/最近打开切换；更多=收藏/取消收藏当前文件夹、打开当前文件夹、复制当前路径 |
| 收藏可见入口 | Spotlight 主窗口空查询时列表顶部优先显示收藏项；面板星标按钮显示收藏列表 |
| 占位文案歧义 | 面板「在此目录中搜索，回车跳转」→「全盘搜索，回车跳转」 |
| 仓库转公开 | `gh repo edit iwvw/Sox --visibility public`，已确认 PUBLIC |
| 移除 GitHub Token | 删除 `UserSettings.GitHubToken`、「关于」页输入框与保存逻辑、`AppUpdateService` 的 token/镜像门控（改为始终走镜像链）、相关 404/401/403 文案改为中性 |
| 版本 | 0.3.0 → 0.3.1 |

## 关键决策

- 仓库公开后，自更新无需任何凭据，token 机制纯属冗余，故整条移除而非保留。
- 收藏此前只有写入（「固定到收藏」）没有读取入口，本次补上两处可见入口（面板星标列表、主窗口空状态置顶）。

## 客观验证

- `git log --all -S "gho_"/"ghp_"/"github_pat_"` 均无结果：token 从未进入 git 历史；仅存在于本地 gitignore 的 `user-settings.json`。
- `rg "GitHubToken|TokenBox|ActiveMirrors|ApplyAuthHeader|CurrentToken"` 全仓无残留。
- `dotnet build Sox.slnx -c Debug` 0 warning / 0 error。

## 待用户视觉验收

1. 面板星标/时钟/更多三按钮：收藏列表、最近打开切换、菜单三项
2. 主窗口空状态顶部收藏项
3. 「关于」页不再有 GitHub Token 输入框，检查更新/一键更新正常

## 下一步

- 打 tag v0.3.1 推送触发 release.yml，回传安装包链接
- 面板三按钮的收藏项在面板内选中后是否应支持「取消收藏」（目前仅工具菜单可取消当前目录）

## 相关文件

- `src/Sox.App/InlineSearchWindow.xaml(.cs)`
- `src/Sox.App/MainWindow.xaml.cs`（`LoadRecentAsync` 置顶收藏）
- `src/Sox.App/Services/AppUpdateService.cs`、`src/Sox.App/Settings/AboutPage.xaml(.cs)`
- `src/Sox.Core/Settings/UserSettings.cs`
- `docs/adr/0021-settings-features-and-self-update.md`
