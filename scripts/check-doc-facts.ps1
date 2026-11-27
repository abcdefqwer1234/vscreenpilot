$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$doc = Get-ChildItem -LiteralPath $root -File -Filter '*.md' | Where-Object { $_.Length -gt 100000 } | Select-Object -First 1
$text = [System.IO.File]::ReadAllText($doc.FullName, [System.Text.Encoding]::UTF8)
$fail = New-Object System.Collections.Generic.List[string]

$defsDir = Join-Path $root 'src\VScreenPilot.Server\Tools\Definitions'
$defs = Get-ChildItem -LiteralPath $defsDir -Filter '*.json' | ForEach-Object {
    [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
}
if ($defs.Count -ne 30) { $fail.Add("tool definitions: $($defs.Count) != 30") }
if (-not $text.Contains('30 个工具')) { $fail.Add('tech doc missing: 30 个工具') }
foreach ($def in $defs) {
    if (-not $text.Contains($def.name)) { $fail.Add("tech doc missing tool: $($def.name)") }
}

foreach ($stale in @('22 个工具', '25 个工具')) {
    if ($text.Contains($stale)) { $fail.Add("tech doc still contains: $stale") }
}

$catalogPath = Join-Path $root 'src\VScreenPilot.Abstractions\Errors\ErrorCatalog.cs'
$catalog = [System.IO.File]::ReadAllText($catalogPath, [System.Text.Encoding]::UTF8)
$codes = [regex]::Matches($catalog, '"E_[A-Z_]+"') | ForEach-Object { $_.Value.Trim('"') } | Sort-Object -Unique
$countMatch = [regex]::Match($text, '共 (\d+) 个工具错误码')
if (-not $countMatch.Success) {
    $fail.Add('tech doc missing error-code count sentence')
} elseif ([int]$countMatch.Groups[1].Value -ne $codes.Count) {
    $fail.Add("error count: doc=$($countMatch.Groups[1].Value) catalog=$($codes.Count)")
}
foreach ($code in $codes) {
    if (-not $text.Contains($code)) { $fail.Add("tech doc missing error code: $code") }
}

$srcFiles = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -Filter '*.cs' -File
$srcText = ($srcFiles | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8) }) -join "`n"
$envs = [regex]::Matches($srcText, '"VSP_[A-Z0-9_]+"') | ForEach-Object { $_.Value.Trim('"') } | Sort-Object -Unique
foreach ($env in $envs) {
    if (-not $text.Contains($env)) { $fail.Add("tech doc missing env var: $env") }
}

foreach ($required in @('单客户端 6 帧', '63 个工具错误码')) {
    if (-not $text.Contains($required)) { $fail.Add("tech doc missing: $required") }
}

if ($fail.Count -gt 0) {
    Write-Host "[fail] doc facts:"
    $fail | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
Write-Host "[ok] doc facts: $($defs.Count) tools, $($codes.Count) error codes, $($envs.Count) env vars"
