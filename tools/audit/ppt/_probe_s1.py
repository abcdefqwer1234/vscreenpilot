import json
import os
import sys
import time

sys.path.insert(0, r"C:\Users\31746\Desktop\VScreenPilot\tools\audit")
from vsp_client import McpClient  # noqa: E402

exe = sys.argv[1]
profile = "s1fix"
env = {"VSP_PROFILE": profile, "VSP_DISPLAY_WIDTH": "1280", "VSP_DISPLAY_HEIGHT": "720"}
if len(sys.argv) > 2:
    env["VSP_DISPLAY_OFFSET"] = sys.argv[2]
client = McpClient(
    exe,
    env=env,
    args=["--profile", profile, "--parent-pid", str(os.getpid())],
)
client.start(timeout=60, warmup=False, protocol="2025-11-25")
try:
    env = client.call("get_environment", {"ensure": True}, 120)
    display = (env.data or {}).get("virtual_display") or {}
    print(json.dumps({
        "ok": env.ok,
        "code": env.code,
        "message": (env.data or {}).get("message"),
        "details": (env.data or {}).get("details"),
        "x": display.get("x"),
        "y": display.get("y"),
        "width": display.get("width"),
        "height": display.get("height"),
        "ms": env.ms,
    }, ensure_ascii=False))
finally:
    rel = client.call("release_environment", {
        "keep_display": False, "on_dirty": "force", "allow_data_loss": True}, 120)
    print("release", rel.ok, rel.code, (rel.data or {}).get("display_removed"))
    client.stop()
