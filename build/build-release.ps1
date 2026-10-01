<#
.SYNOPSIS
  构建 Sox 发布产物：便携版 zip 与 Inno Setup 安装版 exe。

.DESCRIPTION
  单条命令产出两种分发形态，均基于同一份 dotnet publish（self-contained）产物：
    - Sox-<ver>-<arch>-portable.zip   解压即用，数据落在 exe 旁 Data\
    - Sox-<ver>-<arch>-setup.exe      Inno Setup 安装版，注册后台服务
  版本号默认取 Directory.Build.props 的 SoxVersion，可用 -Version 覆盖（CI 传 tag）。

.PARAMETER Arch
  x64 或 arm64，默认 x64。

.PARAMETER Version
  覆盖版本号，如 0.2.0。

.PARAMETER Configuration
  默认 Release。

.PARAMETER SkipInstaller
  只产出便携 zip，不编译安装包（无 Inno Setup 或只想要便携版时用）。

.PARAMETER SkipPortable
  只产出安装包，不打 zip。

.EXAMPLE
  pwsh -File build/build-release.ps1
.EXAMPLE
  pwsh -File build/build-release.ps1 -Version 0.2.0 -Arch x64
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [string]$Version = '',
    [string]$Configuration = 'Release',
    [switch]$SkipInstaller,
    [switch]$SkipPortable
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsDir = Join-Path $repoRoot 'artifacts'

# ---- 版本解析：命令行 > Directory.Build.props ----
if ([string]::IsNullOrWhiteSpace($Version)) {
    $props = Join-Path $repoRoot 'Directory.Build.props'
    [xml]$xml = Get-Content -LiteralPath $props
    # SoxVersion carries a Condition attribute, so the XML adapter hands back an XmlElement
    # (whose ToString is the type name, not the value); read InnerText explicitly.
    $Version = $xml.Project.PropertyGroup.SoxVersion.InnerText
}
if ([string]::IsNullOrWhiteSpace($Version)) { throw '无法解析版本号，请用 -Version 指定。' }
Write-Host "== Sox 发布构建：version=$Version arch=$Arch configuration=$Configuration =="

# ---- RID / Platform 映射 ----
switch ($Arch) {
    'x64'   { $rid = 'win-x64';   $platform = 'x64' }
    'arm64' { $rid = 'win-arm64'; $platform = 'ARM64' }
}

# 安装包面向 x64（Inno 的 ArchitecturesAllowed=x64compatible）；arm64 只出便携版。
if ($Arch -ne 'x64' -and -not $SkipInstaller) {
    Write-Host "== $Arch 只产出便携版，跳过安装包 =="
    $SkipInstaller = $true
}

$publishDir = Join-Path $artifactsDir "publish-$Arch"
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

# ---- 1. publish（App 会把 self-contained 的 Service 一并放进 Service\） ----
Write-Host '== dotnet publish =='
$appProj = Join-Path $repoRoot 'src/Sox.App/Sox.App.csproj'
& dotnet publish $appProj `
    -c $Configuration `
    -r $rid `
    --self-contained true `
    -p:Platform=$platform `
    -p:Version=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）" }

if (-not (Test-Path -LiteralPath (Join-Path $publishDir 'Sox.App.exe'))) {
    throw "publish 产物缺少 Sox.App.exe：$publishDir"
}
if (-not (Test-Path -LiteralPath (Join-Path $publishDir 'Service/Sox.Service.exe'))) {
    throw "publish 产物缺少 Service\Sox.Service.exe：$publishDir"
}

# ---- 2. 便携版 zip ----
if (-not $SkipPortable) {
    Write-Host '== 打包便携版 zip =='
    $zipName = "Sox-$Version-$Arch-portable.zip"
    $zipPath = Join-Path $artifactsDir $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    # 先把 publish 目录改名为 Sox\，让解压后是一个自包含文件夹而不是散落一堆文件。
    $staging = Join-Path $artifactsDir "portable-stage-$Arch"
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $staging 'Sox') | Out-Null
    Copy-Item -Path (Join-Path $publishDir '*') -Destination (Join-Path $staging 'Sox') -Recurse -Force
    Compress-Archive -Path (Join-Path $staging 'Sox') -DestinationPath $zipPath -CompressionLevel Optimal
    Remove-Item -LiteralPath $staging -Recurse -Force
    Write-Host "   已生成 $zipName（$([math]::Round((Get-Item $zipPath).Length/1MB,1)) MB）"
}

# ---- 3. 安装版 ----
if (-not $SkipInstaller) {
    Write-Host '== 编译安装版（Inno Setup） =='

    $isccCandidates = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $iscc) {
        throw '未找到 Inno Setup 6 的 ISCC.exe。请安装 Inno Setup 6（winget install JRSoftware.InnoSetup），或加 -SkipInstaller。'
    }

    # 语言包缺失则跳过（installer.iss 用 #ifdef ChineseISL 判定）。
    $isl = Join-Path $PSScriptRoot 'languages\ChineseSimplified.isl'
    $isccArgs = @(
        "/DAppVersion=$Version",
        "/DSourceDir=$publishDir",
        "/DOutputDir=$artifactsDir",
        (Join-Path $PSScriptRoot 'installer.iss')
    )
    if (Test-Path -LiteralPath $isl) { $isccArgs = @("/DChineseISL=$isl") + $isccArgs }
    else { Write-Warning '未找到 ChineseSimplified.isl，安装包将只有英文界面。' }

    & $iscc @isccArgs
    if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（exit $LASTEXITCODE）" }
}

Write-Host ''
Write-Host '== 完成，产物： =='
Get-ChildItem -LiteralPath $artifactsDir -File | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
