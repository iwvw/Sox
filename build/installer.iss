; Sox 安装版脚本（Inno Setup 6）
; 由 build/build-release.ps1 调用：
;   ISCC /DAppVersion=x.y.z /DSourceDir=<publish 目录> /DOutputDir=<输出目录> installer.iss
;
; 约束：AppId 必须与 src/Sox.Core/Services/Installation/InstallationDetector.cs 里的卸载键常量
; {D37D0B75-B5E3-40D9-92EE-429C7D4D7F2A} 一致，否则安装版会被判成便携版、数据目录走错。

#define AppName "Sox"
#define ServiceName "SoxService"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "."
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef NameSuffix
  #define NameSuffix ""
#endif

[Setup]
AppId={{D37D0B75-B5E3-40D9-92EE-429C7D4D7F2A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=DSUK
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; 注册 Windows 服务需要管理员权限，与 momomi 的 per-user 安装不同。
PrivilegesRequired=admin
OutputDir={#OutputDir}
OutputBaseFilename=Sox-{#AppVersion}-x64{#NameSuffix}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\Sox.App.exe
; 安装/升级时自动关闭正在运行的 Sox，避免文件占用导致复制失败。
CloseApplications=yes
RestartApplications=no

[Languages]
; 简体中文语言包不在 Inno 默认发行版内，由构建脚本下载到 build\languages；
; 若缺失则自动跳过，仅用英文。
#ifdef ChineseISL
Name: "chinesesimplified"; MessagesFile: "{#ChineseISL}"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; 整个发布目录原样打包，含 Service\ 子目录（Sox.Service.exe 及其依赖）。
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Sox.App.exe"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Sox.App.exe"; Tasks: desktopicon

[Run]
; 注册并启动后台服务（幂等：内部会先停旧实例再 config/create + start）。
Filename: "{app}\Service\Sox.Service.exe"; Parameters: "--install"; \
  Flags: runhidden waituntilterminated; StatusMsg: "正在注册后台服务…"
Filename: "{app}\Sox.App.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; 删除服务（内部会先停再 delete），必须在删除文件之前执行。
Filename: "{app}\Service\Sox.Service.exe"; Parameters: "--uninstall"; \
  Flags: runhidden waituntilterminated; RunOnceId: "UninstallService"

[UninstallDelete]
; 只清理程序目录；用户数据在 %ProgramData%\Sox 与 %LocalAppData%\Sox，保留以免误删索引与历史。
Type: filesandordirs; Name: "{app}"

[Code]
// 升级前停掉服务：运行中的 Sox.Service.exe 会锁住文件，导致 [Files] 复制新版本失败。
// 保留服务注册（路径不变），安装完成后由 --install 重新配置并启动。
procedure StopServiceBeforeInstall();
var
  ResultCode: Integer;
begin
  Exec('sc.exe', 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopServiceBeforeInstall();
  Result := '';
end;
