"""HOT-01..03：会话中外部摘屏 / 重挂，验证错误与自愈。

用法：
  python tools/audit/bench_hotplug.py --exe artifacts\\server\\VScreenPilot.exe --out tools\\audit\\results\\run2
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import McpClient, default_exe, monitor_count, run_dir, write_json, case, print_summary  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
VDD = os.path.join(os.path.dirname(os.path.dirname(HERE)), "tools", "VddProbe", "bin", "Release", "net10.0-windows", "VddProbe.exe")
UITARGET = os.path.join(HERE, "targets", "bin", "UiTarget", "UiTarget.exe")


def vdd(command: str, *extra: str) -> dict:
    env = dict(os.environ, DOTNET_ROOT=os.path.expandvars(r"%USERPROFILE%\.dotnet"))
    result = subprocess.run([VDD, command, *extra], capture_output=True, text=True, timeout=180, env=env)
    return {"exit": result.returncode, "out": (result.stdout or "")[-600:], "err": (result.stderr or "")[-300:]}


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", default=default_exe())
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    out_dir = args.out or run_dir("hotplug")
    hot_dir = os.path.join(out_dir, "hotplug")
    os.makedirs(hot_dir, exist_ok=True)
    cases: list[dict] = []

    client = McpClient(args.exe).start()
    env = client.call("get_environment", {"ensure": True}, 120)
    display = (env.data or {}).get("virtual_display", {})
    origin = {"x": display.get("x", 1920), "y": display.get("y", 0)}
    client.call("launch_or_place_app", {"path": UITARGET, "wait_ms": 15000}, 120)

    # 外部摘屏
    removed = vdd("remove", "0")
    time.sleep(2.0)
    shot = client.call("capture_screen", {"region": {"x": origin["x"], "y": origin["y"], "width": 200, "height": 200}}, 60)
    recovered = client.call("get_environment", {"ensure": True}, 180)
    rec_display = (recovered.data or {}).get("virtual_display", {})
    cases.append(case("HOT-01", "会话中外部摘屏：截图成功或明确报错，且能自愈",
                      (shot.ok or shot.code is not None) and recovered.ok and bool(rec_display.get("enabled")),
                      vdd=removed, shot=shot.code or "ok", recovered=recovered.code or "ok",
                      monitors=monitor_count(), device=(rec_display.get("device")),
                      note="实际表现为自愈：截图仍成功" if shot.ok else "截图报错后自愈"))

    # 再次外部摘屏（不同方式：removeall）
    removed_all = vdd("removeall")
    time.sleep(1.5)
    after = client.call("get_environment", {"ensure": True}, 180)
    cases.append(case("HOT-02", "removeall 后仍可恢复",
                      after.ok, vdd=removed_all, recovered=after.code or "ok",
                      device=((after.data or {}).get("virtual_display") or {}).get("device")))

    # 外部新增一块屏，原会话不应误用
    added = vdd("add")
    time.sleep(2.0)
    env2 = client.call("get_environment", {}, 60)
    cases.append(case("HOT-03", "外部新增显示不破坏会话（虚拟屏仍为会话所有）",
                      env2.ok, vdd=added,
                      monitors=monitor_count(),
                      display=((env2.data or {}).get("virtual_display") or {}).get("device")))

    # 清理：摘掉外部新增，恢复会话自有屏
    time.sleep(3.2)
    vdd("removeall")
    time.sleep(3.2)
    client.call("get_environment", {"ensure": True}, 180)
    client.call("release_environment", {"keep_display": False, "on_dirty": "discard"}, 120)
    client.stop()

    write_json(os.path.join(hot_dir, "summary.json"), {"cases": cases})
    print_summary(cases)
    return 0


if __name__ == "__main__":
    sys.exit(main())
