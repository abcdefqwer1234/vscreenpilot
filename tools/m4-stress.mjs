/**
 * M4 进程级完整生命周期压测。
 *
 * 每轮：新起 server -> initialize -> 挂屏 -> 启动记事本 -> 截图/语义查找
 *      -> release(keep_display=false) -> EOF -> 校验虚拟屏/进程/光标。
 *
 * 用法：node tools/m4-stress.mjs .\artifacts\server\VScreenPilot.exe [rounds]
 */
import { spawn, execFileSync } from 'node:child_process';
import { appendFileSync, mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import readline from 'node:readline';

const exe = process.argv[2];
const rounds = Number.parseInt(process.argv[3] ?? process.env.VSP_ROUNDS ?? '50', 10);
const probe = process.argv[4] ?? '.\\tools\\VddProbe\\bin\\Release\\net10.0-windows\\VddProbe.exe';
const notepad = 'C:\\Windows\\System32\\notepad.exe';

if (!exe) {
  console.error('usage: node m4-stress.mjs <VScreenPilot.exe> [rounds] [VddProbe.exe]');
  process.exit(1);
}

const log = [];
const rows = [];
const record = (label, value) => {
  log.push(`--- ${label} ---`);
  log.push(typeof value === 'string' ? value : JSON.stringify(value, null, 2));
};

function powershell(script) {
  try {
    return execFileSync('powershell.exe', ['-NoProfile', '-Command', script], {
      encoding: 'utf8',
      windowsHide: true,
      timeout: 30000,
    }).trim();
  } catch (error) {
    return (error.stdout ?? '').toString().trim();
  }
}

function listPids(name) {
  const text = powershell(
    `(Get-Process -Name '${name}' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }) -join ','`
  );
  return new Set(text.split(',').map((v) => Number.parseInt(v, 10)).filter(Number.isFinite));
}

/** ����ѹ���������� server����������²�Ӧ���У��� */
function killOwnOrphanServers() {
  powershell(
    `Get-CimInstance Win32_Process -Filter "Name='VScreenPilot.exe'" | ` +
    `Where-Object { $_.CommandLine -like '*--parent-pid ${process.pid}*' } | ` +
    `ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }`
  );
}

function cursorPosition() {
  const text = powershell(
    'Add-Type -AssemblyName System.Windows.Forms; $p=[System.Windows.Forms.Cursor]::Position; "$($p.X),$($p.Y)"'
  );
  const [x, y] = text.split(',').map((v) => Number.parseInt(v, 10));
  return Number.isFinite(x) && Number.isFinite(y) ? { x, y } : null;
}

function probeStatus() {
  let output = '';
  try {
    output = execFileSync(probe, ['status'], {
      encoding: 'utf8',
      windowsHide: true,
      timeout: 30000,
    });
  } catch (error) {
    output = (error.stdout ?? '').toString() + (error.stderr ?? '').toString();
  }
  const monitors = Number.parseInt(/monitors=(\d+)/.exec(output)?.[1] ?? 'NaN', 10);
  const parsec = /-\s+.*Parsec.*\battached\b/i.test(output);
  return { output, monitors, parsec };
}

function connect() {
  const child = spawn(exe, ['--parent-pid', String(process.pid)], {
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true,
  });
  const reader = readline.createInterface({ input: child.stdout });
  const pending = new Map();
  const stderr = [];
  let nextId = 1;

  reader.on('line', (line) => {
    if (!line.trim()) return;
    let message;
    try { message = JSON.parse(line); } catch { return; }
    if (message.id !== undefined && pending.has(message.id)) {
      pending.get(message.id)(message);
      pending.delete(message.id);
    }
  });
  child.stderr.on('data', (data) => stderr.push(data.toString()));

  function request(method, params = {}, timeoutMs = 120000) {
    const id = nextId++;
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`timeout ${method}`)), timeoutMs);
      pending.set(id, (response) => { clearTimeout(timer); resolve(response); });
    });
  }

  async function call(name, args = {}, timeoutMs = 120000) {
    const response = await request('tools/call', { name, arguments: args }, timeoutMs);
    if (response.error) return { rpcError: response.error };
    const text = response.result?.content?.find((item) => item.type === 'text');
    return text ? JSON.parse(text.text) : response.result;
  }

  async function close(timeoutMs = 10000) {
    try { child.stdin.end(); } catch { /* �ѹر� */ }
    if (child.exitCode !== null) return child.exitCode;
    return new Promise((resolve) => {
      const timer = setTimeout(() => resolve('timeout'), timeoutMs);
      child.on('exit', (code) => { clearTimeout(timer); resolve(code); });
    });
  }

  return { child, request, call, close, stderr };
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function waitForClean(baselineMonitors, baselineNotepad, timeoutMs = 6000) {
  const deadline = Date.now() + timeoutMs;
  let status = probeStatus();
  let newNotepad = new Set();

  while (Date.now() < deadline) {
    newNotepad = new Set([...listPids('notepad')].filter((pid) => !baselineNotepad.has(pid)));

    if (status.monitors === baselineMonitors && !status.parsec && newNotepad.size === 0) {
      return { ok: true, status, newNotepad };
    }

    await sleep(300);
    status = probeStatus();
  }

  return { ok: false, status, newNotepad };
}

