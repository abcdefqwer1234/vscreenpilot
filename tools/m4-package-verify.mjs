/**
 * M4 npm 单包验证：
 * 1) tgz 安装到临时目录，检查 exe、launcher、LICENSE、驱动安装脚本；
 * 2) --version / --help / --doctor；
 * 3) 非 Windows 平台路由；
 * 4) 通过启动器跑一次 MCP initialize + tools/list + get_environment + release + EOF。
 */
import { spawn, execFileSync } from 'node:child_process';
import { mkdtempSync, existsSync, readdirSync, rmSync, statSync, writeFileSync, mkdirSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';

const root = process.cwd();
const packDir = path.join(root, 'artifacts', 'npm');
const tgz = process.argv[2] ?? findPack();

const log = [];
const results = [];
const record = (label, value) => {
  log.push(`--- ${label} ---`);
  log.push(typeof value === 'string' ? value : JSON.stringify(value, null, 2));
};
const check = (name, ok, detail) => {
  results.push({ name, ok, detail });
  log.push(`[TEST] ${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' :: ' + detail : ''}`);
};

function findPack() {
  if (!existsSync(packDir)) return null;
  const files = readdirSync(packDir)
    .filter((name) => /^vscreenpilot-.*\.tgz$/.test(name))
    .map((name) => path.join(packDir, name))
    .sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs);
  return files[0] ?? null;
}

function runNpm(args, cwd, options = {}) {
  return execFileSync('npm', args, {
    cwd,
    encoding: 'utf8',
    shell: true,
    windowsHide: true,
    timeout: 300000,
    ...options,
  });
}

function callOnce(command, args, env = process.env) {
  try {
    const stdout = execFileSync(command, args, {
      encoding: 'utf8',
      windowsHide: true,
      timeout: 60000,
      env,
    });
    return { code: 0, stdout, stderr: '' };
  } catch (error) {
    return {
      code: error.status ?? 1,
      stdout: error.stdout?.toString() ?? '',
      stderr: error.stderr?.toString() ?? String(error.message ?? error),
    };
  }
}

function connect(launcher, env) {
  const child = spawn(process.execPath, [launcher], {
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true,
    env,
  });
  const reader = readline.createInterface({ input: child.stdout });
  const pending = new Map();
  let nextId = 1;
  const stderr = [];

  reader.on('line', (line) => {
    if (!line.trim()) return;
    let message;
    try { message = JSON.parse(line); } catch { return; }
    if (message.id !== undefined && pending.has(message.id)) {
      pending.get(message.id)(message);
      pending.delete(message.id);
    }
  });
  child.stderr.on('data', (data) => stderr.push(data.toString()));

  function request(method, params = {}, timeoutMs = 60000) {
    const id = nextId++;
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('timeout ' + method)), timeoutMs);
      pending.set(id, (response) => { clearTimeout(timer); resolve(response); });
    });
  }

  async function call(name, args = {}, timeoutMs = 60000) {
    const response = await request('tools/call', { name, arguments: args }, timeoutMs);
    if (response.error) return { rpcError: response.error };
    const text = response.result?.content?.find((item) => item.type === 'text');
    return text ? JSON.parse(text.text) : response.result;
  }

  return { child, request, call, stderr };
}

if (!tgz || !existsSync(tgz)) {
  console.error('usage: node m4-package-verify.mjs [vscreenpilot-0.1.0.tgz]');
  process.exit(1);
}

const temp = mkdtempSync(path.join(os.tmpdir(), 'vsp-npm-'));
const launcher = path.join(temp, 'node_modules', 'vscreenpilot', 'bin', 'vscreenpilot.js');
const exe = path.join(temp, 'node_modules', 'vscreenpilot', 'dist', 'win32-x64', 'VScreenPilot.exe');

