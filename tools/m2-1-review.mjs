import { spawn } from 'node:child_process';
import readline from 'node:readline';

const exe = process.argv[2];
const notepad = process.argv[3] ?? 'C:\\Windows\\System32\\notepad.exe';
const edge = process.argv[4] ?? 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';

if (!exe) {
  console.error('usage: node m2-1-review.mjs <VScreenPilot.exe> [notepad] [edge]');
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

function payload(response) {
  return response?.content?.[0];
}

function show(label, value) {
  console.log(`--- ${label} ---`);
  console.log(JSON.stringify(value, null, 2));
}

function walk(node, visit) {
  if (!node) return;
  visit(node);
  for (const item of node.children ?? []) walk(item, visit);
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

(async () => {
  await request('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'm2-1-review', version: '0.0.1' },
  });
  notify('notifications/initialized');

  const np = payload(await call('launch_or_place_app', { path: notepad, wait_ms: 20000 }));
  show('launch-notepad', np);

  const ed = payload(await call('launch_or_place_app', { path: edge, wait_ms: 20000 }));
  show('launch-edge', ed);

  await sleep(1500);

  const allDoc = payload(await call('find_element', {
    control_type: 'Document',
    all: true,
    max_results: 10,
  }));
  show('document-default-last', {
    count: allDoc?.count,
    pids: (allDoc?.elements ?? []).map((item) => item.process_id),
  });

  const npDoc = payload(await call('find_element', {
    pid: np?.pid,
    control_type: 'Document',
  }));
  show('document-by-pid', {
    isError: npDoc?.code ? true : false,
    pid: npDoc?.process_id,
    expected: np?.pid,
    ref: npDoc?.ref,
  });

  const npTree = payload(await call('get_ui_tree', {
    pid: np?.pid,
    max_depth: 6,
    interactive_only: true,
    max_nodes: 200,
  }));
  let runtimeIds = 0;
  let menuRef = null;
  walk(npTree?.tree, (node) => {
    if ('runtime_id' in node) runtimeIds++;
    if (!menuRef && node.control_type === 'MenuItem') menuRef = node.ref;
  });
  show('tree-by-pid', {
    isError: npTree?.code ? true : false,
    windowPid: npTree?.window?.pid,
    expected: np?.pid,
    nodes: npTree?.nodes,
    pruned: npTree?.pruned,
    maxDepthReached: npTree?.max_depth_reached,
    runtimeIdFields: runtimeIds,
    menuRef,
  });

  const smallTree = payload(await call('get_ui_tree', { pid: np?.pid, max_nodes: 5 }));
  show('tree-max-nodes', {
    nodes: smallTree?.nodes,
    truncated: smallTree?.truncated,
    reason: smallTree?.truncated_reason,
  });

  if (menuRef) {
    show('invoke-pattern-invoke', await call('invoke_element', {
      ref: menuRef,
      pattern: 'invoke',
    }));
    await sleep(600);
    show('invoke-pattern-expand-document', await call('invoke_element', {
      ref: npDoc?.ref,
      pattern: 'expand',
    }));
  }

  show('wait-visual', await call('wait_for', {
    condition: {
      type: 'visual',
      mode: 'color',
      target: '#F9F9F9',
      tolerance: 0,
      min_pixels: 100,
    },
    timeout_ms: 3000,
  }));

  const capture = await call('capture_screen', {
    region: { x: 3080, y: 32, width: 180, height: 32 },
    scale: 1,
    save_path: 'artifacts/m2-1-template.png',
  });
  show('capture-save', payload(capture));

  show('template-by-path', await call('match_visual', {
    mode: 'template',
    target_path: 'artifacts/m2-1-template.png',
    region: { x: 3072, y: 0, width: 1280, height: 720 },
    threshold: 0.9,
    max_results: 3,
  }));

  show('save-outside', await call('capture_screen', {
    save_path: 'C:\\Windows\\vsp-should-not-exist.png',
  }));

  // 关掉最后接管的应用，验证会回退到前一个存活应用�?
  try {
    process.kill(ed?.pid);
  } catch {
    // 已退出�?
  }

  await sleep(2000);

  const afterClose = payload(await call('find_element', { control_type: 'Document' }));
  show('document-after-last-closed', {
    isError: afterClose?.code ? true : false,
    pid: afterClose?.process_id,
    expectedOthers: [np?.pid, ed?.pid],
  });

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
