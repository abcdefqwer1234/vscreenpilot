import json
import os
import sys

sys.path.insert(0, r"C:\Users\31746\Desktop\VScreenPilot\tools\audit\ppt")
import ppt_common as common

client = common.open_client(sys.argv[1], profile="pptready")
try:
    common.ensure_clean(client)
    common.display_rect(client)
    app = client.call("launch_or_place_app", {
        "path": common.POWERPNT, "new_instance": True,
        "ensure_document": True, "wait_ms": 60000}, 150)
    print(json.dumps({
        "ok": app.ok,
        "code": app.code,
        "title": (app.data or {}).get("title"),
        "document_ready": (app.data or {}).get("document_ready"),
        "reason": (app.data or {}).get("document_ready_reason"),
        "warnings": (app.data or {}).get("warnings"),
    }, ensure_ascii=False))
finally:
    print(common.release(client))
    client.stop()

