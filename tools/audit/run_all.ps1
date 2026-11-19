# 一键运行审计套件。
#
# 用法：
#   pwsh tools/audit/run_all.ps1 -Exe artifacts/server/VScreenPilot.exe -Out tools/audit/results/current
#   pwsh tools/audit/run_all.ps1 -Exe ... -Out ... -SkipDisplay     # CI 无显示子集
#
# 设计约定：
#   * 每个脚本把自己的产物写到 $Out 下的子目录（脚本自己建）；
#   * 任何一步失败默认立即停止，-KeepGoing 可继续；
#   * exit code 非 0 表示存在失败项，便于 CI 直接使用。

[CmdletBinding()]
param(
    [string]$Exe = "artifacts/server/VScreenPilot.exe",
    [string]$Out = "tools/audit/results/current",
    [switch]$SkipDisplay,
    [switch]$SkipPpt,
    [switch]$KeepGoing,
    [switch]$RecordOnly,
    [int]$Fuzz = 500,
    [string]$Python = "python",
    [string]$Profile = "vspaudit",
    [string]$PatchedExe = "",
    [string]$WordDocx = ""
)

$ErrorActionPreference = "Stop"

# 审计必须使用独立 profile：默认 profile 可能正被用户会话（opencode 等）占用。
$PreviousProfile = $env:VSP_PROFILE
$env:VSP_PROFILE = $Profile

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$AuditDir = $PSScriptRoot

function Resolve-RepoPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path $RepoRoot $Path
}

$ExePath = Resolve-RepoPath $Exe
$OutPath = Resolve-RepoPath $Out

if (-not (Test-Path $ExePath)) {
    Write-Error "找不到被测二进制：$ExePath"
    exit 2
}

if (-not (Test-Path $OutPath)) {
    New-Item -ItemType Directory -Path $OutPath -Force | Out-Null
}
$LogDir = Join-Path $OutPath "logs"
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

$Steps = New-Object System.Collections.ArrayList

function Add-Step([string]$Name, [string]$Script, [hashtable]$Extra = @{}, [switch]$Display, [switch]$NeedsUiTarget) {
    $null = $Steps.Add([pscustomobject]@{
        Name          = $Name
        Script        = Join-Path $AuditDir $Script
        Extra         = $Extra
        Display       = [bool]$Display
        NeedsUiTarget = [bool]$NeedsUiTarget
    })
}

# UiTarget 需要 dotnet build；缺失时跳过依赖它的用例，而不是假装通过。
$UiTarget = Join-Path $AuditDir "targets/bin/UiTarget/UiTarget.exe"
$HasUiTarget = Test-Path $UiTarget

# ---- 协议与契约（无显示，CI 可跑） ----
Add-Step "smoke_no_display" "smoke_no_display.py"
Add-Step "bench_contract" "bench_contract.py"
Add-Step "bench_protocol" "bench_protocol.py" @{ fuzz = $Fuzz } -NeedsUiTarget

if (-not $SkipDisplay) {
    # ---- 性能与并发（需要 VDD） ----
    Add-Step "bench_baseline" "bench_baseline.py" @{} -Display
    Add-Step "bench_perf" "bench_perf.py" @{} -Display
    Add-Step "bench_capture_soak" "bench_capture_soak.py" @{} -Display
    Add-Step "bench_concurrency" "bench_concurrency.py" @{} -Display
    Add-Step "bench_dialogs" "bench_dialogs.py" @{} -Display
    Add-Step "bench_regressions" "bench_regressions.py" @{} -Display
    Add-Step "bench_stability" "bench_stability.py" @{} -Display
    Add-Step "bench_gui" "bench_gui.py" @{} -Display
    Add-Step "bench_gui2" "bench_gui2.py" @{} -Display
    Add-Step "bench_ccw" "bench_ccw.py" @{} -Display
    Add-Step "bench_hotplug" "bench_hotplug.py" @{} -Display
    Add-Step "bench_fixups" "bench_fixups.py" @{} -Display

    if ($PatchedExe -ne "") {
        Add-Step "verify_virtualdesk" "verify_virtualdesk.py" @{
            original = $ExePath
            patched  = (Resolve-RepoPath $PatchedExe)
        } -Display
    }

    if ($WordDocx -ne "") {
        Add-Step "word_oracle" "word_oracle.py" @{ docx = (Resolve-RepoPath $WordDocx); json = (Join-Path $OutPath "word_oracle/oracle.json") } -Display
    }

    if (-not $SkipPpt) {
        $Deck = Join-Path $RepoRoot "artifacts/ppt/VScreenPilot-PPT-测评.pptx"
        if (Test-Path $Deck) {
            $PptDir = Join-Path $AuditDir "ppt"
            foreach ($Name in @("fixes", "flow", "extra", "unsaved", "click", "concurrency", "dialog", "input", "recovery", "resolution", "snapshot", "soak")) {
                Add-Step "bench_ppt_$Name" (Join-Path $PptDir "bench_ppt_$Name.py") @{} -Display
            }
        }
        else {
            Write-Host "[skip] 未找到 PPT 评测文件：$Deck"
        }
    }

    Add-Step "check_slo" "check_slo.py" @{} -Display
}

