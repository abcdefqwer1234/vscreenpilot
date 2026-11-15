"""VScreenPilot 审计用最小 MCP stdio 客户端（纯标准库）。

特性：
  * 启动/停止服务端，按行收发 JSON-RPC；
  * 记录每次调用的耗时、错误码、文本/图片字节；
  * 采样 RSS、句柄数、光标、前台窗口、显示器拓扑；
  * 不依赖第三方包，便于在干净环境复现。
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import json
import os
import queue
import statistics
import subprocess
import sys
import threading
import time
from dataclasses import dataclass, field
from typing import Any

CREATE_NO_WINDOW = 0x08000000
LAST_BINARY_SHA256 = ""

# 采样必须使用物理像素，否则在 168DPI 主屏上 GetSystemMetrics 会返回虚拟化尺寸。
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PROCESS_PER_MONITOR_DPI_AWARE
except Exception:
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass


# ---------------------------------------------------------------- 系统采样

class _PROCESS_MEMORY_COUNTERS(ctypes.Structure):
    _fields_ = [
        ("cb", wt.DWORD),
        ("PageFaultCount", wt.DWORD),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
    ]


def rss_mb(pid: int) -> float:
    try:
        process = ctypes.windll.kernel32.OpenProcess(0x1000 | 0x0400, False, pid)
        if not process:
            return -1.0
        counters = _PROCESS_MEMORY_COUNTERS()
        counters.cb = ctypes.sizeof(counters)
        ok = ctypes.windll.psapi.GetProcessMemoryInfo(
            process, ctypes.byref(counters), ctypes.sizeof(counters)
        )
        ctypes.windll.kernel32.CloseHandle(process)
        return round(counters.WorkingSetSize / 1048576, 2) if ok else -1.0
    except Exception:
        return -1.0


def handle_count(pid: int) -> int:
    try:
        process = ctypes.windll.kernel32.OpenProcess(0x0400, False, pid)
        if not process:
            return -1
        count = wt.DWORD()
        ok = ctypes.windll.kernel32.GetProcessHandleCount(process, ctypes.byref(count))
        ctypes.windll.kernel32.CloseHandle(process)
        return int(count.value) if ok else -1
    except Exception:
        return -1


def cursor_pos() -> dict[str, int]:
    point = wt.POINT()
    ctypes.windll.user32.GetCursorPos(ctypes.byref(point))
    return {"x": point.x, "y": point.y}


def foreground_hwnd() -> int:
    return int(ctypes.windll.user32.GetForegroundWindow())


def window_title(hwnd: int) -> str:
    if not hwnd:
        return ""
    length = ctypes.windll.user32.GetWindowTextLengthW(hwnd)
    buf = ctypes.create_unicode_buffer(length + 2)
    ctypes.windll.user32.GetWindowTextW(hwnd, buf, length + 2)
    return buf.value


def monitor_count() -> int:
    return int(ctypes.windll.user32.GetSystemMetrics(80))  # SM_CMONITORS


def virtual_screen() -> dict[str, int]:
    return {
        "x": int(ctypes.windll.user32.GetSystemMetrics(76)),
        "y": int(ctypes.windll.user32.GetSystemMetrics(77)),
        "width": int(ctypes.windll.user32.GetSystemMetrics(78)),
        "height": int(ctypes.windll.user32.GetSystemMetrics(79)),
    }


def kill_tree(pid: int) -> None:
    subprocess.run(
        ["taskkill", "/PID", str(pid), "/T", "/F"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False,
    )


# ---------------------------------------------------------------- 结果封装

@dataclass
class ToolResult:
    name: str
    ok: bool
    code: str | None
    data: Any
    text: str
    images: list[dict[str, Any]] = field(default_factory=list)
    ms: float = 0.0
    text_bytes: int = 0
    image_bytes: int = 0
    raw: dict[str, Any] | None = None

    @property
    def message(self) -> str:
        if isinstance(self.data, dict):
            return str(self.data.get("message") or self.data.get("hint") or "")
        return ""


class McpClient:
    """单会话 MCP 客户端。"""

    def __init__(self, exe: str, env: dict[str, str] | None = None,
                 args: list[str] | None = None, cwd: str | None = None):
        global LAST_BINARY_SHA256
        self.exe = os.path.abspath(exe)
        try:
            import hashlib
            with open(self.exe, "rb") as handle:
                LAST_BINARY_SHA256 = hashlib.sha256(handle.read()).hexdigest().upper()
        except OSError:
            LAST_BINARY_SHA256 = ""
        self.env = {**os.environ, **(env or {})}
        self.args = args
        self.cwd = cwd or os.path.dirname(self.exe)
        self.proc: subprocess.Popen | None = None
        self._pending: dict[int, queue.Queue] = {}
        self._lock = threading.Lock()
        self._write_lock = threading.Lock()
        self._next_id = 1
        self.notifications: list[dict[str, Any]] = []
        self.stderr = ""
        self.exit_code: int | None = None
        self.negotiated: str | None = None
        self.tools_meta: dict[str, Any] = {}
        self.calls: list[dict[str, Any]] = []
        self.start_monotonic = 0.0

    # -- 生命周期 ---------------------------------------------------

    def start(self, init: bool = True, protocol: str = "2025-06-18",
              timeout: float = 60.0, warmup: bool = True) -> "McpClient":
        args = self.args if self.args is not None else ["--parent-pid", str(os.getpid())]
        env = dict(self.env)
        if not warmup:
            env.setdefault("VSP_NO_WARMUP", "1")
        self.start_monotonic = time.perf_counter()
        self.proc = subprocess.Popen(
            [self.exe, *args],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            cwd=self.cwd, env=env, bufsize=0,
            creationflags=CREATE_NO_WINDOW,
        )
        threading.Thread(target=self._read_stdout, daemon=True).start()
        threading.Thread(target=self._read_stderr, daemon=True).start()
        if init:
            response = self.request("initialize", {
                "protocolVersion": protocol,
                "capabilities": {},
                "clientInfo": {"name": "vsp-audit-py", "version": "1.0"},
            }, timeout)
            self.negotiated = (response.get("result") or {}).get("protocolVersion")
            self.notify("notifications/initialized")
        return self

    def _read_stdout(self) -> None:
        assert self.proc and self.proc.stdout
        for raw in iter(self.proc.stdout.readline, b""):
            line = raw.decode("utf-8", errors="replace").strip()
            if not line:
                continue
            try:
                message = json.loads(line)
            except json.JSONDecodeError:
                self.notifications.append({"_unparsed": line[:200]})
                continue
            if isinstance(message, dict) and "id" in message and message["id"] in self._pending:
                self._pending.pop(message["id"]).put(message)
            else:
                self.notifications.append(message)

    def _read_stderr(self) -> None:
        assert self.proc and self.proc.stderr
        for raw in iter(self.proc.stderr.readline, b""):
            self.stderr += raw.decode("utf-8", errors="replace")

    def send_line(self, line: str | bytes) -> None:
        assert self.proc and self.proc.stdin
        data = line.encode("utf-8") if isinstance(line, str) else line
        with self._write_lock:
            self.proc.stdin.write(data + b"\n")
            self.proc.stdin.flush()

    def request(self, method: str, params: dict | None = None,
                timeout: float = 180.0) -> dict[str, Any]:
        with self._lock:
            request_id = self._next_id
            self._next_id += 1
        cell: queue.Queue = queue.Queue(maxsize=1)
        self._pending[request_id] = cell
        self.send_line(json.dumps({
            "jsonrpc": "2.0", "id": request_id, "method": method, "params": params or {},
        }, ensure_ascii=False))
        try:
            return cell.get(timeout=timeout)
        except queue.Empty:
            self._pending.pop(request_id, None)
            raise TimeoutError(f"{method} 超时 {timeout}s")

    def notify(self, method: str, params: dict | None = None) -> None:
        self.send_line(json.dumps({
            "jsonrpc": "2.0", "method": method, "params": params or {},
        }, ensure_ascii=False))

    def call(self, name: str, args: dict | None = None,
             timeout: float = 180.0) -> ToolResult:
        started = time.perf_counter()
        try:
            response = self.request("tools/call", {"name": name, "arguments": args or {}}, timeout)
        except TimeoutError:
            result = ToolResult(name, False, "E_CLIENT_TIMEOUT", None, "", ms=round((time.perf_counter() - started) * 1000, 2))
            self.calls.append({"name": name, "ok": False, "code": "E_CLIENT_TIMEOUT", "ms": result.ms})
            return result

        elapsed = round((time.perf_counter() - started) * 1000, 2)
        if "error" in response:
            error = response.get("error") or {}
            result = ToolResult(name, False, f"RPC_{error.get('code')}", None,
                                str(error.get("message", "")), ms=elapsed, raw=response)
            self.calls.append({"name": name, "ok": False, "code": result.code, "ms": elapsed})
            return result

        payload = response.get("result") or {}
        texts: list[str] = []
        images: list[dict[str, Any]] = []
        text_bytes = image_bytes = 0
        for item in payload.get("content") or []:
            if item.get("type") == "image":
                data = item.get("data") or ""
                size = len(data) * 3 // 4
                image_bytes += size
                images.append({"mimeType": item.get("mimeType"), "bytes": size, "base64": data})
            elif item.get("type") == "text":
                texts.append(item.get("text") or "")
                text_bytes += len((item.get("text") or "").encode("utf-8"))
        text = "\n".join(texts)
        try:
            data = json.loads(text)
        except (json.JSONDecodeError, TypeError):
            data = text
        ok = not payload.get("isError", False)
        code = None if ok else (data.get("code") if isinstance(data, dict) else "E_UNKNOWN")
        result = ToolResult(name, ok, code, data, text, images, elapsed,
                            text_bytes, image_bytes, payload)
        self.calls.append({"name": name, "ok": ok, "code": code, "ms": elapsed,
                           "text_bytes": text_bytes, "image_bytes": image_bytes})
        return result

    def close_stdin(self) -> None:
        if self.proc and self.proc.stdin:
            try:
                self.proc.stdin.close()
            except Exception:
                pass

    def wait(self, timeout: float = 30.0) -> int | None:
        if not self.proc:
            return None
        try:
            self.exit_code = self.proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            self.exit_code = None
        return self.exit_code

    def stop(self, timeout: float = 30.0) -> dict[str, Any]:
        if not self.proc:
            return {"exit_code": None}
        started = time.perf_counter()
        self.close_stdin()
        code = self.wait(timeout)
        elapsed = round((time.perf_counter() - started) * 1000, 1)
        if code is None:
            kill_tree(self.proc.pid)
            code = self.proc.poll()
        return {"exit_code": code, "cleanup_ms": elapsed, "stderr": self.stderr[-4000:]}

    def kill(self) -> None:
        if self.proc:
            kill_tree(self.proc.pid)
            try:
                self.proc.wait(timeout=5)
            except Exception:
                pass
            self.exit_code = self.proc.poll()

    # -- 采样 -------------------------------------------------------

    def sample(self) -> dict[str, Any]:
        pid = self.proc.pid if self.proc else -1
        return {
            "t": round(time.time(), 3),
            "pid": pid,
            "rss_mb": rss_mb(pid),
            "handles": handle_count(pid),
            "cursor": cursor_pos(),
            "foreground": window_title(foreground_hwnd()),
            "monitors": monitor_count(),
            "virtual_screen": virtual_screen(),
        }

    # -- 工具元数据 -------------------------------------------------

    def load_tools(self) -> dict[str, Any]:
        response = self.request("tools/list", {}, 60)
        result = response.get("result") or {}
        self.tools_meta = {tool["name"]: tool for tool in result.get("tools") or []}
        return result


# ---------------------------------------------------------------- 统计与输出

def percentile(values: list[float], p: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    if len(ordered) == 1:
        return round(ordered[0], 2)
    index = min(len(ordered) - 1, max(0, int(round((p / 100) * (len(ordered) - 1)))))
    return round(ordered[index], 2)


def stats(values: list[float]) -> dict[str, Any]:
    clean = [v for v in values if v is not None and v >= 0]
    if not clean:
        return {"n": 0}
    return {
        "n": len(clean),
        "min": round(min(clean), 2),
        "p50": percentile(clean, 50),
        "p95": percentile(clean, 95),
        "p99": percentile(clean, 99),
        "max": round(max(clean), 2),
        "mean": round(statistics.fmean(clean), 2),
    }


def write_json(path: str, payload: Any) -> None:
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    # 每组结果绑定被测二进制哈希，避免“数据来自另一个 build”的证据缺口。
    if isinstance(payload, dict) and LAST_BINARY_SHA256:
        payload.setdefault("binary_sha256", LAST_BINARY_SHA256)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2)


def append_csv(path: str, row: dict[str, Any]) -> None:
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    new_file = not os.path.exists(path)
    with open(path, "a", encoding="utf-8", newline="") as handle:
        if new_file:
            handle.write(",".join(row.keys()) + "\n")
        handle.write(",".join(
            json.dumps(v, ensure_ascii=False) if isinstance(v, str) and ("," in v or '"' in v) else str(v)
            for v in row.values()
        ) + "\n")


def repo_root() -> str:
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def default_exe() -> str:
    return os.path.join(repo_root(), "artifacts", "server", "VScreenPilot.exe")


def run_dir(tag: str) -> str:
    stamp = time.strftime("%Y%m%d-%H%M%S")
    return os.path.join(repo_root(), "tools", "audit", "results", f"{stamp}-{tag}")


def case(case_id: str, purpose: str, ok: bool, **extra: Any) -> dict[str, Any]:
    return {"id": case_id, "purpose": purpose, "ok": bool(ok), **extra}


def print_summary(cases: list[dict[str, Any]]) -> None:
    total = len(cases)
    passed = sum(1 for item in cases if item["ok"])
    print(f"\n== {passed}/{total} 通过 ==")
    for item in cases:
        flag = "PASS" if item["ok"] else "FAIL"
        detail = item.get("observed") or item.get("error") or ""
        print(f"[{flag}] {item['id']:<8} {item['purpose']} {detail}"[:220])


if __name__ == "__main__":
    client = McpClient(default_exe()).start()
    tools = client.load_tools()
    print(f"tools={len(client.tools_meta)} version={client.negotiated}")
    print(json.dumps(client.sample(), ensure_ascii=False))
    print(client.stop())
