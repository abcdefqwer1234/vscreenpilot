import { execFile, spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
export const TESTTOOL = path.resolve(HERE, '..');
export const ROOT = path.resolve(TESTTOOL, '..', '..');
export const TMP = path.join(TESTTOOL, '.tmp');
export const RES = path.join(TESTTOOL, 'results');
export const FIX = path.join(TESTTOOL, 'fixtures');

export const EXE = path.join(ROOT, 'artifacts', 'server', 'VScreenPilot.exe');
export const PROBE = path.join(ROOT, 'tools', 'VddProbe', 'bin', 'Release', 'net10.0-windows', 'VddProbe.exe');
export const NOTEPAD = 'C:\\Windows\\System32\\notepad.exe';
export const CALC = 'C:\\Windows\\System32\\calc.exe';
export const EXPLORER = 'C:\\Windows\\explorer.exe';
export const WINWORD = 'C:\\Program Files\\Microsoft Office\\Root\\Office16\\WINWORD.EXE';
export const EXCEL = 'C:\\Program Files\\Microsoft Office\\Root\\Office16\\EXCEL.EXE';
export const UITARGET = path.join(TESTTOOL, 'targets', 'UiTarget', 'bin', 'Release', 'net10.0-windows', 'UiTarget.exe');

export const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

export function ensureDirs() {
  for (const dir of [TMP, RES, FIX]) fs.mkdirSync(dir, { recursive: true });
}

export async function ps(script, options = {}) {
  const { timeout = 60000, allowFail = true } = options;
  return new Promise((resolve) => {
    execFile('powershell', [
      '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command',
      `[Console]::OutputEncoding=[Text.Encoding]::UTF8; ${script}`,
    ], { timeout, windowsHide: true, maxBuffer: 8 * 1024 * 1024 }, (error, stdout, stderr) => {
      if (error && !allowFail) resolve({ ok: false, stdout: stdout ?? '', stderr: stderr ?? String(error), code: error.code ?? -1 });
      else resolve({ ok: !error, stdout: stdout ?? '', stderr: stderr ?? '', code: error?.code ?? 0 });
    });
  });
}

export async function run(exe, args = [], options = {}) {
  const { timeout = 60000, cwd = ROOT } = options;
  return new Promise((resolve) => {
    execFile(exe, args, { timeout, windowsHide: true, cwd, maxBuffer: 8 * 1024 * 1024 }, (error, stdout, stderr) => {
      resolve({ ok: !error, stdout: stdout ?? '', stderr: stderr ?? (error ? String(error) : ''), code: error?.code ?? 0 });
    });
  });
}

/** 只清理本测试启动的进程，避免误杀用户正在编辑的 Word/Excel/记事本。 */
export async function cleanupRuntime({ removeDisplay = true } = {}) {
  await ps(`
    $pids = Get-CimInstance Win32_Process -Filter "Name='VScreenPilot.exe'" -ErrorAction SilentlyContinue |
      Where-Object { $_.CommandLine -like '*--parent-pid*' } | Select-Object -ExpandProperty ProcessId;
    if ($pids) { $pids | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue } }
    $apps = Get-CimInstance Win32_Process -Filter "Name='EXCEL.EXE' OR Name='WINWORD.EXE' OR Name='notepad.exe'" -ErrorAction SilentlyContinue |
      Where-Object { $_.CommandLine -like '*TestTool\\.tmp*' } | Select-Object -ExpandProperty ProcessId;
    if ($apps) { $apps | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue } }
  `);
  if (removeDisplay && fs.existsSync(PROBE)) {
    await run(PROBE, ['removeall'], { timeout: 30000 });
    // Parsec 驱动在 REMOVE 后数秒内会拒绝新的 ADD（实测约 3s），需等待再启动服务端。
    await sleep(5000);
  }
  const local = path.join(process.env.LOCALAPPDATA ?? '', 'VScreenPilot');
  try { fs.rmSync(local, { recursive: true, force: true }); } catch { /* ignore */ }
  await sleep(300);
}

export async function setupFixtures() {
  fs.rmSync(FIX, { recursive: true, force: true });
  fs.mkdirSync(path.join(FIX, 't1'), { recursive: true });
  fs.writeFileSync(path.join(FIX, 't1', 'note.txt'), '', 'utf8');

  const t3 = path.join(FIX, 't3');
  fs.mkdirSync(path.join(t3, 'sub'), { recursive: true });
  fs.writeFileSync(path.join(t3, 'a.txt'), 'alpha', 'utf8');
  fs.writeFileSync(path.join(t3, 'b.txt'), 'beta', 'utf8');
  fs.writeFileSync(path.join(t3, 'sub', 'keep.txt'), 'keep', 'utf8');

  fs.mkdirSync(path.join(FIX, 't4'), { recursive: true });
  fs.mkdirSync(path.join(FIX, 't5'), { recursive: true });

  const t6 = path.join(FIX, 't6');
  fs.mkdirSync(t6, { recursive: true });
  await ps(`
    $ErrorActionPreference='Stop';
    $w = New-Object -ComObject Word.Application; $w.Visible = $false;
    $d = $w.Documents.Add();
    $d.Content.Text = "OLD LINE 1\`rOLD LINE 2";
    $d.SaveAs2('${escapePs(path.join(t6, 'seed.docx'))}', 16);
    $d.Close($false); $w.Quit();
  `, { allowFail: false });

  const t7 = path.join(FIX, 't7');
  fs.mkdirSync(t7, { recursive: true });
  await ps(`
    $ErrorActionPreference='Stop';
    $x = New-Object -ComObject Excel.Application; $x.Visible=$false; $x.DisplayAlerts=$false;
    $wb = $x.Workbooks.Add(); $ws = $wb.Worksheets.Item(1);
    $ws.Cells.Item(1,1).Value2 = 'Item'; $ws.Cells.Item(1,2).Value2 = 'Value';
    $ws.Cells.Item(2,1).Value2 = 'Alpha'; $ws.Cells.Item(2,2).Value2 = 0;
    $wb.SaveAs('${escapePs(path.join(t7, 'seed.xlsx'))}', 51);
    $wb.Close($false); $x.Quit();
  `, { allowFail: false });

  await ps(`
    Add-Type -AssemblyName System.Drawing;
    $bmp = New-Object Drawing.Bitmap 32,32;
    $g = [Drawing.Graphics]::FromImage($bmp); $g.Clear([Drawing.Color]::White);
    $g.FillRectangle([Drawing.Brushes]::Red, 4, 4, 24, 24); $g.Dispose();
    $bmp.Save('${escapePs(path.join(FIX, 't5', 'template.png'))}', [Drawing.Imaging.ImageFormat]::Png);
    $bmp.Dispose();
  `, { allowFail: false });
}

export function prepareWork(arm, task, runIndex) {
  let dir = path.join(TMP, arm, task, String(runIndex));
  try { fs.rmSync(dir, { recursive: true, force: true }); } catch { dir = `${dir}-${Date.now()}`; }
  fs.mkdirSync(dir, { recursive: true });
  if (task === 't1') fs.writeFileSync(path.join(dir, 'note.txt'), '', 'utf8');
  if (task === 't3') {
    fs.mkdirSync(path.join(dir, 'sub'), { recursive: true });
    fs.writeFileSync(path.join(dir, 'a.txt'), 'alpha', 'utf8');
    fs.writeFileSync(path.join(dir, 'b.txt'), 'beta', 'utf8');
    fs.writeFileSync(path.join(dir, 'sub', 'keep.txt'), 'keep', 'utf8');
  }
  if (task === 't6') fs.copyFileSync(path.join(FIX, 't6', 'seed.docx'), path.join(dir, 'doc.docx'));
  if (task === 't7') fs.copyFileSync(path.join(FIX, 't7', 'seed.xlsx'), path.join(dir, 'book.xlsx'));
  return dir;
}

export function escapePs(value) {
  return String(value).replace(/'/g, "''");
}

export async function startSampler(csvPath, serverPid) {
  const script = `
    Add-Type -TypeDefinition @"
    using System;
    using System.Runtime.InteropServices;
    public static class S {
      [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
      [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
      [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    }
"@;
    $sw = [Diagnostics.Stopwatch]::StartNew();
    while ($true) {
      $p = New-Object S+POINT;
      [void][S]::GetCursorPos([ref]$p);
      $fg = [S]::GetForegroundWindow().ToInt64();
      $rss = 0;
      try { $rss = (Get-Process -Id ${serverPid} -ErrorAction Stop).WorkingSet64 } catch { }
      "{0},{1},{2},{3},{4}" -f [int]$sw.Elapsed.TotalMilliseconds, $p.X, $p.Y, $fg, $rss | Out-File -Append -Encoding ascii '${escapePs(csvPath)}';
      Start-Sleep -Milliseconds 100;
    }
  `;
  const child = spawn('powershell', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script], {
    windowsHide: true, stdio: 'ignore',
  });
  return child;
}

export function stopSampler(child) {
  try { child.kill(); } catch { /* ignore */ }
}

export function readSamples(csvPath) {
  if (!fs.existsSync(csvPath)) return { cursorMoves: 0, foregroundChanges: 0, peakRss: 0, samples: 0 };
  const lines = fs.readFileSync(csvPath, 'utf8').trim().split(/\r?\n/).filter(Boolean).slice(1);
  let cursorMoves = 0;
  let foregroundChanges = 0;
  let peakRss = 0;
  let last = null;
  let lastFg = null;
  for (const line of lines) {
    const parts = line.split(',');
    if (parts.length < 5) continue;
    const [, x, y, fg, rss] = parts.map((v) => Number(v));
    if (!Number.isFinite(x) || !Number.isFinite(y)) continue;
    if (last && (Math.abs(x - last.x) > 2 || Math.abs(y - last.y) > 2)) cursorMoves++;
    if (lastFg !== null && fg !== lastFg) foregroundChanges++;
    last = { x, y };
    lastFg = fg;
    peakRss = Math.max(peakRss, rss || 0);
  }
  return { cursorMoves, foregroundChanges, peakRss, samples: lines.length };
}

export async function wordRead(file) {
  return ps(`
    $ErrorActionPreference='Stop';
    $w = New-Object -ComObject Word.Application; $w.Visible=$false;
    $d = $w.Documents.Open('${escapePs(file)}', $false, $true);
    $d.Content.Text; $d.Close($false); $w.Quit();
  `, { timeout: 60000 });
}

export async function excelRead(file) {
  return ps(`
    $ErrorActionPreference='Stop';
    $x = New-Object -ComObject Excel.Application; $x.Visible=$false; $x.DisplayAlerts=$false;
    $wb = $x.Workbooks.Open('${escapePs(file)}', 0, $true); $ws = $wb.Worksheets.Item(1);
    [pscustomobject]@{
      a1 = [string]$ws.Range('A1').Value2; b1 = [string]$ws.Range('B1').Value2;
      a2 = [string]$ws.Range('A2').Value2; b2 = [string]$ws.Range('B2').Value2
    } | ConvertTo-Json -Compress;
    $wb.Close($false); $x.Quit();
  `, { timeout: 60000 });
}
