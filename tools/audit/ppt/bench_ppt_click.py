"""点击通道矩阵：功能区/画布坐标在 auto/message/input 下的成功率与延迟。"""

from __future__ import annotations

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import ppt_common as common  # noqa: E402
from vsp_client import case, print_summary, stats, write_json  # noqa: E402

POINTS = [
    ("ribbon-home", 1948, 70),
    ("ribbon-insert", 2050, 70),
    ("ribbon-design", 2140, 70),
    ("ribbon-tab-slide", 2250, 70),
    ("slide-title", 2485, 300),
    ("slide-body", 2485, 420),
    ("status-bar", 2300, 660),
    ("thumb-pane", 1975, 300),
]


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    cases: list[dict] = []
    per_channel: dict[str, list] = {"auto": [], "message": [], "input": []}
    client = common.open_client(args.exe)

    try:
        common.ensure_clean(client)
        display = common.display_rect(client)
        launch = common.safe_launch(client)
        cases.append(case("CK-00", "PowerPoint 就绪", bool(launch.get("ok")), launch=launch,
                          display={k: display.get(k) for k in ("x", "y", "width", "height")}))

        # 画布坐标随显示器原点平移
        dx = display.get("x", 1920) - 1920
        dy = display.get("y", 0)
        for name, x, y in POINTS:
            for channel in ("auto", "message", "input"):
                result = client.call(
                    "click",
                    {"x": x + dx, "y": y + dy, "channel": channel, "keep_foreground": True},
                    30,
                )
                record = {
                    "point": name,
                    "channel": channel,
                    "ok": result.ok,
                    "code": result.code,
                    "ms": result.ms,
                    "resolved": (result.data or {}).get("channel") or (result.data or {}).get("method"),
                }
                per_channel[channel].append(record)
                if channel == "auto":
                    cases.append(case(
                        f"CK-{name}", f"{name} auto 点击", result.ok,
                        code=result.code, ms=result.ms, via=record["resolved"]))

        for channel, records in per_channel.items():
            passed = sum(1 for r in records if r["ok"])
            cases.append(case(
                f"CK-SUM-{channel}", f"{channel} 通道成功率",
                passed == len(records),
                passed=passed, total=len(records),
                latency=stats([r["ms"] for r in records if r["ok"]]),
                failures=[r for r in records if not r["ok"]][:3]))

        # 双击进入文本编辑：语义层无法判定，只能记录是否“返回成功”。
        dbl = []
        for index in range(5):
            result = client.call(
                "click",
                {"x": 2485 + dx, "y": 300 + dy, "clicks": 2, "channel": "message", "keep_foreground": True},
                30,
            )
            dbl.append({"ok": result.ok, "code": result.code, "ms": result.ms})
        cases.append(case("CK-DBL", "双击（进入文本编辑）消息通道 5 次",
                          all(item["ok"] for item in dbl), runs=dbl))
    finally:
        released = common.release(client)
        cases.append(case("CK-99", "释放会话", bool(released.get("ok")),
                          display_removed=released.get("display_removed")))
        client.stop()

    write_json(os.path.join(args.out, "summary.json"), {
        "suite": "ppt-click",
        "cases": cases,
        "matrix": per_channel,
    })
    print_summary(cases)
    print("结果目录:", args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
