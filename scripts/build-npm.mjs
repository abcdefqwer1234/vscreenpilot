/**
 * 构建 npm 单包：dotnet publish -> 复制 exe -> npm pack -> 移到 artifacts/npm。
 * 只使用 Node 内置模块；不执行 npm publish。
 *
 * 用法：node scripts/build-npm.mjs [Release|Debug]
 */
import { execFileSync, execSync } from 'node:child_process';
import {
  copyFileSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  rmSync,
  statSync,
} from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptsDir = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(scriptsDir, '..');
const configuration = process.argv[2] ?? 'Release';

const serverProject = path.join(root, 'src', 'VScreenPilot.Server', 'VScreenPilot.Server.csproj');
const publishDir = path.join(root, 'artifacts', 'server');
const npmDir = path.join(root, 'npm');
const distDir = path.join(npmDir, 'dist', 'win32-x64');
const packDir = path.join(root, 'artifacts', 'npm');

function run(command, args, options = {}) {
  execFileSync(command, args, {
    cwd: root,
    stdio: 'inherit',
    windowsHide: true,
    timeout: 600000,
    ...options,
  });
}

console.log('[..] dotnet publish');
run('dotnet', ['publish', serverProject, '-c', configuration, '-r', 'win-x64', '-o', publishDir]);

mkdirSync(distDir, { recursive: true });
mkdirSync(packDir, { recursive: true });
copyFileSync(path.join(publishDir, 'VScreenPilot.exe'), path.join(distDir, 'VScreenPilot.exe'));
console.log(`[ok] exe -> ${distDir}`);

// npm 包需要自带 LICENSE 与驱动安装脚本，否则 npx 用户无法补齐前置依赖。
const npmScripts = path.join(npmDir, 'scripts');
mkdirSync(npmScripts, { recursive: true });
copyFileSync(path.join(root, 'LICENSE'), path.join(npmDir, 'LICENSE'));
copyFileSync(
  path.join(root, 'scripts', 'install-driver.ps1'),
  path.join(npmScripts, 'install-driver.ps1')
);
console.log('[ok] LICENSE + install-driver.ps1 -> npm');

const staging = mkdtempSync(path.join(os.tmpdir(), 'vsp-npm-pack-'));
try {
  console.log('[..] npm pack');
  execSync(`npm pack --pack-destination "${staging}" --loglevel=error`, {
    cwd: npmDir,
    stdio: ['ignore', 'pipe', 'ignore'],
    windowsHide: true,
    timeout: 600000,
  });

  const staged = readdirSync(staging)
    .filter((name) => /^vscreenpilot-.*\.tgz$/.test(name))
    .map((name) => path.join(staging, name))
    .sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs)[0];

  if (!staged || !existsSync(staged)) {
    throw new Error('npm pack 未生成 tgz');
  }

  const finalPath = path.join(packDir, path.basename(staged));
  copyFileSync(staged, finalPath);
  console.log(
    `[ok] ${finalPath} (${(statSync(finalPath).size / 1048576).toFixed(1)} MB)`
  );
} finally {
  rmSync(staging, { recursive: true, force: true });
}
