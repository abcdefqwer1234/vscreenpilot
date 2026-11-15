"""修复复验：图片 base64、wait_for text、document_ready、release 默认值、剪贴板、OCR、annotate、observed。

使用记事本，避免 PowerPoint 的启动/浮层噪声；只验证本轮改动的契约。
"""

from __future__ import annotations

import argparse
import base64
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import ppt_common as common  # noqa: E402
from vsp_client import case, print_summary, write_json  # noqa: E402

NOTEPAD = r"C:\Windows\System32\notepad.exe"
MARKER = "VSP-FIX-MARKER"
PROFILE = "pptfix"


def b64_ok(value: str) -> tuple[bool, str]:
    try:
        data = base64.b64decode(value, validate=True)
        return data[:4] == b"\x89PNG", data[:4].hex()
    except Exception as exc:  # noqa: BLE001
        return False, f"{type(exc).__name__}: {exc}"


def clipboard_script(script: str) -> str:
    out = subprocess.run(
        ["powershell", "-STA", "-NoProfile", "-Command", script],
        capture_output=True, text=True, timeout=20)
    return (out.stdout or "").strip()


def image_present() -> bool:
    return clipboard_script(
        "Add-Type -AssemblyName System.Windows.Forms; "
        "[System.Windows.Forms.Clipboard]::ContainsImage()"
    ).lower() == "true"


def set_image() -> bool:
    return clipboard_script(
        "Add-Type -AssemblyName System.Windows.Forms; "
        "Add-Type -AssemblyName System.Drawing; "
        "$b = New-Object System.Drawing.Bitmap 8,8; "
        "[System.Windows.Forms.Clipboard]::SetImage($b); "
        "[System.Windows.Forms.Clipboard]::ContainsImage()"
    ).lower() == "true"