function percentile(values, p) {
  if (values.length === 0) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  const index = Math.max(0, Math.min(sorted.length - 1, Math.ceil(sorted.length * p) - 1));
  return sorted[index];
}

function stats(values) {
  if (values.length === 0) return { min: 0, p50: 0, p95: 0, max: 0 };
  return {
    min: +Math.min(...values).toFixed(1),
    p50: +percentile(values, 0.5).toFixed(1),
    p95: +percentile(values, 0.95).toFixed(1),
    max: +Math.max(...values).toFixed(1),
  };
}

async function runRound(index, baselineMonitors, baselineNotepad) {
  const row = { round: index, ok: false };
  let client = null;
  let launchedPid = null;

  try {
    const startupStart = Date.now();
    client = connect();

    const init = await client.request('initialize', {
      protocolVersion: '2025-06-18',
      capabilities: {},
      clientInfo: { name: 'm4-stress', version: '1' },
    }, 30000);
    client.child.stdin.write(
      JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} }) + '\n'
    );
    row.startup_ms = Date.now() - startupStart;

    if (init.error) throw new Error('initialize: ' + JSON.stringify(init.error));

    const mountStart = Date.now();
    const env = await client.call('get_environment', {}, 120000);
    row.mount_ms = Date.now() - mountStart;

    if (env.virtual_display?.enabled !== true) {
      throw new Error('virtual display not enabled: ' + JSON.stringify(env.virtual_display));
    }

    if (env.virtual_screen?.monitors !== baselineMonitors + 1) {
      throw new Error(
        `monitor count ${env.virtual_screen?.monitors}, expected ${baselineMonitors + 1}`
      );
    }

    const launchStart = Date.now();
    const launch = await client.call('launch_or_place_app', {
      path: notepad,
      wait_ms: 20000,
      ensure_document: false,
    }, 120000);
    row.launch_ms = Date.now() - launchStart;

    if (!launch?.pid) {
      throw new Error('launch failed: ' + JSON.stringify(launch));
    }
    launchedPid = launch.pid;

    await client.call('capture_screen', { scale: 0.25 }, 60000);

    const captureStart = Date.now();
    const capture = await client.call('capture_screen', { scale: 0.25 }, 60000);
    row.capture_ms = Date.now() - captureStart;

    if (!capture?.frame_id) {
      throw new Error('capture failed: ' + JSON.stringify(capture));
    }

    const found = await client.call('find_element', {
      pid: launch.pid,
      control_type: 'Document',
    }, 60000);
    row.document_ref = found?.ref ?? null;

    const peak = await client.call('get_environment', {}, 120000);
    row.peak_memory_mb = peak.session?.working_set_mb ?? null;
    row.peak_handles = peak.session?.handles ?? null;

    const releaseStart = Date.now();
    const release = await client.call('release_environment', {
      keep_display: false,
      on_dirty: 'discard',
    }, 120000);
    row.release_ms = Date.now() - releaseStart;
    row.release_removed = release?.display_removed === true;
    row.release_gc_mb = release?.memory?.after_gc_mb ?? null;
    row.release_memory_mb = release?.memory?.after_mb ?? null;
    row.memory_mb = row.peak_memory_mb;
    row.handles = row.peak_handles;

    if (row.release_removed !== true) {
      throw new Error('release did not remove display: ' + JSON.stringify(release));
    }

    const exitStart = Date.now();
    const exitCode = await client.close(10000);
    row.exit_ms = Date.now() - exitStart;
    if (exitCode === 'timeout') {
      throw new Error('server did not exit after EOF');
    }

    const clean = await waitForClean(baselineMonitors, baselineNotepad);
    row.monitors_after = clean.status.monitors;
    row.parsec_after = clean.status.parsec;
    row.new_notepad = [...clean.newNotepad];

    if (!clean.ok) {
      throw new Error(
        `residual after release: monitors=${clean.status.monitors}/${baselineMonitors} ` +
        `parsec=${clean.status.parsec} notepad=${[...clean.newNotepad].join(',')}`
      );
    }

    // 用户可能在压测期间主动移动鼠标，严格比较基线会误报。
    // 真正要保证的是工具不把光标留在不可见的虚拟屏内。
    const cursor = cursorPosition();
    row.cursor = cursor;

    const vd = env.virtual_display;
    const cursorInsideVirtualScreen = Boolean(
      cursor && vd && vd.enabled &&
      cursor.x >= vd.x && cursor.x < vd.x + vd.width &&
      cursor.y >= vd.y && cursor.y < vd.y + vd.height
    );

    if (!cursor || cursorInsideVirtualScreen) {
      throw new Error(
        `cursor left inside virtual display: ${JSON.stringify(cursor)} vs ${JSON.stringify(vd)}`
      );
    }

    killOwnOrphanServers();
    row.ok = true;
    return row;
  } catch (error) {
    row.error = String(error?.message ?? error);

    if (client) {
      try { client.child.stdin.end(); } catch { /* ���� */ }
      await sleep(500);
      if (client.child.exitCode === null) {
        try { client.child.kill('SIGKILL'); } catch { /* ���� */ }
      }
    }

    if (launchedPid) {
      powershell(`Stop-Process -Id ${launchedPid} -Force -ErrorAction SilentlyContinue`);
    }

    const afterKill = new Set([...listPids('notepad')].filter((pid) => !baselineNotepad.has(pid)));
    if (afterKill.size > 0) {
      powershell(
        `Stop-Process -Id ${[...afterKill].join(',')} -Force -ErrorAction SilentlyContinue`
      );
    }

    const status = probeStatus();
    if (status.parsec) {
      record(`round-${index}-cleanup`, '��⵽���� Parsec �������� VddProbe removeall');
      try {
        execFileSync(probe, ['removeall'], { encoding: 'utf8', windowsHide: true, timeout: 30000 });
      } catch { /* ���� */ }
      await sleep(1000);
    }

    killOwnOrphanServers();
    return row;
  }
}

