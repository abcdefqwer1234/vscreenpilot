import { spawn } from 'node:child_process';
import readline from 'node:readline';

const exe = process.argv[2];
if (!exe) {
  console.error('usage: node m1-errors.mjs <VScreenPilot.exe>');
  process.exit(1);
}

const child = spawn(exe, ['--allow-script'], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
const reader = readline.createInterface({ input: child.stdout });
const pending = new Map();
let nextId = 1;

reader.on('line', (line) => {
  if (!line.trim()) return;
  let message;
  try {
    message = JSON.parse(line);
  } catch {
    return;
  }
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
    const timer = setTimeout(() => reject(new Error(`timeout: ${method}`)), 60000);
    pending.set(id, (response) => {
      clearTimeout(timer);
      resolve(response);
    });
  });
}

async function call(name, args = {}) {
  const response = await request('tools/call', { name, arguments: args });
  if (response.error) return { rpcError: response.error };
  const result = response.result;
  return {
    isError: result.isError,
    text: result.content?.find((item) => item.type === 'text')?.text,
  };
}

function report(label, value) {
  const parsed = value.text ? (() => { try { return JSON.parse(value.text); } catch { return value.text; } })() : undefined;
  console.log(`--- ${label} ---`);
  console.log(JSON.stringify(value.rpcError ?? { isError: value.isError, payload: parsed }, null, 2));
}

(async () => {
  await request('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'errors', version: '0.0.1' },
  });

  report('find-no-target', await call('find_element', { control_type: 'Window' }));
  report('type-no-target', await call('type_text', { text: 'x' }));
  report('click-missing', await call('click', {}));
  report('click-outside', await call('click', { x: 100, y: 100 }));
  report('capture-outside', await call('capture_screen', {
    region: { x: 0, y: 0, width: 100, height: 100 },
  }));
  report('assert-outside', await call('assert_region', {
    region: { x: 0, y: 0, width: 100, height: 100 },
    expectation: { changed: false },
  }));
  report('capture-bad-region', await call('capture_screen', {
    region: { x: 3072, y: 0 },
  }));
  report('click-foreign', await call('click', { x: 3072 + 100, y: 100 }));
  report('gesture-bad-type', await call('gesture', { type: 'nope' }));
  report('click-bad-mode', await call('click', { x: 3072 + 100, y: 100, mode: 'nope' }));
  report('focused-scope', await call('wait_for', {
    condition: { type: 'window', title: 'Program Manager' },
    timeout_ms: 300,
  }));
  report('script-timeout', await call('run_script', { code: 'Start-Sleep -Seconds 5', timeout_ms: 1000 }));
  report('wait-timeout', await call('wait_for', {
    condition: { type: 'element', control_type: 'Window' },
    timeout_ms: 500,
  }));
  report('unknown-tool', await call('nope', {}));
  report('still-alive', await call('get_environment', {}));
  report('release', await call('release_environment', {}));

  child.stdin.end();
  setTimeout(() => {
    try { child.kill(); } catch { /* 已退出 */ }
  }, 2000);
})().catch((error) => {
  console.error(error);
  try { child.kill(); } catch { /* 已退出 */ }
  process.exit(1);
});
