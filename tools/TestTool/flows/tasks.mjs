import fs from 'node:fs';
import path from 'node:path';
import {
  NOTEPAD, CALC, EXPLORER, WINWORD, EXCEL, UITARGET, PROBE, FIX,
  sleep, ps, run, wordRead, excelRead,
} from '../lib/util.mjs';

function makeChecks() {
  const checks = [];
  return {
    checks,
    check(name, ok, detail) { checks.push({ name, ok: Boolean(ok), detail }); return Boolean(ok); },
  };
}

function center(bounds) {
  return { x: Math.round(bounds.x + bounds.width / 2), y: Math.round(bounds.y + bounds.height / 2) };
}

async function launch(mcp, options) {
  const result = await mcp.call('launch_or_place_app', options, 120000);
  await sleep(600);
  return result;
}

/** 用 input 点击把目标窗口真正置前，绕过 Windows 前台锁；光标落在虚拟屏内。
 *  点击前校验坐标严格位于 virtual_display，虚拟屏未挂载或设置了 VSP_NO_INPUT=1 时跳过。 */
async function focusWindow(mcp, launched) {
  const b = launched?.data?.bounds;
  if (!b) return false;
  if (process.env.VSP_NO_INPUT === '1') return false;
  const env = await mcp.call('get_environment', {});
  const display = env.data?.virtual_display;
  if (!display || !(display.enabled ?? display.owned)) return false;
  const x = Math.round(b.x + b.width / 2);
  const y = Math.round(b.y + 20);
  const inside = x >= display.x && y >= display.y &&
    x < display.x + display.width && y < display.y + display.height;
  if (!inside) return false;
  await mcp.call('click', { x, y, mode: 'input' });
  await sleep(300);
  return true;
}

/** T1: Notepad 打开文件、输入中英混排、保存、读回。 */
export async function t1(mcp, ctx) {
  const { check, checks } = makeChecks();
  const file = path.join(ctx.work, 'note.txt');
  const launched = await launch(mcp, { path: NOTEPAD, args: `"${file}"`, wait_ms: 15000 });
  check('launch', launched.ok, launched.code);
  await focusWindow(mcp, launched);
  const typed = await mcp.call('type_text', { text: 'Hello 虚拟屏 123\n第二行 ABC' });
  check('type_text', typed.ok && typed.data?.verified !== false, typed.code ?? typed.data?.verified);
  const saved = await mcp.call('press_keys', { keys: 'Ctrl+S' });
  check('save', saved.ok, saved.code);
  await sleep(700);
  const content = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : '';
  check('file content', content.includes('Hello 虚拟屏 123') && content.includes('第二行 ABC'), content.slice(0, 80));
  return { checks };
}

/** T2: 截图缩放 + 区域变化断言。 */
export async function t2(mcp, ctx) {
  const { check, checks } = makeChecks();
  const log = path.join(ctx.work, 'visual.json');
  const launched = await launch(mcp, { path: UITARGET, args: `"${log}"`, wait_ms: 20000 });
  check('launch', launched.ok, launched.code);
  await sleep(900);

  const canvas = await mcp.call('find_element', { automation_id: 'canvas' });
  check('find canvas', canvas.ok, canvas.code);
  if (canvas.ok) {
    const b = canvas.data.bounds;
    const shot = await mcp.call('capture_screen', { region: b, scale: 0.5 });
    const meta = shot.data?.meta ?? shot.data;
    check('capture scale', shot.ok && meta?.scale === 0.5 && Math.abs(meta.width - b.width / 2) <= 1,
      JSON.stringify(meta));
  }

  const result = await mcp.call('find_element', { automation_id: 'result' });
  check('find result', result.ok, result.code);
  if (result.ok) {
    const b = result.data.bounds;
    const region = { x: b.x, y: b.y, width: Math.max(b.width, 40), height: Math.max(b.height, 20) };
    const before = await mcp.call('capture_screen', { region });
    check('baseline', before.ok, before.code);
    const submit = await mcp.call('find_element', { automation_id: 'submit' });
    if (submit.ok) {
      const sb = submit.data.bounds;
      await mcp.call('click', {
        x: Math.round(sb.x + sb.width / 2), y: Math.round(sb.y + sb.height / 2),
        mode: 'message', keep_foreground: true,
      });
    }
    await sleep(800);
    const changed = await mcp.call('assert_region', { region, expectation: { changed: true } });
    check('changed', changed.ok, changed.code);
  }
  return { checks };
}