$Results = New-Object System.Collections.ArrayList
$Failed = New-Object System.Collections.ArrayList
$Skipped = New-Object System.Collections.ArrayList

foreach ($Step in $Steps) {
    if (-not (Test-Path $Step.Script)) {
        Write-Warning "跳过不存在的脚本：$($Step.Script)"
        continue
    }

    if ($Step.NeedsUiTarget -and -not $HasUiTarget) {
        Write-Warning "跳过 $($Step.Name)：缺少 $UiTarget（先构建审计目标程序）"
        $null = $Skipped.Add($Step.Name)
        continue
    }

    $Arguments = @($Step.Script)
    # smoke_no_display 不按脚本名分目录，需显式给子目录，避免污染对比根目录。
    if ($Step.Name -eq "smoke_no_display") {
        $Arguments += @("--exe", $ExePath, "--out", (Join-Path $OutPath "smoke"))
    }
    # word_oracle 只接受位置参数 docx，没有 --exe/--out。
    elseif ($Step.Name -ne "word_oracle" -and $Step.Name -ne "check_slo") {
        $Arguments += @("--exe", $ExePath, "--out", $OutPath)
    }
    elseif ($Step.Name -eq "check_slo") {
        $Arguments += @("--results", $OutPath)
    }

    if ($Step.Name -eq "word_oracle") {
        $Arguments += @($Step.Extra.docx)
        if ($Step.Extra.json) { $Arguments += @("--json", $Step.Extra.json) }
    }
    else {
        foreach ($Key in $Step.Extra.Keys) {
            $Value = $Step.Extra[$Key]
            $Arguments += @("--$Key", [string]$Value)
        }
    }

    $Log = Join-Path $LogDir "$($Step.Name).log"
    Write-Host "`n==> $($Step.Name)"
    $Started = Get-Date
    # 原生进程写 stderr 会被 PS 5.1 当成 ErrorRecord；这里临时降级，避免 -Stop 直接终止。
    $SavedEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $Output = & $Python @Arguments 2>&1 | ForEach-Object { $_.ToString() }
    $Code = $LASTEXITCODE
    $ErrorActionPreference = $SavedEap
    $Output | Set-Content -Path $Log -Encoding UTF8
    $Output | Select-Object -Last 15 | ForEach-Object { Write-Host "    $_" }

    $null = $Results.Add([pscustomobject]@{
        name       = $Step.Name
        exit_code  = $Code
        duration_s = [math]::Round(((Get-Date) - $Started).TotalSeconds, 2)
        log        = $Log
        display    = $Step.Display
    })

    if ($Code -ne 0) {
        $null = $Failed.Add($Step.Name)
        Write-Warning "$($Step.Name) 失败（exit=$Code），日志：$Log"
        if (-not $KeepGoing) { break }
    }
}

$Summary = [pscustomobject]@{
    exe        = $ExePath
    exe_sha256 = (Get-FileHash $ExePath -Algorithm SHA256).Hash.ToUpper()
    out        = $OutPath
    profile    = $Profile
    skip_display = [bool]$SkipDisplay
    started    = (Get-Date).ToString("o")
    steps      = $Results
    skipped    = $Skipped
    record_only = [bool]$RecordOnly
    failures   = $Failed
}
$Summary | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $OutPath "run_all.json") -Encoding UTF8

Write-Host ""
Write-Host ("通过 {0}/{1}" -f ($Results.Count - $Failed.Count), $Results.Count)
if ($Skipped.Count -gt 0) { Write-Host ("跳过：{0}" -f ($Skipped -join ", ")) }
if ($PreviousProfile) { $env:VSP_PROFILE = $PreviousProfile } else { Remove-Item Env:\VSP_PROFILE -ErrorAction SilentlyContinue }
if ($Failed.Count -gt 0) {
    Write-Host ("失败：{0}" -f ($Failed -join ", "))
    # -RecordOnly 用于“抓基线”：记录失败项的现状，不以它们判定本次运行失败。
    if ($RecordOnly) {
        Write-Host "（-RecordOnly：失败项已记录，退出码按 0 处理）"
        exit 0
    }
    exit 1
}
exit 0
