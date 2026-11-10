import fs from 'node:fs';
import path from 'node:path';
import { McpClient } from './lib/client.mjs';
import { TASKS } from './flows/tasks.mjs';
import { FAULTS } from './flows/faults.mjs';
import { BASELINES } from './baseline/baselines.mjs';
import {
  TESTTOOL, RES, TMP, EXE, UITARGET, ROOT,
  ensureDirs, setupFixtures, prepareWork, cleanupRuntime,
  startSampler, stopSampler, readSamples, run, sleep,
} from './lib/util.mjs';

const argv = process.argv.slice(2);
function arg(name, fallback = null) {
  const index = argv.indexOf(`--${name}`);
  return index >= 0 ? (argv[index + 1] ?? true) : fallback;
}
const has = (name) => argv.includes(`--${name}`);

function stats(values) {
  if (!values.length) return { count: 0, min: 0, p50: 0, p95: 0, max: 0, sum: 0 };
  const sorted = [...values].sort((a, b) => a - b);
  const at = (q) => sorted[Math.min(sorted.length - 1, Math.floor(q * sorted.length))];
  return {
    count: sorted.length,
    min: sorted[0],
    p50: at(0.5),
    p95: at(0.95),
    max: sorted[sorted.length - 1],
    sum: sorted.reduce((a, b) => a + b, 0),
  };
}

async function ensureBuilt() {
  if (fs.existsSync(EXE) && fs.existsSync(UITARGET)) return;
  console.log('[build] dotnet build');
  const result = await run('dotnet', ['build', path.join(ROOT, 'VScreenPilot.slnx'), '-c', 'Release', '--nologo'], { timeout: 300000 });
  if (!result.ok) throw new Error('solution build failed: ' + result.stderr.slice(0, 500));
  const target = await run('dotnet', ['build', path.join(TESTTOOL, 'targets', 'UiTarget', 'UiTarget.csproj'), '-c', 'Release', '--nologo'], { timeout: 300000 });
  if (!target.ok) throw new Error('UiTarget build failed: ' + target.stderr.slice(0, 500));
}

function summarizeCalls(calls) {
  const byName = {};
  for (const call of calls) {
    (byName[call.name] ??= []).push(call.ms);
  }
  const tools = {};
  for (const [name, values] of Object.entries(byName)) tools[name] = stats(values);
  return {
    count: calls.length,
    failed: calls.filter((c) => !c.ok).length,
    errorCodes: calls.filter((c) => !c.ok).reduce((acc, c) => {
      acc[c.code] = (acc[c.code] ?? 0) + 1;
      return acc;
    }, {}),
    textBytes: calls.reduce((a, c) => a + (c.textBytes ?? 0), 0),
    imageBytes: calls.reduce((a, c) => a + (c.imageBytes ?? 0), 0),
    tools,
  };
}

async function runTask(arm, task, runIndex) {
  const startedAt = new Date().toISOString();
  const started = Date.now();
  const work = prepareWork(arm, task, runIndex);
  const client = new McpClient(EXE);
  let sampler = null;
  let result = null;
  let error = null;
  const csv = path.join(work, 'samples.csv');
  try {
    await client.start();
    sampler = await startSampler(csv, client.child.pid);
    result = await TASKS[task].run(client, { work, task, arm, run: runIndex });
  } catch (exception) {
    error = String(exception?.stack ?? exception);
  }
  const wallMs = Date.now() - started;
  try { await client.call('release_environment', { keep_display: true, on_dirty: 'discard' }, 60000); } catch { /* ignore */ }
  await sleep(200);
  const samples = readSamples(csv);
  if (sampler) stopSampler(sampler);
  await client.stop();

  const checks = result?.checks ?? [];
  const ok = !error && checks.length > 0 && checks.every((c) => c.ok);
  const record = {
    arm, task, run: runIndex, ok, error,
    wallMs,
    checks,
    samples,
    calls: summarizeCalls(client.calls),
    callLog: client.calls.map((c) => ({ name: c.name, ms: c.ms, ok: c.ok, code: c.code, textBytes: c.textBytes, imageBytes: c.imageBytes })),
    stderr: client.stderr.slice(-2000),
    startedAt,
  };
  fs.writeFileSync(path.join(RES, `${arm}-${task}-${runIndex}.json`), JSON.stringify(record, null, 2));
  console.log(`[${arm}] ${task}#${runIndex} ${ok ? 'PASS' : 'FAIL'} ${wallMs}ms calls=${record.calls.count} err=${record.calls.failed} ${error ? 'exception' : ''}`);
  return record;
}

