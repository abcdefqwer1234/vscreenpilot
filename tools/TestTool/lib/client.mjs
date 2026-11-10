import { spawn } from 'node:child_process';
import readline from 'node:readline';

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * 最小 MCP stdio 客户端：按行收发 JSON-RPC，记录每次工具调用的耗时、错误码和字节数。
 * 供 bench 长期复用，不依赖任何第三方包。
 */
export class McpClient {
  constructor(exe, options = {}) {
    this.exe = exe;
    this.options = options;
    this.pending = new Map();
    this.nextId = 1;
    this.calls = [];
    this.notifications = [];
    this.stderr = '';
    this.child = null;
  }

  async start() {
    const args = this.options.args ?? ['--parent-pid', String(process.pid)];
    this.child = spawn(this.exe, args, {
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
      cwd: this.options.cwd,
      env: { ...process.env, ...(this.options.env ?? {}) },
    });
    this.child.stderr.on('data', (data) => { this.stderr += data.toString(); });
    const reader = readline.createInterface({ input: this.child.stdout });
    reader.on('line', (line) => {
      if (!line.trim()) return;
      let message;
      try { message = JSON.parse(line); } catch { return; }
      if (message.id !== undefined && this.pending.has(message.id)) {
        const entry = this.pending.get(message.id);
        this.pending.delete(message.id);
        entry.resolve(message);
      } else {
        this.notifications.push(message);
      }
    });
    this.exit = new Promise((resolve) => this.child.on('exit', (code) => resolve(code)));
    await this.request('initialize', {
      protocolVersion: '2025-06-18',
      capabilities: {},
      clientInfo: { name: 'vsp-bench', version: '1.0' },
    }, 30000);
    this.notify('notifications/initialized');
    return this;
  }

  request(method, params = {}, timeoutMs = 180000) {
    return this.begin(method, params, timeoutMs).promise;
  }

  begin(method, params = {}, timeoutMs = 180000) {
    const id = this.nextId++;
    this.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    const promise = new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`timeout: ${method}`));
      }, timeoutMs);
      this.pending.set(id, {
        resolve: (message) => { clearTimeout(timer); resolve(message); },
      });
    });
    return { id, promise };
  }

  notify(method, params = {}) {
    this.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');
  }

  async call(name, args = {}, timeoutMs = 180000) {
    const started = Date.now();
    let response;
    try {
      response = await this.request('tools/call', { name, arguments: args }, timeoutMs);
    } catch (error) {
      const record = {
        name, ms: Date.now() - started, ok: false, code: 'E_CLIENT_TIMEOUT',
        textBytes: 0, imageBytes: 0, error: String(error.message ?? error),
      };
      this.calls.push(record);
      return { ok: false, code: record.code, data: null, images: [], ms: record.ms, raw: null };
    }

    if (response.error) {
      const record = {
        name, ms: Date.now() - started, ok: false, code: `RPC_${response.error.code}`,
        textBytes: 0, imageBytes: 0, error: response.error.message,
      };
      this.calls.push(record);
      return { ok: false, code: record.code, data: null, images: [], ms: record.ms, raw: response };
    }

    const result = response.result ?? {};
    const content = Array.isArray(result.content) ? result.content : [];
    const images = [];
    let text = '';
    let textBytes = 0;
    let imageBytes = 0;
    for (const item of content) {
      if (item.type === 'image') {
        const bytes = Math.floor((item.data?.length ?? 0) * 3 / 4);
        imageBytes += bytes;
        images.push({ mimeType: item.mimeType, bytes, base64: item.data });
      } else if (item.type === 'text') {
        text += item.text;
        textBytes += Buffer.byteLength(item.text ?? '');
      }
    }
    let data = null;
    try { data = JSON.parse(text); } catch { data = text; }
    const ok = !result.isError;
    const code = ok ? null : (data?.code ?? 'E_UNKNOWN');
    const record = {
      name, ms: Date.now() - started, ok, code,
      textBytes, imageBytes,
      args: summarizeArgs(args),
      detail: ok ? undefined : (data?.message ?? String(data).slice(0, 200)),
    };
    this.calls.push(record);
    return { ok, code, data, images, ms: record.ms, raw: result };
  }

  async stop() {
    try { this.child.stdin.end(); } catch { /* ignore */ }
    try { await Promise.race([this.exit, sleep(5000)]); } catch { /* ignore */ }
    try { this.child.kill(); } catch { /* ignore */ }
  }
}

function summarizeArgs(args) {
  const copy = {};
  for (const [key, value] of Object.entries(args ?? {})) {
    if (key === 'value' || key === 'text') {
      copy[key] = typeof value === 'string' && value.length > 40 ? value.slice(0, 40) + '…' : value;
    } else if (key === 'target') {
      copy[key] = typeof value === 'string' && value.length > 40 ? value.slice(0, 40) + '…' : value;
    } else {
      copy[key] = value;
    }
  }
  return copy;
}

export { sleep };
