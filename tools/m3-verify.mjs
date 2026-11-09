import { spawn } from 'node:child_process';
import readline from 'node:readline';
import { writeFileSync, mkdirSync } from 'node:fs';

const exe = process.argv[2];
const wordPath = process.argv[3] ?? 'C:\\Program Files\\Microsoft Office\\Root\\Office16\\WINWORD.EXE';
if (!exe) { console.error('usage: node m3-verify.mjs <exe> [WINWORD]'); process.exit(1); }

const log = [];
const steps = [];
const section = (s) => log.push(`\n===== ${s} =====`);
const record = (l, v) => { log.push(`--- ${l} ---`); log.push(JSON.stringify(v, null, 2)); };
const step = (n, ok, d) => { steps.push({ n, ok, d }); log.push(`[STEP] ${ok ? 'PASS' : 'FAIL'} ${n}${d ? ' :: ' + d : ''}`); };

const child = spawn(exe, ['--allow-script'], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
const reader = readline.createInterface({ input: child.stdout });
const pending = new Map();
let nextId = 1;
reader.on('line', (line) => {
  if (!line.trim()) return;
  let m; try { m = JSON.parse(line); } catch { return; }
  if (m.id !== undefined && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
});
child.stderr.on('data', (d) => log.push('[stderr] ' + d.toString().trim().slice(0, 500)));
child.on('exit', (c) => log.push('[exit] ' + c));
function request(method, params = {}, timeoutMs = 120000) {
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
  const x = r.result;
  if (!x || !Array.isArray(x.content)) return x;
  return { isError: x.isError, content: x.content.map((i) => {
    if (i.type === 'image') return { type: 'image' };
    try { return JSON.parse(i.text); } catch { return i.text; }
  }) };
}
async function call(name, args = {}, timeoutMs) {
  const t0 = Date.now();
  const r = await request('tools/call', { name, arguments: args }, timeoutMs);
  const s = summarize(r);
  return { ...s, payload: s.content?.[0], ms: Date.now() - t0, raw: r };
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function shot(name) {
  return (await call('capture_screen', { scale: 1, save_path: `artifacts/m3-${name}.png` })).payload;
}

(async () => {
  try {
    await request('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'm3-verify', version: '1' } });
    notify('notifications/initialized');
    const tools = await request('tools/list');
    record('tools', tools.result?.tools?.map((t) => t.name));
    step('tools/list', (tools.result?.tools?.length ?? 0) === 18, `${tools.result?.tools?.length} tools`);

    section('A. Word 就绪 + Body 读取');
    const launch = await call('launch_or_place_app', { path: wordPath, wait_ms: 30000 });
    record('launch', { ...launch.payload, ms: launch.ms });
    step('document_ready', launch.payload?.document_ready === true, `ready=${launch.payload?.document_ready}`);

    await sleep(1000);
    const pid = launch.payload?.pid;
    const edits = await call('find_element', { pid, control_type: 'Edit', all: true, max_results: 10 });
    record('edits-default', (edits.payload?.elements ?? []).map((e) => ({ name: e.name, aid: e.automation_id, ref: e.ref })));
    step('find_element default prefers Body', edits.payload?.elements?.[0]?.automation_id === 'Body', edits.payload?.elements?.[0]?.name);
    const body = (await call('find_element', { pid, role: 'document_body' })).payload;
    record('body-by-role', { ...body, ms: undefined });
    step('find_element role=document_body', body?.automation_id === 'Body', `${body?.ref}`);
    const bodyRef = body?.ref;
    const bodyCenter = body?.bounds
      ? { x: body.bounds.x + Math.round(body.bounds.width / 2), y: body.bounds.y + 80 }
      : { x: 3704, y: 260 };

    const text0 = (await call('get_element_text', { ref: bodyRef, max_chars: 65536 })).payload;
    record('get-text-initial', { length: text0?.length, text: text0?.text, code: text0?.code });
    step('get_element_text Body', text0?.code === undefined, `length=${text0?.length}`);

    section('B. 长文本输入完整性');
    // 清空正文
    await call('click', { x: bodyCenter.x, y: bodyCenter.y, mode: 'auto' });
    await call('press_keys', { keys: 'Ctrl+A' });
    await call('press_keys', { keys: 'Delete' });
    await sleep(300);

    const longText = 'M3_0123456789_ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz_END';
    const typed = await call('type_text', { text: longText });
    record('type-long', typed.payload);
    step('type_text verified', typed.payload?.verified === true, `chunks=${typed.payload?.chunks} len=${typed.payload?.observed_length}`);
    await sleep(300);

    const text1 = (await call('get_element_text', { ref: bodyRef, max_chars: 65536 })).payload;
    record('get-text-long', { length: text1?.length, text: text1?.text });
    step('long text exact', typeof text1?.text === 'string' && text1.text.includes(longText), `len=${text1?.length}`);
    record('capture-long', await shot('long'));

    section('C. 基线注册表');
    const region = { x: bodyCenter.x - 250, y: bodyCenter.y - 120, width: 500, height: 240 };
    const base = await call('capture_screen', { region, scale: 1, save_path: 'artifacts/m3-baseline.png' });
    record('capture-baseline', base.payload);
    await call('click', { x: bodyCenter.x, y: bodyCenter.y, mode: 'auto' });
    await call('type_text', { text: 'CHANGE_TOKEN' });
    await sleep(300);

    // 故意插入一次全屏截图，验证不会覆盖同区域基线。
    await shot('interference');
    const changed = await call('assert_region', {
      region,
      expectation: { changed: true, threshold: 0.001 },
    });
    record('assert-changed-after-other-capture', changed.payload);
    step('assert_region same-region after full capture', changed.payload?.passed === true, `ratio=${changed.payload?.changed_ratio} frame=${changed.payload?.baseline_frame_id}`);

    const explicit = await call('assert_region', {
      region,
      expectation: { changed: true, threshold: 0.001, baseline_frame_id: base.payload?.frame_id },
    });
    record('assert-changed-explicit-frame', explicit.payload);
    step('assert_region explicit frame_id', explicit.payload?.passed === true, `frame=${explicit.payload?.baseline_frame_id}`);

    section('D. set_element_value 模式');
    const append = await call('set_element_value', {
      ref: bodyRef,
      value: 'APPEND_TOKEN',
      allow_keyboard: true,
      mode: 'append',
    });
    record('set-append', { ...append.payload, ms: append.ms });
    step('set_element_value append verified', append.payload?.verified === true, `${append.payload?.mode}`);

    const text2 = (await call('get_element_text', { ref: bodyRef, max_chars: 65536 })).payload;
    step('append present', typeof text2?.text === 'string' && text2.text.includes('APPEND_TOKEN'), `len=${text2?.length}`);

    const dangerous = await call('set_element_value', {
      ref: bodyRef,
      value: 'SHOULD_NOT_WRITE',
      allow_keyboard: true,
      mode: 'replace',
    });
    record('set-replace-without-destructive', dangerous.payload);
    step('replace guarded', dangerous.payload?.code === 'E_DESTRUCTIVE_REQUIRED', dangerous.payload?.code);

    const replaced = await call('set_element_value', {
      ref: bodyRef,
      value: 'REPLACED_FINAL',
      allow_keyboard: true,
      mode: 'replace',
      allow_destructive: true,
    });
    record('set-replace-explicit', { ...replaced.payload, ms: replaced.ms });
    step('set_element_value replace verified', replaced.payload?.verified === true, `${replaced.payload?.mode}`);

    const text3 = (await call('get_element_text', { ref: bodyRef, max_chars: 65536 })).payload;
    record('get-text-replaced', { length: text3?.length, text: text3?.text });
    step('replace content exact', typeof text3?.text === 'string' && text3.text.trim() === 'REPLACED_FINAL', `len=${text3?.length}`);

    const cleared = await call('set_element_value', {
      ref: bodyRef,
      value: '',
      allow_keyboard: true,
      mode: 'replace',
      allow_destructive: true,
    });
    record('set-clear', { ...cleared.payload, ms: cleared.ms });
    const text4 = (await call('get_element_text', { ref: bodyRef, max_chars: 65536 })).payload;
    step('replace with empty clears body', cleared.payload?.verified === true && text4?.length === 0, `len=${text4?.length}`);

    section('E. 释放 on_dirty=discard');
    const t0 = Date.now();
    const release = await call('release_environment', { on_dirty: 'discard' });
    record('release-discard', { ...release.payload, ms: Date.now() - t0 });
    step('release discard', !release.isError, `${Date.now() - t0}ms forced=${JSON.stringify(release.payload?.forced_pids)} discarded=${JSON.stringify(release.payload?.discarded_pids)}`);

    await sleep(800);
    const procs = await call('run_script', {
      code: "Get-Process WINWORD -ErrorAction SilentlyContinue | Select-Object Id | ConvertTo-Json -Compress",
      timeout_ms: 10000,
    });
    record('winword-after-release', procs.payload);

    section('F. 复用 hint + 强制释放');
    const launch2 = await call('launch_or_place_app', { path: wordPath, wait_ms: 30000 });
    record('launch2', launch2.payload);
    const reuse = await call('launch_or_place_app', { path: wordPath, wait_ms: 30000, new_instance: false });
    record('reuse', reuse.payload);
    step('reuse hint matches close policy', typeof reuse.payload?.hint === 'string' && (
      reuse.payload.hint.includes('关闭') === (reuse.payload.close_on_release === true)
    ), reuse.payload?.hint);

    const release2 = await call('release_environment', { keep_display: false, on_dirty: 'force' });
    record('release2', release2.payload);
    step('release keep_display=false', release2.payload?.display_removed === true, `forced=${JSON.stringify(release2.payload?.forced_pids)}`);

    record('steps', steps);
  } catch (e) {
    log.push('[FATAL] ' + (e?.stack ?? e));
    record('fatal', String(e));
  } finally {
    child.stdin.end();
    setTimeout(() => { try { child.kill(); } catch { /* */ } }, 2500);
    mkdirSync('docs', { recursive: true });
    writeFileSync('docs/m3-verify.log', log.join('\n'));
    console.log(log.join('\n'));
  }
})();
