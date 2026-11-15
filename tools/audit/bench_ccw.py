"""CCW 12 观测脚本：启动、UIA 树、语义定位、消息/输入通道、截图证据。

说明：按测试计划，CCW 的“AI 直接调用”应在 OpenCode 会话里完成；本脚本用于补充
可重复的客观数据（节点数、错误码、通道），不替代 AI 体验记录。

用法：
  python tools/audit/bench_ccw.py --exe artifacts\\server\\VScreenPilot.exe --out tools\\audit\\results\\run1
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import McpClient, default_exe, kill_tree, run_dir, write_json, case, print_summary  # noqa: E402

CCW = "C:\\Program Files (x86)\\Rockwell Automation\\CCW\\CCW.Shell.exe"


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
    RESULTS = args.out or run_dir("ccw")
    ccw_dir = os.path.join(RESULTS, "ccw")
    os.makedirs(ccw_dir, exist_ok=True)
    cases: list[dict] = []
    client = McpClient(args.exe).start()
    env = client.call("get_environment", {"ensure": True}, 120)
    display = (env.data or {}).get("virtual_display", {})
    origin = {"x": display.get("x", 1920), "y": display.get("y", 0)}
    width = display.get("width", 1280)
    height = display.get("height", 720)

    launch = client.call("launch_or_place_app", {"path": CCW, "wait_ms": 60000}, 120)
    cases.append(case("CCW-C1", "启动 CCW 并移动到虚拟屏",
                      launch.ok and (launch.data or {}).get("pid") is not None,
                      code=launch.code or "ok", data=launch.data, ms=launch.ms))
    if not launch.ok:
        write_json(os.path.join(ccw_dir, "summary.json"), {"cases": cases})
        print_summary(cases)
        client.stop()
        return 0

    pid = (launch.data or {}).get("pid")
    time.sleep(3)

    shot1 = client.call("capture_screen", {"save_path": os.path.join(ccw_dir, "01-launch.png")}, 60)
    cases.append(case("CCW-C3", "主界面截图", shot1.ok, code=shot1.code or "ok", bytes=shot1.image_bytes, ms=shot1.ms))

    trees = {}
    for depth in (3, 5, 8):
        tree = client.call("get_ui_tree", {"max_depth": depth, "interactive_only": False, "max_nodes": 1500}, 120)
        data = tree.data or {}
        nodes = []

        def walk(node):
            nodes.append(node)
            for child in node.get("children") or []:
                walk(child)

        if isinstance(data.get("tree"), dict):
            walk(data["tree"])
        types = {}
        for node in nodes:
            key = node.get("control_type") or "?"
            types[key] = types.get(key, 0) + 1
        trees[str(depth)] = {"nodes": len(nodes), "reported": data.get("nodes"),
                             "truncated": data.get("truncated"), "ms": tree.ms,
                             "bytes": tree.text_bytes, "types": types}
    cases.append(case("CCW-C4", "UIA 树深度与控件类型", True, depths=trees))
    write_json(os.path.join(ccw_dir, "ui-tree.json"), trees)

    finds = {}
    for label, payload in (
        ("new_project", {"name": "新建项目", "name_match": "contains"}),
        ("new_project_en", {"name": "New Project", "name_match": "contains"}),
        ("device", {"name": "设备", "name_match": "contains"}),
        ("programs", {"name": "程序", "name_match": "contains"}),
        ("build", {"name": "生成", "name_match": "contains"}),
        ("build_en", {"name": "Build", "name_match": "contains"}),
    ):
        result = client.call("find_element", payload, 60)
        finds[label] = {"code": result.code or "ok", "data": result.data}
    cases.append(case("CCW-C5", "关键控件语义定位", any(v["code"] == "ok" for v in finds.values()), finds=finds))

    # 消息点击 + 输入通道探测：点击主界面中心，再按 Alt 打开菜单
    center = {"x": origin["x"] + width // 2, "y": origin["y"] + height // 2}
    msg_click = client.call("click", {**center, "mode": "message", "keep_foreground": True}, 60)
    input_click = client.call("click", {**center, "mode": "input", "keep_foreground": True}, 60)
    alt = client.call("press_keys", {"keys": "Alt+F", "keep_foreground": True}, 60)
    shot2 = client.call("capture_screen", {"save_path": os.path.join(ccw_dir, "02-after-input.png")}, 60)
    cases.append(case("CCW-C7", "消息/输入/键盘通道探测",
                      True, message=msg_click.code or "ok", input=input_click.code or "ok",
                      keys=alt.code or "ok", shot=shot2.ok))

    # 记录 UIA 焦点元素/前台
    env2 = client.call("get_environment", {}, 30)
    write_json(os.path.join(ccw_dir, "environment.json"), env2.data or {})

    release = client.call("release_environment", {"keep_display": False, "on_dirty": "discard"}, 120)
    cases.append(case("CCW-C11", "释放与残留", release.ok,
                      released=release.data, ms=release.ms))
    client.stop()

    write_json(os.path.join(ccw_dir, "summary.json"), {"cases": cases, "pid": pid, "origin": origin})
    print_summary(cases)
    return 0


if __name__ == "__main__":
    sys.exit(main())
