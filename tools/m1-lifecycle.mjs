import { spawn, execFileSync } from 'node:child_process';
import readline from 'node:readline';

const exe = process.argv[2];
const mode = process.argv[3] ?? 'eof';
const probe = process.argv[4] ?? '.\\tools\\VddProbe\\bin\\Release\\net10.0-windows\\VddProbe.exe';

if (!exe) {
  console.error('usage: node m1-lifecycle.mjs <VScreenPilot.exe> [eof|kill|mutex] [VddProbe.exe]');
  process.exit(1);
}

function connect(label) {
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

  const stderr = [];
  child.stderr.on('data', (data) => stderr.push(data.toString()));
  child.on('exit', (code) => {
    if (label) console.log(`[${label}] exit=${code}`);
  });

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
    if (response.error) return { error: response.error };
    const result = response.result;
    const text = result?.content?.find((item) => item.type === 'text');
    return text ? JSON.parse(text.text) : result;
  }

  return { child, request, call, stderr, label };
}

function powershell(script) {
  return execFileSync('powershell.exe', ['-NoProfile', '-Command', script], {
    encoding: 'utf8',
    windowsHide: true,
  });
}

function checkState(label) {
  console.log(`--- ${label} ---`);
  console.log(powershell(`& '${probe}' status | Select-String 'DISPLAY1|Parsec|virtual screen' | ForEach-Object { $_.Line }`));
  console.log(powershell(`$p = Get-Process Notepad -ErrorAction SilentlyContinue; if ($p) { "notepad: " + ($p.Id -join ',') } else { 'notepad: none' }`));
}

async function main() {
  if (mode === 'mutex') {
    const first = connect('first');
    await first.request('initialize', {
      protocolVersion: '2025-06-18',
      capabilities: {},
      clientInfo: { name: 'lifecycle', version: '0.0.1' },
    });

    const second = connect('second');
    const code = await new Promise((resolve) => {
      second.child.on('exit', resolve);
      setTimeout(() => resolve('timeout'), 5000);
    });
    console.log('second exit:', code);
    console.log('second stderr:', second.stderr.join('').trim());

    first.child.stdin.end();
    await new Promise((resolve) => first.child.on('exit', resolve));
    return;
  }

  const client = connect(mode);
  await client.request('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'lifecycle', version: '0.0.1' },
  });

  console.log('launch:', JSON.stringify(await client.call('launch_or_place_app', {
    path: 'C:\\Windows\\System32\\notepad.exe',
    wait_ms: 20000,
  })));

  console.log('capture:', JSON.stringify(await client.call('capture_screen', { scale: 0.25 })));

  if (mode === 'eof') {
    console.log('closing stdin (EOF)...');
    client.child.stdin.end();
    const code = await new Promise((resolve) => client.child.on('exit', resolve));
    console.log('server exit:', code);
  } else {
    console.log('terminating server process', client.child.pid);
    process.kill(client.child.pid, 'SIGKILL');
  }

  await new Promise((resolve) => setTimeout(resolve, 3000));
  checkState(`after ${mode}`);
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
