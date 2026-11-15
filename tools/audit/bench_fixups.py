"""对 bench_regressions 首轮失败用例的修正复测（坐标/基线/单实例限制导致的假失败）。

用法：
  python tools/audit/bench_fixups.py --exe artifacts\\server\\VScreenPilot.exe --out tools\\audit\\results\\run1
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import McpClient, default_exe, kill_tree, run_dir, write_json, case, print_summary  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
UITARGET = os.path.join(HERE, "targets", "bin", "UiTarget", "UiTarget.exe")


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
    RESULTS = args.out or run_dir("fixups")
    os.makedirs(os.path.join(RESULTS, "regressions"), exist_ok=True)
    cases: list[dict] = []

    # ---- REG-09：独立进程 + VSP_ALLOW_SCRIPT=1（单实例互斥要求主客户端已退出）
    script = McpClient(args.exe, env={"VSP_ALLOW_SCRIPT": "1"}).start()
    result = script.call("run_script", {"code": "Write-Output '中文输出 OK'; [Console]::OutputEncoding.WebName"}, 60)
    text = result.text
    cases.append(case("REG-09", "run_script 中文输出不乱码",
                      result.ok and "中文输出 OK" in text and "\ufffd" not in text,
                      observed=text[:200], code=result.code))
    script.stop()

    client = McpClient(args.exe).start()
    env = client.call("get_environment", {"ensure": True}, 120)
    display = (env.data or {}).get("virtual_display", {})
    origin = {"x": display.get("x", 1920), "y": display.get("y", 0)}
    launch = client.call("launch_or_place_app", {"path": UITARGET, "wait_ms": 15000}, 120)
    bounds = (launch.data or {}).get("bounds") or {}

    # ---- REG-08：先建立 baseline，再测越界阈值
    inside = {"x": origin["x"] + 20, "y": origin["y"] + 20, "width": 80, "height": 80}
    frame = client.call("capture_screen", {"region": inside}, 60)
    base = {"baseline_frame_id": (frame.data or {}).get("frame_id")}
    checks = {}
    checks["coverage_1.5"] = client.call("assert_region", {"region": inside, "expectation": {"color": "#FFFFFF", "coverage": 1.5}}, 30).code
    checks["coverage_-0.5"] = client.call("assert_region", {"region": inside, "expectation": {"color": "#FFFFFF", "coverage": -0.5}}, 30).code
    checks["threshold_-1"] = client.call("assert_region", {"region": inside, "expectation": {"changed": True, "threshold": -1, **base}}, 30).code
    checks["threshold_2"] = client.call("assert_region", {"region": inside, "expectation": {"changed": True, "threshold": 2, **base}}, 30).code
    cases.append(case("REG-08", "断言阈值越界返回 E_INVALID_ARG",
                      all(code == "E_INVALID_ARG" for code in checks.values()),
                      observed=checks))

    # ---- SEC-09c：同区域 baseline 应通过（对照），不同区域已有缺陷证据
    same = client.call("assert_region", {"region": inside, "expectation": {"changed": False, **base}}, 60)
    cases.append(case("SEC-09c", "同区域 baseline 对照",
                      same.ok and (same.data or {}).get("passed") is True,
                      same=same.code or "ok", data=same.data))

    # ---- REG-13：修正为虚拟屏坐标
    c13 = {}
    c13["assert_empty"] = client.call("assert_region", {"region": inside, "expectation": {}}, 30).code
    c13["assert_bad_region"] = client.call("assert_region", {"region": {"x": "a"}, "expectation": {"changed": True}}, 30).code
    c13["find_name_number"] = client.call("find_element", {"name": 123}, 30).code
    cases.append(case("REG-13", "condition/expectation 错误字段返回 E_INVALID_ARG",
                      all(code == "E_INVALID_ARG" for code in c13.values()), observed=c13))

    # ---- REG-14：UWP Calculator（先 shell 启动，再按窗口接管，模拟真实用户路径）
    calc = subprocess.Popen(["C:\\Windows\\System32\\calc.exe"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(4)
    adopted = client.call("launch_or_place_app", {"window": "计算器", "wait_ms": 20000}, 120)
    if not adopted.ok:
        adopted = client.call("launch_or_place_app", {"window": "Calculator", "wait_ms": 20000}, 120)
    find = client.call("find_element", {"name": "Seven", "control_type": "Button"}, 60)
    if not find.ok:
        find = client.call("find_element", {"name": "七", "control_type": "Button"}, 60)
    invoke = client.call("invoke_element", {"ref": (find.data or {}).get("ref")}, 60) if find.ok else None
    read = client.call("get_ui_tree", {"max_depth": 6}, 60)
    cases.append(case("REG-14", "UWP Calculator 可按窗口接管并触发按钮",
                      adopted.ok and find.ok and bool(invoke and invoke.ok),
                      adopt=adopted.code or "ok", adopt_data=adopted.data if not adopted.ok else None,
                      find=find.code or "ok", invoke=(invoke.code if invoke else None),
                      ui_ok=read.ok))
    if adopted.ok:
        client.call("release_environment", {"keep_display": True, "on_dirty": "discard"}, 120)
    kill_tree(calc.pid)

    # 重新接管 UiTarget，供后续 GUI 脚本使用前清理
    client.call("release_environment", {"keep_display": False, "on_dirty": "discard"}, 120)
    client.stop()

    write_json(os.path.join(RESULTS, "regressions", "fixups.json"), {"cases": cases})
    print_summary(cases)
    print("bounds", bounds, "origin", origin)
    return 0


if __name__ == "__main__":
    sys.exit(main())
