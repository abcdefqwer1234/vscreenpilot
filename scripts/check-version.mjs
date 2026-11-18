/**
 * 版本一致性检查：csproj / winget / Inno Setup / BuildInfo / CHANGELOG。
 * 用法：node scripts/check-version.mjs [--tag v0.1.0]
 */
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const failures = [];
const read = (relative) => readFileSync(path.join(root, relative), 'utf8');

const csproj = /<Version>([^<]+)<\/Version>/.exec(read('src/VScreenPilot.Server/VScreenPilot.Server.csproj'))?.[1];
const buildInfo = /Version\s*=\s*"([^"]+)"/.exec(
  read('src/VScreenPilot.Server/Infrastructure/BuildInfo.cs'))?.[1];
const installer = /#define\s+MyAppVersion\s+"([^"]+)"/.exec(
  read('packaging/installer/VScreenPilot.iss'))?.[1];

const wingetVersion = read('packaging/winget/VScreenPilot.VScreenPilot.yaml');
const wingetLocale = read('packaging/winget/VScreenPilot.VScreenPilot.locale.en-US.yaml');
const wingetInstaller = read('packaging/winget/VScreenPilot.VScreenPilot.installer.yaml');

const versions = {
  'VScreenPilot.Server.csproj': csproj,
  'BuildInfo.cs': buildInfo,
  'Inno Setup script': installer,
  'winget version': /^PackageVersion:\s*(\S+)/m.exec(wingetVersion)?.[1],
  'winget locale': /^PackageVersion:\s*(\S+)/m.exec(wingetLocale)?.[1],
  'winget installer': /^PackageVersion:\s*(\S+)/m.exec(wingetInstaller)?.[1],
};

const expected = csproj;

for (const [file, version] of Object.entries(versions)) {
  if (version !== expected) {
    failures.push(`${file} 版本 ${version} 与 csproj ${expected} 不一致`);
  }
}

const displayVersion = /^\s+DisplayVersion:\s*(\S+)/m.exec(wingetInstaller)?.[1];
if (displayVersion !== expected) {
  failures.push(`winget DisplayVersion ${displayVersion} 与 ${expected} 不一致`);
}

const installerUrl = /^\s+InstallerUrl:\s*(\S+)/m.exec(wingetInstaller)?.[1];
const expectedInstaller = `VScreenPilot-Setup-${expected}.exe`;
if (!installerUrl || !installerUrl.includes(`/v${expected}/`) || !installerUrl.endsWith(expectedInstaller)) {
  failures.push(`winget InstallerUrl 不是 /v${expected}/${expectedInstaller}: ${installerUrl}`);
}

const tagIndex = process.argv.indexOf('--tag');
if (tagIndex >= 0) {
  const tag = process.argv[tagIndex + 1] ?? '';
  if (tag !== `v${expected}`) {
    failures.push(`git tag ${tag} 与版本 v${expected} 不一致（先改版本或重打 tag）`);
  }
}

if (!read('CHANGELOG.md').includes(`## ${expected}`)) {
  failures.push(`CHANGELOG.md 缺少 ${expected} 段落`);
}

if (failures.length > 0) {
  console.error('[fail] 版本检查失败：');
  for (const failure of failures) {
    console.error(`  - ${failure}`);
  }
  process.exit(1);
}

console.log(`[ok] 版本一致：${expected}（tag 检查${tagIndex >= 0 ? '已启用' : '未启用'}）`);
