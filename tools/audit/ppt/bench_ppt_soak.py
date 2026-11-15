"""40 轮 soak：观察 RSS/句柄增长与错误率。"""

from __future__ import annotations

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import ppt_common as common  # noqa: E402
from vsp_client import case, print_summary, stats, write_json  # noqa: E402


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--rounds", type=int, default=40)
    args = parser.parse_args()

    cases: list[dict] = []
    samples: list[dict] = []
    errors: list[str] = []
    latencies: list[float] = []
    client = common.open_client(args.exe)

    try:
        common.ensure_clean(client)
        display = common.display_rect(client)
        launch = common.safe_launch(client)
        cases.append(case("SO-00", "PowerPoint 就绪", bool(launch.get("ok")), launch=launch))

        area = common.slide_area(client)
        tx, ty = common.title_point(area)
        base = client.sample()
        cases.append(case("SO-01", "基线采样", True, sample=base))

        for index in range(args.rounds):
            snap = client.call("snapshot", {"max_depth": 3, "max_nodes": 150}, 60)
            latencies.append(snap.ms)
            if not snap.ok:
                errors.append(f"snapshot:{snap.code}")
            client.call("click", {"x": tx, "y": ty, "channel": "message", "keep_foreground": True}, 30)
            typed = client.call("type_text", {"text": f"r{index}", "verify": False}, 60)
            if not typed.ok:
                errors.append(f"type:{typed.code}")
            shot = client.call(
                "capture_screen",
                {"region": {"x": display["x"], "y": display["y"], "width": 400, "height": 300}, "inline": False},
                60,
            )
            if not shot.ok:
                errors.append(f"capture:{shot.code}")
            if index % 10 == 9:
                samples.append({"round": index + 1, **client.sample()})

        last = samples[-1] if samples else client.sample()
        cases.append(case(
            "SO-02", f"{args.rounds} 轮后无错误", not errors,
            errors=errors[:6], snapshot_latency=stats(latencies)))
        cases.append(case(
            "SO-03", "RSS 增长 <= 50MB",
            last.get("rss_mb", 0) - base.get("rss_mb", 0) <= 50,
            before=base.get("rss_mb"), after=last.get("rss_mb")))
        cases.append(case(
            "SO-04", "句柄增长 <= 100",
            last.get("handles", 0) - base.get("handles", 0) <= 100,
            before=base.get("handles"), after=last.get("handles")))
    finally:
        released = common.release(client)
        cases.append(case("SO-99", "释放会话", bool(released.get("ok")),
                          display_removed=released.get("display_removed")))
        client.stop()

    write_json(os.path.join(args.out, "summary.json"), {
        "suite": "ppt-soak",
        "rounds": args.rounds,
        "cases": cases,
        "samples": samples,
    })
    print_summary(cases)
    print("结果目录:", args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
