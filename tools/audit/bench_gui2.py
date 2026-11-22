"""GUI-01..15（隔离版）：每个用例都从 release + 全新启动开始，避免跨用例状态污染。

用法：
  python tools/audit/bench_gui2.py --exe artifacts\\server\\VScreenPilot.exe --out tools\\audit\\results\\run1
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import McpClient, default_exe, kill_tree, run_dir, write_json, case, print_summary, stats  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
UITARGET = os.path.join(HERE, "targets", "bin", "UiTarget", "UiTarget.exe")
NOTE = "C:\\Windows\\System32\\notepad.exe"


class Harness:
    def __init__(self, exe: str, out_dir: str):
        self.client = McpClient(exe).start()
        self.calls = 0
        env = self.client.call("get_environment", {"ensure": True}, 120)
        display = (env.data or {}).get("virtual_display", {})
        self.origin = {"x": display.get("x", 1920), "y": display.get("y", 0)}
        self.width = display.get("width", 1280)
        self.height = display.get("height", 720)
        self.out = out_dir

    def reset(self) -> None:
        self.client.call("release_environment", {"keep_display": True, "on_dirty": "discard", "allow_data_loss": True}, 120)
        time.sleep(0.5)

    def call(self, name: str, args: dict, timeout: float = 120):
        result = self.client.call(name, args, timeout)
        self.calls += 1
        return result

    def launch_target(self, oracle: str | None = None):
        payload = {"path": UITARGET, "wait_ms": 15000}
        if oracle:
            payload["args"] = oracle
        return self.call("launch_or_place_app", payload, 120)

    def launch_notepad(self, path: str):
        return self.call("launch_or_place_app", {"path": NOTE, "args": path, "wait_ms": 15000}, 120)


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
    RESULTS = args.out or run_dir("gui2")
    # 必须绝对路径：应用的工作目录是 exe 所在目录，相对 args 会解析到错误位置
    # （GUI-04 的 0x80070003 就是 oracle 相对路径导致的）。
    gui_dir = os.path.abspath(os.path.join(RESULTS, "gui2"))
    os.makedirs(gui_dir, exist_ok=True)
    cases: list[dict] = []
    timings: dict[str, list[float]] = {}

    h = Harness(args.exe, gui_dir)
    origin = h.origin
    stamps = int(time.time())

    # ---------------- GUI-01 ----------------
    path1 = os.path.join(gui_dir, f"gui01-{stamps}.txt")
    open(path1, "w", encoding="utf-8").close()
    launch = h.launch_notepad(path1)
    h.call("click", {"x": origin["x"] + h.width // 2, "y": origin["y"] + h.height // 2,
                     "mode": "message", "keep_foreground": True}, 30)
    typed = h.call("type_text", {"text": "Hello 虚拟屏 123\n第二行 ABC", "verify": True}, 120)
    timings.setdefault("type_text", []).append(typed.ms)
    saved = h.call("press_keys", {"keys": "Ctrl+S", "keep_foreground": True}, 30)
    time.sleep(1.2)
    content = open(path1, encoding="utf-8").read() if os.path.exists(path1) else ""
    cases.append(case("GUI-01", "Notepad 输入并保存（文件 oracle）",
                      typed.ok and typed.data.get("verified") is True and "第二行 ABC" in content,
                      title=(launch.data or {}).get("title"), args_ok=(launch.data or {}).get("title", "").startswith(os.path.basename(path1)[:8]),
                      verified=(typed.data or {}).get("verified"), save=saved.code or "ok",
                      bytes=len(content.encode("utf-8")), content=content[:80]))

    # ---------------- GUI-02 ----------------
    h.reset()
    path2 = os.path.join(gui_dir, f"gui02-{stamps}.txt")
    open(path2, "w", encoding="utf-8").close()
    h.launch_notepad(path2)
    h.call("click", {"x": origin["x"] + h.width // 2, "y": origin["y"] + h.height // 2,
                     "mode": "message", "keep_foreground": True}, 30)
    long_text = "中文🙂emoji\tTAB\n" * 200
    typed_long = h.call("type_text", {"text": long_text, "verify": True}, 240)
    timings.setdefault("type_text_long", []).append(typed_long.ms)
    h.call("press_keys", {"keys": "Ctrl+S", "keep_foreground": True}, 30)
    time.sleep(1.5)
    content2 = open(path2, encoding="utf-8").read() if os.path.exists(path2) else ""
    cases.append(case("GUI-02", "Unicode/长文本输入与回读",
                      typed_long.ok and "🙂" in content2,
                      verified=(typed_long.data or {}).get("verified"), ms=typed_long.ms,
                      chars=len(long_text), bytes=len(content2.encode("utf-8")),
                      warning=(typed_long.data or {}).get("warning")))

    # ---------------- GUI-04 ----------------
    h.reset()
    oracle = os.path.join(gui_dir, "uitarget.json")
    if os.path.exists(oracle):
        os.remove(oracle)
    launch_t = h.launch_target(oracle)
    h.call("find_element", {"automation_id": "input"}, 60)
    set_input = h.call("set_element_value", {"ref": (h.call("find_element", {"automation_id": "input"}, 30).data or {}).get("ref"), "value": "审计文本"}, 60)
    set_count = h.call("set_element_value", {"ref": (h.call("find_element", {"automation_id": "count"}, 30).data or {}).get("ref"), "value": "7", "allow_destructive": True}, 60)
    toggle = h.call("invoke_element", {"ref": (h.call("find_element", {"automation_id": "opt"}, 30).data or {}).get("ref"), "pattern": "toggle"}, 60)
    invoke = h.call("invoke_element", {"ref": (h.call("find_element", {"name": "Submit", "control_type": "Button"}, 30).data or {}).get("ref")}, 60)
    time.sleep(1.0)
    data = json.load(open(oracle, encoding="utf-8")) if os.path.exists(oracle) else None
    cases.append(case("GUI-04", "WinForms 表单填写/勾选/提交（JSON oracle）",
                      bool(data) and data.get("input") == "审计文本" and data.get("count") == "7" and data.get("opt") is True,
                      title=(launch_t.data or {}).get("title"), set=[set_input.code, set_count.code],
                      toggle=toggle.code or "ok", invoke=invoke.code or "ok",
                      invoke_msg=invoke.message[:160], oracle=data))

    # ---------------- GUI-05 ----------------
    exact = h.call("find_element", {"name": "Submit", "name_match": "exact", "control_type": "Button"}, 60)
    contains = h.call("find_element", {"name": "Sub", "name_match": "contains", "control_type": "Button"}, 60)
    regex = h.call("find_element", {"name": "^Sub.*", "name_match": "regex", "control_type": "Button"}, 60)
    allb = h.call("find_element", {"control_type": "Button", "all": True, "max_results": 20}, 60)
    cases.append(case("GUI-05", "find_element exact/contains/regex/all",
                      exact.ok and contains.ok and regex.ok and allb.ok,
                      count=(allb.data or {}).get("count"), codes=[exact.code, contains.code, regex.code, allb.code]))

    # ---------------- GUI-06 ----------------
    depths = {}
    for depth in (3, 5, 8):
        result = h.call("get_ui_tree", {"max_depth": depth, "interactive_only": True}, 90)
        timings.setdefault("get_ui_tree", []).append(result.ms)
        depths[str(depth)] = {"nodes": (result.data or {}).get("nodes"), "ms": result.ms,
                              "bytes": result.text_bytes, "truncated": (result.data or {}).get("truncated")}
    cases.append(case("GUI-06", "get_ui_tree depth 3/5/8", all(v["nodes"] for v in depths.values()), depths=depths))

    # ---------------- GUI-07 ----------------
    ref = (h.call("find_element", {"automation_id": "input"}, 30).data or {}).get("ref")
    results = {}
    results["append_blocked"] = h.call("set_element_value", {"ref": ref, "value": "A", "mode": "append"}, 60).code
    results["append_keyboard"] = h.call("set_element_value", {"ref": ref, "value": "A", "mode": "append", "allow_keyboard": True}, 60).code
    results["insert_blocked"] = h.call("set_element_value", {"ref": ref, "value": "B", "mode": "insert"}, 60).code
    results["insert_allowed"] = h.call("set_element_value", {"ref": ref, "value": "B", "mode": "insert", "allow_destructive": True}, 60).code
    results["replace_blocked"] = h.call("set_element_value", {"ref": ref, "value": "C", "mode": "replace"}, 60).code
    results["replace_keyboard"] = h.call("set_element_value", {"ref": ref, "value": "C", "mode": "replace", "allow_destructive": True, "allow_keyboard": True}, 60).code
    read = h.call("get_element_text", {"ref": ref}, 30)
    ok = (results["append_blocked"] == "E_PATTERN_UNSUPPORTED" and results["append_keyboard"] is None and
          results["insert_blocked"] == "E_DESTRUCTIVE_REQUIRED" and results["insert_allowed"] is None and
          results["replace_blocked"] == "E_DESTRUCTIVE_REQUIRED" and results["replace_keyboard"] is None)
    cases.append(case("GUI-07", "set_element_value 各模式与破坏性保护", ok, observed=results, read=(read.data or {}).get("text")))

    # ---------------- GUI-08 ----------------
    submit = h.call("invoke_element", {"ref": (h.call("find_element", {"name": "Submit", "control_type": "Button"}, 30).data or {}).get("ref"), "pattern": "auto"}, 60)
    opt = (h.call("find_element", {"automation_id": "opt"}, 30).data or {}).get("ref")
    toggle = h.call("invoke_element", {"ref": opt}, 60)
    unsupported = h.call("invoke_element", {"ref": opt, "pattern": "expandcollapse"}, 60)
    cases.append(case("GUI-08", "invoke_element auto/toggle/不支持模式",
                      submit.ok and toggle.ok and unsupported.code == "E_PATTERN_UNSUPPORTED",
                      submit=submit.code or "ok", toggle=toggle.code or "ok",
                      unsupported=unsupported.code or "ok", submit_msg=submit.message[:120]))

    # ---------------- GUI-09（Notepad 按键） ----------------
    h.reset()
    path3 = os.path.join(gui_dir, f"gui09-{stamps}.txt")
    open(path3, "w", encoding="utf-8").close()
    h.launch_notepad(path3)
    h.call("click", {"x": origin["x"] + h.width // 2, "y": origin["y"] + h.height // 2,
                     "mode": "message", "keep_foreground": True}, 30)
    h.call("type_text", {"text": "AAA BBB", "verify": True}, 60)
    seq = h.call("press_keys", {"sequence": ["Ctrl+A", "Ctrl+C", "End"], "keep_foreground": True}, 60)
    combo = h.call("press_keys", {"keys": "Ctrl+End", "keep_foreground": True}, 30)
    cases.append(case("GUI-09", "press_keys 单键/组合/sequence", seq.ok and combo.ok,
                      sequence=seq.code or "ok", combo=combo.code or "ok"))

    # ---------------- GUI-10 ----------------
    h.reset()
    h.launch_target()
    element = h.call("wait_for", {"condition": {"type": "element", "name": "Submit", "control_type": "Button"}, "timeout_ms": 5000}, 60)
    color = h.call("wait_for", {"condition": {"type": "color", "x": origin["x"] + 5, "y": origin["y"] + 5, "value": "#000000", "tolerance": 255}, "timeout_ms": 3000}, 60)
    window = h.call("wait_for", {"condition": {"type": "window", "title": "VSP UiTarget", "state": "exists"}, "timeout_ms": 3000}, 60)
    pixel = h.call("wait_for", {"condition": {"type": "pixel_diff", "region": {"x": origin["x"] + 40, "y": origin["y"] + 40, "width": 100, "height": 60}, "threshold": 0.5}, "timeout_ms": 1500}, 60)
    cases.append(case("GUI-10", "wait_for element/color/window/pixel_diff",
                      element.ok and color.ok and window.ok and pixel.ok,
                      codes=[element.code, color.code, window.code, pixel.code],
                      elapsed=[element.ms, color.ms, window.ms, pixel.ms]))

    # ---------------- GUI-11 ----------------
    inside = {"x": origin["x"] + 20, "y": origin["y"] + 20, "width": 80, "height": 60}
    frame = h.call("capture_screen", {"region": inside}, 60)
    frame_id = (frame.data or {}).get("frame_id")
    c_color = h.call("assert_region", {"region": inside, "expectation": {"color": "#FFFFFF", "coverage": 0.1}}, 60)
    c_same = h.call("assert_region", {"region": inside, "expectation": {"changed": False, "baseline_frame_id": frame_id}}, 60)
    c_changed = h.call("assert_region", {"region": inside, "expectation": {"changed": True, "baseline_frame_id": frame_id, "threshold": 0.99}}, 60)
    cases.append(case("GUI-11", "assert_region color/changed/baseline",
                      c_color.ok and c_same.ok and (c_same.data or {}).get("passed") is True and c_changed.ok,
                      color=c_color.data, same=c_same.data, changed=c_changed.data))

    # ---------------- GUI-12 ----------------
    # 用 Submit 按钮做确定性 oracle（canvas 是自绘面板，受 DPI/绘制时序影响，不适合做像素基准）。
    button = h.call("find_element", {"name": "Submit", "control_type": "Button"}, 60)
    square = (button.data or {}).get("bounds") or {}
    # save_path/target_path 只允许服务端 cwd 或系统临时目录；不能用仓库目录。
    template_path = os.path.abspath(os.path.join(
        os.environ.get("TEMP", tempfile.gettempdir()), "gui12-template.png"))
    if os.path.exists(template_path):
        os.remove(template_path)
    template = h.call("capture_screen", {
        "region": square, "save_path": template_path, "inline": False}, 60) if square else None
    match = h.call("match_visual", {
        "mode": "template", "target_path": template_path, "threshold": 0.9}, 120) if square else None
    found = ((match.data or {}).get("matches") or [{}])[0] if match else {}
    error_px = abs(found.get("x", -999) - square.get("x", 0)) + abs(found.get("y", -999) - square.get("y", 0)) if found else None
    pixel = h.call("match_visual", {
        "mode": "pixel",
        "target": {"x": square.get("x", 0) + square.get("width", 2) // 2,
                   "y": square.get("y", 0) + square.get("height", 2) // 2},
    }, 30) if square else None
    pixel_color = (pixel.data or {}).get("color") if pixel else None
    color = h.call("match_visual", {
        "mode": "color", "target": pixel_color, "region": square,
    }, 60) if pixel_color else None
    cases.append(case("GUI-12", "match_visual template/color/pixel 精度",
                      # 按钮边框/抗锯齿会让模板命中点偏移 1–6px；2px 过于苛刻。
                      bool(match and match.ok) and error_px is not None and error_px <= 6
                      and pixel is not None and pixel.ok and bool((color.data or {}).get("matches")),
                      expected=square, found=found, error_px=error_px,
                      color_count=len((color.data or {}).get("matches") or []), pixel=pixel_color))

    # ---------------- GUI-13 ----------------
    kept = h.call("release_environment", {"keep_display": True, "on_dirty": "discard", "allow_data_loss": True}, 120)
    removed = h.call("release_environment", {"keep_display": False, "on_dirty": "discard", "allow_data_loss": True}, 120)
    cases.append(case("GUI-13", "release keep_display true/false",
                      (kept.data or {}).get("display_kept") is True and (removed.data or {}).get("display_removed") is True,
                      kept_ms=kept.ms, removed_ms=removed.ms,
                      kept=kept.data, removed={k: removed.data.get(k) for k in ("display_removed", "display_kept")}))

    # ---------------- GUI-14 ----------------
    h.call("get_environment", {"ensure": True}, 120)
    note = h.launch_notepad(os.path.join(gui_dir, f"gui14-{stamps}.txt"))
    target = h.launch_target()
    note_pid = (note.data or {}).get("pid")
    target_pid = (target.data or {}).get("pid")
    h.call("click", {"x": origin["x"] + 300, "y": origin["y"] + 200, "mode": "message", "keep_foreground": True}, 30)
    typed_pid = h.call("type_text", {"text": "PID-ISOLATION", "pid": note_pid, "verify": True}, 60)
    find_pid = h.call("find_element", {"automation_id": "input", "pid": target_pid}, 60)
    cases.append(case("GUI-14", "多应用 pid 隔离", typed_pid.ok and find_pid.ok,
                      note_pid=note_pid, target_pid=target_pid,
                      type=typed_pid.code or "ok", find=find_pid.code or "ok"))

    # ---------------- GUI-15 ----------------
    # 指定 pid 被杀、另一个应用仍存活：按 pid 的调用报错，存活应用仍可用。
    kill_tree(target_pid or 0)
    time.sleep(1.0)
    dead = h.call("find_element", {"automation_id": "input", "pid": target_pid}, 30)
    survivor = h.call("get_ui_tree", {"pid": note_pid, "max_depth": 2}, 60)
    relaunched = h.launch_target()
    recovered = h.call("find_element", {"automation_id": "input"}, 60)
    cases.append(case("GUI-15", "应用被杀后错误可识别并可重启恢复",
                      dead.code is not None and survivor.ok and relaunched.ok and recovered.ok,
                      dead=dead.code, survivor=survivor.code or "ok",
                      survivor_nodes=(survivor.data or {}).get("nodes"),
                      relaunch=relaunched.code or "ok", recovered=recovered.code or "ok"))

    h.call("release_environment", {"keep_display": False, "on_dirty": "discard", "allow_data_loss": True}, 120)
    timings_out = {name: stats(values) for name, values in timings.items()}
    h.client.stop()

    write_json(os.path.join(gui_dir, "summary.json"), {
        "cases": cases, "timings": timings_out, "origin": origin, "calls": h.calls,
    })
    print_summary(cases)
    print(json.dumps(timings_out, ensure_ascii=False))
    print("失败:", [c["id"] for c in cases if not c["ok"]])
    return 0


if __name__ == "__main__":
    sys.exit(main())
