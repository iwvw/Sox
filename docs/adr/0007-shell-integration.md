# ADR-0007. Shell 集成：对话框 Hook + 注册表右键菜单

日期：2026-09-29
状态：Accepted

## 背景

Listary 的灵魂场景：保存/打开对话框内快速选文件、资源管理器右键菜单。工作量最大且最脏的系统集成。

## 决策

- 对话框集成：`CBTProc` 钩子识别保存/打开对话框，检测 Ctrl 呼出 SearchWindow，选中结果后 `SendInput` 将路径注入文件名输入框
- 对话框两套处理：
  - 旧对话框（`GetOpenFileName` / `GetSaveFileName`）
  - Win11 新对话框（`IFileDialog`）
- 右键菜单：注册表 `HKEY_CLASSES_ROOT\Directory\shell` / `Background\shell` 注入「在 Sox 中搜索」
- 文件拖放（拖到窗口标题栏即打开）作为 P2 保留

## 后果

- 获得 Listary 级集成体验；这是测试成本最高的模块
- 权限：修改注册表 + 注入输入，均需管理员（与 ADR-0004 一致）
- Windows 版本差异处理集中在此层，需防御式编码
