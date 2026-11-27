/**
 * 协议/契约冒烟：不依赖虚拟屏，不需要真实桌面输入。
 * 覆盖：工具数量、schema、确定性排序、协议版本回退、structuredContent、
 * 系统键拦截、超长文本拒绝、环境隔离字段。
 */
import { McpClient } from './lib/client.mjs';
import { EXE } from './lib/util.mjs';

let failed = 0;

function check(name, condition, detail = '') {
  if (condition) {
    console.log(`[ok] ${name}`);
  } else {
    failed++;
    console.error(`[FAIL] ${name}${detail ? `: ${detail}` : ''}`);
  }
}

// 独立 profile：避免共享实例上线后把协议冒烟挂到用户真实的 default 实例上。
const profile = process.env.VSP_PROFILE ?? `smoke-v2-${process.pid}`;
const env = { VSP_NO_WARMUP: '1', VSP_PROFILE: profile, VSP_DAEMON_IDLE_MS: '1000' };

const client = new McpClient(EXE, { env });
await client.start();

const list = await client.request('tools/list', {});
const tools = list.result?.tools ?? [];
check('工具数量为 30', tools.length === 30, String(tools.length));

const names = tools.map((tool) => tool.name);
const sorted = [...names].sort((a, b) => a.localeCompare(b, 'en'));
check('工具顺序确定且按名称排序', JSON.stringify(names) === JSON.stringify(sorted), names.join(','));

check(
  '所有工具都有 outputSchema',
  tools.every((tool) => tool.outputSchema && tool.outputSchema.type === 'object'),
);

const typeText = tools.find((tool) => tool.name === 'type_text');
check(
  'type_text 默认分块为 32',
  typeText?.inputSchema?.properties?.chunk_chars?.description?.includes('32') === true,
);

const pressKeys = tools.find((tool) => tool.name === 'press_keys');
check(
  'press_keys 暴露 allow_system_keys',
  Boolean(pressKeys?.inputSchema?.properties?.allow_system_keys),
);

const envResult = await client.call('get_environment', {});
check('get_environment 成功', envResult.ok, envResult.code ?? '');
check('get_environment 有 structuredContent', Boolean(envResult.raw?.structuredContent));
check(
  'session.isolation=shared_desktop',
  envResult.data?.session?.isolation === 'shared_desktop',
  JSON.stringify(envResult.data?.session ?? {}),
);
check(
  'session.session_id 是数字',
  typeof envResult.data?.session?.session_id === 'number',
);

const blockedWinL = await client.call('press_keys', { keys: 'Win+L' });
check('Win+L 被拦截', !blockedWinL.ok && blockedWinL.code === 'E_SYSTEM_KEY_BLOCKED', blockedWinL.code ?? '');

const blockedAltTab = await client.call('press_keys', { keys: 'Alt+Tab' });
check(
  'Alt+Tab 被拦截',
  !blockedAltTab.ok && blockedAltTab.code === 'E_SYSTEM_KEY_BLOCKED',
  blockedAltTab.code ?? '',
);

const blockedWinModified = await client.call('press_keys', { keys: 'Ctrl+Win+R' });
check(
  'Ctrl+Win+R 也被拦截',
  !blockedWinModified.ok && blockedWinModified.code === 'E_SYSTEM_KEY_BLOCKED',
  blockedWinModified.code ?? '',
);

const tooLong = await client.call('type_text', { text: 'x'.repeat(100_001) });
check('超长文本被拒绝', !tooLong.ok && tooLong.code === 'E_INVALID_ARG', tooLong.code ?? '');

const log = await client.call('get_log', { tail: 2 });
check('get_log 成功', log.ok, log.code ?? '');
check('get_log 有 structuredContent', Boolean(log.raw?.structuredContent));

const filteredLog = await client.call('get_log', { tail: 2, level: 'ERROR' });
check('get_log level 过滤可用', filteredLog.ok, filteredLog.code ?? '');

const noTarget = await client.call('find_element', { name: 'anything' });
check(
  'find_element 无目标返回 E_NO_TARGET（无虚拟屏时允许 E_DRIVER）',
  !noTarget.ok && ['E_NO_TARGET', 'E_DRIVER', 'E_DRIVER_OPEN'].includes(noTarget.code),
  noTarget.code ?? '',
);

const noTree = await client.call('get_ui_tree', {});
check('get_ui_tree 无目标返回 E_NO_TARGET', !noTree.ok && noTree.code === 'E_NO_TARGET', noTree.code ?? '');

const release = await client.call('release_environment', { keep_display: true });
check('release_environment 在无显示环境下也能完成', release.ok, release.code ?? '');

const afterRelease = await client.call('get_environment', {});
check('release 后仍可继续调用', afterRelease.ok, afterRelease.code ?? '');

await client.stop();

const fallback = new McpClient(EXE, {
  env: { ...env, VSP_PROFILE: `${env.VSP_PROFILE ?? 'default'}-fallback-${process.pid}` },
  protocolVersion: '2099-01-01',
});
await fallback.start();
const init = fallback.lastInitialize ?? null;
const initError = init?.error;
const supportedVersions = initError?.data?.supported ?? [];
check(
  '未知协议版本被明确拒绝并列出支持版本',
  initError?.code === -32022 && supportedVersions.includes('2025-06-18'),
  JSON.stringify(init ?? {}),
);
await fallback.stop();

if (failed > 0) {
  console.error(`\n${failed} 项失败`);
  process.exit(1);
}

console.log('\n全部协议冒烟通过');
