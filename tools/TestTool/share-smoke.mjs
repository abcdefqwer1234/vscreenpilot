/**
 * 多客户端共享冒烟：不需要虚拟屏，不注入输入。
 * 覆盖：第二个 MCP 进程代理、c0/c1 身份、30 工具、--cli env/status/stop、
 * 首个客户端 stdin EOF 后第二个客户端仍可用。
 */
import { execFile } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { promisify } from 'node:util';
import { McpClient } from './lib/client.mjs';
import { EXE } from './lib/util.mjs';

const exec = promisify(execFile);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const profile = `share-smoke-${process.pid}`;
const env = { VSP_PROFILE: profile, VSP_NO_WARMUP: '1', VSP_DAEMON_IDLE_MS: '1000' };
let failed = 0;

function check(name, condition, detail = '') {
  if (condition) {
    console.log(`[ok] ${name}`);
  } else {
    failed++;
    console.error(`[FAIL] ${name}${detail ? `: ${detail}` : ''}`);
  }
}

const a = new McpClient(EXE, { env });
const b = new McpClient(EXE, { env });

try {
  await a.start();
  await b.start();

  const listA = await a.request('tools/list', {});
  const listB = await b.request('tools/list', {});
  check('A 返回 30 工具', listA.result?.tools?.length === 30, String(listA.result?.tools?.length));
  check('B 返回 30 工具', listB.result?.tools?.length === 30, String(listB.result?.tools?.length));

  const envA = await a.call('get_environment', {});
  const envB = await b.call('get_environment', {});
  check('A 环境成功', envA.ok, envA.code ?? '');
  check('B 环境成功', envB.ok, envB.code ?? '');
  check('A/B 使用同一会话', envA.data?.session?.pid === envB.data?.session?.pid,
    `${envA.data?.session?.pid}/${envB.data?.session?.pid}`);
  check('A/B 同一 virtual_display', JSON.stringify(envA.data?.virtual_display) === JSON.stringify(envB.data?.virtual_display));
  check(
    'A/B 有独立 client_id',
    Boolean(envA.data?.client_id) &&
    Boolean(envB.data?.client_id) &&
    envA.data.client_id !== envB.data.client_id,
    `${envA.data?.client_id}/${envB.data?.client_id}`,
  );
  check(
    'session.clients 包含 A/B',
    envB.data?.session?.clients?.length === 2,
    JSON.stringify(envB.data?.session?.clients ?? null),
  );
  check(
    'session.screen_lease 存在',
    Boolean(envB.data?.session?.screen_lease),
    JSON.stringify(envB.data?.session?.screen_lease ?? null),
  );

  const bLog = await b.call('get_log', { tail: 50 });
  check(
    'B get_log 默认不返回 c0 条目',
    bLog.ok && (bLog.data?.entries ?? []).every((entry) => entry.client_id !== 'c0'),
    JSON.stringify(bLog.data?.entries ?? null),
  );

  const cliEnv = await exec(EXE, ['--cli', 'env'], {
    env: { ...process.env, ...env },
    timeout: 20000,
  });
  const parsed = JSON.parse(cliEnv.stdout);
  check(
    '--cli env 查询活跃实例',
    parsed.ok === true && parsed.session?.pid > 0,
    cliEnv.stdout.slice(0, 120),
  );

  const cliEnvEnsure = await exec(EXE, ['--cli', 'env', '--ensure'], {
    env: { ...process.env, ...env },
    timeout: 20000,
  });
  const ensured = JSON.parse(cliEnvEnsure.stdout);
  check('--cli env --ensure 明确标记只读忽略', ensured.ensure_ignored === true,
    cliEnvEnsure.stdout.slice(0, 160));

  const cliSnapshot = await exec(EXE, ['--cli', 'snapshot'], {
    env: { ...process.env, ...env },
    timeout: 20000,
  });
  const snapshot = JSON.parse(cliSnapshot.stdout);
  check(
    '--cli snapshot 返回管理员 apps[]',
    snapshot.ok === true && Array.isArray(snapshot.apps) && snapshot.count === 0,
    cliSnapshot.stdout.slice(0, 160),
  );

  const cliStatus = await exec(EXE, ['--cli', 'status'], {
    env: { ...process.env, ...env },
    timeout: 20000,
  });
  const status = JSON.parse(cliStatus.stdout);
  check('--cli status 返回 daemon', status.ctl_pipe?.length > 0, cliStatus.stdout.slice(0, 120));
  check('--cli status 包含 screen_lease/display', Boolean(status.screen_lease && status.display));
  check(
    '--cli status 包含 client_list',
    Array.isArray(status.client_list) && status.client_list.length === 2,
    JSON.stringify(status.client_list ?? null),
  );

  let stopWithoutForce = '';
  try {
    await exec(EXE, ['--cli', 'stop'], { env: { ...process.env, ...env }, timeout: 20000 });
  } catch (error) {
    stopWithoutForce = `${error.stdout ?? ''}${error.stderr ?? ''}`;
  }
  check(
    '有活跃客户端时 --cli stop 被拒绝',
    stopWithoutForce.includes('E_CLIENTS_ACTIVE'),
    stopWithoutForce.slice(0, 160),
  );

  // 首个 MCP 客户端 stdin EOF 后，进程应作为 daemon 继续服务 B。
  a.child.stdin.end();
  await sleep(500);
  const listAfter = await b.request('tools/list', {}, 10000);
  check('A EOF 后 B 仍可用', listAfter.result?.tools?.length === 30, String(listAfter.result?.tools?.length));

  // 最后一个客户端断开后，daemon 应在 VSP_DAEMON_IDLE_MS 内退出。
  await b.stop();
  await sleep(1800);

  let idleStatus = '';
  try {
    await exec(EXE, ['--cli', 'status'], { env: { ...process.env, ...env }, timeout: 10000 });
  } catch (error) {
    idleStatus = `${error.stdout ?? ''}${error.stderr ?? ''}`;
  }
  check(
    'daemon 空闲后退出',
    idleStatus.includes('E_NO_INSTANCE'),
    idleStatus.slice(0, 160),
  );
} finally {
  try { await b.stop(); } catch {}
  try {
    await exec(EXE, ['--cli', 'stop', '--force'], { env: { ...process.env, ...env }, timeout: 10000 });
  } catch {}
  try { a.child.kill(); } catch {}

  // 清掉本次唯一 profile 的 registry/state/artifacts，避免测试残留。
  const local = process.env.LOCALAPPDATA;
  if (local) {
    const root = path.join(local, 'VScreenPilot');
    for (const target of [
      path.join(root, 'instances', `${profile}.json`),
      path.join(root, `state.${profile}.json`),
      path.join(root, 'artifacts', profile),
    ]) {
      try { fs.rmSync(target, { recursive: true, force: true }); } catch {}
    }
  }
}

if (failed > 0) {
  console.error(`\n${failed} 项失败`);
  process.exit(1);
}

console.log('\n多客户端共享冒烟通过');
