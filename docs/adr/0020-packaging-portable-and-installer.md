# ADR-0020. 发布形态：便携版 zip 与 Inno Setup 安装版

日期：2026-10-01
状态：Accepted

## 背景

NFR-07 要求「非打包 exe 或 MSIX 两种形态」，但仓库此前**没有任何打包脚本**：无 `.iss`、无发布脚本、无 CI。ADR-0016 只说了路线（`WindowsPackageType=None` 的非打包 exe），没有落地一条能产出可分发物的流水线。

同时代码里已经写死了对「安装版」的判定：`InstallationDetector` 通过固定 Inno AppId 的卸载注册表项推断 `InstallationMode`，据此决定数据目录（安装版 `%ProgramData%\Sox` / `%LocalAppData%\Sox`，便携版 exe 旁 `Data\`）。也就是说安装包必须由 Inno Setup 生成，且 AppId 不能随便定。

参考实现：`E:\Code\momomi`（Inno Setup 6 + `fetch-bundle.ps1` + GitHub Actions `release.yml`）。

## 决策

提供两种运行时变体 × 两种分发形态，均由一条 `build/build-release.ps1` 产出：

### 运行时变体

1. **合并版（merged）**：`.NET` 与 Windows App SDK 运行时随包携带（`SoxSelfContained=true`）。开箱即用，体积大（x64 约 400MB）。
2. **分离版（split）**：框架依赖（`SoxSelfContained=false`）。体积小很多（x64 约 46MB），但要求机器已装 .NET 10 Desktop Runtime 与 Windows App SDK 2.x Runtime。安装包在安装前检测并自动下载安装这两个运行时；便携包要求用户自行预装。

`SoxSelfContained` 同时控制 App 与随包的 Service，二者模式一致。

### 分发形态

1. **便携版**：`dotnet publish` 产物原样打 zip（`Sox-<ver>-<arch>-<variant>-portable.zip`）。解压即用，数据落在 exe 旁 `Data\`（`InstallationMode.Portable`）。
2. **安装版**：Inno Setup 6 编译的 `Sox-<ver>-x64-<variant>-setup.exe`。安装到 `{autopf}\Sox`，注册卸载项供 `InstallationDetector` 识别，安装时注册/卸载时删除 Windows 服务。

### 关键约束

- **AppId 固定**：`{D37D0B75-B5E3-40D9-92EE-429C7D4D7F2A}`，与 `InstallationDetector` 里的卸载键常量一致，改动会破坏安装版判定。
- **安装版需要管理员**：服务注册（`sc.exe create`）要管理员权限，故 `PrivilegesRequired=admin`（区别于 momomi 的 per-user 最低权限安装）。
- **服务随包分发**：`Sox.Service.exe` 及其依赖放在 `Service\` 子目录（`ServiceBootstrapper.ResolveServiceExe` 与 `ServicePluginLoader` 都按这个布局找），安装时执行 `Service\Sox.Service.exe --install`，卸载时 `--uninstall`。
- **裁剪 AI/ML**：`Microsoft.WindowsAppSDK` 元包会强拉 AI / ML（onnxruntime + DirectML，约 45MB）与 Widgets，Sox 完全不用。用直接引用 + `ExcludeAssets="all"` 把这几个包排除，输出不再带这些 DLL。
- **不引入 MSIX**：MSIX 与「自注册 Windows 服务 + 读写全盘 USN」的模型冲突（打包应用受文件系统虚拟化限制），ADR-0016 里 MSIX 的主要价值是运行时依赖分发，而合并版已经解决、分离版由安装包引导解决。故本期只做便携 zip + Inno 安装版，MSIX 留作后续可选。
- **运行时引导**：分离版安装包用 Inno 的 `DownloadTemporaryFile` 下载 .NET Desktop Runtime 与 Windows App SDK Runtime 官方引导包并静默安装，安装前用注册表/共享框架目录检测是否已装，避免重复安装。

## 后果

- 版本号单一来源：`Directory.Build.props` 的 `VersionPrefix`，可被 `-p:Version` 覆盖（CI 用 tag）。
- CI（`.github/workflows/release.yml`，对齐 momomi）在 `v*` tag 上产出两种形态的 artifact 并创建 Release。
- 便携版与安装版共享同一份 publish 产物，只是安装版多一层 Inno 包装与注册。
- 插件目录（`Plugins\`，PinyinAlias/FileDialog）当前不在解决方案内构建，故不进包；等 F-27 前端插件加载器落地后再补进打包脚本。

## 关联

- ADR-0016（前端重写与发布形态路线）、NFR-07
- `src/Sox.Core/Services/Installation/InstallationDetector.cs`（AppId 约束）
- `src/Sox.Service/ServiceInstaller.cs`（`--install` / `--uninstall`）
- `src/Sox.App/Services/ServiceBootstrapper.cs`（`Service\Sox.Service.exe` 布局）
