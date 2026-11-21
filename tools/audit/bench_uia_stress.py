"""UIA 压力与恢复：200 次 snapshot/find、连续超时、应用中途退出后的恢复。

用法：
  python tools/audit/bench_uia_stress.py --exe artifacts\\server\\VScreenPilot.exe --out tools\\audit\\results\\uia
"""

from __future__ import annotations

import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import (  # noqa: E402
    McpClient, case, default_exe, handle_count, kill_tree, print_summary,
    rss_mb, run_dir, stats, write_json,
)

HERE = os.path.dirname(os.path.abspath(__file__))
UITARGET = os.path.join(HERE, "targets", "bin", "UiTarget", "UiTarget.exe")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", default=default_exe())
    parser.add_argument("--out", default=None)
    parser.add_argument("--iterations", type=int, default=200)
    args = parser.parse_args()

    out_dir = args.out or run_dir("uia")
    stress_dir = os.path.join(out_dir, "uia")
    os.makedirs(stress_dir, exist_ok=True)
    cases: list[dict] = []

    client = McpClient(args.exe).start()
    try:
        env = client.call("get_environment", {"ensure": True}, 120)
        pid = int((env.data or {}).get("session", {}).get("pid") or 0)
        target = client.call("launch_or_place_app", {"path": UITARGET, "wait_ms": 15000}, 120)
        target_pid = (target.data or {}).get("pid")

        start_handles = handle_count(pid)
        start_rss = rss_mb(pid)
        snapshot_ms: list[float] = []
        find_ms: list[float] = []
        failures: list[str] = []
        handle_samples: list[int] = [start_handles]
        rss_samples: list[float] = [start_rss]
        steady_handles = start_handles
        steady_rss = start_rss

        for index in range(args.iterations):
            started = time.perf_counter()
            snap = client.call("snapshot", {"max_depth": 3, "max_nodes": 120}, 30)
            snapshot_ms.append((time.perf_counter() - started) * 1000)

            started = time.perf_counter()
            found = client.call("find_element", {"automation_id": "input"}, 30)
            find_ms.append((time.perf_counter() - started) * 1000)

            if not snap.ok or not found.ok:
                failures.append(f"{index}:{snap.code or 'ok'}/{found.code or 'ok'}")

            if index % 20 == 19:
                handle_samples.append(handle_count(pid))
                rss_samples.append(rss_mb(pid))

                # 前 40 次包含 UIA/帧缓存的首次分配；之后进入稳态，才用于判泄漏。
                if index == 39:
                    steady_handles = handle_samples[-1]
                    steady_rss = rss_samples[-1]

        end_handles = handle_count(pid)
        end_rss = rss_mb(pid)
        handle_delta = end_handles - steady_handles
        rss_delta = round(end_rss - steady_rss, 2)

        cases.append(case(
            "UIA-01", f"{args.iterations} 次 snapshot + find_element 无错误、稳态无句柄/内存泄漏",
            not failures and handle_delta <= 20 and rss_delta <= 10.0,
            failures=failures[:5], snapshot=stats(snapshot_ms), find=stats(find_ms),
            handles={"start": start_handles, "steady": steady_handles, "end": end_handles,
                     "steady_delta": handle_delta, "samples": handle_samples},
            rss={"start": start_rss, "steady": steady_rss, "end": end_rss,
                 "steady_delta": rss_delta, "samples": rss_samples}))

        # 连续超时后仍能正常查找（协作式取消 + 队列不被堵死）
        timeout_codes: list[str | None] = []
        for _ in range(10):
            timed = client.call("wait_for", {
                "condition": {"type": "element", "automation_id": "no-such-element"},
                "timeout_ms": 120}, 30)
            timeout_codes.append(timed.code)
        after_timeouts = client.call("find_element", {"automation_id": "input"}, 30)
        cases.append(case(
            "UIA-02", "连续 10 次 wait_for 超时后 UIA 仍可用",
            after_timeouts.ok, codes=timeout_codes, after=after_timeouts.code or "ok"))

        # 应用在等待中退出：错误可识别，重新启动后恢复
        killer = client.call("find_element", {"automation_id": "input", "pid": target_pid}, 30)
        kill_tree(int(target_pid or 0))
        time.sleep(0.5)
        dead = client.call("find_element", {"automation_id": "input"}, 30)
        relaunched = client.call("launch_or_place_app", {"path": UITARGET, "wait_ms": 15000}, 120)
        recovered = client.call("find_element", {"automation_id": "input"}, 30)
        cases.append(case(
            "UIA-03", "应用中途退出后重新启动可恢复",
            killer.ok and dead.code is not None and relaunched.ok and recovered.ok,
            dead=dead.code, relaunch=relaunched.code or "ok", recovered=recovered.code or "ok"))
    finally:
        client.call("release_environment",
                    {"keep_display": False, "on_dirty": "discard", "allow_data_loss": True}, 120)
        client.stop()

    write_json(os.path.join(stress_dir, "summary.json"), {"cases": cases})
    print_summary(cases)
    return 0


if __name__ == "__main__":
    sys.exit(main())
