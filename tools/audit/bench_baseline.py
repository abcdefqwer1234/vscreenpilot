"""基线性能采样（只读，不注入输入、不启动外部应用）。

用途：P0.4 的“安静基线”。需要 VDD，但不点击、不输入、不开窗口，
因此可以在开发者自己的桌面上跑，不会干扰正在使用的会话。

用法：
  python tools/audit/bench_baseline.py --exe artifacts/server/VScreenPilot.exe --out tools/audit/baseline/3ddb85b

输出：<out>/baseline/summary.json 与 cases.json（case 只含 id/ok，便于跨阶段对比通过集）。
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import (  # noqa: E402
    McpClient, default_exe, handle_count, rss_mb, run_dir, stats, write_json,
)

CASES = [
    ("BL-01", "握手 P95 ≤ 300ms", "handshake"),
    ("BL-02", "首次挂屏 P95 ≤ 3000ms", "ensure_first"),
    ("BL-03", "全屏截图 P95 ≤ 50ms", "capture_screen"),
    ("BL-04", "区域截图 P95 ≤ 50ms", "capture_region"),
    ("BL-05", "get_environment（已挂屏）P95 ≤ 200ms", "get_environment"),
    ("BL-06", "空闲 RSS ≤ 400MB", "rss_mb"),
]

THRESHOLDS = {
    "BL-01": 300.0,
    "BL-02": 3000.0,
    "BL-03": 50.0,
    "BL-04": 50.0,
    "BL-05": 200.0,
    "BL-06": 400.0,
}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", default=default_exe())
    parser.add_argument("--out", default=None)
    parser.add_argument("--shots", type=int, default=50)
    parser.add_argument("--env-polls", type=int, default=20)
    parser.add_argument("--sessions", type=int, default=5)
    parser.add_argument("--profile", default="vspaudit",
                        help="审计 profile；不要用 default，避免打扰正在使用的会话")
    args = parser.parse_args()

    env = {"VSP_PROFILE": args.profile}

    out_dir = args.out or run_dir("baseline")
    baseline_dir = os.path.join(out_dir, "baseline")
    os.makedirs(baseline_dir, exist_ok=True)

    metrics: dict[str, list[float]] = {}

    # 冷启动握手
    handshake: list[float] = []
    for _ in range(5):
        started = time.perf_counter()
        client = McpClient(args.exe, env=env).start(init=False)
        client.request("initialize", {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "vsp-audit-py", "version": "1.0"},
        }, 60)
        client.notify("notifications/initialized")
        handshake.append(round((time.perf_counter() - started) * 1000, 2))
        client.stop()
    metrics["handshake"] = handshake

    client = McpClient(args.exe, env=env).start()
    try:
        ensure = client.call("get_environment", {"ensure": True}, 180)
        if not ensure.ok:
            print(f"挂屏失败：{ensure.code} {ensure.message}", file=sys.stderr)
            return 1
        metrics["ensure_first"] = [ensure.ms]

        display = (ensure.data or {}).get("virtual_display") or {}
        screen = (ensure.data or {}).get("virtual_screen") or {}
        origin_x = int(display.get("x", screen.get("x", 0)) or 0)
        origin_y = int(display.get("y", screen.get("y", 0)) or 0)

        metrics["capture_screen"] = [client.call("capture_screen", {"inline": False}, 60).ms
                                     for _ in range(args.shots)]
        metrics["capture_region"] = [
            client.call("capture_screen", {
                "region": {"x": origin_x, "y": origin_y, "width": 320, "height": 180},
                "inline": False,
            }, 60).ms
            for _ in range(args.shots)
        ]
        metrics["get_environment"] = [client.call("get_environment", {}, 60).ms
                                      for _ in range(args.env_polls)]

        time.sleep(0.5)
        idle_rss = rss_mb(client.proc.pid)
        idle_handles = handle_count(client.proc.pid)
    finally:
        client.stop()

    # 断开后的清理耗时（不挂屏，只测生命周期）
    eof: list[float] = []
    for _ in range(args.sessions):
        session = McpClient(args.exe, env=env).start()
        session.call("get_environment", {}, 60)
        session.close_stdin()
        started = time.perf_counter()
        session.wait(30)
        eof.append(round((time.perf_counter() - started) * 1000, 2))
        time.sleep(0.3)
    metrics["eof_cleanup"] = eof

    summary = {name: stats(values) for name, values in metrics.items()}
    observed = {
        "BL-01": summary["handshake"]["p95"],
        "BL-02": summary["ensure_first"]["p95"],
        "BL-03": summary["capture_screen"]["p95"],
        "BL-04": summary["capture_region"]["p95"],
        "BL-05": summary["get_environment"]["p95"],
        "BL-06": idle_rss,
    }

    cases = []
    for case_id, purpose, _key in CASES:
        value = observed[case_id]
        cases.append({
            "id": case_id,
            "purpose": purpose,
            "ok": value is not None and value >= 0 and value <= THRESHOLDS[case_id],
            "observed": value,
            "threshold": THRESHOLDS[case_id],
        })

    write_json(os.path.join(baseline_dir, "summary.json"), {
        "summary": summary,
        "idle_rss_mb": idle_rss,
        "idle_handles": idle_handles,
        "display": {"x": origin_x, "y": origin_y},
        "raw": metrics,
    })
    write_json(os.path.join(baseline_dir, "cases.json"), cases)

    print(json.dumps({"summary": summary, "idle_rss_mb": idle_rss, "idle_handles": idle_handles},
                     ensure_ascii=False, indent=1))
    for item in cases:
        print(f"[{'PASS' if item['ok'] else 'FAIL'}] {item['id']} {item['purpose']} = {item['observed']}")

    return 0 if all(item["ok"] for item in cases) else 1


if __name__ == "__main__":
    sys.exit(main())
