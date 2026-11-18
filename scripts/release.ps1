# 发布准备脚本（不自动上传）
# 用法: powershell -File scripts/release.ps1 -Version 0.2.0
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Output = "artifacts/release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$versionTag = "v$Version"
$staging = Join-Path $Output $versionTag
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

Write-Host "Publishing $versionTag ..."
dotnet publish src/VScreenPilot.Server/VScreenPilot.Server.csproj `
    -c Release -r win-x64 -o (Join-Path $staging "server")

$exe = Join-Path $staging "server/VScreenPilot.exe"
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
$size = (Get-Item $exe).Length

$zip = Join-Path $Output "VScreenPilot-win-x64-$versionTag.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging "server/*") -DestinationPath $zip
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash
$zipSize = (Get-Item $zip).Length

Write-Host ""
Write-Host "产物: $zip"
Write-Host "exe SHA256: $hash"
Write-Host "zip SHA256: $zipHash"
Write-Host "exe bytes : $size"
Write-Host "zip bytes : $zipSize"
Write-Host ""
Write-Host "下一步（需要远程/证书/令牌，脚本不会代做）:"
Write-Host "  1. 更新 packaging/winget/VScreenPilot.VScreenPilot.yaml 的 InstallerUrl/InstallerSha256"
Write-Host "  2. 更新 packaging/server.json 的 version"
Write-Host "  3. 签名 exe，创建 GitHub Release 并上传 zip"
Write-Host "  4. npm publish（需要 npm token）"
