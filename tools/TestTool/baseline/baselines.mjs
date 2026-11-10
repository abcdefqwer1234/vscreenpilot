import { execFile } from 'node:child_process';
import path from 'node:path';
import { TESTTOOL } from '../lib/util.mjs';

const SCRIPT = path.join(TESTTOOL, 'baseline', 'baseline.ps1');

function runPowerShell(task, work) {
  return new Promise((resolve) => {
    execFile('powershell', [
      '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-STA',
      '-File', SCRIPT, '-Task', task, '-Work', work,
    ], { timeout: 180000, windowsHide: true, maxBuffer: 4 * 1024 * 1024 }, (error, stdout, stderr) => {
      const text = `${stdout}\n${stderr}`;
      const ok = /RESULT:OK/.test(text);
      const steps = Number(/steps=(\d+)/.exec(text)?.[1] ?? 0);
      resolve({
        ok,
        steps,
        stdout: stdout.slice(-800),
        stderr: stderr.slice(-400),
        checks: [{ name: 'baseline', ok, detail: (stdout.match(/RESULT:[^\r\n]*/)?.[0] ?? stderr.slice(0, 200)) }],
        error: ok ? null : (stderr.slice(0, 300) || 'baseline failed'),
      });
    });
  });
}

export const BASELINES = {
  t1: ({ work }) => runPowerShell('t1', work),
  t4: ({ work }) => runPowerShell('t4', work),
};
