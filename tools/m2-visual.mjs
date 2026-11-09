import { spawn } from 'node:child_process';
import readline from 'node:readline';
import { writeFileSync } from 'node:fs';

const exe = process.argv[2];
const appPath = process.argv[3] ?? 'C:\\Windows\\System32\\notepad.exe';

if (!exe) {
  console.error('usage: node m2-visual.mjs <VScreenPilot.exe> [appPath]');
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

async function callRaw(name, args = {}) {
  const response = await request('tools/call', { name, arguments: args });
  return response.result;
}

function payload(response) {
  return response?.content?.[0];
}

function imageData(result) {
  return result?.content?.find((item) => item.type === 'image')?.data ?? null;
}

function show(label, value) {
  console.log(`--- ${label} ---`);
  console.log(JSON.stringify(value, null, 2));
}

(async () => {
  await request('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'm2-visual', version: '0.0.1' },
  });
  notify('notifications/initialized');

  show('launch', await call('launch_or_place_app', { path: appPath, wait_ms: 20000 }));
  await new Promise((resolve) => setTimeout(resolve, 2500));

  const env = await call('get_environment');
  const display = payload(env)?.virtual_display;
  console.log('--- display ---');
  console.log(JSON.stringify(display));

  const dx = display?.x ?? 3072;
  const dy = display?.y ?? 0;
  const dw = display?.width ?? 1280;
  const dh = display?.height ?? 720;

  const full = await call('capture_screen', { scale: 1, save_path: 'artifacts/m2-capture.png' });
  show('capture', payload(full));

  const templateRegion = {
    x: dx + 8,
    y: dy + 32,
    width: 180,
    height: 32,
  };

  const templateResult = await callRaw('capture_screen', {
    region: templateRegion,
    scale: 1,
    save_path: 'artifacts/m2-template.png',
  });
  const templateBase64 = imageData(templateResult);
  console.log('--- template-meta ---');
  console.log(JSON.stringify({
    region: templateRegion,
    bytes: templateBase64 ? Math.floor(templateBase64.length * 3 / 4) : 0,
  }));

  show('template-match', await call('match_visual', {
    mode: 'template',
    target: templateBase64,
    region: { x: dx, y: dy, width: dw, height: dh },
    threshold: 0.9,
    max_results: 5,
  }));

  const firstMatch = payload(await call('match_visual', {
    mode: 'template',
    target: templateBase64,
    region: { x: dx, y: dy, width: dw, height: dh },
    threshold: 0.9,
    max_results: 1,
  }))?.matches?.[0];
  console.log('--- template-delta ---');
  console.log(JSON.stringify({
    expected: templateRegion,
    found: firstMatch,
    dx: firstMatch ? firstMatch.x - templateRegion.x : null,
    dy: firstMatch ? firstMatch.y - templateRegion.y : null,
  }));

  show('template-match-narrow', await call('match_visual', {
    mode: 'template',
    target: templateBase64,
    region: {
      x: dx + Math.round(dw / 2),
      y: dy,
      width: Math.round(dw / 2),
      height: dh,
    },
    threshold: 0.95,
    max_results: 5,
  }));

  const pixel = await call('match_visual', {
    mode: 'pixel',
    target: { x: dx + Math.round(dw / 2), y: dy + Math.round(dh / 2) },
  });
  show('pixel', payload(pixel));

  const color = payload(pixel)?.color;
  if (color) {
    show('color-match', await call('match_visual', {
      mode: 'color',
      target: color,
      region: { x: dx, y: dy, width: dw, height: dh },
      tolerance: 0,
      min_pixels: 50,
      max_results: 5,
    }));
  }

  show('bad-base64', await call('match_visual', {
    mode: 'template',
    target: 'data:image/png;base64,AAAA',
  }));

  show('pixel-outside', await call('match_visual', {
    mode: 'pixel',
    target: { x: 100, y: 100 },
  }));

  show('template-too-large', await call('match_visual', {
    mode: 'template',
    target: templateBase64,
    region: { x: dx, y: dy, width: 100, height: 20 },
  }));

  show('out-of-bounds', await call('match_visual', {
    mode: 'color',
    target: '#FFFFFF',
    region: { x: 0, y: 0, width: 100, height: 100 },
  }));

  show('bad-mode', await call('match_visual', { mode: 'nope', target: '#FFFFFF' }));

  show('release', await call('release_environment'));

  child.stdin.end();
  setTimeout(() => {
    try { child.kill(); } catch { /* 已退�?*/ }
  }, 3000);
})().catch((error) => {
  console.error(error);
  try { child.kill(); } catch { /* 已退�?*/ }
  process.exit(1);
});
