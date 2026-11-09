/** M4 启动器父进程监听：只杀 node 启动器，服务端应自行退出并清理虚拟屏。 */
import { spawn, execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import readline from 'node:readline';

const launcher = process.argv[2] ?? '.\\npm\\bin\\vscreenpilot.js';
const probe = process.argv[3] ?? '.\\tools\\VddProbe\\bin\\Release\\net10.0-windows\\VddProbe.exe';

const log = [];
const results = [];
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

function probeStatus() {
  let output = '';
  try {
    output = execFileSync(probe, ['status'], { encoding: 'utf8', windowsHide: true, timeout: 30000 });
  } catch (error) {
    output = (error.stdout ?? '').toString() + (error.stderr ?? '').toString();
  }
  return {
    monitors: Number.parseInt(/monitors=(\d+)/.exec(output)?.[1] ?? 'NaN', 10),
    parsec: /-\s+.*Parsec.*\battached\b/i.test(output),
  };
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

(async () => {
  const baseline = probeStatus();
  log.push(JSON.stringify({ baseline }));

  const wrapper = spawn(process.execPath, [launcher], {
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true,
  });
  const reader = readline.createInterface({ input: wrapper.stdout });
  const pending = new Map();
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
  wrapper.stderr.on('data', () => {});

  function request(method, params = {}, timeoutMs = 120000) {
    const id = nextId++;
    wrapper.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
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

  try {
    await request('initialize', {
      protocolVersion: '2025-06-18',
      capabilities: {},
      clientInfo: { name: 'm4-parent-watch', version: '1' },
    }, 30000);
    wrapper.stdin.write(
      JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} }) + '\n'
    );

    const env = await call('get_environment');
    check('mounted before kill', env.virtual_display?.enabled === true,
      `monitors=${env.virtual_screen?.monitors}`);

    const launch = await call('launch_or_place_app', {
      path: 'C:\\Windows\\System32\\notepad.exe',
      wait_ms: 20000,
      ensure_document: false,
    });
    check('app launched before kill', Boolean(launch?.pid), String(launch?.pid));

    const nodePid = wrapper.pid;
    wrapper.kill('SIGKILL');
    log.push(`[KILL] wrapper pid=${nodePid}`);
    await sleep(5000);

    const servers = powershell(
      "(Get-Process -Name 'VScreenPilot' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }) -join ','"
    );
    const notepads = powershell(
      "(Get-Process -Name 'notepad' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }) -join ','"
    );
    const after = probeStatus();

    log.push(JSON.stringify({ servers, notepads, after }));
    check('server exited with wrapper', servers.trim() === '', servers);
    check('app cleaned up', notepads.trim() === '', notepads);
    check('display restored', after.monitors === baseline.monitors, `${after.monitors}/${baseline.monitors}`);
    check('no attached parsec display', !after.parsec, String(after.parsec));
  } catch (error) {
    log.push('[FATAL] ' + (error?.stack ?? error));
    results.push({ name: 'fatal', ok: false, detail: String(error?.message ?? error) });
  } finally {
    try { wrapper.kill('SIGKILL'); } catch { /* 忽略 */ }
    const pass = results.filter((item) => item.ok).length;
    log.push(`\nSUMMARY pass=${pass}/${results.length}`);
    mkdirSync('docs', { recursive: true });
    writeFileSync(path.join('docs', 'm4-parent-watch.log'), log.join('\n'));
    console.log(log.join('\n'));
    process.exit(pass === results.length ? 0 : 1);
  }
})();
