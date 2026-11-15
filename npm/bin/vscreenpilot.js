#!/usr/bin/env node
'use strict';

// 极薄启动器：只做平台路由、定位原生二进制、透传 stdio。
// 不在这里做任何自动化逻辑，stdout 必须保持纯净供 MCP 使用。

const { spawn } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const pkg = require('../package.json');

function fail(code, message) {
  process.stderr.write(JSON.stringify({ code, message }) + '\n');
  process.exit(78);
}

const args = process.argv.slice(2);

if (args.includes('--help') || args.includes('-h')) {
  process.stdout.write(
    [
      `vscreenpilot ${pkg.version}`,
      '',
      '用法:',
      '  vscreenpilot [--allow-script] [--parent-pid <pid>] [--profile <name>]',
      '  vscreenpilot --doctor                                输出 JSON 诊断',
      '  vscreenpilot --version                               输出版本',
      '  vscreenpilot --help                                  输出本帮助',
      '',
      '选项:',
      '  --allow-script            启用 run_script（默认关闭）',
      '  --parent-pid <pid>        父进程退出时自动清理',
      '  --profile <name>          多会话隔离（互斥体/状态/追踪目录）',
      '',
      '环境变量:',
      '  VSP_ALLOW_SCRIPT=1        等价于 --allow-script',
      '  VSP_APP_ALLOWLIST=dir1;dir2  仅允许启动这些目录下的 exe（默认允许全部）',
      '',
      '首次使用前请安装 Parsec VDD：',
      '  npm run install-driver    （在包目录内）',
      '',
    ].join('\n')
  );
  process.exit(0);
}

if (args.includes('--version') || args.includes('-v')) {
  process.stdout.write(`vscreenpilot ${pkg.version}\n`);
  process.exit(0);
}

const platform = process.env.VSP_TEST_PLATFORM || process.platform;
const arch = process.env.VSP_TEST_ARCH || process.arch;

if (platform !== 'win32' || arch !== 'x64') {
  fail(
    'E_UNSUPPORTED_PLATFORM',
    `VScreenPilot 目前只支持 win32-x64，当前平台是 ${platform}-${arch}。` +
      '请在 Windows 10/11 x64 上运行，或改用远程 Windows 主机。'
  );
}

const exe = path.join(__dirname, '..', 'dist', 'win32-x64', 'VScreenPilot.exe');

if (!fs.existsSync(exe)) {
  fail(
    'E_BINARY_MISSING',
    `未找到原生二进制: ${exe}。请在仓库根目录运行 scripts/build-npm.ps1 重新打包。`
  );
}

// 服务端通过父进程句柄感知启动器退出，确保宿主关闭后仍能清理虚拟屏。
const hasParentPid = args.some(
  (arg) => arg === '--parent-pid' || arg.startsWith('--parent-pid=')
);
if (!hasParentPid) {
  args.push('--parent-pid', String(process.pid));
}

const child = spawn(exe, args, {
  stdio: 'inherit',
  windowsHide: true,
});

child.on('error', (error) => {
  fail('E_SPAWN_FAILED', `启动 VScreenPilot.exe 失败: ${error.message}`);
});

// 不拦截 SIGINT/SIGTERM：控制台信号会同时送达子进程；若宿主只杀启动器，
// 子进程的 --parent-pid 监听会触发统一清理。
child.on('exit', (code, signal) => {
  process.exit(code === null ? (signal ? 1 : 0) : code);
});
