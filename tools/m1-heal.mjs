import { spawn, execFileSync } from 'node:child_process';
import readline from 'node:readline';

const exe = process.argv[2];
const probe = process.argv[3] ?? '.\\tools\\VddProbe\\bin\\Release\\net10.0-windows\\VddProbe.exe';

if (!exe) {
  console.error('usage: node m1-heal.mjs <VScreenPilot.exe> [VddProbe.exe]');
  process.exit(1);
}

const child = spawn(exe, ['--allow-script'], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
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

child.stderr.on('data', (data) => process.stderr.write('[stderr] ' + data.toString()));
child.on('exit', (code) => console.log('[exit]', code));

function request(method, params = {}) {
  const id = nextId++;
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`timeout: ${method}`)), 120000);
    pending.set(id, (response) => { clearTimeout(timer); resolve(response); });
  });
}

async function call(name, args = {}) {
  const response = await request('tools/call', { name, arguments: args });
  if (response.error) return { rpcError: response.error };
  const result = response.result;
  const text = result?.content?.find((item) => item.type === 'text');
  return text ? JSON.parse(text.text) : result;
}

(async () => {
  await request('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'heal', version: '0.0.1' },
  });

  console.log('env-before:', JSON.stringify(await call('get_environment')));
  console.log('launch:', JSON.stringify(await call('launch_or_place_app', {
    path: 'C:\\Windows\\System32\\notepad.exe',
    wait_ms: 20000,
  })));

  console.log('--- removing display behind the server ---');
  try {
    const output = execFileSync(probe, ['remove', '--index', '0'], { encoding: 'utf8' });
    console.log(output.trim());
  } catch (error) {
    console.log('probe-remove-failed:', error.message);
  }

  await new Promise((resolve) => setTimeout(resolve, 1500));

  const envDown = await call('get_environment');
  console.log('display-after-remove:', envDown.virtual_display?.enabled);

  const capture = await call('capture_screen', { scale: 0.25 });
  console.log('capture-after-remove:', JSON.stringify({
    isError: capture.isError,
    code: capture.code,
    message: capture.message,
    width: capture.width,
    height: capture.height,
  }));

  const envHealed = await call('get_environment');
  console.log('display-after-recover:', envHealed.virtual_display?.enabled,
    'self_healed:', envHealed.virtual_display?.self_healed);

  console.log('release:', JSON.stringify(await call('release_environment', {})));
  child.stdin.end();
})().catch((error) => {
  console.error(error);
  try { child.kill(); } catch { /* 已退出 */ }
  process.exit(1);
});
