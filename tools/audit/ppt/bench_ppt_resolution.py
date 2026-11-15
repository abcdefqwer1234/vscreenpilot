"""1920x1080 对照：点击成功率与快照成本（C8）。"""

from __future__ import annotations

import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import ppt_common as common  # noqa: E402
from vsp_client import McpClient, case, print_summary, stats, write_json  # noqa: E402

DECK = os.path.join(common.DECK_DIR, "VScreenPilot-PPT-测评.pptx")
PROFILE = "pptres"


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    client = McpClient(
        args.exe,
        env={"VSP_PROFILE": PROFILE, "VSP_DISPLAY_OFFSET": "0",
             "VSP_DISPLAY_WIDTH": "1920", "VSP_DISPLAY_HEIGHT": "1080"},
        args=["--profile", PROFILE, "--parent-pid", str(os.getpid())],
    ).start(timeout=60, warmup=False, protocol="2025-11-25")

    cases: list[dict] = []
    try:
        env = client.call("get_environment", {}, 30)
        vd = (env.data or {}).get("virtual_display") or {}
        if vd.get("enabled"):
            client.call("release_environment",
                        {"keep_display": False, "on_dirty": "force", "allow_data_loss": True}, 90)
        display = common.display_rect(client)
        cases.append(case("RES-00", "1920x1080 虚拟屏", 
                          display.get("width") == 1920 and display.get("height") == 1080,
                          display={k: display.get(k) for k in
                                   ("width", "height", "dpi", "x", "y")}))

        app = client.call("launch_or_place_app", {
            "path": common.POWERPNT, "args": f'"{DECK}"', "wait_ms": 60000}, 150)
        time.sleep(3)
        cases.append(case("RES-01", "打开 deck", app.ok, title=(app.data or {}).get("title"),
                          ms=app.ms))

        latencies = []
        for _ in range(10):
            snap = client.call("snapshot", {"max_depth": 4, "max_nodes": 300}, 60)
            latencies.append(snap.ms)
        cases.append(case("RES-02", "快照成本", True, latency=stats(latencies),
                          nodes=(snap.data or {}).get("nodes")))

        # 功能区与画布 8 个点：auto 通道成功率
        points = [(60, 70), (150, 70), (260, 70), (380, 70),
                  (600, 300), (700, 450), (500, 640), (150, 640)]
        ok = 0
        results = []
        for dx, dy in points:
            click = client.call("click", {
                "x": int(display["x"] + dx * 1.5), "y": int(display["y"] + dy * 1.5),
                "channel": "auto", "keep_foreground": True}, 30)
            ok += 1 if click.ok else 0
            results.append({"x": dx, "y": dy, "ok": click.ok,
                            "code": click.code,
                            "message": (click.data or {}).get("message"),
                            "via": (click.data or {}).get("channel")})
        cases.append(case("RES-03", "1080p 点击 8/8", ok == len(points),
                          passed=ok, results=results))

        shot_path = os.path.join(os.environ.get("TEMP", "."), "vsp-res-1080p.png")
        if os.path.exists(shot_path):
            os.remove(shot_path)
        shot = client.call("capture_screen", {"scale": 0.5, "inline": False,
                                              "save_path": shot_path}, 60)
        cases.append(case("RES-04", "1080p 截图", shot.ok,
                          width=(shot.data or {}).get("width"),
                          height=(shot.data or {}).get("height"),
                          ms=shot.ms))
    finally:
        released = common.release(client)
        cases.append(case("RES-99", "释放", bool(released.get("ok")),
                          display_removed=released.get("display_removed")))
        client.stop()

    write_json(os.path.join(args.out, "summary.json"),
               {"suite": "ppt-resolution", "cases": cases})
    print_summary(cases)
    print("结果目录:", args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
