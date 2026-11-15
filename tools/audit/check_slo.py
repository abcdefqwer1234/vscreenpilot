"""CI SLO 门禁：读取 audit summary，断言截图延迟与内存没有劣化。

用法：
  python tools/audit/check_slo.py --results tools/audit/results/nightly
"""

from __future__ import annotations

import argparse
import json
import os
import sys


def load(path: str):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--results", required=True)
    parser.add_argument("--capture-p95-ms", type=float, default=80.0)
    parser.add_argument("--peak-rss-mb", type=float, default=400.0)
    parser.add_argument("--contract-min", type=int, default=10)
    args = parser.parse_args()

    failures: list[str] = []

    capture_path = os.path.join(args.results, "capture", "summary.json")
    if os.path.exists(capture_path):
        capture = load(capture_path)
        for case in capture.get("cases") or []:
            stats = case.get("server_stats") or {}
            p95 = stats.get("p95")
            if p95 is not None and p95 > args.capture_p95_ms:
                failures.append(f"{case.get('id')}: p95={p95}ms > {args.capture_p95_ms}ms")
            rss = case.get("peak_rss_mb")
            if rss is not None and rss > args.peak_rss_mb:
                failures.append(f"{case.get('id')}: peak_rss={rss}MB > {args.peak_rss_mb}MB")
    else:
        failures.append("capture/summary.json 缺失")

    contract_path = os.path.join(args.results, "contract", "summary.json")
    if os.path.exists(contract_path):
        contract = load(contract_path)
        passed = sum(1 for case in contract.get("cases") or [] if case.get("ok"))
        if passed < args.contract_min:
            failures.append(f"contract {passed} < {args.contract_min}")
    else:
        failures.append("contract/summary.json 缺失")

    print("SLO failures:", failures)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
