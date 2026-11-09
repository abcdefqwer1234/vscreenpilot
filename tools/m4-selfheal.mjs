/**
 * M4 自愈验证：强杀正在持有虚拟屏的 server，立即重启，验证状态恢复。
 *
 * 通过条件（二选一）：
 * - 驱动尚未回收：按 state.json 的 displayIndex 接管（adopted_orphan=true）；
 * - 驱动已自动回收：新 server 正常重建虚拟屏。
 * 两种情况都必须无 notepad / VScreenPilot 残留，最终 monitors 回到基线。
 */
import { spawn, execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import readline from 'node:readline';

const exe = process.argv[2];
const rounds = Number.parseInt(process.argv[3] ?? '3', 10);
const probe = process.argv[4] ?? '.\\tools\\VddProbe\\bin\\Release\\net10.0-windows\\VddProbe.exe';
const notepad = 'C:\\Windows\\System32\\notepad.exe';

if (!exe) {
  console.error('usage: node m4-selfheal.mjs <VScreenPilot.exe> [rounds] [VddProbe.exe]');
  process.exit(1);
}

const log = [];
const results = [];
const record = (label, value) => {
  log.push(`--- ${label} ---`);
  log.push(typeof value === 'string' ? value : JSON.stringify(value, null, 2));
};
const check = (name, ok, detail) => {
  results.push({ name, ok, detail });
  log.push(`[TEST] ${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' :: ' + detail : ''}`);
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
  return {
    monitors: Number.parseInt(/monitors=(\d+)/.exec(output)?.[1] ?? 'NaN', 10),
    parsec: /-\s+.*Parsec.*\battached\b/i.test(output),
  };
}

function connect() {
  const child = spawn(exe, ['--allow-script'], {
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
    try { child.stdin.end(); } catch { /* 忽略 */ }
    if (child.exitCode !== null) return child.exitCode;
    return new Promise((resolve) => {
      const timer = setTimeout(() => resolve('timeout'), timeoutMs);
      child.on('exit', (code) => { clearTimeout(timer); resolve(code); });
    });
  }

  return { child, request, call, close, stderr };
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function initialize(client) {
  const init = await client.request('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'm4-selfheal', version: '1' },
  }, 30000);
  client.child.stdin.write(
    JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} }) + '\n'
  );
  if (init.error) throw new Error('initialize: ' + JSON.stringify(init.error));
}

async function waitForNoNewNotepad(baseline, timeoutMs = 5000) {
  const deadline = Date.now() + timeoutMs;
  let remaining = new Set();

  while (Date.now() < deadline) {
    remaining = new Set([...listPids('notepad')].filter((pid) => !baseline.has(pid)));
    if (remaining.size === 0) return remaining;
    await sleep(300);
  }

  return remaining;
}

(async () => {
  const baselineMonitors = probeStatus().monitors;
  const baselineNotepad = listPids('notepad');
  const baselinePids = listPids('VScreenPilot');
  record('baseline', {
    monitors: baselineMonitors,
    notepad: [...baselineNotepad],
    vscreenpilot: [...baselinePids],
  });

  try {
    for (let round = 1; round <= rounds; round++) {
      log.push(`\n===== round ${round}/${rounds} =====`);
      const first = connect();
      await initialize(first);
      const env1 = await first.call('get_environment');
      check(`r${round} first mount`, env1.virtual_display?.enabled === true,
        `monitors=${env1.virtual_screen?.monitors}`);

      const launch = await first.call('launch_or_place_app', {
        path: notepad,
        wait_ms: 20000,
        ensure_document: false,
      });
      record(`r${round} launch`, { pid: launch?.pid, title: launch?.title });
      check(`r${round} app launched`, Boolean(launch?.pid), String(launch?.pid));

      const capture = await first.call('capture_screen', { scale: 0.25 });
      check(`r${round} capture`, Boolean(capture?.frame_id), capture?.frame_id);

      const killedPid = first.child.pid;
      first.child.kill('SIGKILL');
      if (first.child.exitCode === null) {
        await new Promise((resolve) => {
          const timer = setTimeout(resolve, 5000);
          first.child.on('exit', () => { clearTimeout(timer); resolve(); });
        });
      }
      record(`r${round} killed`, { pid: killedPid });

      const second = connect();
      await initialize(second);
      const env2 = await second.call('get_environment');
      record(`r${round} restarted-env`, {
        enabled: env2.virtual_display?.enabled,
        owned: env2.virtual_display?.owned,
        adopted_orphan: env2.virtual_display?.adopted_orphan,
        self_heal_count: env2.virtual_display?.self_heal_count,
        last_reason: env2.virtual_display?.last_self_heal_reason,
        monitors: env2.virtual_screen?.monitors,
        error: env2.code,
      });

      check(`r${round} second mount`, env2.virtual_display?.enabled === true,
        `monitors=${env2.virtual_screen?.monitors} code=${env2.code ?? ''}`);

      const orphanHandled =
        env2.virtual_display?.adopted_orphan === true ||
        env2.virtual_display?.last_self_heal_reason === 'orphan_reclaimed' ||
        env2.virtual_display?.last_self_heal_reason === 'display_reclaimed' ||
        env2.virtual_display?.self_heal_count > 0 ||
        env2.virtual_display?.enabled === true;
      check(`r${round} orphan handled`, orphanHandled,
        `adopted=${env2.virtual_display?.adopted_orphan} reason=${env2.virtual_display?.last_self_heal_reason}`);

      const remainingNotepad = await waitForNoNewNotepad(baselineNotepad);
      check(`r${round} no leftover app`, remainingNotepad.size === 0, [...remainingNotepad].join(','));

      const release = await second.call('release_environment', {
        keep_display: false,
        on_dirty: 'force',
      });
      check(`r${round} release`, release.display_removed === true,
        `removed=${release.display_removed}`);

      const exit = await second.close(10000);
      check(`r${round} second eof`, exit === 0, `exit=${exit}`);

      await sleep(500);
      const status = probeStatus();
      check(`r${round} monitors restored`, status.monitors === baselineMonitors,
        `${status.monitors}/${baselineMonitors}`);
      check(`r${round} no parsec display`, !status.parsec, String(status.parsec));
    }
  } catch (error) {
    log.push('[FATAL] ' + (error?.stack ?? error));
    results.push({ name: 'fatal', ok: false, detail: String(error?.message ?? error) });
  } finally {
    const pass = results.filter((item) => item.ok).length;
    log.push(`\nSUMMARY pass=${pass}/${results.length}`);
    mkdirSync('docs', { recursive: true });
    writeFileSync('docs/m4-selfheal.log', log.join('\n'));
    console.log(log.join('\n'));
    process.exit(pass === results.length ? 0 : 1);
  }
})();
