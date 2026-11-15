import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ppt_common as common  # noqa: E402

EXE = sys.argv[1]
DECK = os.path.join(common.DECK_DIR, "VScreenPilot-PPT-测评.pptx")
client = common.open_client(EXE)
try:
    common.ensure_clean(client)
    display = common.display_rect(client)
    app = client.call("launch_or_place_app", {
        "path": common.POWERPNT, "args": f'"{DECK}"', "wait_ms": 60000}, 150)
    print("app", app.ok, app.code, (app.data or {}).get("title"))
    time.sleep(3)
    cap = client.call("capture_screen", {
        "region": {"x": display["x"] + 300, "y": display["y"] + 260,
                   "width": 160, "height": 90}, "scale": 1.0}, 60)
    print("cap", cap.ok, cap.code, "ms", cap.ms, "text_bytes", cap.text_bytes,
          "images", len(cap.images), "raw_keys", list((cap.raw or {}).keys()))
    if cap.data:
        print("cap_data", json.dumps(cap.data, ensure_ascii=False)[:400])
    snap = client.call("snapshot", {
        "max_depth": 2, "max_nodes": 30, "include_screenshot": True, "scale": 0.4}, 90)
    print("snap", snap.ok, snap.code, "images", len(snap.images),
          "keys", list((snap.data or {}).keys())[:8])
    if not snap.ok:
        print("snap_err", json.dumps(snap.data, ensure_ascii=False)[:400])
finally:
    print("release", common.release(client))
    client.stop()