/** T3: Notepad 与 WinForms 靶标双应用并行，验证多应用 pid 与窗口归属。 */
export async function t3(mcp, ctx) {
  const { check, checks } = makeChecks();
  const fileA = path.join(ctx.work, 'a.txt');
  const log = path.join(ctx.work, 'second.json');

  const first = await launch(mcp, { path: NOTEPAD, args: `"${fileA}"`, wait_ms: 15000 });
  check('launch A', first.ok, first.code);
  await focusWindow(mcp, first);
  const typeA = await mcp.call('type_text', { text: 'window A 一号' });
  check('type A', typeA.ok, typeA.code);
  await mcp.call('press_keys', { keys: 'Ctrl+S' });
  await sleep(500);

  const second = await launch(mcp, { path: UITARGET, args: `"${log}"`, wait_ms: 20000 });
  check('launch B', second.ok, second.code);
  await sleep(800);
  const input = await mcp.call('find_element', { automation_id: 'input' });
  check('find B input', input.ok, input.code);
  if (input.ok) {
    const set = await mcp.call('set_element_value', { ref: input.data.ref, value: 'window B 二号', allow_destructive: true });
    check('set B', set.ok, set.code);
  }
  const submit = await mcp.call('find_element', { automation_id: 'submit' });
  if (submit.ok) await mcp.call('invoke_element', { ref: submit.data.ref });
  await sleep(500);

  const contentA = fs.existsSync(fileA) ? fs.readFileSync(fileA, 'utf8') : '';
  let payload = null;
  try { payload = JSON.parse(fs.readFileSync(log, 'utf8')); } catch { /* ignore */ }
  check('A content', contentA.includes('window A 一号') && !contentA.includes('二号'), contentA.slice(0, 40));
  check('B content', payload?.input === 'window B 二号' && payload?.submitted === true, JSON.stringify(payload));
  return { checks };
}

/** T4: WinForms 靶标表单写入 + 复选框 + 提交。 */
export async function t4(mcp, ctx) {
  const { check, checks } = makeChecks();
  const log = path.join(ctx.work, 'event.json');
  const launched = await launch(mcp, { path: UITARGET, args: `"${log}"`, wait_ms: 20000 });
  check('launch', launched.ok, launched.code);
  await sleep(900);

  const input = await mcp.call('find_element', { automation_id: 'input' });
  check('find input', input.ok, input.code);
  if (input.ok) {
    const r = await mcp.call('set_element_value', { ref: input.data.ref, value: 'hello-bench', allow_destructive: true });
    check('set input', r.ok, r.code);
  }
  const count = await mcp.call('find_element', { automation_id: 'count' });
  check('find count', count.ok, count.code);
  if (count.ok) {
    const r = await mcp.call('set_element_value', { ref: count.data.ref, value: '7', allow_destructive: true });
    check('set count', r.ok, r.code);
  }
  const opt = await mcp.call('find_element', { automation_id: 'opt' });
  check('find opt', opt.ok, opt.code);
  if (opt.ok) {
    const r = await mcp.call('invoke_element', { ref: opt.data.ref });
    check('toggle opt', r.ok, r.code);
  }
  const submit = await mcp.call('find_element', { automation_id: 'submit' });
  check('find submit', submit.ok, submit.code);
  if (submit.ok) {
    const r = await mcp.call('invoke_element', { ref: submit.data.ref });
    check('click submit', r.ok, r.code);
  }
  await sleep(600);
  let payload = null;
  try { payload = JSON.parse(fs.readFileSync(log, 'utf8')); } catch { /* missing */ }
  check('event json', payload?.input === 'hello-bench' && payload?.count === '7' && payload?.opt === true && payload?.submitted === true,
    JSON.stringify(payload));
  return { checks };
}

/** T5: 视觉模板匹配。模板在 fixture 阶段独立生成。 */
export async function t5(mcp, ctx) {
  const { check, checks } = makeChecks();
  const launched = await launch(mcp, { path: UITARGET, args: `"${path.join(ctx.work, 'visual.json')}"`, wait_ms: 20000 });
  check('launch', launched.ok, launched.code);
  await sleep(900);
  const canvas = await mcp.call('find_element', { automation_id: 'canvas' });
  check('find canvas', canvas.ok, canvas.code);
  if (!canvas.ok) return { checks };
  const bounds = canvas.data.bounds;
  const matched = await mcp.call('match_visual', {
    mode: 'template',
    target_path: path.join(FIX, 't5', 'template.png'),
    region: bounds,
    threshold: 0.9,
  });
  check('match', matched.ok, matched.code);
  const match = matched.ok ? (matched.data.matches?.[0] ?? matched.data) : null;
  const x = match?.x ?? match?.bounds?.x;
  const y = match?.y ?? match?.bounds?.y;
  const expectedX = bounds.x + 36;
  const expectedY = bounds.y + 36;
  check('position', Number.isFinite(x) && Math.abs(x - expectedX) <= 2 && Math.abs(y - expectedY) <= 2,
    `got=${x},${y} expected=${expectedX},${expectedY}`);
  return { checks };
}

/** T6: Word 打开文档、全选替换、保存、COM 读回。 */
export async function t6(mcp, ctx) {
  const { check, checks } = makeChecks();
  const file = path.join(ctx.work, 'doc.docx');
  const launched = await launch(mcp, { path: WINWORD, args: `"${file}"`, ensure_document: false, wait_ms: 30000 });
  check('launch', launched.ok, launched.code);
  await focusWindow(mcp, launched);
  await sleep(2500);
  const select = await mcp.call('press_keys', { keys: 'Ctrl+A' });
  check('select all', select.ok, select.code);
  const typed = await mcp.call('type_text', { text: 'BENCH WORD 2026 替换完成' });
  check('type_text', typed.ok, typed.code);
  const saved = await mcp.call('press_keys', { keys: 'Ctrl+S' });
  check('save', saved.ok, saved.code);
  await sleep(1800);
  const read = await wordRead(file);
  check('oracle text', read.ok && read.stdout.includes('BENCH WORD 2026'), read.stdout.slice(0, 120) || read.stderr.slice(0, 120));
  return { checks };
}

