<#
.SYNOPSIS
    Build the npm single package.

.DESCRIPTION
    Thin wrapper around scripts/build-npm.mjs so the build can be launched
    from PowerShell. The Node script publishes the exe, copies it into npm/dist,
    runs npm pack, and writes artifacts/npm/vscreenpilot-<version>.tgz.
    It does not run npm publish.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'build-npm.mjs'

node $script $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "npm package build failed (exit=$LASTEXITCODE)"
}
