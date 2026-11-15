import json
import os
import subprocess
import sys
import time

EXE = r"C:\Users\31746\Desktop\VScreenPilot\artifacts\server7\VScreenPilot.exe"
ENV = {**os.environ, "VSP_PROFILE": "imgprobe", "VSP_DISPLAY_OFFSET": "0", "VSP_NO_WARMUP": "1"}

proc = subprocess.Popen(
    [EXE, "--profile", "imgprobe", "--parent-pid", str(os.getpid())],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    cwd=r"C:\Users\31746\Desktop\VScreenPilot\artifacts\server7", env=ENV, bufsize=0,
)


def send(payload):
    proc.stdin.write((json.dumps(payload) + "\n").encode("utf-8"))
    proc.stdin.flush()


def read():
    line = proc.stdout.readline()
    return line


send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
    "protocolVersion": "2025-11-25", "capabilities": {},
    "clientInfo": {"name": "imgprobe", "version": "1"}}})
print("init", read()[:120])
send({"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})

# 先确保有虚拟屏，再抓一个很小的区域（远小于 128KB，走 inline 分支）
send({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {
    "name": "get_environment", "arguments": {"ensure": True}}})
print("env", read()[:120])

send({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {
    "name": "capture_screen", "arguments": {"region": {"x": 1920, "y": 0, "width": 120, "height": 80}}}})
raw = read()
print("raw_len", len(raw))
text = raw.decode("utf-8", errors="replace")
print("has_replacement", "\ufffd" in text)
index = text.find('"data":"')
print("data_prefix", repr(text[index:index + 40]))
message = json.loads(text)
blocks = message["result"]["content"]
print("blocks", [(b.get("type"), type(b.get("data")).__name__, len(b.get("data") or "")) for b in blocks])
data = blocks[1]["data"]
try:
    import base64
    decoded = base64.b64decode(data, validate=True)
    print("b64_ok", len(decoded), decoded[:8].hex())
except Exception as exc:
    print("b64_fail", type(exc).__name__, exc, repr(data[:30]))

send({"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {
    "name": "release_environment", "arguments": {"keep_display": False, "on_dirty": "force", "allow_data_loss": True}}})
print("rel", read()[:120])
proc.stdin.close()
proc.wait(timeout=20)
print("exit", proc.returncode)
