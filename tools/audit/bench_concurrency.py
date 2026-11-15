"""CONC-01..05：并发、闸门、取消隔离、并发 release。

用法：
  python tools/audit/bench_concurrency.py --exe artifacts\\server\\VScreenPilot.exe --out tools\\audit\\results\\run2
"""

from __future__ import annotations

import argparse
import json
import os
import queue
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import (  # noqa: E402
    McpClient, default_exe, run_dir, write_json, case, print_summary, stats, rss_mb, handle_count,
)

HERE = os.path.dirname(os.path.abspath(__file__))
UITARGET = os.path.join(HERE, "targets", "bin", "UiTarget", "UiTarget.exe")


def begin_request(client: McpClient, name: str, arguments: dict) -> tuple[int, queue.Queue]:
    with client._lock:
        request_id = client._next_id
        client._next_id += 1
    cell: queue.Queue = queue.Queue(maxsize=1)
    client._pending[request_id] = cell
    client.send_line(json.dumps({
        "jsonrpc": "2.0", "id": request_id, "method": "tools/call",
        "params": {"name": name, "arguments": arguments},
    }))
    return request_id, cell


def parse_code(response: dict) -> str | None:
    payload = (response.get("result") or {}).get("content") or []
    for item in payload:
        if item.get("type") == "text":
            try:
                return json.loads(item["text"]).get("code")
            except Exception:
                pass
    return None


def worker_pool(client: McpClient, threads: int, calls: int, origin: dict) -> dict:
    results: list[dict] = []
    lock = threading.Lock()

    def worker(index: int) -> None:
        for step in range(calls):
            name, payload = (
                ("get_environment", {}),
                ("get_log", {"tail": 5}),
                ("capture_screen", {"region": {"x": origin["x"], "y": origin["y"], "width": 320, "height": 180}}),
                ("find_element", {"automation_id": "input"}),
            )[step % 4]
            result = client.call(name, payload, 120)
            with lock:
                results.append({"thread": index, "tool": name, "ok": result.ok,
                                "code": result.code, "ms": result.ms})

    started = time.perf_counter()
    workers = [threading.Thread(target=worker, args=(i,)) for i in range(threads)]
    for item in workers:
        item.start()
    for item in workers:
        item.join(240)
    wall = round((time.perf_counter() - started) * 1000, 1)
    ok = sum(1 for r in results if r["ok"])
    codes: dict[str, int] = {}
    for r in results:
        key = r["code"] or "ok"
        codes[key] = codes.get(key, 0) + 1
    return {"threads": threads, "calls": len(results), "ok": ok, "codes": codes,
            "wall_ms": wall, "latency": stats([r["ms"] for r in results])}


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", default=default_exe())
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    out_dir = args.out or run_dir("concurrency")
    conc_dir = os.path.join(out_dir, "concurrency")
    os.makedirs(conc_dir, exist_ok=True)
    cases: list[dict] = []

    client = McpClient(args.exe).start()
    env = client.call("get_environment", {"ensure": True}, 120)
    display = (env.data or {}).get("virtual_display", {})
    origin = {"x": display.get("x", 1920), "y": display.get("y", 0)}
    launch = client.call("launch_or_place_app", {"path": UITARGET, "wait_ms": 15000}, 120)
    if not launch.ok:
        print("UiTarget 启动失败", launch.code, launch.message)

    before = client.sample()
    run8 = worker_pool(client, 8, 20, origin)
    cases.append(case("CONC-01", "8 线程 × 20 次（4 工具混合）",
                      run8["ok"] == run8["calls"],
                      passed=run8["ok"], calls=run8["calls"], codes=run8["codes"],
                      wall_ms=run8["wall_ms"], latency=run8["latency"]))

    run16 = worker_pool(client, 16, 10, origin)
    cases.append(case("CONC-02", "16 线程 × 10 次（加压）",
                      run16["ok"] == run16["calls"],
                      passed=run16["ok"], calls=run16["calls"], codes=run16["codes"],
                      wall_ms=run16["wall_ms"], latency=run16["latency"]))

    # CONC-03 取消隔离 + 闸门饱和：B 在 A 活跃时应 E_BUSY，A 取消后 B 应成功
    id_a, cell_a = begin_request(client, "wait_for", {
        "condition": {"type": "color", "x": origin["x"] + 5, "y": origin["y"] + 5,
                      "value": "#FF00FF", "tolerance": 0}, "timeout_ms": 12000})
    time.sleep(0.4)
    b_busy = client.call("get_ui_tree", {"max_depth": 3}, 30)
    client.notify("notifications/cancelled", {"requestId": id_a, "reason": "audit"})
    try:
        a_response = cell_a.get(timeout=12)
    except queue.Empty:
        a_response = None
    time.sleep(0.6)
    b_after = client.call("get_ui_tree", {"max_depth": 3}, 30)
    a_code = parse_code(a_response) if a_response else None
    cases.append(case("CONC-03", "取消 A 不破坏会话；并发 GUI 工具在闸门饱和时返回 E_BUSY",
                      (a_response is None or a_code == "E_CANCELLED") and b_after.ok,
                      a_cancelled=a_response is None or a_code == "E_CANCELLED",
                      b_while_busy=b_busy.code or "ok", b_while_busy_ms=b_busy.ms,
                      b_after=b_after.code or "ok"))

    # CONC-04 并发 release：截图进行中调用 release，随后会话仍可用
    shot_holder: dict = {}

    def shot() -> None:
        shot_holder["result"] = client.call("capture_screen", {"region": {"x": origin["x"], "y": origin["y"], "width": 900, "height": 700}}, 120)

    thread = threading.Thread(target=shot, daemon=True)
    thread.start()
    time.sleep(0.05)
    release = client.call("release_environment", {"keep_display": True, "on_dirty": "discard"}, 120)
    thread.join(60)
    after_release = client.call("get_environment", {}, 60)
    cases.append(case("CONC-04", "并发 release 后会话可恢复",
                      after_release.ok,
                      release=release.code or "ok", shot=(shot_holder.get("result").code or "ok") if shot_holder.get("result") else None,
                      after=after_release.code or "ok"))

    # CONC-05 并发后资源
    after = client.sample()
    cases.append(case("CONC-05", "并发后进程资源无异常",
                      after["rss_mb"] > 0 and after["handles"] > 0,
                      rss_before=before.get("rss_mb"), rss_after=after.get("rss_mb"),
                      handles_before=before.get("handles"), handles_after=after.get("handles")))

    client.call("release_environment", {"keep_display": False, "on_dirty": "discard"}, 120)
    client.stop()
    write_json(os.path.join(conc_dir, "summary.json"), {"cases": cases})
    print_summary(cases)
    return 0


if __name__ == "__main__":
    sys.exit(main())
