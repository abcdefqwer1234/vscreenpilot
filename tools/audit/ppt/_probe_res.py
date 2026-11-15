import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ppt_common as common  # noqa: E402
from vsp_client import McpClient  # noqa: E402

EXE = sys.argv[1]
DECK = os.path.join(common.DECK_DIR, "VScreenPilot-PPT-测评.pptx")
c = McpClient(EXE, env={"VSP_PROFILE": "pptres2", "VSP_DISPLAY_OFFSET": "0",
                        "VSP_DISPLAY_WIDTH": "1920", "VSP_DISPLAY_HEIGHT": "1080"},
              args=["--profile", "pptres2", "--parent-pid", str(os.getpid())]
              ).start(timeout=60, warmup=False)
try:
    env = c.call("get_environment", {"ensure": True}, 120)
    vd = (env.data or {}).get("virtual_display")
    print("vd", json.dumps(vd, ensure_ascii=False))
    app = c.call("launch_or_place_app", {"path": common.POWERPNT,
                "args": f'"{DECK}"', "wait_ms": 60000}, 150)
    print("app", app.ok, (app.data or {}).get("bounds"))
    r = c.call("click", {"x": 2010, "y": 105, "channel": "auto", "keep_foreground": True}, 30)
    print("click", r.ok, r.code, json.dumps(r.data, ensure_ascii=False)[:300])
    shot = c.call("capture_screen", {"scale": 0.5, "inline": False,
                 "save_path": os.path.join(os.environ.get("TEMP", "."), "res1080.png")}, 60)
    print("shot", shot.ok, shot.code, json.dumps(shot.data or {}, ensure_ascii=False)[:200])
finally:
    print("release", common.release(c))
    c.stop()
