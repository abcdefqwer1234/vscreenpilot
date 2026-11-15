"""输入通道：长度 × 通道的注入耗时、吞吐与剪贴板副作用。"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import ppt_common as common  # noqa: E402
from vsp_client import case, print_summary, stats, write_json  # noqa: E402

TEXTS = {
    "20": "VScreenPilot 能力测评" * 1,
    "200": ("虚拟屏隔离，不打扰主桌面；UIA 语义定位与 MSAA 兜底。") * 6,
    "2000": ("VScreenPilot 在虚拟屏上完成自动化，输入通道包含 UIA、窗口消息与 SendInput。" * 30),
    "20000": ("VScreenPilot 长文本输入测试。中文与 English mixed 0123456789。" * 300)[:20000],
}


def clipboard_get() -> str:
    try:
        out = subprocess.run(
            ["powershell", "-NoProfile", "-Command", "Get-Clipboard -Raw"],
            capture_output=True, text=True, timeout=10)
        return (out.stdout or "").strip()
    except Exception:
        return ""


def clipboard_set(value: str) -> None:
    subprocess.run(
        ["powershell", "-NoProfile", "-Command", f"Set-Clipboard -Value '{value}'"],
        capture_output=True, text=True, timeout=10)


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
    rows: list[dict] = []
    client = common.open_client(args.exe)

    try:
        common.ensure_clean(client)
        display = common.display_rect(client)
        launch = common.safe_launch(client)
        cases.append(case("IN-00", "PowerPoint 就绪", bool(launch.get("ok")), launch=launch))

        area = common.slide_area(client)
        tx, ty = common.title_point(area)
        client.call("click", {"x": tx, "y": ty, "channel": "message", "keep_foreground": True}, 30)

        for label, text in TEXTS.items():
            channels = ["auto", "clipboard"] + (["keys"] if label != "20000" else [])
            for channel in channels:
                # 清空：进入编辑后 Ctrl+A + Delete
                client.call("press_keys", {"keys": "Ctrl+A", "keep_foreground": True}, 30)
                client.call("press_keys", {"keys": "Delete", "keep_foreground": True}, 30)
                result = client.call(
                    "type_text",
                    {"text": text, "verify": False, "channel": channel, "chunk_chars": 128},
                    180,
                )
                data = result.data or {}
                row = {
                    "chars": len(text),
                    "channel": channel,
                    "ok": result.ok,
                    "code": result.code,
                    "ms": result.ms,
                    "typed": data.get("typed"),
                    "method": data.get("method"),
                    "inject_ms": data.get("inject_ms"),
                    "chunks": data.get("chunks"),
                }
                rows.append(row)
                cases.append(case(
                    f"IN-{label}-{channel}", f"{len(text)} 字 / {channel}", result.ok,
                    ms=result.ms, typed=row["typed"], method=row["method"]))

        # 剪贴板副作用：非文本内容是否被破坏（S7）
        clipboard_set("SENTINEL-VSP-CLIP")
        before = clipboard_get()
        client.call("press_keys", {"keys": "Ctrl+A", "keep_foreground": True}, 30)
        client.call("press_keys", {"keys": "Delete", "keep_foreground": True}, 30)
        client.call("type_text", {"text": TEXTS["2000"], "verify": False, "channel": "clipboard"}, 120)
        after = clipboard_get()
        cases.append(case(
            "IN-CLIP-RESTORE", "长文本输入后文本剪贴板被还原",
            before == after and bool(before),
            before=before[:40], after=after[:40]))

        # 汇总
        for channel in ("auto", "clipboard", "keys"):
            values = [r["ms"] for r in rows if r["channel"] == channel and r["ok"]]
            if values:
                cases.append(case(
                    f"IN-SUM-{channel}", f"{channel} 延迟分布（{len(values)} 次）",
                    True, latency=stats(values)))
    finally:
        released = common.release(client)
        cases.append(case("IN-99", "释放会话", bool(released.get("ok")),
                          display_removed=released.get("display_removed")))
        client.stop()

    write_json(os.path.join(args.out, "summary.json"), {
        "suite": "ppt-input",
        "cases": cases,
        "rows": rows,
    })
    print_summary(cases)
    print("结果目录:", args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
