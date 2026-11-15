import os
import shutil
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ppt_common as common  # noqa: E402

exe = sys.argv[1]
out_png = sys.argv[2]
target = os.path.join(tempfile.gettempdir(), "vsp-probe-start.png")
if os.path.exists(target):
    os.remove(target)

client = common.open_client(exe)
try:
    common.ensure_clean(client)
    display = common.display_rect(client)
    app = client.call("launch_or_place_app", {"path": common.POWERPNT, "wait_ms": 60000}, 150)
    print("app", app.ok, app.code, (app.data or {}).get("title"), (app.data or {}).get("document_ready"))
    time.sleep(1.0)
    shot = client.call("capture_screen", {"scale": 1, "inline": False, "save_path": target}, 60)
    print("shot", shot.ok, shot.code, (shot.data or {}).get("path"), os.path.getsize(target) if os.path.exists(target) else 0)
    if os.path.exists(target):
        shutil.copyfile(target, out_png)
    snap = client.call("snapshot", {"max_depth": 3, "max_nodes": 200, "interactive_only": False}, 60)
    print((snap.data or {}).get("text", "")[:1200])
finally:
    print("release", common.release(client))
    client.stop()
