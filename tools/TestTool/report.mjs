import fs from 'node:fs';
import path from 'node:path';
import { RES, ROOT } from './lib/util.mjs';

const TASK_NAMES = {
  t1: 'Notepad 保存', t2: '计算器运算', t3: 'Explorer 文件操作', t4: 'WinForms 表单',
  t5: '视觉模板匹配', t6: 'Word 替换保存', t7: 'Excel 单元格写入', t8: '故障恢复',
};

function readJsonFiles(prefix) {
  if (!fs.existsSync(RES)) return [];
  return fs.readdirSync(RES)
    .filter((name) => name.startsWith(prefix) && name.endsWith('.json'))
    .map((name) => {
      try { return JSON.parse(fs.readFileSync(path.join(RES, name), 'utf8')); } catch { return null; }
    })
    .filter(Boolean);
}

function quantile(values, q) {
  if (!values.length) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.floor(q * sorted.length))];
}

function fmt(value, digits = 0) {
  return Number(value ?? 0).toFixed(digits);
}

export async function generateReport() {
  const aRuns = readJsonFiles('A-');
  const bRuns = readJsonFiles('B-');
  const stabilityFiles = readJsonFiles('stability-');
  const stability = stabilityFiles.at(-1) ?? null;

  const taskRows = [];
  for (const task of Object.keys(TASK_NAMES)) {
    const runs = aRuns.filter((run) => run.task === task);
    if (!runs.length) continue;
    const pass = runs.filter((run) => run.ok).length;
    const wall = runs.map((run) => run.wallMs);
    const calls = runs.map((run) => run.calls.count);
    const failedChecks = runs.flatMap((run) => (run.checks ?? []).filter((c) => !c.ok).map((c) => `${c.name}:${c.detail ?? ''}`));
    taskRows.push({
      task, name: TASK_NAMES[task], runs: runs.length, pass,
      rate: `${pass}/${runs.length}`,
      wallP50: quantile(wall, 0.5), wallP95: quantile(wall, 0.95),
      callsP50: quantile(calls, 0.5),
      failedCalls: runs.reduce((a, run) => a + run.calls.failed, 0),
      cursorMoves: runs.reduce((a, run) => a + (run.samples?.cursorMoves ?? 0), 0),
      peakRssMb: Math.max(...runs.map((run) => run.samples?.peakRss ?? 0)) / 1024 / 1024,
      failures: [...new Set(failedChecks)].slice(0, 4),
    });
  }

  const toolLatency = {};
  for (const run of aRuns) {
    for (const call of run.callLog ?? []) {
      (toolLatency[call.name] ??= []).push(call.ms);
    }
  }
  const toolRows = Object.entries(toolLatency)
    .map(([name, values]) => ({
      name, count: values.length,
      p50: quantile(values, 0.5), p95: quantile(values, 0.95), max: Math.max(...values),
    }))
    .sort((a, b) => b.count - a.count);

  const bRows = [];
  for (const task of ['t1', 't2', 't3', 't4']) {
    const runs = bRuns.filter((run) => run.task === task);
    if (!runs.length) continue;
    bRows.push({
      task, name: TASK_NAMES[task], runs: runs.length,
      pass: runs.filter((run) => run.ok).length,
      wallP50: quantile(runs.map((run) => run.wallMs), 0.5),
      stepsP50: quantile(runs.map((run) => run.calls.count), 0.5),
    });
  }

  const totalA = aRuns.length;
  const passA = aRuns.filter((run) => run.ok).length;
  const totalB = bRuns.length;
  const passB = bRuns.filter((run) => run.ok).length;

  const pairs = ['t1', 't2', 't3', 't4'].map((task) => {
    const a = aRuns.filter((run) => run.task === task);
    const b = bRuns.filter((run) => run.task === task);
    if (!a.length || !b.length) return null;
    const aw = quantile(a.map((run) => run.wallMs), 0.5);
    const bw = quantile(b.map((run) => run.wallMs), 0.5);
    return { task, a: aw, b: bw, ratio: bw ? aw / bw : 0, aPass: a.filter((r) => r.ok).length, bPass: b.filter((r) => r.ok).length };
  }).filter(Boolean);

  const lines = [];
  lines.push('# VScreenPilot 自动化测试报告');
  lines.push('');
  lines.push(`> 生成时间：${new Date().toISOString()}`);
  lines.push('> 方案：[测试方案.md](测试方案.md)（v2）');
  lines.push('> 说明：AI 全自动执行，oracle 独立于被测工具；原始数据在报告生成后清理，可用 `node tools/TestTool/run-all.mjs` 复现。');
  lines.push('');
  lines.push('## 0. 结论摘要');
  lines.push('');
  lines.push(`- A 臂（VScreenPilot）共 ${totalA} 次，通过 ${passA} 次，成功率 ${totalA ? fmt(passA / totalA * 100, 1) : '0'}%。`);
  lines.push(`- B 臂（PowerShell + UIA）共 ${totalB} 次，通过 ${passB} 次。`);
  lines.push(stability
    ? `- 稳定性：${stability.sessions.pass}/${stability.sessions.total} 会话成功；故障注入 ${stability.faults.pass}/${stability.faults.total}。`
    : '- 稳定性：本轮未执行或未产出数据。');
  lines.push('- token 仅统计交互字节，不含模型推理开销，不能直接换算成“节省 X%”。');
  lines.push('');

  lines.push('## 1. A 臂逐任务');
  lines.push('');
  lines.push('| 任务 | 通过 | 中位耗时 | P95 | 工具调用中位 | 失败调用 | 光标移动采样 | 峰值 RSS |');
  lines.push('| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |');
  for (const row of taskRows) {
    lines.push(`| ${row.name} | ${row.rate} | ${fmt(row.wallP50)}ms | ${fmt(row.wallP95)}ms | ${fmt(row.callsP50)} | ${row.failedCalls} | ${row.cursorMoves} | ${fmt(row.peakRssMb, 1)}MB |`);
  }
  lines.push('');
  const failing = taskRows.filter((row) => row.pass < row.runs);
  if (failing.length) {
    lines.push('失败明细：');
    for (const row of failing) lines.push(`- ${row.name}：${row.failures.join('；') || '见原始 checks'}`);
    lines.push('');
  }

  lines.push('## 2. A vs B（脚本基线）');
  lines.push('');
  lines.push('| 任务 | A 通过 | B 通过 | A 中位 | B 中位 | A/B 耗时比 |');
  lines.push('| --- | ---: | ---: | ---: | ---: | ---: |');
  for (const pair of pairs) {
    lines.push(`| ${TASK_NAMES[pair.task]} | ${pair.aPass}/3 | ${pair.bPass}/3 | ${fmt(pair.a)}ms | ${fmt(pair.b)}ms | ${fmt(pair.ratio, 2)} |`);
  }
  if (!pairs.length) lines.push('| 无 B 数据 | - | - | - | - | - |');
  lines.push('');
  lines.push('B 臂由 PowerShell + 内置 UIAutomation 实现，包含脚本编写后的直接执行时间，不含模型写脚本的思考与调试成本。');
  lines.push('');

  lines.push('## 3. 工具耗时');
  lines.push('');
  lines.push('| 工具 | 调用数 | P50 | P95 | Max |');
  lines.push('| --- | ---: | ---: | ---: | ---: |');
  for (const row of toolRows.slice(0, 18)) {
    lines.push(`| \`${row.name}\` | ${row.count} | ${fmt(row.p50, 1)}ms | ${fmt(row.p95, 1)}ms | ${fmt(row.max, 1)}ms |`);
  }
  lines.push('');

  lines.push('## 4. 稳定性与故障注入');
  lines.push('');
  if (stability) {
    lines.push(`- 会话循环：${stability.sessions.pass}/${stability.sessions.total}，P50 ${fmt(stability.sessions.durations.p50)}ms，P95 ${fmt(stability.sessions.durations.p95)}ms。`);
    lines.push('');
    lines.push('| 故障 | 结果 | 详情 |');
    lines.push('| --- | --- | --- |');
    for (const item of stability.faults.items) {
      lines.push(`| ${item.name} | ${item.ok ? 'PASS' : 'FAIL'} | ${String(item.detail ?? '').replace(/\|/g, '/')} |`);
    }
  } else {
    lines.push('未执行。');
  }
  lines.push('');

  lines.push('## 5. 逐问题判定');
  lines.push('');
  lines.push(`- Q1 实用性：A 臂 ${passA}/${totalA}，详见第 1 节；未达预注册 7/8 时可判定为“部分可用”。`);
  lines.push(`- Q2 相比写脚本：见第 2 节。A 臂工具调用次数、耗时、失败重试均低于脚本基线时，可认为在“操作层”更省；token 未做精确统计。`);
  lines.push('- Q3 社区对比：本机无 uv/Python 3.14，未实跑 Windows-MCP；其公开资料给出的动作延迟 0.2–0.5s/步，VScreenPilot 的 P95 见第 3 节。');
  lines.push('- Q4 稳定性：见第 4 节；会话循环与故障注入全部通过时判定为稳定。');
  lines.push('- Q5 其他问题：见第 1 节失败明细与第 4 节失败项。');
  lines.push('');

  lines.push('## 6. 局限');
  lines.push('');
  lines.push('- 同一模型设计与执行，存在自评偏差；');
  lines.push('- token 为交互字节代理，非 API usage；');
  lines.push('- 单机、单版本、未覆盖浏览器 DOM 与多智能体并发；');
  lines.push('- B 臂只覆盖 4 个任务，且不含脚本编写阶段的 token。');

  const target = path.join(ROOT, '测试报告.md');
  fs.writeFileSync(target, lines.join('\n'), 'utf8');
  console.log(`[report] wrote ${target}`);
  return { target, taskRows, pairs, stability };
}
