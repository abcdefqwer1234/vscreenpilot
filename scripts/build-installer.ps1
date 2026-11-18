#Requires -Version 5.1
<#
.SYNOPSIS
    构建 per-user 安装包（Inno Setup）。

.DESCRIPTION
    - 默认从 VScreenPilot.Server.csproj 读取版本；
    - 默认 publish 到 artifacts/release/v<version>/server；
    - 输出 artifacts/release/VScreenPilot-Setup-<version>.exe 并打印 SHA256。
    不会安装驱动、不会签名的（签名在发布流水线里单独处理）。
#>
param(
    [string]$Version,
    [string]$PublishDir,
    [string]$OutputDir = "artifacts/release",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Resolve-UnderRoot([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) {
        return [System.IO.Path]::GetFullPath($path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $root $path))
}

if (-not $Version) {
    [xml]$project = Get-Content "src\VScreenPilot.Server\VScreenPilot.Server.csproj"
    $Version = @($project.Project.PropertyGroup.Version)[0]
}

if (-not $Version) {
    throw "无法从 csproj 读取版本号，请用 -Version 指定"
}

if (-not $PublishDir) {
    $PublishDir = "artifacts\release\v$Version\server"
}

$publish = Resolve-UnderRoot $PublishDir
$output = Resolve-UnderRoot $OutputDir

if (-not $SkipPublish) {
    Write-Host "[..] dotnet publish -> $publish"
    dotnet publish src\VScreenPilot.Server\VScreenPilot.Server.csproj `
        -c Release -r win-x64 -o $publish

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败"
    }
}

$exe = Join-Path $publish "VScreenPilot.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "找不到已发布的 exe: $exe"
}

New-Item -ItemType Directory -Path $output -Force | Out-Null

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
)

$iscc = $isccCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

if (-not $iscc) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        $iscc = $command.Source
    }
}

if (-not $iscc) {
    throw "未找到 Inno Setup 编译器 ISCC.exe。安装: winget install --id JRSoftware.InnoSetup -e"
}

Write-Host "[..] ISCC $iscc"
& $iscc `
    "/DMyAppVersion=$Version" `
    "/DSourceDir=$publish" `
    "/DRepoRoot=$root" `
    "/DOutputDir=$output" `
    "packaging\installer\VScreenPilot.iss"

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup 编译失败（exit $LASTEXITCODE）"
}

$setup = Join-Path $output "VScreenPilot-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $setup)) {
    throw "安装包未生成: $setup"
}

$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
$size = (Get-Item -LiteralPath $setup).Length

Write-Host ""
Write-Host "[ok] 安装包: $setup"
Write-Host "     size  : $size"
Write-Host "     sha256: $hash"
