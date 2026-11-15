"""并发：长输入期间只读观察是否被阻塞，以及 release 抢占的行为。"""

from __future__ import annotations

import argparse
import os
import sys
import threading
import time

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
    args = parser.parse_args()

    cases: list[dict] = []
    client = common.open_client(args.exe)

    try:
        common.ensure_clean(client)
        common.display_rect(client)
        launch = common.safe_launch(client)
        cases.append(case("CC-00", "PowerPoint 就绪", bool(launch.get("ok")), launch=launch))

        area = common.slide_area(client)
        tx, ty = common.title_point(area)
        client.call("click", {"x": tx, "y": ty, "channel": "message", "keep_foreground": True}, 30)

        long_text = ("并发输入测试。VScreenPilot。" * 900)[:18000]
        writer_result: dict = {}

        def writer():
            result = client.call(
                "type_text", {"text": long_text, "verify": False, "channel": "clipboard"}, 180)
            writer_result.update({"ok": result.ok, "code": result.code, "ms": result.ms})

        thread = threading.Thread(target=writer)
        thread.start()
        time.sleep(0.15)

        reads = []
        for _ in range(8):
            snap = client.call("snapshot", {"max_depth": 3, "max_nodes": 200}, 60)
            reads.append({"ok": snap.ok, "code": snap.code, "ms": snap.ms,
                          "nodes": (snap.data or {}).get("nodes")})
        thread.join(timeout=200)

        cases.append(case("CC-01", "长输入期间 snapshot 不被阻塞",
                          all(r["ok"] for r in reads),
                          reads=reads,
                          latency=stats([r["ms"] for r in reads])))
        cases.append(case("CC-02", "长输入完成", bool(writer_result.get("ok")),
                          writer=writer_result))

        # release 抢占：在另一个长输入进行时调用 release
        release_holder: dict = {}
        second = threading.Thread(target=lambda: release_holder.update(
            {"result": common.release(client)}))
        input_again = threading.Thread(target=lambda: client.call(
            "type_text", {"text": long_text, "verify": False, "channel": "clipboard"}, 180))
        input_again.start()
        time.sleep(0.1)
        second.start()
        second.join(timeout=60)
        input_again.join(timeout=200)

        released = release_holder.get("result") or {}
        cases.append(case("CC-03", "并发 release 返回明确结果",
                          bool(released.get("ok") or released.get("code")),
                          release=released))

        # 会话可恢复
        common.ensure_clean(client)
        env = client.call("get_environment", {"ensure": True}, 120)
        cases.append(case("CC-04", "release 后会话可重建", env.ok, code=env.code, ms=env.ms))
    finally:
        common.release(client)
        client.stop()

    write_json(os.path.join(args.out, "summary.json"), {
        "suite": "ppt-concurrency",
        "cases": cases,
    })
    print_summary(cases)
    print("结果目录:", args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
