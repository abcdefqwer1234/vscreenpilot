"""可复现的最小 PPT 流程：启动 → 标题 → 新建页 → 版式 → 要点 → 另存为。

用于得到逐步延迟与往返次数，作为 OpenCode 手工场景的客观对照。
"""

from __future__ import annotations

import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import ppt_common as common  # noqa: E402
from vsp_client import case, print_summary, stats, write_json  # noqa: E402


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    steps: list[dict] = []
    cases: list[dict] = []
    client = common.open_client(args.exe)
    deck = os.path.join(common.DECK_DIR, "script-deck.pptx")
    if os.path.exists(deck):
        os.remove(deck)

    def step(name, result, **extra):
        steps.append({"step": name, "ok": result.ok, "code": result.code,
                      "ms": result.ms, "text_bytes": result.text_bytes,
                      "image_bytes": result.image_bytes, **extra})
        return result

    started = time.time()
    try:
        common.ensure_clean(client)
        display = common.display_rect(client)
        launch = client.call("launch_or_place_app", {"path": common.POWERPNT, "wait_ms": 60000}, 150)
        step("launch", launch, title=(launch.data or {}).get("title"),
             document_ready=(launch.data or {}).get("document_ready"))

        # 开始屏幕 → 空白演示文稿（Ctrl+N 在 Backstage 无效，已实测）
        if " - PowerPoint" not in str((launch.data or {}).get("title") or ""):
            step("blank-tile", client.call(
                "click",
                {"x": display["x"] + 200, "y": 225, "channel": "message", "keep_foreground": True},
                30))
            time.sleep(3)
            client.call("click", {"x": display["x"] + 1005, "y": 131,
                                  "channel": "input", "keep_foreground": True}, 30)

        step("snapshot", client.call("snapshot", {"max_depth": 3, "max_nodes": 200}, 60))

        area = common.slide_area(client)
        tx, ty = common.title_point(area)
        step("click-title", client.call(
            "click", {"x": tx, "y": ty, "channel": "message", "keep_foreground": True}, 30))
        step("type-title", client.call(
            "type_text", {"text": "脚本化流程验证", "verify": False}, 60))

        step("new-slide", client.call("press_keys", {"keys": "Ctrl+M", "keep_foreground": True}, 30))
        step("layout", client.call("press_keys", {
            "sequence": ["Alt+H", "L", "Home", "Down", "Right", "Right", "Enter"],
            "keep_foreground": True}, 30))
        time.sleep(0.5)

        area = common.slide_area(client)
        tx, ty = common.title_point(area)
        step("click-title2", client.call(
            "click", {"x": tx, "y": ty, "channel": "message", "keep_foreground": True}, 30))
        step("type-title2", client.call(
            "type_text", {"text": "要点", "verify": False}, 60))

        bx, by = common.body_point(area)
        step("click-body", client.call(
            "click", {"x": bx, "y": by, "channel": "message", "keep_foreground": True}, 30))
        step("type-body", client.call("type_text", {
            "text": "第一条\n第二条\n第三条", "verify": False}, 60))

        step("save-key", client.call("press_keys", {"keys": "F12", "keep_foreground": True}, 30))
        time.sleep(1)
        snap = client.call("snapshot", {"max_depth": 3, "max_nodes": 120}, 60)
        steps.append({"step": "save-dialog-snapshot", "ok": snap.ok, "code": snap.code,
                      "ms": snap.ms, "text_bytes": snap.text_bytes, "image_bytes": snap.image_bytes})

        edit = None
        # 文件名输入框：从快照文本里找 ref
        text = str((snap.data or {}).get("text") or "")
        ref = None
        for line in text.splitlines():
            if 'Edit "文件名:' in line or 'Edit "文件名(N):' in line:
                ref = line.split("[")[-1].split("]")[0]
                break
        if ref:
            set_name = client.call("set_element_value", {
                "ref": ref, "value": deck,
                "allow_destructive": True, "allow_keyboard": True, "mode": "replace"}, 30)
            steps.append({"step": "save-filename", "ok": set_name.ok, "code": set_name.code,
                          "ms": set_name.ms, "text_bytes": set_name.text_bytes, "image_bytes": 0})
        else:
            steps.append({"step": "save-filename", "ok": False, "code": "NO_EDIT_REF",
                          "ms": 0, "text_bytes": 0, "image_bytes": 0})

        client.call("press_keys", {"keys": "Enter", "keep_foreground": True}, 30)
        time.sleep(1.5)
        client.call("click", {"x": display["x"] + 2520, "y": 470, "channel": "input",
                              "keep_foreground": True}, 30)
        time.sleep(1.0)

        saved = os.path.exists(deck)
        steps.append({"step": "saved", "ok": saved, "code": None, "ms": 0,
                      "text_bytes": 0, "image_bytes": 0,
                      "bytes": os.path.getsize(deck) if saved else 0})

        cases.append(case("FL-00", "流程完成并落盘", saved, deck=deck,
                          bytes=os.path.getsize(deck) if saved else 0))
        cases.append(case("FL-01", "逐步延迟", True,
                          latency=stats([s["ms"] for s in steps if s["ms"]])))
    finally:
        released = common.release(client)
        cases.append(case("FL-99", "释放会话", bool(released.get("ok")),
                          display_removed=released.get("display_removed")))
        client.stop()

    write_json(os.path.join(args.out, "summary.json"), {
        "suite": "ppt-flow",
        "wall_ms": round((time.time() - started) * 1000, 1),
        "cases": cases,
        "steps": steps,
    })
    print_summary(cases)
    print("结果目录:", args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
