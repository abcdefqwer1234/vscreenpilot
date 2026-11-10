param(
    [Parameter(Mandatory = $true)][string]$Task,
    [Parameter(Mandatory = $true)][string]$Work
)

if ($env:VSP_ALLOW_REAL_DESKTOP -ne '1') {
    Write-Output 'RESULT:SKIP real-desktop disabled (set VSP_ALLOW_REAL_DESKTOP=1)'
    exit 0
}

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName Microsoft.VisualBasic

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

function Click-Window([IntPtr]$Hwnd) {
    $r = New-Object W+RECT
    if (-not [W]::GetWindowRect($Hwnd, [ref]$r)) { return }
    $x = [int](($r.Left + $r.Right) / 2)
    $y = [int]($r.Top + 40)
    [void][W]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 150
    [W]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    [W]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

$script:steps = 0
function Step([string]$name) { $script:steps++; Write-Host "STEP $name" }

function Focus-Process([int]$ProcId) {
    try { [Microsoft.VisualBasic.Interaction]::AppActivate($ProcId) } catch { }
    $p = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
    if (-not $p) { return }
    [void][W]::ShowWindow($p.MainWindowHandle, 9)
    [void][W]::SetForegroundWindow($p.MainWindowHandle)
    Start-Sleep -Milliseconds 500
}

function Get-Window([int]$ProcId, [int]$TimeoutMs = 15000) {
    $root = [Windows.Automation.AutomationElement]::RootElement
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $cond = New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcId)
        $win = $root.FindFirst([Windows.Automation.TreeScope]::Children, $cond)
        if ($win) { return $win }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Find-ById($Window, [string]$Id) {
    $cond = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Window.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-ByType($Root, [string]$TypeName) {
    $cond = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::$TypeName)
    return $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
}

function Send([string]$keys) {
    [Windows.Forms.SendKeys]::SendWait($keys)
    Start-Sleep -Milliseconds 250
}

function Finish([bool]$ok, [string]$reason = '') {
    if ($ok) { Write-Output "RESULT:OK steps=$script:steps" }
    else { Write-Output "RESULT:FAIL steps=$script:steps reason=$reason" }
    exit 0
}

try {
    switch ($Task) {
        't1' {
            $file = Join-Path $Work 'note.txt'
            $p = Start-Process -FilePath 'C:\Windows\System32\notepad.exe' -ArgumentList "`"$file`"" -PassThru
            Step 'launch'
            Start-Sleep -Seconds 2
            Focus-Process $p.Id
            Click-Window $p.MainWindowHandle
            $win = Get-Window $p.Id
            $doc = $null
            if ($win) {
                $doc = Find-ByType $win 'Document'
                if (-not $doc) { $doc = Find-ByType $win 'Edit' }
            }
            $text = "Hello 虚拟屏 123`r`n第二行 ABC"
            $typed = $false
            if ($doc) {
                try {
                    $doc.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($text)
                    $typed = $true
                    Step 'uia-set'
                } catch { }
            }
            if (-not $typed) {
                [Windows.Forms.Clipboard]::SetText($text)
                Step 'clipboard'
                Send '^v'
                Step 'paste'
            }
            Send '^s'
            Step 'save'
            Start-Sleep -Seconds 1
            $text = Get-Content -LiteralPath $file -Raw -Encoding UTF8
            $ok = $text -match 'Hello' -and $text -match '第二行'
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            Finish $ok "content=$($text.Length)"
        }
        't2' {
            $p = Start-Process -FilePath 'C:\Windows\System32\calc.exe' -PassThru
            Step 'launch'
            Start-Sleep -Seconds 3
            $calc = Get-Process | Where-Object { $_.MainWindowTitle -match 'Calculator|计算器' } | Select-Object -First 1
            if ($calc) { Focus-Process $calc.Id }
            Send '12*34='
            Step 'keys'
            Start-Sleep -Milliseconds 800
            $root = [Windows.Automation.AutomationElement]::RootElement
            $all = $root.FindAll([Windows.Automation.TreeScope]::Descendants,
                [Windows.Automation.Condition]::TrueCondition)
            $found = $false
            foreach ($el in $all) {
                try {
                    $n = $el.Current.Name
                    if ($n -and $n -match '408') { $found = $true; break }
                } catch { }
            }
            if ($calc) { Stop-Process -Id $calc.Id -Force -ErrorAction SilentlyContinue }
            Finish $found "found=$found"
        }
        't3' {
            $p = Start-Process -FilePath 'C:\Windows\explorer.exe' -ArgumentList "`"$Work`"" -PassThru
            Step 'launch'
            Start-Sleep -Seconds 2
            Focus-Process $p.Id
            Send 'a'
            Send '{F2}'
            Send 'renamed.txt'
            Send '{ENTER}'
            Step 'rename'
            Start-Sleep -Milliseconds 800
            Send 'b'
            Send '{DELETE}'
            Step 'delete'
            Start-Sleep -Milliseconds 800
            Send '^+n'
            Send 'newfolder'
            Send '{ENTER}'
            Step 'mkdir'
            Start-Sleep -Milliseconds 800
            $ok = (Test-Path (Join-Path $Work 'renamed.txt')) -and
                  -not (Test-Path (Join-Path $Work 'a.txt')) -and
                  -not (Test-Path (Join-Path $Work 'b.txt')) -and
                  (Test-Path (Join-Path $Work 'newfolder'))
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            Finish $ok "fs=$ok"
        }
        't4' {
            $log = Join-Path $Work 'event.json'
            $exe = Join-Path $PSScriptRoot '..\targets\UiTarget\bin\Release\net10.0-windows\UiTarget.exe'
            $p = Start-Process -FilePath $exe -ArgumentList "`"$log`"" -PassThru
            Step 'launch'
            Start-Sleep -Seconds 2
            $win = Get-Window $p.Id
            if (-not $win) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; Finish $false 'no-window' }
            $input = Find-ById $win 'input'
            $count = Find-ById $win 'count'
            $opt = Find-ById $win 'opt'
            $submit = Find-ById $win 'submit'
            if (-not ($input -and $count -and $opt -and $submit)) {
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                Finish $false 'missing-control'
            }
            $input.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('hello-bench')
            Step 'set-input'
            $count.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('7')
            Step 'set-count'
            $opt.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
            Step 'toggle'
            $submit.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            Step 'submit'
            Start-Sleep -Milliseconds 800
            $ok = $false
            if (Test-Path -LiteralPath $log) {
                $json = Get-Content -LiteralPath $log -Raw | ConvertFrom-Json
                $ok = $json.input -eq 'hello-bench' -and $json.count -eq '7' -and $json.opt -eq $true
            }
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            Finish $ok "json=$ok"
        }
        default { Finish $false "unknown-task=$Task" }
    }
} catch {
    Finish $false $_.Exception.Message
}
