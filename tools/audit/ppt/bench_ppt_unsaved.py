"""未保存内容 + release 的默认行为（S6）。"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import ppt_common as common  # noqa: E402
from vsp_client import case, print_summary, write_json  # noqa: E402


def powerpnt_running() -> bool:
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq POWERPNT.EXE"],
                         capture_output=True, text=True, timeout=20)
    return "POWERPNT.EXE" in (out.stdout or "")


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
        display = common.display_rect(client)
        launch = common.safe_launch(client)
        cases.append(case("UNS-00", "PowerPoint 就绪", bool(launch.get("ok")), launch=launch))

        area = common.slide_area(client)
        tx, ty = common.title_point(area)
        client.call("click", {"x": tx, "y": ty, "channel": "message",
                              "keep_foreground": True}, 30)
        client.call("type_text", {"text": "UNSAVED-MARKER-未保存内容",
                                  "verify": False}, 60)

        # 不保存，直接 release（默认 force）
        released = client.call("release_environment",
                               {"keep_display": False, "on_dirty": "force"}, 120)
        time.sleep(1.5)
        data = released.data or {}
        cases.append(case(
            "UNS-01", "默认 force release 直接结束进程",
            released.ok and powerpnt_running() is False,
            forced=data.get("forced_pids"), closed=data.get("closed_pids"),
            warnings=data.get("warnings")))
        cases.append(case(
            "UNS-02", "未出现保存对话框（无人工确认）",
            not any("对话框" in str(w) for w in (data.get("warnings") or [])),
            warnings=data.get("warnings")))
    finally:
        common.release(client)
        client.stop()

    write_json(os.path.join(args.out, "summary.json"),
               {"suite": "ppt-unsaved", "cases": cases})
    print_summary(cases)
    print("结果目录:", args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
