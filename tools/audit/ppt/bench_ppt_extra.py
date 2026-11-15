"""补充测试：图片通道、视觉兜底、手势、工具正反例、剪贴板、双击、重开。

对应用户复核后新增的 A1b/A2/A4/B5/B6/B7/C9 项。
"""

from __future__ import annotations

import argparse
import base64
import os
import subprocess
import sys
import tempfile
import time
import zipfile
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import ppt_common as common  # noqa: E402
from vsp_client import case, print_summary, write_json  # noqa: E402

DECK = os.path.join(common.DECK_DIR, "VScreenPilot-PPT-测评.pptx")
NS = {"a": "http://schemas.openxmlformats.org/drawingml/2006/main"}


def b64_ok(value: str) -> tuple[bool, str]:
    try:
        data = base64.b64decode(value, validate=True)
        return True, data[:4].hex()
    except Exception as exc:
        return False, f"{type(exc).__name__}: {exc}"


def _clipboard(script: str) -> str:
    out = subprocess.run(
        ["powershell", "-STA", "-NoProfile", "-Command", script],
        capture_output=True, text=True, timeout=20)
    return (out.stdout or "").strip()


def clipboard_image_present() -> bool:
    return _clipboard(
        "Add-Type -AssemblyName System.Windows.Forms; "
        "[System.Windows.Forms.Clipboard]::ContainsImage()"
    ).lower() == "true"


def set_clipboard_image() -> bool:
    return _clipboard(
        "Add-Type -AssemblyName System.Windows.Forms; "
        "Add-Type -AssemblyName System.Drawing; "
        "$b = New-Object System.Drawing.Bitmap 12,12; "
        "[System.Windows.Forms.Clipboard]::SetImage($b); "
        "[System.Windows.Forms.Clipboard]::ContainsImage()"
    ).lower() == "true"


def save_as(client, path: str) -> dict:
    client.call("press_keys", {"keys": "F12", "keep_foreground": True}, 30)
    time.sleep(1.0)
    snap = client.call("snapshot", {"max_depth": 3, "max_nodes": 120}, 60)
    ref = None
    for line in str((snap.data or {}).get("text") or "").splitlines():
        if 'Edit "' in line and "文件名" in line:
            ref = line.split("[")[-1].split("]")[0]
            break
    if ref is None:
        return {"ok": False, "reason": "no_filename_edit"}
    client.call("set_element_value", {
        "ref": ref, "value": path, "allow_destructive": True,
        "allow_keyboard": True, "mode": "replace"}, 30)
    client.call("press_keys", {"keys": "Enter", "keep_foreground": True}, 30)
    time.sleep(2.0)
    return {"ok": os.path.exists(path),
            "bytes": os.path.getsize(path) if os.path.exists(path) else 0}