/** T7: Excel 用名称框写入 3 个单元格、保存、COM 读回。 */
export async function t7(mcp, ctx) {
  const { check, checks } = makeChecks();
  const file = path.join(ctx.work, 'book.xlsx');
  const launched = await launch(mcp, { path: EXCEL, args: `"${file}"`, wait_ms: 30000 });
  check('launch', launched.ok, launched.code);
  await sleep(4000);
  // Excel 的启动壳进程会退出，真实窗口由另一个进程承载；按标题接管存活窗口。
  let adopted = null;
  for (let attempt = 0; attempt < 6; attempt++) {
    adopted = await mcp.call('launch_or_place_app', { window: 'book.xlsx', wait_ms: 15000 }, 60000);
    if (adopted.ok) break;
    await sleep(1000);
  }
  check('adopt window', adopted?.ok, adopted?.code);
  const target = adopted?.ok ? adopted : launched;
  await focusWindow(mcp, target);
  await sleep(1200);

  // 不用 Ctrl+G（模态对话框会阻断后续激活），直接从 A1 起用键盘填写。
  const steps = [
    ['type', 'Alpha2'], ['key', 'Enter'],
    ['type', 'Beta'], ['key', 'Enter'],
    ['keys', ['Up', 'Up', 'Right']],
    ['type', '10'], ['key', 'Enter'],
    ['type', '20'], ['key', 'Enter'],
  ];
  for (const [kind, value] of steps) {
    if (kind === 'type') {
      const typed = await mcp.call('type_text', { text: value });
      check(`type ${value}`, typed.ok, typed.code);
    } else if (kind === 'key') {
      const pressed = await mcp.call('press_keys', { keys: value });
      check(`key ${value}`, pressed.ok, pressed.code);
    } else {
      const pressed = await mcp.call('press_keys', { sequence: value });
      check('move to B1', pressed.ok, pressed.code);
    }
    await sleep(250);
  }
  const saved = await mcp.call('press_keys', { keys: 'Ctrl+S' });
  check('save', saved.ok, saved.code);
  await sleep(1500);
  const read = await excelRead(file);
  let json = null;
  try { json = JSON.parse(read.stdout.trim()); } catch { /* ignore */ }
  check('oracle cells',
    read.ok && json?.a1 === 'Alpha2' && json?.b1 === '10' && json?.a2 === 'Beta' && json?.b2 === '20',
    read.stdout.slice(0, 160) || read.stderr.slice(0, 160));
  return { checks };
}

/** T8: 故障恢复：应用被杀、外部摘屏。 */
export async function t8(mcp, ctx) {
  const { check, checks } = makeChecks();
  const launched = await launch(mcp, { path: NOTEPAD, wait_ms: 15000 });
  check('launch', launched.ok, launched.code);
  const pid = launched.data?.pid;
  if (pid) {
    await ps(`Stop-Process -Id ${pid} -Force -ErrorAction SilentlyContinue`);
  }
  await sleep(700);
  const dead = await mcp.call('type_text', { text: 'x' });
  check('dead app error', !dead.ok && ['E_NO_TARGET', 'E_TARGET_OFFSCREEN', 'E_APP_EXITED'].includes(dead.code), dead.code);

  const relaunched = await launch(mcp, { path: NOTEPAD, wait_ms: 15000 });
  check('relaunch', relaunched.ok, relaunched.code);
  const shot = await mcp.call('capture_screen', {});
  check('capture after relaunch', shot.ok, shot.code);

  const removed = await run(PROBE, ['removeall'], { timeout: 30000 });
  check('remove display', removed.ok, removed.stderr.slice(0, 80));
  // 驱动 REMOVE 后短时间内拒绝 ADD，等冷却后再触发 EnsureEnvironment。
  await sleep(5500);
  const shot2 = await mcp.call('capture_screen', {});
  check('capture after heal', shot2.ok, shot2.code);
  const env = await mcp.call('get_environment', {});
  const display = env.data?.virtual_display;
  check('self heal', env.ok && Boolean(display?.enabled ?? display?.owned), JSON.stringify(display ?? env.code));
  return { checks };
}

export const TASKS = {
  t1: { name: 'Notepad 保存', run: t1 },
  t2: { name: '截图缩放与断言', run: t2 },
  t3: { name: '多窗口记事本', run: t3 },
  t4: { name: 'WinForms 表单', run: t4 },
  t5: { name: '视觉模板匹配', run: t5 },
  t6: { name: 'Word 替换保存', run: t6 },
  t7: { name: 'Excel 单元格写入', run: t7 },
  t8: { name: '故障恢复', run: t8 },
};
