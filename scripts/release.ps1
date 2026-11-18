# 发布准备脚本（不自动上传）
# 用法: powershell -File scripts/release.ps1 -Version 0.1.0
# 产物:
#   VScreenPilot-win-x64-v<ver>.zip   绿色版（exe + LICENSE/NOTICE/README）
#   VScreenPilot-symbols-v<ver>.zip   符号（pdb）
#   VScreenPilot-Setup-<ver>.exe      安装版（per-user）
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Output = "artifacts/release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$versionTag = "v$Version"
$staging = Join-Path $Output $versionTag
if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $staging | Out-Null

$server = Join-Path $staging "server"
Write-Host "[..] dotnet publish -> $server"
dotnet publish src/VScreenPilot.Server/VScreenPilot.Server.csproj `
    -c Release -r win-x64 -o $server

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败"
}

$exe = Join-Path $server "VScreenPilot.exe"
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$size = (Get-Item -LiteralPath $exe).Length

# 绿色版：只带运行必需的文件和许可证，不塞 pdb。
$portable = Join-Path $staging "portable"
New-Item -ItemType Directory -Path $portable -Force | Out-Null
Copy-Item -LiteralPath $exe -Destination $portable
Copy-Item -LiteralPath "LICENSE", "THIRD-PARTY-NOTICES.md", "README.md" -Destination $portable

$zip = Join-Path $Output "VScreenPilot-win-x64-$versionTag.zip"
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive -Path (Join-Path $portable "*") -DestinationPath $zip
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
$zipSize = (Get-Item -LiteralPath $zip).Length

# 符号单独打包，方便排查崩溃，不干扰普通用户。
$symbols = Join-Path $Output "VScreenPilot-symbols-$versionTag.zip"
if (Test-Path -LiteralPath $symbols) {
    Remove-Item -LiteralPath $symbols -Force
}
$pdbFiles = @(Get-ChildItem -LiteralPath $server -Filter *.pdb)
if ($pdbFiles.Count -gt 0) {
    Compress-Archive -Path $pdbFiles.FullName -DestinationPath $symbols
}
$symbolsHash = if (Test-Path -LiteralPath $symbols) {
    (Get-FileHash -LiteralPath $symbols -Algorithm SHA256).Hash
} else {
    $null
}

# 安装版（Inno Setup，per-user，无 UAC）。
& (Join-Path $PSScriptRoot "build-installer.ps1") `
    -Version $Version `
    -PublishDir $server `
    -OutputDir $Output `
    -SkipPublish

$setup = Join-Path $Output "VScreenPilot-Setup-$Version.exe"
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
$setupSize = (Get-Item -LiteralPath $setup).Length

Write-Host ""
Write-Host "产物:"
Write-Host "  portable: $zip"
Write-Host "  symbols : $symbols"
Write-Host "  setup   : $setup"
Write-Host ""
Write-Host "exe     SHA256: $hash"
Write-Host "zip     SHA256: $zipHash"
Write-Host "symbols SHA256: $symbolsHash"
Write-Host "setup   SHA256: $setupHash"
Write-Host ""
Write-Host "大小: exe=$size zip=$zipSize setup=$setupSize"
Write-Host ""
Write-Host "下一步（需要远程/证书/令牌，脚本不会代做）:"
Write-Host "  1. 发布 Release，上传 zip + setup（+ 可选 symbols）"
Write-Host "  2. powershell -File scripts/update-winget.ps1 -Version $Version"
Write-Host "  3. wingetcreate submit packaging/winget（需 GitHub 账号 + CLA）"
