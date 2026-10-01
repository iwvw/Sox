# ADR-0001. 技术栈：C# + WPF (.NET 10 LTS)

日期：2026-09-29
状态：Superseded（前端部分由 ADR-0016 取代：WPF 前端重写为 WinUI 3；核心仍为 C# / .NET 10 LTS）

## 背景

Sox 需要满足：原生 Windows 桌面工具、美观（Spotlight 风格毛玻璃圆角）、常驻内存 < 100MB、以及全局热键 / USN / 对话框 Hook 等底层能力。备选：C# + WPF、C# + WinUI 3、Rust + Tauri、C++。

- Electron：Chromium 嵌套，内存不可控，直接排除
- Tauri：UI 用 WebView2，常驻内存 80-150MB，有超标风险；对话框 Hook 等底层集成写 Rust 成本高
- WinUI 3：Fluent 原生好看、亚克力效果好，但基础负载比 WPF 高 10-30MB，且框架年轻有版本坑
- C++：内存最低但 UI 手绘工作量大，自用项目不值当

## 决策

采用 C# + WPF，目标框架 `net10.0-windows`（.NET 10 LTS，本机 SDK 已装 9/10，取 LTS）。理由：

- 内存可控：WPF UI 常驻 ~25MB，给索引留出 ~63MB 预算，稳进 100MB
- 美观成熟：SystemBackdrop（Win11）/ SetWindowCompositionAttribute（Win10）出毛玻璃，无边框圆角浮窗套路成熟
- 底层全通：P/Invoke 覆盖热键 / USN / 对话框 Hook / 注册表
- 铁证参照：PowerToys Run 即 C# + WPF 的 Spotlight 风格启动器
- 自用项目开发效率高，XAML + C# 迭代快

## 后果

- 获得成熟生态与海量案例；缺点是相比 C++ 内存略高、启动略慢
- NativeAOT 暂不采用（WPF 支持尚不成熟），压内存靠数据结构而非 AOT
- 亚克力效果在 Win10 上弱于 Win11，需条件分支处理

## 关联

- 前端部分被 **ADR-0016**（前端重写为 WinUI 3）取代：C# / .NET 10 LTS 的选型保留，WPF 前端废弃
