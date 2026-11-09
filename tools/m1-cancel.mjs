import { spawn } from 'node:child_process';
import readline from 'node:readline';

const exe = process.argv[2];
if (!exe) {
  console.error('usage: node m1-cancel.mjs <VScreenPilot.exe>');
  process.exit(1);
}

const child = spawn(exe, ['--allow-script'], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
const reader = readline.createInterface({ input: child.stdout });
const pending = new Map();
const seen = new Map();
let nextId = 1;

reader.on('line', (line) => {
  if (!line.trim()) return;
  let message;
  try { message = JSON.parse(line); } catch { return; }
  if (message.id !== undefined) seen.set(message.id, message);
  if (message.id !== undefined && pending.has(message.id)) {
    pending.get(message.id)(message);
    pending.delete(message.id);
  }
});

child.stderr.on('data', (data) => process.stderr.write('[stderr] ' + data.toString()));
child.on('exit', (code) => console.log('[exit]', code));

function send(method, params = {}, id = nextId++) {
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  return id;
}

function notify(method, params = {}) {
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');
}

function waitFor(id, timeoutMs) {
  return new Promise((resolve, reject) => {
    if (seen.has(id)) { resolve(seen.get(id)); return; }
    const timer = setTimeout(() => reject(new Error(`timeout waiting for ${id}`)), timeoutMs);
    pending.set(id, (response) => { clearTimeout(timer); resolve(response); });
  });
}

(async () => {
  const initId = send('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'cancel', version: '0.0.1' },
  });
  await waitFor(initId, 30000);
  notify('notifications/initialized');

  // 1) 启动一个 30 秒的 wait_for（没有目标窗口，会一直轮询）。
  const waitId = send('tools/call', {
    name: 'wait_for',
    arguments: {
      condition: { type: 'element', control_type: 'Window' },
      timeout_ms: 30000,
    },
  });

  await new Promise((resolve) => setTimeout(resolve, 500));

  // 2) 工具执行期间，ping 必须立即得到响应。
  const pingId = send('ping', {});
  const ping = await waitFor(pingId, 3000);
  console.log('ping-during-tool:', JSON.stringify(ping));

  // 3) 取消长请求。
  notify('notifications/cancelled', { requestId: waitId, reason: 'test' });
  await new Promise((resolve) => setTimeout(resolve, 500));

  // 4) 服务器仍可正常工作，且被取消的请求不应返回结果。
  const envId = send('tools/call', { name: 'get_environment', arguments: {} });
  const env = await waitFor(envId, 15000);
  console.log('after-cancel-ok:', !!env.result && !env.result.isError);
  console.log('cancelled-request-responded:', seen.has(waitId));
  console.log('cancelled-request-type:', JSON.stringify(seen.get(waitId) ?? null));
  console.log('server-still-alive:', !child.killed && child.exitCode === null);

  const releaseId = send('tools/call', { name: 'release_environment', arguments: {} });
  const release = await waitFor(releaseId, 30000);
  console.log('release-ok:', !!release.result && !release.result.isError);

  child.stdin.end();
})().catch((error) => {
  console.error(error);
  try { child.kill(); } catch { /* 已退出 */ }
  process.exit(1);
});