def deck_texts(path: str) -> str:
    with zipfile.ZipFile(path) as archive:
        chunks = []
        for name in archive.namelist():
            if name.startswith("ppt/slides/slide") and name.endswith(".xml"):
                root = ET.fromstring(archive.read(name))
                chunks.extend(item.text or "" for item in root.findall(".//a:t", NS))
        return "".join(chunks)


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    cases: list[dict] = []
    client = common.open_client(args.exe)
    template_path = os.path.join(tempfile.gettempdir(), "vsp-title-template.png")
    if os.path.exists(template_path):
        os.remove(template_path)

    try:
        common.ensure_clean(client)
        display = common.display_rect(client)

        # B7：直接打开已保存的 deck，检查修复提示与标题
        app = client.call("launch_or_place_app", {
            "path": common.POWERPNT, "args": f'"{DECK}"', "wait_ms": 60000}, 150)
        time.sleep(3.0)
        title = str((app.data or {}).get("title") or "")
        open_snap = client.call("snapshot", {"max_depth": 2, "max_nodes": 60}, 60)
        dialogs = (open_snap.data or {}).get("dialogs") or []
        cases.append(case("SUP-14", "关闭后重开 pptx，无修复提示",
                          app.ok and "VScreenPilot" in title
                          and not any("修复" in (d.get("title") or "") or
                                      "repair" in (d.get("title") or "").lower()
                                      for d in dialogs),
                          title=title, dialogs=[d.get("title") for d in dialogs],
                          nodes=(open_snap.data or {}).get("nodes")))

        client.call("press_keys", {"sequence": ["Esc", "Home"], "keep_foreground": True}, 30)
        time.sleep(0.5)

        # A1b：内联图片是否合法 base64（capture_screen）
        inline = client.call("capture_screen", {
            "region": {"x": display["x"] + 300, "y": display["y"] + 260,
                       "width": 160, "height": 90},
            "scale": 1.0}, 60)
        data = inline.images[0]["base64"] if inline.images else ""
        ok, detail = b64_ok(data) if data else (False, "no image block")
        cases.append(case("SUP-01", "capture_screen 内联图是合法 base64", ok,
                          detail=detail, bytes=len(data)))

        # A1b2：snapshot 的 include_screenshot 走同一路径
        snap_shot = client.call("snapshot", {
            "max_depth": 2, "max_nodes": 30, "include_screenshot": True, "scale": 0.4}, 90)
        data2 = snap_shot.images[0]["base64"] if snap_shot.images else ""
        ok2, detail2 = b64_ok(data2) if data2 else (False, "no image block")
        cases.append(case("SUP-02", "snapshot 内联图是合法 base64", ok2,
                          detail=detail2, bytes=len(data2)))

        # B5：视觉兜底（像素/颜色/模板）
        pixel = client.call("match_visual", {
            "mode": "pixel",
            "target": {"x": display["x"] + 300, "y": display["y"] + 300}}, 30)
        pixel_color = (pixel.data or {}).get("color")
        cases.append(case("SUP-04a", "match_visual pixel", pixel.ok and bool(pixel_color),
                          color=pixel_color))

        color = client.call("match_visual", {
            "mode": "color", "target": pixel_color, "tolerance": 0,
            "region": {"x": display["x"] + 200, "y": display["y"] + 200,
                       "width": 500, "height": 300}}, 30)
        cases.append(case("SUP-04b", "match_visual color 命中", color.ok,
                          count=(color.data or {}).get("count")))

        title_region = {"x": display["x"] + 250, "y": display["y"] + 300,
                        "width": 400, "height": 110}
        saved = client.call("capture_screen", {
            "region": title_region, "scale": 1.0, "inline": False,
            "save_path": template_path}, 60)
        if (saved.data or {}).get("path"):
            template = client.call("match_visual", {
                "mode": "template", "target_path": template_path,
                "region": {"x": display["x"], "y": display["y"],
                           "width": display["width"], "height": display["height"]},
                "threshold": 0.9, "scales": [1]}, 90)
            cases.append(case("SUP-03", "match_visual template 找回自身",
                              template.ok, x=(template.data or {}).get("x"),
                              y=(template.data or {}).get("y"),
                              code=template.code))
        else:
            cases.append(case("SUP-03", "match_visual template 找回自身", False,
                              reason="template capture failed"))

        # B5：assert_region（输入前后变化）
        client.call("click", {"x": display["x"] + 565, "y": display["y"] + 372,
                              "channel": "message", "keep_foreground": True}, 30)
        baseline = client.call("capture_screen", {"region": title_region, "scale": 1.0}, 60)
        frame = (baseline.data or {}).get("frame_id")
        client.call("type_text", {"text": "Z", "verify": False}, 60)
        changed = client.call("assert_region", {
            "region": title_region,
            "expectation": {"changed": True, "baseline_frame_id": frame,
                            "threshold": 0.002}}, 60)
        cases.append(case("SUP-05", "assert_region 检测到输入变化",
                          changed.ok and bool((changed.data or {}).get("passed")),
                          passed=(changed.data or {}).get("passed"),
                          ratio=(changed.data or {}).get("changed_ratio"),
                          code=changed.code))
        client.call("press_keys", {"keys": "Ctrl+Z", "keep_foreground": True}, 30)

        # B5：wait_for 三种条件（含 text 的 schema/实现一致性）
        wait_text = client.call("wait_for", {
            "timeout_ms": 800, "interval_ms": 100,
            "condition": {"type": "text", "text": "VScreenPilot"}}, 30)
        cases.append(case("SUP-06", "wait_for text 可用（schema 声明了 text）",
                          wait_text.ok, code=wait_text.code,
                          message=(wait_text.data or {}).get("message")))

        wait_color = client.call("wait_for", {
            "timeout_ms": 800, "interval_ms": 100,
            "condition": {"type": "color", "x": display["x"] + 300,
                          "y": display["y"] + 300, "value": pixel_color,
                          "tolerance": 12}}, 30)
        cases.append(case("SUP-07", "wait_for color 立即满足", wait_color.ok,
                          satisfied=(wait_color.data or {}).get("satisfied")))

        wait_element = client.call("wait_for", {
            "timeout_ms": 1200, "interval_ms": 200,
            "condition": {"type": "element", "name": "VScreenPilot"}}, 30)
        cases.append(case("SUP-08", "wait_for element 在 PPT 上应超时且不崩",
                          wait_element.ok
                          and (wait_element.data or {}).get("satisfied") is False,
                          satisfied=(wait_element.data or {}).get("satisfied"),
                          code=wait_element.code))

        # B6：手势拖拽（第 3 页的图片）
        client.call("press_keys", {
            "sequence": ["Esc", "Home", "PageDown", "PageDown"],
            "keep_foreground": True}, 30)
        time.sleep(0.8)
        drag_region = {"x": display["x"] + 340, "y": display["y"] + 260,
                       "width": 420, "height": 280}
        before_drag = client.call("capture_screen", {"region": drag_region, "scale": 1.0}, 60)
        drag_frame = (before_drag.data or {}).get("frame_id")
        drag = client.call("gesture", {
            "type": "drag",
            "from": {"x": display["x"] + 552, "y": display["y"] + 390},
            "to": {"x": display["x"] + 632, "y": display["y"] + 390},
            "duration_ms": 500, "mode": "input"}, 60)
        moved = client.call("assert_region", {
            "region": drag_region,
            "expectation": {"changed": True, "baseline_frame_id": drag_frame,
                            "threshold": 0.01}}, 60)
        cases.append(case("SUP-09", "gesture 拖拽图片并检测到位置变化",
                          drag.ok and moved.ok
                          and bool((moved.data or {}).get("passed")),
                          drag_code=drag.code,
                          passed=(moved.data or {}).get("passed"),
                          ratio=(moved.data or {}).get("changed_ratio")))
        client.call("press_keys", {"keys": "Ctrl+Z", "keep_foreground": True}, 30)

        # C9：invoke_element / get_element_text / get_log 正反例
        snap = client.call("snapshot", {"max_depth": 3, "max_nodes": 60}, 60)
        pane_ref = None
        for line in str((snap.data or {}).get("text") or "").splitlines():
            if "Pane" in line and "[" in line:
                pane_ref = line.split("[")[-1].split("]")[0]
                break
        if pane_ref:
            invoke = client.call("invoke_element", {"ref": pane_ref}, 30)
            cases.append(case("SUP-10a", "对 Pane 调 invoke_element 返回明确结果",
                              invoke.ok or bool(invoke.code), code=invoke.code,
                              invoked=(invoke.data or {}).get("invoked")))
            read = client.call("get_element_text", {"ref": pane_ref}, 30)
            cases.append(case("SUP-10b", "对 Pane 调 get_element_text 返回明确错误",
                              not read.ok, code=read.code))
        else:
            cases.append(case("SUP-10a", "找到可测试的 Pane ref", False))

        logs = client.call("get_log", {"tail": 20}, 30)
        cases.append(case("SUP-11", "get_log 可读", logs.ok,
                          entries=len((logs.data or {}).get("entries") or [])))

        # A2：非文本剪贴板是否被长文本输入破坏
        if set_clipboard_image():
            before = clipboard_image_present()
            client.call("click", {"x": display["x"] + 565, "y": display["y"] + 372,
                                  "channel": "message", "keep_foreground": True}, 30)
            client.call("press_keys", {"keys": "Ctrl+A", "keep_foreground": True}, 30)
            client.call("press_keys", {"keys": "Delete", "keep_foreground": True}, 30)
            typed = client.call("type_text", {"text": "剪贴板测试" * 60, "verify": False,
                                              "channel": "clipboard"}, 120)
            after = clipboard_image_present()
            cases.append(case("SUP-12", "长文本输入后图片剪贴板仍在",
                              before and after and typed.code == "E_CLIPBOARD_UNSAFE",
                              before=before, after=after, code=typed.code))
        else:
            cases.append(case("SUP-12", "长文本输入后图片剪贴板仍在", False,
                              reason="set image clipboard failed"))

        # A4：双击进入文本编辑 → 输入 → 另存 → 从 XML 确认
        client.call("click", {"x": display["x"] + 565, "y": display["y"] + 372,
                              "clicks": 2, "channel": "message", "keep_foreground": True}, 30)
        client.call("press_keys", {"keys": "Ctrl+A", "keep_foreground": True}, 30)
        client.call("type_text", {"text": "双击验证", "verify": False}, 60)
        target = os.path.join(common.DECK_DIR, "dblclick-deck.pptx")
        if os.path.exists(target):
            os.remove(target)
        saved_deck = save_as(client, target)
        text = deck_texts(target) if saved_deck.get("ok") else ""
        cases.append(case("SUP-13", "双击进入文本编辑并写入成功",
                          saved_deck.get("ok") and "双击验证" in text,
                          saved=saved_deck, found="双击验证" in text))
    finally:
        released = common.release(client)
        cases.append(case("SUP-99", "释放会话", bool(released.get("ok")),
                          display_removed=released.get("display_removed"),
                          forced=released.get("closed_pids")))
        client.stop()

    write_json(os.path.join(args.out, "summary.json"), {
        "suite": "ppt-extra",
        "cases": cases,
    })
    print_summary(cases)
    print("结果目录:", args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
