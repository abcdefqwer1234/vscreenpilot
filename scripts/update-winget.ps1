# 发布后更新 winget 清单：填入 GitHub Release 的 zip URL 与 SHA256。
# 用法: powershell -File scripts/update-winget.ps1 -Version 0.1.0
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Zip = "artifacts/release/VScreenPilot-win-x64-v$Version.zip",
    [string]$Manifest = "packaging/winget/VScreenPilot.VScreenPilot.yaml"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Test-Path -LiteralPath $Zip)) {
    throw "找不到发布 zip: $Zip（先运行 scripts/release.ps1 -Version $Version）"
}

$hash = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash
$url = "https://github.com/abcdefqwer1234/vscreenpilot/releases/download/v$Version/VScreenPilot-win-x64-v$Version.zip"

$content = Get-Content -LiteralPath $Manifest -Raw
$content = $content -replace '(?m)^PackageVersion:.*$', "PackageVersion: $Version"
$content = $content -replace '(?m)^(\s+InstallerUrl:).*$', "`$1 $url"
$content = $content -replace '(?m)^(\s+InstallerSha256:).*$', "`$1 $hash"

# 保留 LF；winget-pkgs 不接受 CRLF 之外的意外 BOM。
[System.IO.File]::WriteAllText(
    (Join-Path $root $Manifest),
    $content,
    (New-Object System.Text.UTF8Encoding($false))
)

Write-Host "[ok] $Manifest"
Write-Host "  version: $Version"
Write-Host "  url    : $url"
Write-Host "  sha256 : $hash"
