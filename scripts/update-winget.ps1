# 发布后更新 winget 清单：填入 GitHub Release 的安装包 URL 与 SHA256。
# 用法: powershell -File scripts/update-winget.ps1 -Version 0.1.0
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Setup = "artifacts/release/VScreenPilot-Setup-$Version.exe",
    [string]$Manifest = "packaging/winget/VScreenPilot.VScreenPilot.yaml"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Test-Path -LiteralPath $Setup)) {
    throw "找不到安装包: $Setup（先运行 scripts/release.ps1 -Version $Version）"
}

$hash = (Get-FileHash -LiteralPath $Setup -Algorithm SHA256).Hash
$url = "https://github.com/abcdefqwer1234/vscreenpilot/releases/download/v$Version/VScreenPilot-Setup-$Version.exe"

$content = Get-Content -LiteralPath $Manifest -Raw
$content = $content -replace '(?m)^PackageVersion:.*$', "PackageVersion: $Version"
$content = $content -replace '(?m)^(\s+InstallerUrl:).*$', "`$1 $url"
$content = $content -replace '(?m)^(\s+InstallerSha256:).*$', "`$1 $hash"

# 保留 LF；不要 BOM。
[System.IO.File]::WriteAllText(
    (Join-Path $root $Manifest),
    $content,
    (New-Object System.Text.UTF8Encoding($false))
)

Write-Host "[ok] $Manifest"
Write-Host "  version: $Version"
Write-Host "  url    : $url"
Write-Host "  sha256 : $hash"