def process_alive(pid: int) -> bool:
    out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}"],
                         capture_output=True, text=True, timeout=20)
    return str(pid) in (out.stdout or "")


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:  # noqa: BLE001
        pass

    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    cases: list[dict] = []
    client = common.open_client(args.exe, profile=PROFILE)
    pid = None
    scratch = os.path.join(os.environ.get("TEMP", "."), "vsp-fix-notepad.txt")

    with open(scratch, "w", encoding="utf-8") as handle:
        handle.write("")

    try:
        common.ensure_clean(client)
        display = common.display_rect(client)

        app = client.call("launch_or_place_app", {
            "path": NOTEPAD, "args": f'"{scratch}"',
            "new_instance": True, "wait_ms": 30000}, 60)
        pid = (app.data or {}).get("pid")
        cases.append(case("FIX-00", "记事本就绪", app.ok and isinstance(pid, int),
                          pid=pid,
                          document_ready=(app.data or {}).get("document_ready"),
                          reason=(app.data or {}).get("document_ready_reason")))

        cases.append(case(
            "FIX-01", "未建模应用 document_ready=null",
            (app.data or {}).get("document_ready") is None
            and (app.data or {}).get("document_ready_reason") == "not_modeled",
            value=(app.data or {}).get("document_ready"),
            reason=(app.data or {}).get("document_ready_reason")))

        area = {"x": display["x"] + 120, "y": display["y"] + 140,
                "width": 800, "height": 400}
        time.sleep(1.0)
        client.call("click", {"x": area["x"] + 40, "y": area["y"] + 40,
                              "channel": "message", "keep_foreground": True}, 30)
        client.call("press_keys", {"keys": "Ctrl+A", "keep_foreground": True}, 30)
        client.call("type_text", {"text": MARKER, "verify": False}, 60)
        time.sleep(0.8)

        shot = client.call("capture_screen", {
            "region": {"x": display["x"] + 60, "y": display["y"] + 60,
                       "width": 120, "height": 80}, "scale": 1.0}, 60)
        data = shot.images[0]["base64"] if shot.images else ""
        ok, detail = b64_ok(data) if data else (False, "no image block")
        cases.append(case("FIX-02", "capture_screen 内联图是合法 base64", ok,
                          detail=detail, bytes=len(data)))

        snap = client.call("snapshot", {
            "max_depth": 3, "max_nodes": 120,
            "include_screenshot": True, "scale": 0.4}, 90)
        data2 = snap.images[0]["base64"] if snap.images else ""
        ok2, detail2 = b64_ok(data2) if data2 else (False, "no image block")
        cases.append(case("FIX-03", "snapshot{include_screenshot} 是合法 base64", ok2,
                          detail=detail2, bytes=len(data2),
                          nodes=(snap.data or {}).get("nodes")))

        wait = client.call("wait_for", {
            "timeout_ms": 3000, "interval_ms": 150,
            "condition": {"type": "text", "text": MARKER}}, 30)
        cases.append(case("FIX-04", "wait_for{type:text} 命中", wait.ok
                          and (wait.data or {}).get("satisfied") is True,
                          satisfied=(wait.data or {}).get("satisfied"),
                          code=wait.code))

        ocr = client.call("read_screen_text", {"query": MARKER}, 120)
        cases.append(case("FIX-05", "OCR 读到屏幕文本", ocr.ok
                          and len((ocr.data or {}).get("matches") or []) >= 1,
                          count=(ocr.data or {}).get("count"),
                          matches=len((ocr.data or {}).get("matches") or []),
                          language=(ocr.data or {}).get("language"),
                          code=ocr.code))

        annotate = client.call("snapshot", {
            "max_depth": 3, "max_nodes": 120, "annotate": True, "scale": 0.5}, 90)
        cases.append(case("FIX-06", "snapshot{annotate} 返回标注",
                          annotate.ok
                          and len((annotate.data or {}).get("annotations") or []) >= 1
                          and bool(annotate.images),
                          annotations=len((annotate.data or {}).get("annotations") or []),
                          images=len(annotate.images)))

        selected = client.call("press_keys", {
            "keys": "Ctrl+A", "keep_foreground": True, "observe": True}, 60)
        changed = ((selected.data or {}).get("observed") or {}).get("changed")
        cases.append(case("FIX-07", "press_keys 返回 observed.changed", selected.ok
                          and changed is True,
                          changed=changed,
                          ratio=((selected.data or {}).get("observed") or {}).get("ratio")))
        client.call("press_keys", {"keys": "Right", "keep_foreground": True}, 30)

        region = {"x": display["x"] + 100, "y": display["y"] + 120,
                  "width": 900, "height": 500}
        region_ocr = client.call("read_screen_text", {
            "region": region, "query": MARKER}, 120)
        clicked_text = client.call("click_text", {
            "text": MARKER, "region": region, "observe": False}, 120)
        bounds = (clicked_text.data or {}).get("bounds") or {}
        cases.append(case("FIX-14", "click_text 按 region 定位",
                          clicked_text.ok
                          and region["x"] <= bounds.get("x", -1) <= region["x"] + region["width"]
                          and region["y"] <= bounds.get("y", -1) <= region["y"] + region["height"],
                          x=bounds.get("x"), y=bounds.get("y"),
                          region_matches=len((region_ocr.data or {}).get("matches") or []),
                          ambiguous=(clicked_text.data or {}).get("ambiguous"),
                          code=clicked_text.code))

        blocked = client.call("release_environment", {
            "keep_display": True, "on_dirty": "force"}, 60)
        cases.append(case("FIX-08", "force 缺少 allow_data_loss 被拒绝",
                          (not blocked.ok) and blocked.code == "E_DATA_LOSS_REQUIRED"
                          and process_alive(pid),
                          code=blocked.code, alive=process_alive(pid)))

        default_release = client.call("release_environment", {
            "keep_display": True}, 90)
        kept = (default_release.data or {}).get("kept_pids") or []
        cases.append(case("FIX-09", "release 默认 abort 保留未保存进程",
                          default_release.ok and pid in kept and process_alive(pid)
                          and (default_release.data or {}).get("on_dirty") == "abort",
                          on_dirty=(default_release.data or {}).get("on_dirty"),
                          kept=kept, alive=process_alive(pid)))

        env_after = client.call("get_environment", {}, 30)
        tracked_after = [a for a in ((env_after.data or {}).get("apps") or [])
                         if a.get("pid") == pid]
        cases.append(case("FIX-13", "abort 后仍保留应用跟踪",
                          bool(tracked_after) and tracked_after[0].get("alive") is True,
                          apps=tracked_after))

        # abort 之后应用仍被跟踪；显式 taskkill 收尾，再重新接管新实例测剪贴板/强制释放。
        subprocess.run(["taskkill", "/PID", str(pid), "/F"],
                       capture_output=True, text=True, timeout=20)
        time.sleep(0.5)

        app2 = client.call("launch_or_place_app", {
            "path": NOTEPAD, "args": f'"{scratch}"',
            "new_instance": True, "wait_ms": 30000}, 60)
        pid2 = (app2.data or {}).get("pid")
        cases.append(case("FIX-10", "重新接管记事本", app2.ok, pid=pid2))

        if app2.ok:
            client.call("click", {"x": area["x"] + 40, "y": area["y"] + 40,
                                  "channel": "message", "keep_foreground": True}, 30)
            before = set_image()
            typed = client.call("type_text", {
                "text": "剪贴板保护" * 60, "verify": False, "channel": "auto"}, 120)
            after = image_present()
            cases.append(case("FIX-11", "长文本自动回退且图片剪贴板仍在",
                              before and after and typed.ok
                              and (typed.data or {}).get("channel") != "clipboard",
                              before=before, after=after,
                              channel=(typed.data or {}).get("channel"),
                              code=typed.code))

            forced = client.call("release_environment", {
                "keep_display": False, "on_dirty": "force",
                "allow_data_loss": True}, 90)
            cases.append(case("FIX-12", "显式 allow_data_loss 才强杀",
                              forced.ok and pid2 in ((forced.data or {}).get("forced_pids") or []),
                              forced=(forced.data or {}).get("forced_pids")))
    finally:
        for candidate in (pid,):
            if candidate and process_alive(candidate):
                subprocess.run(["taskkill", "/PID", str(candidate), "/F"],
                               capture_output=True, text=True, timeout=20)
        released = common.release(client)
        cases.append(case("FIX-99", "释放会话", bool(released.get("ok")),
                          display_removed=released.get("display_removed")))
        client.stop()

    write_json(os.path.join(args.out, "summary.json"),
               {"suite": "ppt-fixes", "cases": cases})
    print_summary(cases)
    print("结果目录:", args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