(async () => {
  const started = Date.now();
  mkdirSync(path.join(process.cwd(), 'docs'), { recursive: true });
  const progressFile = path.join(process.cwd(), 'docs', 'm4-stress-progress.log');
  writeFileSync(progressFile, '');
  const progress = (message) => appendFileSync(progressFile, `${new Date().toISOString()} ${message}\n`);

  const baselinePids = listPids('VScreenPilot');
  const baselineNotepad = listPids('notepad');
  const baseline = probeStatus();
  const baselineCursor = cursorPosition();

  record('preflight', {
    rounds,
    driver: /driver status\s*:\s*(\w+)/.exec(baseline.output)?.[1] ?? 'unknown',
    monitors: baseline.monitors,
    parsec: baseline.parsec,
    baseline_notepad: [...baselineNotepad],
    cursor: baselineCursor,
  });

  if (!Number.isFinite(baseline.monitors) || baseline.monitors < 1) {
    console.error('�޷���ȡ��ʾ����������ֹ');
    process.exit(1);
  }

  if (baseline.parsec) {
    console.error('ѹ��ǰ�Ѵ��� Parsec ���������������������');
    process.exit(1);
  }

  let consecutiveFailures = 0;

  for (let index = 1; index <= rounds; index++) {
    progress(`[START] round ${index}`);
    const row = await runRound(index, baseline.monitors, baselineNotepad);
    rows.push(row);
    consecutiveFailures = row.ok ? 0 : consecutiveFailures + 1;
    progress(`[END] round ${index} ok=${row.ok}${row.error ? ' error=' + row.error : ''}`);

    log.push(
      `[ROUND ${String(index).padStart(2, '0')}] ${row.ok ? 'PASS' : 'FAIL'} ` +
      `startup=${row.startup_ms ?? '-'}ms mount=${row.mount_ms ?? '-'}ms ` +
      `launch=${row.launch_ms ?? '-'}ms capture=${row.capture_ms ?? '-'}ms ` +
      `release=${row.release_ms ?? '-'}ms exit=${row.exit_ms ?? '-'}ms ` +
      `mem=${row.memory_mb ?? '-'}MB` +
      (row.error ? ` error=${row.error}` : '')
    );

    if (!row.ok && consecutiveFailures >= 3) {
      log.push('[ABORT] ���� 3 ��ʧ��');
      break;
    }
  }

  const passed = rows.filter((row) => row.ok).length;
  const failed = rows.filter((row) => !row.ok);
  const startup = rows.filter((r) => r.ok).map((r) => r.startup_ms);
  const mount = rows.filter((r) => r.ok).map((r) => r.mount_ms);
  const launch = rows.filter((r) => r.ok).map((r) => r.launch_ms);
  const capture = rows.filter((r) => r.ok).map((r) => r.capture_ms);
  const release = rows.filter((r) => r.ok).map((r) => r.release_ms);
  const exit = rows.filter((r) => r.ok).map((r) => r.exit_ms);
  const memory = rows.filter((r) => typeof r.memory_mb === 'number').map((r) => r.memory_mb);
  const releaseMemory = rows
    .filter((r) => typeof r.release_memory_mb === 'number')
    .map((r) => r.release_memory_mb);
  const releaseGc = rows
    .filter((r) => typeof r.release_gc_mb === 'number')
    .map((r) => r.release_gc_mb);

  const summary = {
    rounds: rows.length,
    requested: rounds,
    pass: passed,
    fail: failed.length,
    elapsed_s: +((Date.now() - started) / 1000).toFixed(1),
    startup_ms: stats(startup),
    mount_ms: stats(mount),
    launch_ms: stats(launch),
    capture_ms: stats(capture),
    release_ms: stats(release),
    exit_ms: stats(exit),
    peak_memory_mb: {
      first: memory[0] ?? null,
      last: memory.at(-1) ?? null,
      max: memory.length ? +Math.max(...memory).toFixed(1) : null,
    },
    release_memory_mb: {
      first: releaseMemory[0] ?? null,
      last: releaseMemory.at(-1) ?? null,
      max: releaseMemory.length ? +Math.max(...releaseMemory).toFixed(1) : null,
    },
    release_gc_mb: {
      first: releaseGc[0] ?? null,
      last: releaseGc.at(-1) ?? null,
      max: releaseGc.length ? +Math.max(...releaseGc).toFixed(1) : null,
    },
    failures: failed.map((row) => ({ round: row.round, error: row.error })),
  };

  log.push('\n--- SUMMARY ---');
  log.push(JSON.stringify(summary, null, 2));

  const csv = [
    'round,ok,startup_ms,mount_ms,launch_ms,capture_ms,release_ms,exit_ms,peak_memory_mb,release_gc_mb,release_memory_mb,handles,monitors_after,cursor,document_ref,error',
    ...rows.map((row) => [
      row.round,
      row.ok ? 1 : 0,
      row.startup_ms ?? '',
      row.mount_ms ?? '',
      row.launch_ms ?? '',
      row.capture_ms ?? '',
      row.release_ms ?? '',
      row.exit_ms ?? '',
      row.peak_memory_mb ?? '',
      row.release_gc_mb ?? '',
      row.release_memory_mb ?? '',
      row.handles ?? '',
      row.monitors_after ?? '',
      row.cursor ? `${row.cursor.x}x${row.cursor.y}` : '',
      row.document_ref ?? '',
      (row.error ?? '').replaceAll(',', ';'),
    ].join(',')),
  ].join('\n');

  mkdirSync(path.join(process.cwd(), 'docs'), { recursive: true });
  writeFileSync(path.join(process.cwd(), 'docs', 'm4-stress.json'), JSON.stringify({ summary, rows, log }, null, 2));
  writeFileSync(path.join(process.cwd(), 'docs', 'm4-stress.csv'), csv);
  console.log(log.join('\n'));

  const ok =
    passed === rounds &&
    rows.length === rounds &&
    summary.startup_ms.p95 <= 300 &&
    summary.capture_ms.p95 <= 50 &&
    summary.release_ms.p95 <= 3000 &&
    summary.exit_ms.p95 <= 5000 &&
    (summary.release_memory_mb.max === null || summary.release_memory_mb.max <= 40);

  process.exit(ok ? 0 : 1);
})();
