import { spawn } from 'node:child_process';
import readline from 'node:readline';

const exe = process.argv[2];
const appPath = process.argv[3] ?? 'C:\\Windows\\System32\\notepad.exe';

if (!exe) {
  console.error('usage: node m2-semantic.mjs <VScreenPilot.exe> [appPath]');
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
  for (const child of node.children ?? []) walk(child, visit);
}

(async () => {
  await request('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'm2-semantic', version: '0.0.1' },
  });
  notify('notifications/initialized');

  const tools = await request('tools/list');
  console.log('--- tools ---');
  console.log(JSON.stringify(tools.result?.tools?.map((tool) => tool.name)));

  show('launch', await call('launch_or_place_app', { path: appPath, wait_ms: 20000 }));

  const cursorBefore = payload(await call('get_environment'))?.cursor;

  const tree = await call('get_ui_tree', { max_depth: 8, interactive_only: true, max_nodes: 300 });
  const treeBody = payload(tree);

  if (tree.isError) {
    show('tree-raw', tree);
  }

  if ((treeBody?.nodes ?? 0) <= 5) {
    show('tree-small', treeBody?.tree);
  }
  console.log('--- tree-meta ---');
  console.log(JSON.stringify({
    isError: tree.isError,
    nodes: treeBody?.nodes,
    visited: treeBody?.visited,
    truncated: treeBody?.truncated,
    reason: treeBody?.truncated_reason,
    elapsed_ms: treeBody?.elapsed_ms,
    root_ref: treeBody?.root_ref,
    window: treeBody?.window,
  }, null, 2));

  const interactive = [];
  walk(treeBody?.tree, (node) => {
    if (node.control_type === 'Button' || node.control_type === 'MenuItem' ||
        node.control_type === 'Edit' || node.control_type === 'Document' ||
        node.control_type === 'TabItem' || node.control_type === 'Hyperlink') {
      interactive.push({
        ref: node.ref,
        control_type: node.control_type,
        name: node.name,
        automation_id: node.automation_id,
        value: node.value,
        enabled: node.enabled,
        bounds: node.bounds,
      });
    }
  });
  console.log('--- interactive ---');
  console.log(JSON.stringify(interactive.slice(0, 30), null, 2));

  show('find-edit', await call('find_element', { control_type: 'Edit', all: true, max_results: 5 }));
  show('find-index', await call('find_element', { control_type: 'Button', all: true, max_results: 5 }));
  show('find-index-1', await call('find_element', { control_type: 'Button', index: 1 }));

  const truncatedTree = payload(await call('get_ui_tree', {
    max_depth: 3,
    interactive_only: true,
    max_nodes: 5,
  }));
  console.log('--- tree-truncated-meta ---');
  console.log(JSON.stringify({
    nodes: truncatedTree?.nodes,
    truncated: truncatedTree?.truncated,
    reason: truncatedTree?.truncated_reason,
  }));
  show('find-doc', await call('find_element', { control_type: 'Document' }));
  show('find-contains', await call('find_element', {
    name: '编辑',
    name_match: 'contains',
    all: true,
    max_results: 10,
  }));

  const editRef = interactive.find((item) => item.control_type === 'Edit' && item.ref)?.ref
    ?? interactive.find((item) => item.control_type === 'Document' && item.ref)?.ref;

  if (editRef) {
    const first = await call('set_element_value', {
      ref: editRef,
      value: 'M2 semantic hello 你好',
    });
    show('set-value', first);

    if (payload(first)?.code === 'E_PATTERN_UNSUPPORTED') {
      show('set-value-keyboard', await call('set_element_value', {
        ref: editRef,
        value: 'M2 semantic hello 你好',
        allow_keyboard: true,
      }));
    }

    show('set-value-verify', await call('get_ui_tree', {
      max_depth: 3,
      interactive_only: true,
      max_nodes: 50,
      root: editRef,
    }));
  } else {
    console.log('--- set-value ---');
    console.log('skipped: no Edit/Document ref');
  }

  const unsafeName = /最小化|最大化|关闭|还原|Minimize|Maximize|Close|Restore/i;
  const usable = interactive.filter((item) => item.enabled !== false && item.ref);
  const buttonRef = usable.find((item) => item.control_type === 'MenuItem')?.ref
    ?? usable.find((item) => item.control_type === 'Button' && !unsafeName.test(item.name ?? ''))?.ref;

  if (buttonRef) {
    show('invoke', await call('invoke_element', { ref: buttonRef }));
    await new Promise((resolve) => setTimeout(resolve, 500));
    show('invoke-tree', await call('get_ui_tree', { max_depth: 2, interactive_only: true, max_nodes: 60 }));
  } else {
    console.log('--- invoke ---');
    console.log('skipped: no Button/MenuItem ref');
  }

  show('stale-ref', await call('invoke_element', { ref: 'e999999' }));
  show('bad-ref', await call('invoke_element', { ref: '' }));

  const env = await call('get_environment');
  console.log('--- foreground ---');
  console.log(JSON.stringify(env.content?.[0]?.foreground));
  console.log('--- cursor ---');
  console.log(JSON.stringify({
    before: cursorBefore,
    after: env.content?.[0]?.cursor,
    unchanged: cursorBefore && env.content?.[0]?.cursor &&
      cursorBefore.x === env.content[0].cursor.x && cursorBefore.y === env.content[0].cursor.y,
  }));

  const release = await call('release_environment');
  show('release', release);

  if (editRef) {
    show('invoke-after-release', await call('invoke_element', { ref: editRef }));
  }

  show('log', await call('get_log', { tail: 20 }));

  child.stdin.end();
  setTimeout(() => {
    try { child.kill(); } catch { /* 已退出 */ }
  }, 3000);
})().catch((error) => {
  console.error(error);
  try { child.kill(); } catch { /* 已退出 */ }
  process.exit(1);
});
