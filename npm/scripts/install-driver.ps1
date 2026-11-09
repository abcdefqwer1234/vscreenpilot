#Requires -Version 5.1
<#
.SYNOPSIS
    一次性安装 Parsec VDD 官方驱动（需管理员，winget 会触发一次 UAC）。

.DESCRIPTION
    - 已安装则直接返回 0。
    - 仅检查不安装时使用 -CheckOnly。
    - 运行期不需要管理员权限：挂载/摘除虚拟屏是用户态 IOCTL。
#>
[CmdletBinding()]
param(
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$packageId = 'Parsec.ParsecVDD'

function Test-VddInstalled {
    $device = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
        Where-Object {
            $_.InstanceId -eq 'ROOT\DISPLAY\0000' -or
            $_.FriendlyName -eq 'Parsec Virtual Display Adapter'
        }
    return $null -ne $device
}

if (Test-VddInstalled) {
    Write-Host '[ok] Parsec VDD 已安装'
    exit 0
}

if ($CheckOnly) {
    Write-Host '[missing] Parsec VDD 未安装'
    exit 1
}

Write-Host '[..] 正在通过 winget 安装 Parsec VDD（会弹一次 UAC）'
winget install --id $packageId -e --accept-package-agreements --accept-source-agreements

if (-not (Test-VddInstalled)) {
    throw '安装结束但未检测到 Parsec Virtual Display Adapter，请检查 winget 输出或设备管理器。'
}

Write-Host '[ok] Parsec VDD 安装完成，无需重启'
exit 0
