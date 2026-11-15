"""故障恢复：强杀 PowerPoint 后的错误码、重启与会话恢复。"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import ppt_common as common  # noqa: E402
from vsp_client import case, print_summary, write_json  # noqa: E402


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
        cases.append(case("RC-00", "PowerPoint 就绪", bool(launch.get("ok")), launch=launch))
        pid = (launch or {}).get("pid")

        subprocess.run(["taskkill", "/IM", "POWERPNT.EXE", "/F"], capture_output=True, timeout=30)
        time.sleep(1.5)

        broken = client.call("snapshot", {"max_depth": 2}, 30)
        cases.append(case("RC-01", "强杀后 snapshot 返回可识别错误",
                          (not broken.ok) and broken.code in ("E_NO_TARGET", "E_APP_GONE", "E_APP_EXITED"),
                          code=broken.code, message=(broken.data or {}).get("message")))

        typed = client.call("type_text", {"text": "x", "verify": False}, 30)
        cases.append(case("RC-02", "强杀后 type_text 返回可识别错误",
                          not typed.ok, code=typed.code))

        relaunch = common.safe_launch(client)
        cases.append(case("RC-03", "可重新启动并进入演示文稿",
                          bool(relaunch.get("ok")) and " - PowerPoint" in str(relaunch.get("title_after")),
                          launch=relaunch))

        env = client.call("get_environment", {}, 30)
        apps = (env.data or {}).get("apps") or []
        cases.append(case("RC-04", "会话内只有一个存活应用", len(apps) == 1,
                          apps=[{"pid": a.get("pid"), "alive": a.get("alive")} for a in apps],
                          old_pid=pid))
    finally:
        released = common.release(client)
        cases.append(case("RC-99", "释放会话", bool(released.get("ok")),
                          display_removed=released.get("display_removed")))
        client.stop()

    write_json(os.path.join(args.out, "summary.json"), {
        "suite": "ppt-recovery",
        "cases": cases,
    })
    print_summary(cases)
    print("结果目录:", args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
