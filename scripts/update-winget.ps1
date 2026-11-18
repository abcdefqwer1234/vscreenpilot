# 发布后更新 winget 多文件清单：版本号、安装包 URL 与 SHA256。
# 用法: powershell -File scripts/update-winget.ps1 -Version 0.1.0
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Setup = "artifacts/release/VScreenPilot-Setup-$Version.exe",
    [string]$Dir = "packaging/winget"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Test-Path -LiteralPath $Setup)) {
    throw "找不到安装包: $Setup（先运行 scripts/release.ps1 -Version $Version）"
}

$hash = (Get-FileHash -LiteralPath $Setup -Algorithm SHA256).Hash
$url = "https://github.com/abcdefqwer1234/vscreenpilot/releases/download/v$Version/VScreenPilot-Setup-$Version.exe"
$dirPath = Join-Path $root $Dir

$versionFile = Join-Path $dirPath "VScreenPilot.VScreenPilot.yaml"
$localeFile = Join-Path $dirPath "VScreenPilot.VScreenPilot.locale.en-US.yaml"
$installerFile = Join-Path $dirPath "VScreenPilot.VScreenPilot.installer.yaml"

foreach ($file in @($versionFile, $localeFile, $installerFile)) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "缺少 winget 清单: $file"
    }
}

function Save-Manifest([string]$path, [string]$content) {
    [System.IO.File]::WriteAllText(
        $path,
        $content,
        (New-Object System.Text.UTF8Encoding($false))
    )
}

function Read-Manifest([string]$path) {
    # winget 清单不带 BOM，必须显式按 UTF-8 读，否则 PS5.1 会按 ANSI 解码。
    return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
}

$content = Read-Manifest $versionFile
$content = $content -replace '(?m)^PackageVersion:.*$', "PackageVersion: $Version"
Save-Manifest $versionFile $content

$content = Read-Manifest $localeFile
$content = $content -replace '(?m)^PackageVersion:.*$', "PackageVersion: $Version"
Save-Manifest $localeFile $content

$content = Read-Manifest $installerFile
$content = $content -replace '(?m)^PackageVersion:.*$', "PackageVersion: $Version"
$content = $content -replace '(?m)^(\s+InstallerUrl:).*$', "`$1 $url"
$content = $content -replace '(?m)^(\s+InstallerSha256:).*$', "`$1 $hash"
$content = $content -replace '(?m)^(\s+DisplayVersion:).*$', "`$1 $Version"
Save-Manifest $installerFile $content

Write-Host "[ok] $dirPath"
Write-Host "  version: $Version"
Write-Host "  url    : $url"
Write-Host "  sha256 : $hash"
Write-Host "  校验   : cd $Dir; winget validate --manifest ."
