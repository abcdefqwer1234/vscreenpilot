/**
 * 发布前守卫：确保 npm 目录里真的有可运行的二进制和许可证。
 *
 * 背景：dist/ 与 LICENSE 由 scripts/build-npm.mjs 生成，且被 .gitignore 忽略。
 * 如果直接 npm publish，会发出一个只有 4KB、没有 exe 的空包。
 * 该脚本由 package.json 的 prepublishOnly 调用，发布失败优于发坏包。
 */
import { existsSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const npmDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const pkg = JSON.parse(
  await (await import('node:fs/promises')).readFile(path.join(npmDir, 'package.json'), 'utf8')
);

const failures = [];

function requireFile(relative, minBytes = 1) {
  const full = path.join(npmDir, relative);

  if (!existsSync(full)) {
    failures.push(`缺少 ${relative}（先运行 scripts/build-npm.ps1）`);
    return;
  }

  const size = statSync(full).size;

  if (size < minBytes) {
    failures.push(`${relative} 只有 ${size} 字节，低于预期 ${minBytes}`);
  }
}

if (pkg.name !== 'vscreenpilot') {
  failures.push(`package.name 应为 vscreenpilot，实际 ${pkg.name}`);
}

if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(pkg.version ?? '')) {
  failures.push(`package.version 非法: ${pkg.version}`);
}

if (!/^io\.github\.[a-z0-9-]+\/[a-z0-9._-]+$/.test(pkg.mcpName ?? '')) {
  failures.push(`package.mcpName 非法或缺失: ${pkg.mcpName}`);
}

const repoOwner = /github\.com[/:]([^/]+)\/([^/.]+)/.exec(pkg.repository?.url ?? '')?.[1];
const mcpOwner = /^io\.github\.([^/]+)\//.exec(pkg.mcpName ?? '')?.[1];

if (repoOwner && mcpOwner && repoOwner.toLowerCase() !== mcpOwner.toLowerCase()) {
  failures.push(`mcpName 的 owner (${mcpOwner}) 与 repository (${repoOwner}) 不一致`);
}

// 自包含单文件 exe 约 60MB；20MB 足以发现“只复制了 dll/空文件”的错误。
requireFile('dist/win32-x64/VScreenPilot.exe', 20 * 1024 * 1024);
requireFile('LICENSE', 100);
requireFile('README.md', 100);
requireFile('THIRD-PARTY-NOTICES.md', 100);
requireFile('bin/vscreenpilot.js', 100);
requireFile('scripts/install-driver.ps1', 100);

if (failures.length > 0) {
  console.error('[fail] npm 包不完整，拒绝发布：');
  for (const failure of failures) {
    console.error(`  - ${failure}`);
  }
  process.exit(1);
}

const exeMb = (statSync(path.join(npmDir, 'dist/win32-x64/VScreenPilot.exe')).size / 1048576).toFixed(1);
console.log(`[ok] npm 包完整：${pkg.name}@${pkg.version}，exe ${exeMb} MB，mcpName ${pkg.mcpName}`);