(async () => {
  try {
    record('pack', { file: tgz, size_mb: +(statSync(tgz).size / 1048576).toFixed(1) });
    check('tgz exists', existsSync(tgz), tgz);

    runNpm(['install', tgz, '--no-audit', '--no-fund', '--loglevel=error'], temp, { stdio: 'pipe' });
    const license = path.join(temp, 'node_modules', 'vscreenpilot', 'LICENSE');
    const installDriver = path.join(
      temp, 'node_modules', 'vscreenpilot', 'scripts', 'install-driver.ps1');
    record('files', {
      launcher: existsSync(launcher),
      exe: existsSync(exe),
      license: existsSync(license),
      install_driver: existsSync(installDriver),
      exe_mb: existsSync(exe) ? +(statSync(exe).size / 1048576).toFixed(1) : null,
    });
    check('launcher installed', existsSync(launcher), launcher);
    check('exe installed', existsSync(exe), exe ? statSync(exe).size + ' bytes' : 'missing');
    check('LICENSE packaged', existsSync(license), license);
    check('install-driver packaged', existsSync(installDriver), installDriver);

    const builtExe = path.join(root, 'artifacts', 'server', 'VScreenPilot.exe');
    check('packaged exe matches build',
      existsSync(builtExe) && existsSync(exe) && statSync(exe).size === statSync(builtExe).size,
      `${existsSync(exe) ? statSync(exe).size : 'missing'} vs ${existsSync(builtExe) ? statSync(builtExe).size : 'missing'}`);

    const version = callOnce(process.execPath, [launcher, '--version']);
    record('version', version);
    check('--version via launcher', version.code === 0 && version.stdout.includes('0.1.0'), version.stdout.trim());

    const help = callOnce(process.execPath, [launcher, '--help']);
    record('help', help);
    check('--help via launcher',
      help.code === 0 && help.stdout.includes('用法') && help.stdout.includes('--allow-script'),
      help.stdout.slice(0, 80));

    const binShim = path.join(temp, 'node_modules', '.bin', process.platform === 'win32'
      ? 'vscreenpilot.cmd'
      : 'vscreenpilot');
    const shimVersion = process.platform === 'win32'
      ? callOnce('cmd.exe', ['/c', binShim, '--version'])
      : callOnce(binShim, ['--version']);
    record('npm-bin-shim', shimVersion);
    check('npm bin shim works', existsSync(binShim) && shimVersion.code === 0 &&
      shimVersion.stdout.includes('0.1.0'), `${binShim} -> ${shimVersion.stdout.trim()}`);

    const doctor = callOnce(process.execPath, [launcher, '--doctor']);
    let doctorJson = null;
    try { doctorJson = JSON.parse(doctor.stdout); } catch { /* ��¼ԭ�� */ }
    record('doctor', doctorJson ?? doctor);
    check('--doctor returns json', doctorJson !== null && doctorJson.version === '0.1.0', doctor.stdout.slice(0, 120));
    check('driver ready', doctorJson?.driver?.status === 'Ok', doctorJson?.driver?.status);

    const unsupported = callOnce(process.execPath, [launcher, '--doctor'], {
      ...process.env,
      VSP_TEST_PLATFORM: 'darwin',
      VSP_TEST_ARCH: 'arm64',
    });
    record('unsupported', unsupported);
    check('unsupported platform exit 78', unsupported.code === 78, 'exit=' + unsupported.code);
    check('unsupported platform message',
      unsupported.stderr.includes('E_UNSUPPORTED_PLATFORM') && unsupported.stderr.includes('darwin-arm64'),
      unsupported.stderr.trim());

    const client = connect(launcher, process.env);
    const init = await client.request('initialize', {
      protocolVersion: '2025-06-18',
      capabilities: {},
      clientInfo: { name: 'm4-package', version: '1' },
    });
    client.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} }) + '\n');
    record('initialize', init.result);
    const tools = await client.request('tools/list', {});
    const toolCount = tools.result?.tools?.length ?? 0;
    check('tools/list 18', toolCount === 18, `${toolCount} tools`);

    const env = await client.call('get_environment');
    record('env', {
      enabled: env.virtual_display?.enabled,
      owned: env.virtual_display?.owned,
      monitors: env.virtual_screen?.monitors,
      memory: env.session?.working_set_mb,
    });
    check('virtual display mounted', env.virtual_display?.enabled === true, `monitors=${env.virtual_screen?.monitors}`);

    const script = await client.call('run_script', { code: 'Write-Output hidden' });
    record('run-script-default', script);
    check('run_script disabled by default', script?.code === 'E_DISABLED', script?.code);

    const release = await client.call('release_environment', { keep_display: false, on_dirty: 'force' });
    record('release', release);
    check('release removes display', release.display_removed === true, `removed=${release.display_removed}`);
    check('release reports memory trim', typeof release.memory?.after_mb === 'number',
      `${release.memory?.before_mb} -> ${release.memory?.after_mb}`);

    client.child.stdin.end();
    const exitCode = await new Promise((resolve) => {
      const timer = setTimeout(() => resolve('timeout'), 10000);
      client.child.on('exit', (code) => { clearTimeout(timer); resolve(code); });
    });
    check('EOF exit 0', exitCode === 0, `exit=${exitCode}`);
    if (client.stderr.length > 0) record('launcher stderr', client.stderr.join(''));
  } catch (error) {
    log.push('[FATAL] ' + (error?.stack ?? error));
    results.push({ name: 'fatal', ok: false, detail: String(error?.message ?? error) });
  } finally {
    try { rmSync(temp, { recursive: true, force: true }); } catch { /* ���� */ }
    const pass = results.filter((item) => item.ok).length;
    log.push(`\nSUMMARY pass=${pass}/${results.length}`);
    mkdirSync(path.join(root, 'docs'), { recursive: true });
    writeFileSync(path.join(root, 'docs', 'm4-package.log'), log.join('\n'));
    console.log(log.join('\n'));
    process.exit(pass === results.length ? 0 : 1);
  }
})();
