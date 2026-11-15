"""P0 对照实验：mode=input 的绝对坐标是否落到虚拟屏（原始 vs 补丁）。

用法：
  python tools/audit/verify_virtualdesk.py --original artifacts\\server\\VScreenPilot.exe --patched artifacts\\patched\\VScreenPilot.exe
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import McpClient, default_exe, cursor_pos, run_dir, write_json, case, print_summary  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
CLICKPROBE = os.path.join(HERE, "targets", "bin", "ClickProbe", "ClickProbe.exe")


def run_click_probe(exe: str, label: str, out_dir: str) -> dict:
    log_path = os.path.abspath(os.path.join(out_dir, f"clicks-{label}.jsonl"))
    if os.path.exists(log_path):
        os.remove(log_path)
    client = McpClient(exe).start()
    env = client.call("get_environment", {"ensure": True}, 120)
    display = (env.data or {}).get("virtual_display", {})
    origin = {"x": display.get("x", 1920), "y": display.get("y", 0)}
    launch = client.call("launch_or_place_app", {"path": CLICKPROBE, "args": log_path, "wait_ms": 15000}, 120)
    bounds = (launch.data or {}).get("bounds") or {}
    time.sleep(0.6)
    # 预热：窗口首次被点击时会被“点击激活”吞掉，因此先用窗口消息激活并清空日志
    client.call("click", {"x": origin["x"] + 80, "y": origin["y"] + 80, "mode": "message", "keep_foreground": True}, 30)
    time.sleep(0.4)
    if os.path.exists(log_path):
        open(log_path, "w", encoding="utf-8").close()
    expected = {"x": origin["x"] + 320, "y": origin["y"] + 240}
    before_cursor = cursor_pos()
    result = client.call("click", {"x": expected["x"], "y": expected["y"], "mode": "input", "keep_foreground": True}, 60)
    time.sleep(0.8)
    after_cursor = cursor_pos()
    records = []
    if os.path.exists(log_path):
        with open(log_path, encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if line:
                    records.append(json.loads(line))
    downs = [item for item in records if item.get("msg") == 0x0201]
    observed = downs[-1]["screen"] if downs else None
    delta = None if observed is None else {
        "dx": observed["x"] - expected["x"],
        "dy": observed["y"] - expected["y"],
    }
    # 原始构建会把坐标映射到主屏：预期落在 primary 的比例位置
    client.call("release_environment", {"keep_display": False, "on_dirty": "discard", "allow_data_loss": True}, 120)
    client.stop()
    return {
        "label": label,
        "exe": exe,
        "display": display,
        "expected": expected,
        "click_code": result.code or "ok",
        "click_ms": result.ms,
        "observed": observed,
        "delta": delta,
        "records": len(records),
        "bounds": bounds,
        "cursor_before": before_cursor,
        "cursor_after": after_cursor,
    }


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    parser = argparse.ArgumentParser()
    parser.add_argument("--original", default=default_exe())
    parser.add_argument("--patched", default=None)
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    out_dir = args.out or run_dir("virtualdesk")
    os.makedirs(os.path.join(out_dir, "virtualdesk"), exist_ok=True)
    cases: list[dict] = []
    results = {}

    original = run_click_probe(args.original, "original", os.path.join(out_dir, "virtualdesk"))
    results["original"] = original
    cases.append(case("P0-01", "原始构建：mode=input 点击落到虚拟屏坐标",
                      original["delta"] is not None and abs(original["delta"]["dx"]) <= 2 and abs(original["delta"]["dy"]) <= 2,
                      expected=original["expected"], observed=original["observed"],
                      delta=original["delta"], records=original["records"]))

    if args.patched and os.path.exists(args.patched):
        patched = run_click_probe(args.patched, "patched", os.path.join(out_dir, "virtualdesk"))
        results["patched"] = patched
        cases.append(case("P0-02", "补丁构建（VIRTUALDESK）：点击落到虚拟屏坐标",
                          patched["delta"] is not None and abs(patched["delta"]["dx"]) <= 2 and abs(patched["delta"]["dy"]) <= 2,
                          expected=patched["expected"], observed=patched["observed"],
                          delta=patched["delta"], records=patched["records"]))

    write_json(os.path.join(out_dir, "virtualdesk", "summary.json"), {"cases": cases, "results": results})
    print_summary(cases)
    print(json.dumps({k: {kk: vv for kk, vv in v.items() if kk in ("expected", "observed", "delta", "records", "click_code")}
                      for k, v in results.items()}, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
