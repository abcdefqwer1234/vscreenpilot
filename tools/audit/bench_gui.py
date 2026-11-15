"""GUI-01..15：基础 GUI 能力测试（脚本驱动，记录 P50/P95 与通道/错误码）。

说明：按测试计划，GUI 组的“AI 直接调用体验”由 OpenCode 会话记录；
本脚本用于可重复测量与 Oracle 校验，两者分开统计。

用法：
  python tools/audit/bench_gui.py --exe artifacts\\server\\VScreenPilot.exe --out tools\\audit\\results\\run1
"""

from __future__ import annotations

import argparse
import json
import os
import time

import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import McpClient, default_exe, kill_tree, run_dir, write_json, case, print_summary, stats  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
UITARGET = os.path.join(HERE, "targets", "bin", "UiTarget", "UiTarget.exe")
NOTE = "C:\\Windows\\System32\\notepad.exe"


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", default=default_exe())
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    global RESULTS
    RESULTS = args.out or run_dir("gui")
    gui_dir = os.path.join(RESULTS, "gui")
    os.makedirs(gui_dir, exist_ok=True)
    cases: list[dict] = []
    timings: dict[str, list[float]] = {}

    client = McpClient(args.exe).start()
    env = client.call("get_environment", {"ensure": True}, 120)
    # Windows 11 Notepad 是单进程多标签，测试前清掉本机遗留实例，保证每个用例从干净状态开始
    import subprocess as _sp
    _sp.run(["taskkill", "/IM", "notepad.exe", "/F"], capture_output=True)
    time.sleep(2.0)
    display = (env.data or {}).get("virtual_display", {})
    origin = {"x": display.get("x", 1920), "y": display.get("y", 0)}
    width = display.get("width", 1280)
    height = display.get("height", 720)

    # ---------------- GUI-01 输入→保存闭环 ----------------
    note_path = os.path.join(gui_dir, "gui01.txt")
    text1 = "Hello 虚拟屏 123\n第二行 ABC"
    attempts = []
    verified = False
    content = ""
    for attempt in range(1, 3):
        open(note_path, "w", encoding="utf-8").close()
        note = client.call("launch_or_place_app", {"path": NOTE, "args": note_path, "wait_ms": 15000}, 120)
        client.call("click", {"x": origin["x"] + width // 2, "y": origin["y"] + height // 2,
                              "mode": "message", "keep_foreground": True}, 30)
        typed = client.call("type_text", {"text": text1, "verify": True}, 120)
        timings.setdefault("type_text", []).append(typed.ms)
        saved = client.call("press_keys", {"keys": "Ctrl+S", "keep_foreground": True}, 60)
        time.sleep(1.2)
        content = open(note_path, encoding="utf-8").read() if os.path.exists(note_path) else ""
        verified = typed.ok and typed.data.get("verified") is True
        attempts.append({"attempt": attempt, "launch": note.code or "ok", "title": (note.data or {}).get("title"),
                         "verified": verified, "file": "第二行 ABC" in content, "save": saved.code or "ok"})
        if verified and "第二行 ABC" in content:
            break
        client.call("release_environment", {"keep_display": True, "on_dirty": "discard"}, 120)
        time.sleep(1.0)
    cases.append(case("GUI-01", "Notepad 输入并保存（文件 oracle）",
                      verified and "第二行 ABC" in content,
                      attempts=attempts, file_ok="第二行 ABC" in content,
                      bytes=len(content.encode("utf-8"))))

    # ---------------- GUI-02 Unicode/长文本 ----------------
    long_text = ("中文🙂emoji\tTAB\n" * 200)
    typed_long = client.call("type_text", {"text": long_text, "verify": True}, 180)
    saved2 = client.call("press_keys", {"keys": "Ctrl+S", "keep_foreground": True}, 60)
    time.sleep(1.5)
    content2 = open(note_path, encoding="utf-8").read()
    cases.append(case("GUI-02", "Unicode/长文本输入与回读",
                      typed_long.ok and typed_long.data.get("verified") is True and "🙂" in content2,
                      verified=typed_long.data.get("verified") if typed_long.ok else None,
                      ms=typed_long.ms, chars=len(long_text), file_bytes=len(content2.encode("utf-8")),
                      save=saved2.code or "ok"))

    # ---------------- GUI-04 UiTarget 表单 ----------------
    # 先释放 Notepad（close_on_release），避免默认目标歧义
    client.call("release_environment", {"keep_display": True, "on_dirty": "discard"}, 120)
    oracle_path = os.path.join(gui_dir, "uitarget.json")
    attempts = []
    oracle = None
    bounds = {}
    input_ref = count_ref = opt_ref = submit_ref = None
    for attempt in range(1, 3):
        if os.path.exists(oracle_path):
            os.remove(oracle_path)
        target = client.call("launch_or_place_app", {"path": UITARGET, "args": oracle_path, "wait_ms": 15000}, 120)
        bounds = (target.data or {}).get("bounds") or {}
        input_ref = client.call("find_element", {"automation_id": "input"}, 60)
        count_ref = client.call("find_element", {"automation_id": "count"}, 60)
        opt_ref = client.call("find_element", {"automation_id": "opt"}, 60)
        submit_ref = client.call("find_element", {"name": "Submit", "control_type": "Button"}, 60)
        set_input = client.call("set_element_value", {"ref": (input_ref.data or {}).get("ref"), "value": "审计文本"}, 60)
        set_count = client.call("set_element_value", {"ref": (count_ref.data or {}).get("ref"), "value": "7", "allow_destructive": True}, 60)
        toggle = client.call("invoke_element", {"ref": (opt_ref.data or {}).get("ref"), "pattern": "toggle"}, 60)
        invoke = client.call("invoke_element", {"ref": (submit_ref.data or {}).get("ref"), "pattern": "invoke"}, 60)
        time.sleep(1.0)
        oracle = json.load(open(oracle_path, encoding="utf-8")) if os.path.exists(oracle_path) else None
        attempts.append({"attempt": attempt, "launch": target.code or "ok", "title": (target.data or {}).get("title"),
                         "set": [set_input.code, set_count.code], "toggle": toggle.code or "ok",
                         "invoke": invoke.code or "ok", "invoke_msg": invoke.message[:160],
                         "oracle": oracle})
        if oracle and oracle.get("input") == "审计文本" and oracle.get("count") == "7" and oracle.get("opt") is True:
            break
        client.call("release_environment", {"keep_display": True, "on_dirty": "discard"}, 120)
        time.sleep(1.0)
    cases.append(case("GUI-04", "WinForms 表单填写/勾选/提交（JSON oracle）",
                      bool(oracle) and oracle.get("input") == "审计文本" and oracle.get("count") == "7" and oracle.get("opt") is True,
                      attempts=attempts, bounds=bounds))

    # ---------------- GUI-05 语义定位 ----------------
    exact = client.call("find_element", {"name": "Submit", "name_match": "exact", "control_type": "Button"}, 60)
    contains = client.call("find_element", {"name": "Sub", "name_match": "contains", "control_type": "Button"}, 60)
    regex = client.call("find_element", {"name": "^Sub.*", "name_match": "regex", "control_type": "Button"}, 60)
    all_buttons = client.call("find_element", {"control_type": "Button", "all": True, "max_results": 20}, 60)
    cases.append(case("GUI-05", "find_element exact/contains/regex/all",
                      exact.ok and contains.ok and regex.ok and all_buttons.ok,
                      refs=[(exact.data or {}).get("ref"), (contains.data or {}).get("ref"), (regex.data or {}).get("ref")],
                      count=(all_buttons.data or {}).get("count") if isinstance(all_buttons.data, dict) else None,
                      codes=[exact.code, contains.code, regex.code, all_buttons.code]))

    # ---------------- GUI-06 语义树裁剪 ----------------
    tree_sizes = {}
    for depth in (3, 5, 8):
        result = client.call("get_ui_tree", {"max_depth": depth, "interactive_only": True}, 90)
        timings.setdefault("get_ui_tree", []).append(result.ms)
        tree_sizes[depth] = {"nodes": (result.data or {}).get("nodes"), "ms": result.ms,
                             "bytes": result.text_bytes, "truncated": (result.data or {}).get("truncated")}
    cases.append(case("GUI-06", "get_ui_tree depth 3/5/8 节点与耗时", all(v["nodes"] for v in tree_sizes.values()),
                      depths=tree_sizes))

    # ---------------- GUI-07 写值模式（按实际契约：append/replace 需键盘路径） ----------------
    ref = (client.call("find_element", {"automation_id": "input"}, 60).data or {}).get("ref")
    append_blocked = client.call("set_element_value", {"ref": ref, "value": "A", "mode": "append"}, 60)
    append_keyboard = client.call("set_element_value", {"ref": ref, "value": "A", "mode": "append", "allow_keyboard": True}, 60)
    insert_blocked = client.call("set_element_value", {"ref": ref, "value": "B", "mode": "insert"}, 60)
    insert_allowed = client.call("set_element_value", {"ref": ref, "value": "B", "mode": "insert", "allow_destructive": True}, 60)
    replace_blocked = client.call("set_element_value", {"ref": ref, "value": "C", "mode": "replace"}, 60)
    replace_keyboard = client.call("set_element_value", {"ref": ref, "value": "C", "mode": "replace", "allow_destructive": True, "allow_keyboard": True}, 60)
    read = client.call("get_element_text", {"ref": ref}, 60)
    cases.append(case("GUI-07", "set_element_value 各模式与破坏性保护",
                      append_blocked.code == "E_PATTERN_UNSUPPORTED" and append_keyboard.ok and
                      insert_blocked.code == "E_DESTRUCTIVE_REQUIRED" and insert_allowed.ok and
                      replace_blocked.code == "E_DESTRUCTIVE_REQUIRED" and replace_keyboard.ok,
                      append_blocked=append_blocked.code, append_keyboard=append_keyboard.code or "ok",
                      insert_blocked=insert_blocked.code, insert_allowed=insert_allowed.code or "ok",
                      replace_blocked=replace_blocked.code, replace_keyboard=replace_keyboard.code or "ok",
                      read=read.data))

    # ---------------- GUI-08 触发模式 ----------------
    submit_now = (client.call("find_element", {"name": "Submit", "control_type": "Button"}, 60).data or {}).get("ref")
    opt_now = (client.call("find_element", {"automation_id": "opt"}, 60).data or {}).get("ref")
    submit2 = client.call("invoke_element", {"ref": submit_now, "pattern": "auto"}, 60)
    opt2 = client.call("invoke_element", {"ref": opt_now}, 60)
    bad_pattern = client.call("invoke_element", {"ref": opt_now, "pattern": "expandcollapse"}, 60)
    cases.append(case("GUI-08", "invoke_element auto/toggle/不支持模式",
                      submit2.ok and opt2.ok and bad_pattern.code in ("E_PATTERN_UNSUPPORTED", "E_INVALID_ARG"),
                      submit=submit2.code or "ok", submit_msg=submit2.message[:200],
                      toggle=opt2.code or "ok",
                      unsupported=bad_pattern.code or "ok"))

    # ---------------- GUI-09 按键 ----------------
    client.call("launch_or_place_app", {"window": "无标题 - Notepad", "wait_ms": 5000}, 60)
    keys_ok = []
    for keys in ("End", "Ctrl+End"):
        result = client.call("press_keys", {"keys": keys, "keep_foreground": True}, 60)
        keys_ok.append(result.ok or result.code)
    sequence = client.call("press_keys", {"sequence": ["Ctrl+A", "Backspace"], "keep_foreground": True}, 60)
    cases.append(case("GUI-09", "press_keys 单键/组合/sequence",
                      all(item is True for item in keys_ok) and sequence.ok,
                      keys=keys_ok, sequence=sequence.code or "ok"))

    # ---------------- GUI-10 wait_for ----------------
    wait_element = client.call("wait_for", {"condition": {"type": "element", "name": "Submit", "control_type": "Button"}, "timeout_ms": 5000}, 60)
    wait_color = client.call("wait_for", {"condition": {"type": "color", "x": origin["x"] + 5, "y": origin["y"] + 5, "value": "#000000", "tolerance": 255}, "timeout_ms": 3000}, 60)
    wait_window = client.call("wait_for", {"condition": {"type": "window", "title": "VSP UiTarget", "state": "exists"}, "timeout_ms": 3000}, 60)
    wait_pixel = client.call("wait_for", {"condition": {"type": "pixel_diff", "region": {"x": origin["x"] + 40, "y": origin["y"] + 40, "width": 100, "height": 60}, "threshold": 0.5}, "timeout_ms": 1200}, 60)
    cases.append(case("GUI-10", "wait_for element/color/window/pixel_diff",
                      wait_element.ok and wait_color.ok and wait_window.ok and wait_pixel.ok,
                      codes=[wait_element.code, wait_color.code, wait_window.code, wait_pixel.code],
                      elapsed=[wait_element.ms, wait_color.ms, wait_window.ms, wait_pixel.ms]))

    # ---------------- GUI-11 assert_region ----------------
    inside = {"x": origin["x"] + 20, "y": origin["y"] + 20, "width": 80, "height": 60}
    frame = client.call("capture_screen", {"region": inside}, 60)
    color = client.call("assert_region", {"region": inside, "expectation": {"color": "#FFFFFF", "coverage": 0.1}}, 60)
    unchanged = client.call("assert_region", {"region": inside, "expectation": {"changed": False, "baseline_frame_id": (frame.data or {}).get("frame_id")}}, 60)
    changed = client.call("assert_region", {"region": inside, "expectation": {"changed": True, "baseline_frame_id": (frame.data or {}).get("frame_id"), "threshold": 0.99}}, 60)
    cases.append(case("GUI-11", "assert_region color/changed/baseline",
                      color.ok and unchanged.ok and (unchanged.data or {}).get("passed") is True and changed.ok,
                      color=color.data, unchanged=unchanged.data, changed=changed.data))

    # ---------------- GUI-12 视觉定位（UiTarget 画布上的红色方块） ----------------
    canvas_ref = client.call("find_element", {"automation_id": "canvas"}, 60)
    canvas_bounds = (canvas_ref.data or {}).get("bounds") or {}
    square = {"x": canvas_bounds.get("x", origin["x"] + 20) + 40, "y": canvas_bounds.get("y", origin["y"] + 210) + 40,
              "width": 24, "height": 24}
    template = client.call("capture_screen", {"region": square}, 60)
    image = template.images[0] if template.images else None
    match = client.call("match_visual", {"mode": "template", "target": image["base64"], "threshold": 0.9}, 120) if image else None
    match_data = (match.data or {}) if match else {}
    candidates = match_data.get("matches") or match_data.get("results") or []
    first = candidates[0] if candidates else {}
    error_px = (abs(first.get("x", -999) - square["x"]) + abs(first.get("y", -999) - square["y"])) if first else None
    color_match = client.call("match_visual", {"mode": "color", "target": "#FF0000", "region": {"x": origin["x"], "y": origin["y"], "width": width, "height": height}, "max_results": 5}, 60)
    pixel = client.call("match_visual", {"mode": "pixel", "target": {"x": square["x"] + 4, "y": square["y"] + 4}}, 30)
    cases.append(case("GUI-12", "match_visual template/color/pixel 精度",
                      bool(match and match.ok) and (error_px is not None and error_px <= 2) and pixel.ok,
                      canvas=canvas_bounds, expected=square, found=first, error_px=error_px,
                      color=(color_match.data or {}), pixel=(pixel.data or {}).get("color")))

    # ---------------- GUI-14 多应用隔离 ----------------
    note2 = client.call("launch_or_place_app", {"path": NOTE, "wait_ms": 15000}, 120)
    note_pid = (note2.data or {}).get("pid")
    ui_pid = (target.data or {}).get("pid")
    client.call("click", {"x": origin["x"] + 300, "y": origin["y"] + 200, "mode": "message", "keep_foreground": True}, 30)
    typed_pid = client.call("type_text", {"text": "PID-ISOLATION", "pid": note_pid, "verify": True}, 60)
    find_pid = client.call("find_element", {"automation_id": "input", "pid": ui_pid}, 60)
    cases.append(case("GUI-14", "多应用 pid 隔离", typed_pid.ok and find_pid.ok,
                      note_pid=note_pid, ui_pid=ui_pid, type=typed_pid.code or "ok", find=find_pid.code or "ok"))

    # ---------------- GUI-15 故障恢复 ----------------
    kill_tree(ui_pid or 0)
    time.sleep(1.0)
    dead = client.call("find_element", {"automation_id": "input"}, 30)
    relaunched = client.call("launch_or_place_app", {"path": UITARGET, "wait_ms": 15000}, 120)
    recovered = client.call("find_element", {"automation_id": "input"}, 60)
    cases.append(case("GUI-15", "应用被杀后错误可识别并可重启恢复",
                      dead.code in ("E_NO_TARGET", "E_APP_GONE", "E_WINDOW_NOT_FOUND", "E_ELEMENT_NOT_FOUND") and relaunched.ok and recovered.ok,
                      dead=dead.code, relaunch=relaunched.code or "ok", recovered=recovered.code or "ok"))

    # ---------------- GUI-13 释放 ----------------
    kept = client.call("release_environment", {"keep_display": True, "on_dirty": "discard"}, 120)
    removed = client.call("release_environment", {"keep_display": False, "on_dirty": "discard"}, 120)
    cases.append(case("GUI-13", "release keep_display true/false",
                      kept.ok and (kept.data or {}).get("display_kept") is True and removed.ok and (removed.data or {}).get("display_removed") is True,
                      kept=kept.data, removed={k: removed.data.get(k) for k in ("display_removed", "display_kept")} if isinstance(removed.data, dict) else removed.data,
                      kept_ms=kept.ms, removed_ms=removed.ms))

    timings_out = {name: stats(values) for name, values in timings.items()}
    client.stop()
    write_json(os.path.join(gui_dir, "summary.json"), {"cases": cases, "timings": timings_out, "origin": origin})
    print_summary(cases)
    print(json.dumps(timings_out, ensure_ascii=False))
    failed = [c["id"] for c in cases if not c["ok"]]
    print("失败:", failed)
    return 0


if __name__ == "__main__":
    sys.exit(main())