async function runSuite() {
  const arm = arg('arm', 'A');
  if (arm === 'B' && process.env.VSP_ALLOW_REAL_DESKTOP !== '1') {
    console.error('[B] 已在真实桌面禁用脚本基线。确需运行请设置 VSP_ALLOW_REAL_DESKTOP=1，并确保无人在使用该桌面。');
    process.exit(2);
  }
  await ensureBuilt();
  ensureDirs();
  await cleanupRuntime();
  await setupFixtures();

  const taskArg = arg('task', 'all');
  const runs = Number(arg('runs', 3));
  const taskIds = taskArg === 'all' ? Object.keys(TASKS) : String(taskArg).split(',');
  const results = [];
  for (const task of taskIds) {
    if (arm === 'A') {
      for (let i = 1; i <= runs; i++) {
        await cleanupRuntime();
        results.push(await runTask('A', task, i));
      }
    } else if (arm === 'B') {
      if (!BASELINES[task]) { console.log(`[B] ${task} 无基线实现，跳过`); continue; }
      for (let i = 1; i <= runs; i++) {
        await cleanupRuntime();
        results.push(await runBaseline(task, i));
      }
    } else {
      throw new Error('arm must be A or B');
    }
  }
  await cleanupRuntime();
  const summary = {
    arm, runs, tasks: taskIds,
    total: results.length,
    pass: results.filter((r) => r.ok).length,
    medianWallMs: stats(results.map((r) => r.wallMs)).p50,
    results: results.map((r) => ({ task: r.task, run: r.run, ok: r.ok, wallMs: r.wallMs, calls: r.calls.count, failedCalls: r.calls.failed })),
  };
  fs.writeFileSync(path.join(RES, `summary-${arm}-${Date.now()}.json`), JSON.stringify(summary, null, 2));
  console.log(`[summary] ${summary.pass}/${summary.total} pass`);
}

async function runBaseline(task, runIndex) {
  const started = Date.now();
  const work = prepareWork('B', task, runIndex);
  let outcome = { ok: false, checks: [], error: 'not implemented' };
  try {
    outcome = await BASELINES[task]({ work });
  } catch (exception) {
    outcome = { ok: false, checks: [], error: String(exception?.stack ?? exception) };
  }
  const record = {
    arm: 'B', task, run: runIndex, ok: Boolean(outcome.ok),
    wallMs: Date.now() - started,
    checks: outcome.checks ?? [],
    error: outcome.error ?? null,
    calls: { count: outcome.steps ?? 0, failed: 0, tools: {}, textBytes: 0, imageBytes: 0 },
    startedAt: new Date().toISOString(),
  };
  fs.writeFileSync(path.join(RES, `B-${task}-${runIndex}.json`), JSON.stringify(record, null, 2));
  console.log(`[B] ${task}#${runIndex} ${record.ok ? 'PASS' : 'FAIL'} ${record.wallMs}ms`);
  return record;
}

async function runStability() {
  await ensureBuilt();
  ensureDirs();
  await cleanupRuntime();
  await setupFixtures();
  const sessions = [];
  const sessionCount = Number(arg('sessions', 50));
  for (let i = 1; i <= sessionCount; i++) {
    const started = Date.now();
    const client = new McpClient(EXE);
    let ok = false;
    let code = null;
    try {
      await client.start();
      const env = await client.call('get_environment', {}, 60000);
      const shot = await client.call('capture_screen', {}, 60000);
      const release = await client.call('release_environment', { keep_display: true }, 60000);
      ok = env.ok && shot.ok && release.ok;
      if (!ok) code = env.code ?? shot.code ?? release.code;
    } catch (exception) {
      code = String(exception?.message ?? exception);
    }
    await client.stop();
    sessions.push({ index: i, ok, code, ms: Date.now() - started });
    if (i % 10 === 0) console.log(`[stability] sessions ${i}/${sessionCount}`);
  }

  const faults = [];
  for (const [name, fault] of Object.entries(FAULTS)) {
    await cleanupRuntime();
    const record = await fault();
    faults.push({ name, ...record });
    console.log(`[fault] ${name} ${record.ok ? 'PASS' : 'FAIL'} ${record.detail ?? ''}`);
  }
  await cleanupRuntime();

  const summary = {
    sessions: {
      total: sessions.length,
      pass: sessions.filter((s) => s.ok).length,
      durations: stats(sessions.map((s) => s.ms)),
      failures: sessions.filter((s) => !s.ok),
    },
    faults: {
      total: faults.length,
      pass: faults.filter((f) => f.ok).length,
      items: faults,
    },
  };
  fs.writeFileSync(path.join(RES, `stability-${Date.now()}.json`), JSON.stringify(summary, null, 2));
  console.log(`[stability] sessions ${summary.sessions.pass}/${summary.sessions.total}, faults ${summary.faults.pass}/${summary.faults.total}`);
}

async function main() {
  if (has('report')) {
    const { generateReport } = await import('./report.mjs');
    await generateReport();
    return;
  }
  if (has('stability')) { await runStability(); return; }
  await runSuite();
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
