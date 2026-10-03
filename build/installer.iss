; Sox 安装版脚本（Inno Setup 6）
; 由 build/build-release.ps1 调用：
;   ISCC /DAppVersion=x.y.z /DSourceDir=<publish 目录> /DOutputDir=<输出目录>
;        /DVariant=merged|split /DBootstrapRuntime=0|1 installer.iss
;
; 约束：AppId 必须与 src/Sox.Core/Services/Installation/InstallationDetector.cs 里的卸载键常量
; {D37D0B75-B5E3-40D9-92EE-429C7D4D7F2A} 一致，否则安装版会被判成便携版、数据目录走错。
;
; 两种变体：
;   merged（合并版）：运行时随包携带，开箱即用。
;   split（分离版）：框架依赖，安装时检测并引导安装 .NET Desktop Runtime 与 Windows App SDK Runtime。

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
; merged / split
#ifndef Variant
  #define Variant "merged"
#endif
; 1 时安装前引导安装运行时（仅 split 版）
#ifndef BootstrapRuntime
  #define BootstrapRuntime "0"
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
; 注册 Windows 服务需要管理员权限。
PrivilegesRequired=admin
OutputDir={#OutputDir}
OutputBaseFilename=Sox-{#AppVersion}-x64-{#Variant}-setup
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
const
  // 运行时引导下载地址（官方 aka.ms 短链，始终指向最新受支持版本）。
  DotNetDesktopUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  WindowsAppSdkUrl = 'https://aka.ms/windowsappsdk/2.2/latest/windowsappruntimeinstall-x64.exe';

// 升级前停掉服务：运行中的 Sox.Service.exe 会锁住文件，导致 [Files] 复制新版本失败。
// 服务自身在 OnStop 里会顺带终止 hook 子进程（同一 exe 镜像），但 sc stop 返回后 SCM 未必已到
// STOPPED，固定 sleep 会在服务仍持有 exe 时开始复制。这里轮询 sc query，直到出现 STOPPED 或服务
// 已不存在（卸载过），最多等 30 秒。保留服务注册（路径不变），安装完成后由 --install 重新配置并启动。
procedure StopServiceBeforeInstall();
var
  ResultCode: Integer;
  Tries: Integer;
begin
  Exec('sc.exe', 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Tries := 0;
  while Tries < 30 do
  begin
    // sc query 对已不存在的服务返回非 0；用 cmd 判断输出里是否含 STOPPED。
    if Exec('cmd.exe', '/c sc query {#ServiceName} 2>nul | find "STOPPED" >nul', '',
        SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      if ResultCode = 0 then
        Break;
    if Exec('cmd.exe', '/c sc query {#ServiceName} >nul 2>&1', '',
        SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      if ResultCode <> 0 then
        Break;
    Sleep(1000);
    Tries := Tries + 1;
  end;

  // 服务停掉后仍可能有 --hook 子进程存活：修复前的版本不会在 OnStop 里清理它，而它锁着
  // Service\Sox.Service.exe，会让 [Files] 覆盖失败。安装程序自身不是 Sox.Service.exe 镜像，
  // 这里强制结束所有残留实例（含 hook 子进程）是安全的。
  Exec('taskkill.exe', '/F /IM Sox.Service.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// 检测 .NET Desktop Runtime：读取共享框架目录里是否存在 Microsoft.WindowsDesktop.App。
// 要求主版本 10，与 TargetFramework 对应。
function HasDotNetDesktopRuntime(): Boolean;
var
  FindRec: TFindRec;
  Root: String;
begin
  Result := False;
  Root := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(Root + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          if Copy(FindRec.Name, 1, 3) = '10.' then
            Result := True;
      until (not FindNext(FindRec)) or Result;
    finally
      FindClose(FindRec);
    end;
  end;
end;

// 检测 Windows App SDK Runtime（2.x 线）：运行时以 MSIX 框架包注册在 PackageRepository 下，
// 键名形如 Microsoft.WindowsAppRuntime.2_<版本>_x64__8wekyb3d8bbwe。x64 应用需要 x64 包。
function HasWindowsAppSdkRuntime(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetSubkeyNames(HKLM,
       'SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PackageRepository\Packages',
       Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if (Pos('Microsoft.WindowsAppRuntime.2_', Names[I]) = 1) and (Pos('_x64_', Names[I]) > 0) then
        Result := True;
end;

// 下载并静默安装一个运行时；失败返回 False 并记录原因。
// DownloadTemporaryFile 出错时抛异常（不是返回 False），所以整段包在 try/except 里。
function DownloadAndRun(Url, FileName, Params, DisplayName: String; var ErrorMsg: String): Boolean;
var
  TempFile: String;
  ResultCode: Integer;
begin
  Result := False;
  try
    DownloadTemporaryFile(Url, FileName, '', nil);
  except
    ErrorMsg := '下载 ' + DisplayName + ' 失败：' + GetExceptionMessage;
    Exit;
  end;

  TempFile := ExpandConstant('{tmp}\') + FileName;
  if not FileExists(TempFile) then
  begin
    ErrorMsg := '下载 ' + DisplayName + ' 后未找到文件。';
    Exit;
  end;

  if not Exec(TempFile, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    ErrorMsg := '运行 ' + DisplayName + ' 安装程序失败。';
    Exit;
  end;
  if (ResultCode <> 0) and (ResultCode <> 1638) then // 1638 = 已安装更高版本
  begin
    ErrorMsg := DisplayName + ' 安装未成功（退出码 ' + IntToStr(ResultCode) + '）。';
    Exit;
  end;
  Result := True;
end;

// split 版：缺哪个运行时装哪个。合并版（BootstrapRuntime=0）不需要，直接跳过。
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ErrMsg: String;
begin
  StopServiceBeforeInstall();
  Result := '';

#if BootstrapRuntime == "1"
  if not HasDotNetDesktopRuntime() then
  begin
    if not DownloadAndRun(DotNetDesktopUrl, 'windowsdesktop-runtime.exe', '/install /quiet /norestart',
        '.NET Desktop Runtime 10', ErrMsg) then
    begin
      Result := ErrMsg;
      Exit;
    end;
  end;

  if not HasWindowsAppSdkRuntime() then
  begin
    if not DownloadAndRun(WindowsAppSdkUrl, 'windowsappruntimeinstall.exe', '--quiet',
        'Windows App SDK Runtime', ErrMsg) then
    begin
      Result := ErrMsg;
      Exit;
    end;
  end;
#endif
end;
