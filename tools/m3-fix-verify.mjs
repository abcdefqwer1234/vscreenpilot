/**
 * M3 语义修复验证：ValuePattern 不得吞掉 append/replace；空值回读必须真实为空。
 * 覆盖 Notepad 的 Edit（支持 ValuePattern）与键盘路径，Word Body 由 m3-verify 覆盖。
 */
import { spawn } from 'node:child_process';
import readline from 'node:readline';
import { writeFileSync, mkdirSync } from 'node:fs';

const exe = process.argv[2];
const notepad = process.argv[3] ?? 'C:\\Windows\\System32\\notepad.exe';
if (!exe) process.exit(1);

const log = [];
const results = [];
const record = (l, v) => { log.push(`--- ${l} ---`); log.push(JSON.stringify(v, null, 2)); };
const check = (name, ok, detail) => {
  results.push({ name, ok, detail });
  log.push(`[TEST] ${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' :: ' + detail : ''}`);
};

const child = spawn(exe, ['--allow-script'], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
const reader = readline.createInterface({ input: child.stdout });
const pending = new Map();
let nextId = 1;
reader.on('line', (line) => {
  if (!line.trim()) return;
  let m; try { m = JSON.parse(line); } catch { return; }
  if (m.id !== undefined && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
});
child.stderr.on('data', () => {});
function request(method, params = {}, timeoutMs = 90000) {
  const id = nextId++;
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  return new Promise((res, rej) => {
    const t = setTimeout(() => rej(new Error('timeout ' + method)), timeoutMs);
    pending.set(id, (r) => { clearTimeout(t); res(r); });
  });
}
const notify = (m, p = {}) => child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: m, params: p }) + '\n');
function summarize(r) {
  if (r.error) return { rpcError: r.error };
  const x = r.result; if (!x || !Array.isArray(x.content)) return x;
  return {
    isError: x.isError,
    content: x.content.map((i) => {
      if (i.type === 'image') return { type: 'image' };
      try { return JSON.parse(i.text); } catch { return i.text; }
    }),
  };
}
async function call(name, args = {}, timeoutMs) {
  const t0 = Date.now();
  const r = await request('tools/call', { name, arguments: args }, timeoutMs);
  const s = summarize(r);
  return { ...s, payload: s.content?.[0], ms: Date.now() - t0 };
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  try {
    await request('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'm3-fix', version: '1' } });
    notify('notifications/initialized');

    const launch = await call('launch_or_place_app', { path: notepad, wait_ms: 15000 }, 60000);
    record('launch', launch.payload);
    const pid = launch.payload?.pid;

    const found = await call('find_element', { pid, role: 'document' });
    record('find-document', found.payload);
    const ref = found.payload?.ref;
    check('found Document ref', Boolean(ref), ref);

    // 新版记事本编辑区是 Document；先探明是否支持 ValuePattern。
    // Notepad 可能恢复上次内容；用 allow_destructive 明确把初始内容重置为 AAA。
    const first = await call('set_element_value', {
      ref, value: 'AAA', mode: 'insert', allow_destructive: true,
    });
    record('set-initial', first.payload);
    const firstMethod = first.payload?.method;
    check('insert writes value',
      firstMethod === 'value_pattern' || firstMethod === 'keyboard',
      `${firstMethod}/${first.payload?.mode}/${first.payload?.code ?? ''}`);
    if (firstMethod !== 'value_pattern') {
      record('set-initial-keyboard', (await call('set_element_value', {
        ref, value: 'AAA', mode: 'insert', allow_keyboard: true, allow_destructive: true,
      })).payload);
    }

    const appendNoKeyboard = await call('set_element_value', { ref, value: 'BAD_APPEND', mode: 'append' });
    record('append-without-keyboard', appendNoKeyboard.payload);
    check('append without keyboard rejected', appendNoKeyboard.payload?.code === 'E_PATTERN_UNSUPPORTED',
      appendNoKeyboard.payload?.code);

    const append = await call('set_element_value', { ref, value: 'BBB', mode: 'append', allow_keyboard: true });
    record('append-keyboard', append.payload);
    check('append goes keyboard', append.payload?.method === 'keyboard' && append.payload?.mode === 'append',
      `${append.payload?.method}/${append.payload?.mode}`);

    const afterAppend = await call('get_element_text', { ref, max_chars: 4096 });
    record('text-after-append', afterAppend.payload);
    check('append preserved existing text',
      afterAppend.payload?.text === 'AAABBB', JSON.stringify(afterAppend.payload?.text));

    const replaceNoAuth = await call('set_element_value', { ref, value: 'NOPE', mode: 'replace', allow_keyboard: true });
    record('replace-without-auth', replaceNoAuth.payload);
    check('replace still guarded', replaceNoAuth.payload?.code === 'E_DESTRUCTIVE_REQUIRED', replaceNoAuth.payload?.code);

    const replaced = await call('set_element_value', {
      ref, value: 'REPLACED_FINAL', mode: 'replace', allow_keyboard: true, allow_destructive: true,
    });
    record('replace-explicit', replaced.payload);
    check('replace verified', replaced.payload?.verified === true, `${replaced.payload?.mode}`);

    const cleared = await call('set_element_value', {
      ref, value: '', mode: 'replace', allow_keyboard: true, allow_destructive: true,
    });
    record('clear', cleared.payload);
    const afterClear = await call('get_element_text', { ref, max_chars: 4096 });
    record('text-after-clear', afterClear.payload);
    check('empty replace verified and empty',
      cleared.payload?.verified === true && afterClear.payload?.length === 0,
      `verified=${cleared.payload?.verified} len=${afterClear.payload?.length}`);

    await call('release_environment', { keep_display: true, on_dirty: 'force' });
    await sleep(800);
    const st = await call('run_script', {
      code: "(Get-Process notepad -ErrorAction SilentlyContinue | Measure-Object).Count",
      timeout_ms: 10000,
    });
    check('no leftover notepad', (st.payload?.stdout ?? '').trim() === '0', st.payload?.stdout);

    const pass = results.filter((r) => r.ok).length;
    log.push(`\nSUMMARY pass=${pass}/${results.length}`);
  } catch (e) {
    log.push('[FATAL] ' + (e?.stack ?? e));
  } finally {
    child.stdin.end();
    setTimeout(() => { try { child.kill(); } catch { /* */ } }, 3000);
    mkdirSync('docs', { recursive: true });
    writeFileSync('docs/m3-fix-verify.log', log.join('\n'));
    console.log(log.join('\n'));
  }
})();
