import { spawn } from 'node:child_process';
import path from 'node:path';
import { McpClient } from '../lib/client.mjs';
import { EXE, TMP, NOTEPAD, sleep, cleanupRuntime } from '../lib/util.mjs';

async function withClient(fn) {
  const client = new McpClient(EXE);
  await client.start();
  try {
    return await fn(client);
  } finally {
    await client.stop();
  }
}

function expectCode(result, codes) {
  return { ok: codes.includes(result.code), detail: `code=${result.code}` };
}

export const FAULTS = {
  'capture-negative-region': async () => withClient(async (client) => {
    const result = await client.call('capture_screen', { region: { x: -10, y: 0, width: 100, height: 100 } });
    return expectCode(result, ['E_OUT_OF_BOUNDS', 'E_INVALID_ARG']);
  }),

  'capture-zero-size': async () => withClient(async (client) => {
    const result = await client.call('capture_screen', { region: { x: 0, y: 0, width: 0, height: 0 } });
    return expectCode(result, ['E_INVALID_ARG', 'E_OUT_OF_BOUNDS']);
  }),

  'template-missing': async () => withClient(async (client) => {
    const result = await client.call('match_visual', {
      mode: 'template',
      target_path: path.join(TMP, 'no-such-template.png'),
    });
    return expectCode(result, ['E_IO', 'E_INVALID_ARG', 'E_UNSUPPORTED_IMAGE']);
  }),

  'element-not-found': async () => withClient(async (client) => {
    const launched = await client.call('launch_or_place_app', { path: NOTEPAD, wait_ms: 15000 }, 120000);
    if (!launched.ok) return { ok: false, detail: `launch ${launched.code}` };
    const result = await client.call('find_element', { name: '__no_such_element__' });
    return expectCode(result, ['E_ELEMENT_NOT_FOUND']);
  }),

  'cancel-long-call': async () => withClient(async (client) => {
    const launched = await client.call('launch_or_place_app', { path: NOTEPAD, wait_ms: 15000 }, 120000);
    if (!launched.ok) return { ok: false, detail: `launch ${launched.code}` };
    const pending = client.begin('tools/call', {
      name: 'wait_for',
      arguments: { condition: { type: 'element', name: '__never__' }, timeout_ms: 30000 },
    });
    await sleep(400);
    client.notify('notifications/cancelled', { requestId: pending.id, reason: 'bench' });
    const response = await pending.promise;
    const text = response?.result?.content?.find((item) => item.type === 'text')?.text ?? '{}';
    let code = null;
    try { code = JSON.parse(text).code; } catch { /* ignore */ }
    const ping = await client.request('ping', {}, 5000);
    return { ok: code === 'E_CANCELLED' && Boolean(ping.result), detail: `code=${code} ping=${Boolean(ping.result)}` };
  }),

  'eof-clean-exit': async () => {
    const client = new McpClient(EXE);
    await client.start();
    const started = Date.now();
    client.child.stdin.end();
    const code = await Promise.race([client.exit, sleep(8000).then(() => 'timeout')]);
    return { ok: code === 0, detail: `exit=${code} in ${Date.now() - started}ms` };
  },

  'second-instance-mutex': async () => {
    const child = spawn(EXE, ['--parent-pid', String(process.pid)], { stdio: 'ignore', windowsHide: true });
    const code = await Promise.race([
      new Promise((resolve) => child.on('exit', resolve)),
      sleep(8000).then(() => 'timeout'),
    ]);
    if (code === 'timeout') { try { child.kill(); } catch { /* ignore */ } }
    return { ok: code === 3, detail: `exit=${code}` };
  },

  'save-path-too-long': async () => withClient(async (client) => {
    const long = path.join(TMP, 'x'.repeat(320) + '.png');
    const result = await client.call('capture_screen', { save_path: long });
    return expectCode(result, ['E_INVALID_ARG', 'E_IO']);
  }),

  'click-outside-virtual-screen': async () => withClient(async (client) => {
    await client.call('get_environment', {});
    const result = await client.call('click', { x: 10, y: 10 });
    return expectCode(result, ['E_OUT_OF_BOUNDS', 'E_TARGET_OFFSCREEN', 'E_NO_TARGET']);
  }),
};

export async function runFaults(onEach) {
  const results = [];
  for (const [name, fault] of Object.entries(FAULTS)) {
    await cleanupRuntime();
    const result = await fault();
    results.push({ name, ...result });
    onEach?.(results[results.length - 1]);
  }
  await cleanupRuntime();
  return results;
}
