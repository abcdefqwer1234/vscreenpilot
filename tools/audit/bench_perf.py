"""PERF-01..：启动/挂屏/截图/UIA/输入/释放/EOF 性能采样。

用法：
  python tools/audit/bench_perf.py --exe artifacts\\server\\VScreenPilot.exe --out tools\\audit\\results\\run1 --shots 200
"""

from __future__ import annotations

import argparse
import json
import os
import statistics
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import (  # noqa: E402
    McpClient, default_exe, run_dir, write_json, write_json as _w, stats, percentile,
    rss_mb, handle_count, monitor_count, case, print_summary,
)

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
    parser.add_argument("--shots", type=int, default=200)
    parser.add_argument("--release-cycles", type=int, default=20)
    parser.add_argument("--sessions", type=int, default=20)
    args = parser.parse_args()

    global RESULTS
    RESULTS = args.out or run_dir("perf")
    perf_dir = os.path.join(RESULTS, "perf")
    os.makedirs(perf_dir, exist_ok=True)
    metrics: dict[str, list[float]] = {}
    cases: list[dict] = []

    handshake = []
    for _ in range(5):
        started = time.perf_counter()
        client = McpClient(args.exe).start(init=False)
        response = client.request("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "vsp-audit-py", "version": "1.0"},
        }, 60)
        handshake.append(round((time.perf_counter() - started) * 1000, 2))
        client.notify("notifications/initialized")
        client.stop()
    metrics["handshake"] = handshake

    client = McpClient(args.exe).start()
    ensure_ms = client.call("get_environment", {"ensure": True}, 120).ms
    metrics.setdefault("ensure_first", []).append(ensure_ms)

    # 截图
    shots = []
    for index in range(args.shots):
        result = client.call("capture_screen", {}, 60)
        shots.append(result.ms)
        if index == 0:
            first_shot = result
    metrics["capture_screen"] = shots

    # 区域截图
    metrics["capture_region"] = [client.call("capture_screen", {"region": {"x": 1920, "y": 0, "width": 320, "height": 180}}, 60).ms for _ in range(30)]

    # UIA
    client.call("launch_or_place_app", {"path": UITARGET, "wait_ms": 15000}, 120)
    finds = [client.call("find_element", {"automation_id": "input"}, 60).ms for _ in range(args.shots)]
    metrics["find_element"] = finds
    trees = [client.call("get_ui_tree", {"max_depth": 6, "interactive_only": True}, 60) for _ in range(50)]
    metrics["get_ui_tree"] = [item.ms for item in trees]

    # 输入
    note = client.call("launch_or_place_app", {"path": NOTE, "wait_ms": 15000}, 120)
    client.call("click", {"x": 2560, "y": 360, "mode": "message", "keep_foreground": True}, 30)
    metrics["type_1k"] = [client.call("type_text", {"text": "x" * 1000, "verify": True}, 120).ms for _ in range(3)]
    metrics["type_10k"] = [client.call("type_text", {"text": "y" * 10000, "verify": True}, 240).ms for _ in range(3)]

    # 释放（keep_display=true，重复挂/放）
    releases, peaks = [], []
    for _ in range(args.release_cycles):
        result = client.call("release_environment", {"keep_display": False, "on_dirty": "discard", "allow_data_loss": True}, 120)
        releases.append(result.ms)
        ensure = client.call("get_environment", {"ensure": True}, 120)
        ensure_ms = ensure.ms
        metrics.setdefault("ensure_cycle", []).append(ensure_ms)
        peaks.append(rss_mb(client.proc.pid))
    metrics["release"] = releases
    final_rss = rss_mb(client.proc.pid)
    final_handles = handle_count(client.proc.pid)
    client.stop()

    # EOF 清理
    eof = []
    for _ in range(args.sessions):
        session = McpClient(args.exe).start()
        session.call("get_environment", {"ensure": True}, 120)
        session.close_stdin()
        started = time.perf_counter()
        session.wait(30)
        eof.append(round((time.perf_counter() - started) * 1000, 2))
        time.sleep(0.4)
    metrics["eof_cleanup"] = eof

    summary = {name: stats(values) for name, values in metrics.items()}
    write_json(os.path.join(perf_dir, "summary.json"), {
        "summary": summary,
        "first_shot": first_shot.data if first_shot else None,
        "final_rss_mb": final_rss,
        "final_handles": final_handles,
        "monitors": monitor_count(),
        "raw": metrics,
    })

    checks = [
        ("PERF-01", "启动握手 P95 ≤ 300ms", summary["handshake"]["p95"] <= 300),
        ("PERF-02", "挂屏 P95 ≤ 3000ms", summary["ensure_first"]["p95"] <= 3000),
        ("PERF-03", "截图 P95 ≤ 50ms", summary["capture_screen"]["p95"] <= 50),
        ("PERF-04", "UIA find P95 ≤ 100ms", summary["find_element"]["p95"] <= 100),
        ("PERF-05", "UIA tree P95 ≤ 200ms", summary["get_ui_tree"]["p95"] <= 200),
        ("PERF-06", "release P95 ≤ 3000ms", summary["release"]["p95"] <= 3000),
        ("PERF-07", "EOF 清理 P95 ≤ 5000ms", summary["eof_cleanup"]["p95"] <= 5000),
    ]
    for case_id, purpose, ok in checks:
        key = {"PERF-01": "handshake", "PERF-02": "ensure_first", "PERF-03": "capture_screen",
               "PERF-04": "find_element", "PERF-05": "get_ui_tree", "PERF-06": "release",
               "PERF-07": "eof_cleanup"}[case_id]
        cases.append(case(case_id, purpose, ok, observed=summary[key]))
    write_json(os.path.join(perf_dir, "cases.json"), cases)
    print(json.dumps(summary, ensure_ascii=False, indent=1))
    print_summary(cases)
    return 0


if __name__ == "__main__":
    sys.exit(main())
