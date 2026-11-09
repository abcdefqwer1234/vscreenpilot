/** M4 日志验证：工具耗时打点 + 重启后 source=file 仍可取证。 */
import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import readline from 'node:readline';

const exe = process.argv[2];
if (!exe) {
  console.error('usage: node m4-log-verify.mjs <VScreenPilot.exe>');
  process.exit(1);
}

const log = [];
const results = [];
const record = (label, value) => {
  log.push(`--- ${label} ---`);
  log.push(JSON.stringify(value, null, 2));
};
const check = (name, ok, detail) => {
  results.push({ name, ok, detail });
  log.push(`[TEST] ${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' :: ' + detail : ''}`);
};

function connect() {
  const child = spawn(exe, ['--parent-pid', String(process.pid)], {
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true,
  });
  const reader = readline.createInterface({ input: child.stdout });
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
  child.stderr.on('data', () => {});

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

  return { child, request, call, close };
}

async function initialize(client) {
  await client.request('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'm4-log', version: '1' },
  }, 30000);
  client.child.stdin.write(
    JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} }) + '\n'
  );
}

(async () => {
  try {
    const first = connect();
    await initialize(first);
    await first.call('get_environment');

    const memory = await first.call('get_log', { tail: 30, source: 'memory' });
    record('memory-log', memory);
    check('tool timing logged',
      Array.isArray(memory.entries) &&
      memory.entries.some((entry) => entry.message?.includes('工具 get_environment 完成')),
      `${memory.entries?.length} entries`);

    const file = await first.call('get_log', { tail: 30, source: 'file' });
    record('file-log', file);
    check('file source returns entries', Array.isArray(file.entries) && file.entries.length > 0,
      `${file.entries?.length} entries`);

    const release = await first.call('release_environment', { keep_display: false, on_dirty: 'force' });
    check('first release', release.display_removed === true, String(release.display_removed));
    check('first eof', (await first.close()) === 0, 'exit');

    const second = connect();
    await initialize(second);
    const afterRestart = await second.call('get_log', { tail: 50, source: 'file' });
    record('file-log-after-restart', {
      entries: afterRestart.entries?.length,
      file: afterRestart.file,
      last_error: afterRestart.last_error,
    });
    check('file log survives restart',
      Array.isArray(afterRestart.entries) && afterRestart.entries.length > 0,
      `${afterRestart.entries?.length} entries`);

    const both = await second.call('get_log', { tail: 10, source: 'both' });
    check('both returns memory + file_entries',
      Array.isArray(both.entries) && Array.isArray(both.file_entries),
      `memory=${both.entries?.length} file=${both.file_entries?.length}`);

    const release2 = await second.call('release_environment', { keep_display: false, on_dirty: 'force' });
    check('second release', release2.display_removed === true, String(release2.display_removed));
    check('second eof', (await second.close()) === 0, 'exit');
  } catch (error) {
    log.push('[FATAL] ' + (error?.stack ?? error));
    results.push({ name: 'fatal', ok: false, detail: String(error?.message ?? error) });
  } finally {
    const pass = results.filter((item) => item.ok).length;
    log.push(`\nSUMMARY pass=${pass}/${results.length}`);
    mkdirSync('docs', { recursive: true });
    writeFileSync('docs/m4-log.log', log.join('\n'));
    console.log(log.join('\n'));
    process.exit(pass === results.length ? 0 : 1);
  }
})();
