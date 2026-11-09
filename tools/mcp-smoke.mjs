import { spawn } from 'node:child_process';
import readline from 'node:readline';

const exe = process.argv[2];
const scenario = process.argv[3] ?? 'basic';
const appPath = process.argv[4] ?? 'C:\\Windows\\System32\\notepad.exe';

if (!exe) {
  console.error('usage: node mcp-smoke.mjs <VScreenPilot.exe> [basic|full]');
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
    console.error('[bad-json]', line.slice(0, 300));
    return;
  }

  if (message.id !== undefined && pending.has(message.id)) {
    pending.get(message.id)(message);
    pending.delete(message.id);
  } else {
    console.log('[server]', JSON.stringify(message).slice(0, 500));
  }
});

child.stderr.on('data', (data) => process.stderr.write('[stderr] ' + data.toString()));
child.on('exit', (code) => console.log('[exit]', code));

function request(method, params = {}) {
  const id = nextId++;
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`timeout: ${method}`)), 120000);
    pending.set(id, (response) => {
      clearTimeout(timer);
      resolve(response);
    });
  });
}

function notify(method, params = {}) {
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');
}

function summarize(response) {
  if (response.error) return response;
  const result = response.result;
  if (!result || !Array.isArray(result.content)) return result;

  return {
    isError: result.isError,
    content: result.content.map((item) => {
      if (item.type === 'image') {
        return { type: 'image', mimeType: item.mimeType, bytes: Math.floor(item.data.length * 3 / 4) };
      }
      try {
        return JSON.parse(item.text);
      } catch {
        return item.text;
      }
    }),
  };
}

async function call(name, args = {}) {
  return summarize(await request('tools/call', { name, arguments: args }));
}

function show(label, value) {
  console.log(`--- ${label} ---`);
  console.log(JSON.stringify(value, null, 2));
}

(async () => {
  show('initialize', await request('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'smoke', version: '0.0.1' },
  }));
  notify('notifications/initialized');

  const tools = await request('tools/list');
  show('tools', tools.result?.tools?.map((tool) => tool.name));

  show('env-before', await call('get_environment'));

  if (scenario === 'full') {
    const envBefore = await call('get_environment');
    const cursorBefore = envBefore.content?.[0]?.cursor;
    show('launch', await call('launch_or_place_app', {
      path: appPath,
      wait_ms: 20000,
    }));

    const envLaunch = await call('get_environment');
    await new Promise((resolve) => setTimeout(resolve, 3000));
    const envLaunchAfter = await call('get_environment');
    console.log('--- launch-foreground ---');
    console.log(JSON.stringify(envLaunchAfter.content?.[0]?.foreground));

    show('capture', await call('capture_screen', {
      scale: 1,
      save_path: 'artifacts/m1_6-capture.png',
    }));

    show('assert-changed', await call('assert_region', {
      region: { x: 3072, y: 0, width: 1280, height: 720 },
      expectation: { changed: false, threshold: 0.05 },
    }));

    show('assert-color', await call('assert_region', {
      region: { x: 3072 + 640, y: 360, width: 10, height: 10 },
      expectation: { color: '#FFFFFF', tolerance: 30, coverage: 0.8 },
    }));

    show('find-window', await call('find_element', { control_type: 'Window' }));
    show('find-doc', await call('find_element', { control_type: 'Document' }));

    show('set-cursor', await call('run_script', {
      code: 'Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point(100,100)',
      timeout_ms: 10000,
    }));

    const envBeforeClick = await call('get_environment');
    const cursorBeforeClick = envBeforeClick.content?.[0]?.cursor;

    show('click', await call('click', { x: 3072 + 400, y: 300 }));

    const envClick = await call('get_environment');
    const cursorClick = envClick.content?.[0]?.cursor;
    console.log('--- cursor-after-click ---');
    console.log(JSON.stringify({
      before: cursorBeforeClick,
      after: cursorClick,
      unchanged: cursorBeforeClick && cursorClick &&
        cursorBeforeClick.x === cursorClick.x && cursorBeforeClick.y === cursorClick.y,
      foreground: envClick.content?.[0]?.foreground,
    }, null, 2));

    show('type', await call('type_text', { text: 'Hello 虚拟屏 123' }));
    const envType = await call('get_environment');
    console.log('--- type-foreground ---');
    console.log(JSON.stringify(envType.content?.[0]?.foreground));

    show('keys', await call('press_keys', { keys: 'Ctrl+A' }));
    const envKeys = await call('get_environment');
    console.log('--- keys-foreground ---');
    console.log(JSON.stringify(envKeys.content?.[0]?.foreground));

    show('gesture', await call('gesture', {
      type: 'scroll',
      from: { x: 3072 + 640, y: 360 },
      dy: -2,
    }));

    const envGesture = await call('get_environment');
    const cursorGesture = envGesture.content?.[0]?.cursor;
    console.log('--- cursor-after-gesture ---');
    console.log(JSON.stringify({
      before: cursorBeforeClick,
      after: cursorGesture,
      unchanged: cursorBeforeClick && cursorGesture &&
        cursorBeforeClick.x === cursorGesture.x && cursorBeforeClick.y === cursorGesture.y,
      foreground: envGesture.content?.[0]?.foreground,
    }, null, 2));

    show('script', await call('run_script', {
      code: 'Write-Output "hello"; [Console]::Error.WriteLine("err"); exit 7',
      timeout_ms: 10000,
    }));

    show('wait', await call('wait_for', {
      condition: { type: 'element', control_type: 'Window' },
      timeout_ms: 5000,
    }));

    show('env-after', await call('get_environment'));
    show('release', await call('release_environment'));
  }

  show('log', await call('get_log', { tail: 30 }));

  child.stdin.end();
  setTimeout(() => {
    try {
      child.kill();
    } catch {
      // 已退出。
    }
  }, 3000);
})().catch((error) => {
  console.error(error);
  try {
    child.kill();
  } catch {
    // 已退出。
  }
  process.exit(1);
});
