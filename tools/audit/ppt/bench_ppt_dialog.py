"""对话框正例：find_element / get_element_text / set_element_value / click_ref。"""

from __future__ import annotations

import argparse
import os
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
        cases.append(case("DLG-00", "PowerPoint 就绪", bool(launch.get("ok")), launch=launch))

        area = common.slide_area(client)
        tx, ty = common.title_point(area)
        client.call("click", {"x": tx, "y": ty, "channel": "message",
                              "keep_foreground": True}, 30)
        client.call("press_keys", {"keys": "Ctrl+T", "keep_foreground": True}, 30)
        time.sleep(0.8)

        found = client.call("find_element", {"name": "大小", "control_type": "Edit"}, 30)
        cases.append(case("DLG-01", "find_element 在字体对话框命中",
                          found.ok, ref=(found.data or {}).get("ref"),
                          bounds=(found.data or {}).get("bounds"),
                          code=found.code))
        ref = (found.data or {}).get("ref")

        if ref:
            read = client.call("get_element_text", {"ref": ref}, 30)
            cases.append(case("DLG-02", "get_element_text 读到字号",
                              read.ok and bool((read.data or {}).get("text")),
                              text=(read.data or {}).get("text"), code=read.code))

            written = client.call("set_element_value", {
                "ref": ref, "value": "36", "allow_destructive": True,
                "allow_keyboard": True, "mode": "replace"}, 30)
            cases.append(case("DLG-03", "set_element_value 写入字号",
                              written.ok and (written.data or {}).get("verified") is True,
                              value=(written.data or {}).get("value"), code=written.code))

        ok_button = client.call("find_element", {"name": "确定", "control_type": "Button"}, 30)
        cases.append(case("DLG-04", "find_element 找到确定按钮", ok_button.ok,
                          code=ok_button.code))
        if ok_button.ok:
            clicked = client.call("click_ref", {"ref": (ok_button.data or {}).get("ref")}, 30)
            cases.append(case("DLG-05", "click_ref 关闭对话框", clicked.ok,
                              pattern=(clicked.data or {}).get("pattern"),
                              code=clicked.code))
    finally:
        released = common.release(client)
        cases.append(case("DLG-99", "释放会话", bool(released.get("ok")),
                          display_removed=released.get("display_removed")))
        client.stop()

    write_json(os.path.join(args.out, "summary.json"),
               {"suite": "ppt-dialog", "cases": cases})
    print_summary(cases)
    print("结果目录:", args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
