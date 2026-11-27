"""PPT 轮 SLO 门禁：读取 ppt-run 下的 summary.json 并判定阈值。"""

from __future__ import annotations

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "results", "ppt-run"))
DECK_DIR = os.path.normpath(os.path.join(HERE, "..", "..", "..", "artifacts", "ppt"))
THRESHOLDS = {
    "snapshot_p95_ms": 1500,
    "click_p95_ms": 300,
    "type_text_200_p95_ms": 1200,
    "soak_rss_growth_mb": 50,
    "soak_handle_growth": 100,
}


def load(name: str) -> dict | None:
    path = os.path.join(ROOT, name, "summary.json")
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def find(cases: list[dict], cid: str) -> dict | None:
    return next((c for c in cases if c.get("id") == cid), None)


def main() -> int:
    failures = []
    report = {}

    snap = load("ppt-snapshot")
    if snap:
        p95 = max(
            (c.get("latency_ms", {}).get("p95", 0) for c in snap["cases"] if "latency_ms" in c),
            default=0,
        )
        report["snapshot_p95_ms"] = p95
        if p95 > THRESHOLDS["snapshot_p95_ms"]:
            failures.append(f"snapshot p95 {p95} > {THRESHOLDS['snapshot_p95_ms']}")

    click = load("ppt-click")
    if click:
        values = [
            r["ms"] for rows in click.get("matrix", {}).values() for r in rows if r["ok"]
        ]
        values.sort()
        p95 = values[min(len(values) - 1, int(round(0.95 * (len(values) - 1))))] if values else 0
        report["click_p95_ms"] = p95
        if p95 > THRESHOLDS["click_p95_ms"]:
            failures.append(f"click p95 {p95} > {THRESHOLDS['click_p95_ms']}")
        report["click_success"] = (
            f"{sum(1 for rows in click.get('matrix', {}).values() for r in rows if r['ok'])}"
            f"/{sum(len(rows) for rows in click.get('matrix', {}).values())}"
        )

    text = load("ppt-input")
    if text:
        candidates = [
            r["ms"] for r in text.get("rows", [])
            if r["channel"] == "clipboard" and 150 <= r["chars"] <= 250
        ]
        p95 = max(candidates) if candidates else 0
        report["type_text_200_p95_ms"] = p95
        if p95 > THRESHOLDS["type_text_200_p95_ms"]:
            failures.append(f"type_text(200) {p95} > {THRESHOLDS['type_text_200_p95_ms']}")
        report["clipboard_restore"] = next(
            (c["ok"] for c in text["cases"] if c["id"] == "IN-CLIP-RESTORE"), None)

    soak = load("ppt-soak")
    if soak:
        growth = find(soak["cases"], "SO-03") or {}
        handles = find(soak["cases"], "SO-04") or {}
        rss_growth = (growth.get("after") or 0) - (growth.get("before") or 0)
        handle_growth = (handles.get("after") or 0) - (handles.get("before") or 0)
        report["soak_rss_growth_mb"] = round(rss_growth, 2)
        report["soak_handle_growth"] = handle_growth
        if rss_growth > THRESHOLDS["soak_rss_growth_mb"]:
            failures.append(f"soak rss +{rss_growth}MB")
        if handle_growth > THRESHOLDS["soak_handle_growth"]:
            failures.append(f"soak handles +{handle_growth}")

    flow = load("ppt-flow")
    if flow:
        report["flow_wall_ms"] = flow.get("wall_ms")
        report["flow_saved"] = (find(flow["cases"], "FL-00") or {}).get("ok")

    extra = load("ppt-extra")
    if extra:
        failed = [c["id"] for c in extra["cases"] if not c.get("ok")]
        report["known_defects"] = failed
        report["extra_passed"] = f"{len(extra['cases']) - len(failed)}/{len(extra['cases'])}"

    deck = os.path.join(DECK_DIR, "oracle.json")
    if os.path.exists(deck):
        with open(deck, encoding="utf-8") as handle:
            oracle = json.load(handle)
        report["deck_quality"] = f"{oracle['passed']}/{oracle['total']}"

    print(json.dumps({"thresholds": THRESHOLDS, "metrics": report, "failures": failures},
                     ensure_ascii=False, indent=2))
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
