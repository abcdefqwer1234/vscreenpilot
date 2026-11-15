"""S4/S21 验证：PowerPoint 的语义树规模、裁剪行为与快照成本。

用法：
  $env:VSP_PROFILE='pptbench'; $env:VSP_DISPLAY_OFFSET='0'
  python tools/audit/ppt/bench_ppt_snapshot.py --exe artifacts/server7/VScreenPilot.exe
"""

from __future__ import annotations

import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import ppt_common as common  # noqa: E402
from vsp_client import case, print_summary, run_dir, stats, write_json  # noqa: E402

PROBE_TEXT = "单击此处添加标题"
MATRIX = [
    ("SN-01", "PPT interactive_only=true depth4 nodes300", True, 4, 300, 15),
    ("SN-02", "PPT interactive_only=false depth4 nodes300", False, 4, 300, 15),
    ("SN-03", "PPT depth2 nodes100", True, 2, 100, 10),
    ("SN-04", "PPT depth6 nodes1024", True, 6, 1024, 10),
]


def run_matrix(client, tag, desc, interactive, depth, nodes, repeats, pid=None):
    latencies, node_counts, text_bytes, reasons = [], [], [], []
    truncated = 0
    probe_hits = 0
    sample_text = ""

    for _ in range(repeats):
        args = {
            "max_depth": depth,
            "max_nodes": nodes,
            "interactive_only": interactive,
        }
        if pid:
            args["pid"] = pid
        result = client.call("snapshot", args, 90)
        if not result.ok:
            return case(tag, desc, False, code=result.code, detail=result.data), {}

        data = result.data or {}
        latencies.append(result.ms)
        node_counts.append(int(data.get("nodes") or 0))
        text_bytes.append(result.text_bytes)
        truncated += 1 if data.get("truncated") else 0
        reasons.append(data.get("truncated_reason"))
        text = str(data.get("text") or "")
        if PROBE_TEXT in text:
            probe_hits += 1
        if not sample_text:
            sample_text = text[:1200]

    latency = stats(latencies)
    detail = {
        "repeats": repeats,
        "nodes": stats([float(v) for v in node_counts]),
        "text_bytes": stats([float(v) for v in text_bytes]),
        "truncated_ratio": round(truncated / max(1, repeats), 2),
        "truncated_reasons": sorted({str(r) for r in reasons if r}),
        "probe_text_hits": f"{probe_hits}/{repeats}",
    }
    ok = latency.get("n", 0) == repeats
    return case(tag, desc, ok, latency_ms=latency, **detail), {
        "text_head": sample_text,
        "latencies": latencies,
    }


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    out = args.out or run_dir("ppt-snapshot")
    cases: list[dict] = []
    artifacts: dict = {}
    client = common.open_client(args.exe)

    try:
        common.ensure_clean(client)
        display = common.display_rect(client)
        launch = common.safe_launch(client)
        cases.append(case("SN-00", "PowerPoint 启动并进入空白演示文稿",
                          bool(launch.get("ok")), launch=launch,
                          display={k: display.get(k) for k in ("x", "y", "width", "height")}))

        for tag, desc, interactive, depth, nodes, repeats in MATRIX:
            item, evidence = run_matrix(client, tag, desc, interactive, depth, nodes, repeats)
            cases.append(item)
            if evidence:
                artifacts[tag] = evidence

        # 记事本基线：同一机器、同一虚拟屏。
        notepad = client.call("launch_or_place_app", {"path": "notepad.exe", "wait_ms": 20000}, 90)
        if notepad.ok:
            np_pid = (notepad.data or {}).get("pid")
            client.call("type_text", {"text": "baseline", "verify": False}, 60)
            item, evidence = run_matrix(
                client, "SN-05", "记事本 interactive_only=true depth4 nodes300",
                True, 4, 300, 10, pid=np_pid)
            cases.append(item)
            if evidence:
                artifacts["SN-05"] = evidence
        else:
            cases.append(case("SN-05", "记事本基线", False, code=notepad.code))

        snap = client.call("capture_screen", {"scale": 0.5}, 60)
        cases.append(case("SN-06", "虚拟屏截图可用", snap.ok, code=snap.code,
                          png_bytes=(snap.data or {}).get("png_bytes"), ms=snap.ms))
    finally:
        released = common.release(client)
        cases.append(case("SN-99", "释放会话并摘屏", bool(released.get("ok")),
                          display_removed=released.get("display_removed"),
                          warnings=released.get("warnings")))
        client.stop()

    write_json(os.path.join(out, "summary.json"), {
        "suite": "ppt-snapshot",
        "cases": cases,
        "artifacts": artifacts,
    })
    print_summary(cases)
    print("结果目录:", out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
