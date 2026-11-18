/**
 * 独立 daemon 冒烟：daemon 崩溃时所有 proxy 退出 3；首个 proxy 被杀后第二个客户端仍可用。
 * 不需要虚拟屏/输入。运行前先 publish artifacts/server。
 */
import { execFile } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { promisify } from 'node:util';
import { McpClient } from './lib/client.mjs';

const exec = promisify(execFile);
const exe = path.resolve('artifacts/server/VScreenPilot.exe');
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
let failed = 0;

function check(name, condition, detail = '') {
  if (condition) {
    console.log(`[ok] ${name}`);
  } else {
    failed++;
    console.error(`[FAIL] ${name}${detail ? `: ${detail}` : ''}`);
  }
}

async function getStatus(env) {
  const result = await exec(exe, ['--cli', 'status'], { env: { ...process.env, ...env } });
  return JSON.parse(result.stdout);
}

async function daemonCrashCase() {
  const profile = `daemon-crash-${process.pid}`;
  const env = { VSP_PROFILE: profile, VSP_NO_WARMUP: '1' };
  const a = new McpClient(exe, { env });
  const b = new McpClient(exe, { env });

  try {
    await a.start();
    await b.start();
    const status = await getStatus(env);
    check('daemon crash: 两个客户端已连接', status.clients === 2, String(status.clients));

    process.kill(status.pid);
    const exit = await Promise.race([b.exit, sleep(8000).then(() => 'timeout')]);
    check('daemon crash: B 退出 3', exit === 3, String(exit));
    check('daemon crash: B 有明确 stderr', b.stderr.includes('共享实例已断开'), b.stderr);
  } finally {
    try { await a.stop(); } catch {}
    try { await b.stop(); } catch {}
  }
}

async function proxyCrashCase() {
  const profile = `daemon-proxy-${process.pid}`;
  const env = { VSP_PROFILE: profile, VSP_NO_WARMUP: '1' };
  const a = new McpClient(exe, { env });
  const b = new McpClient(exe, { env });

  try {
    await a.start();
    await b.start();
    a.child.kill();
    await sleep(1000);
    const listed = await b.request('tools/list', {}, 5000).catch(() => null);
    check('proxy crash: B 仍可用', listed?.result?.tools?.length === 22,
      String(listed?.result?.tools?.length));
  } finally {
    try { await a.stop(); } catch {}
    try { await b.stop(); } catch {}
  }
}

try {
  await daemonCrashCase();
  await proxyCrashCase();
} catch (error) {
  failed++;
  console.error(error);
}

const local = process.env.LOCALAPPDATA;
if (local) {
  const root = path.join(local, 'VScreenPilot');
  for (const profile of [`daemon-crash-${process.pid}`, `daemon-proxy-${process.pid}`]) {
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

console.log('\n独立 daemon 冒烟通过');
