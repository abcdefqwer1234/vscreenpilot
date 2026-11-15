"""CAP-01..03：长跑截图（区域 500 + 全屏 120），记录 RSS / GDI / USER 句柄与悬崖起点。

用法：
  python tools/audit/bench_capture_soak.py --exe artifacts\\server\\VScreenPilot.exe --out tools\\audit\\results\\run2
"""

from __future__ import annotations

import argparse
import ctypes
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import McpClient, default_exe, run_dir, write_json, case, print_summary, stats, rss_mb  # noqa: E402


def gui_resources(pid: int) -> dict[str, int]:
    process = ctypes.windll.kernel32.OpenProcess(0x0400, False, pid)
    if not process:
        return {"gdi": -1, "user": -1}
    try:
        gdi = ctypes.windll.user32.GetGuiResources(process, 0)
        user = ctypes.windll.user32.GetGuiResources(process, 1)
        return {"gdi": int(gdi), "user": int(user)}
    finally:
        ctypes.windll.kernel32.CloseHandle(process)


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", default=default_exe())
    parser.add_argument("--out", default=None)
    parser.add_argument("--region", type=int, default=500)
    parser.add_argument("--full", type=int, default=120)
    args = parser.parse_args()

    out_dir = args.out or run_dir("capture-soak")
    cap_dir = os.path.join(out_dir, "capture")
    os.makedirs(cap_dir, exist_ok=True)
    cases: list[dict] = []

    client = McpClient(args.exe).start()
    env = client.call("get_environment", {"ensure": True}, 120)
    display = (env.data or {}).get("virtual_display", {})
    origin = {"x": display.get("x", 1920), "y": display.get("y", 0)}

    region_samples: list[dict] = []
    region_ms: list[float] = []
    region_server_ms: list[float] = []
    for index in range(args.region):
        result = client.call("capture_screen", {"region": {"x": origin["x"], "y": origin["y"], "width": 320, "height": 180}}, 60)
        region_ms.append(result.ms)
        region_server_ms.append(float((result.data or {}).get("duration_ms") or 0))
        if index % 50 == 0:
            sample = client.sample()
            sample.update(gui_resources(client.proc.pid))
            region_samples.append(sample)

    full_ms: list[float] = []
    full_server_ms: list[float] = []
    slow_flags: list[bool] = []
    full_samples: list[dict] = []
    onset = None
    for index in range(args.full):
        result = client.call("capture_screen", {}, 120)
        full_ms.append(result.ms)
        full_server_ms.append(float((result.data or {}).get("duration_ms") or 0))
        slow_flags.append(bool((result.data or {}).get("slow_capture")))
        if result.ms > 500 and onset is None:
            onset = index + 1
        if index % 10 == 0:
            sample = client.sample()
            sample.update(gui_resources(client.proc.pid))
            full_samples.append(sample)

    def slope(samples: list[dict], key: str) -> float:
        points = [(s["t"], s[key]) for s in samples if s.get(key, -1) > 0]
        if len(points) < 5:
            return 0.0
        t0 = points[0][0]
        xs = [(t - t0) / 3600 for t, _ in points]
        ys = [v for _, v in points]
        mx = sum(xs) / len(xs)
        my = sum(ys) / len(ys)
        den = sum((x - mx) ** 2 for x in xs)
        return round(sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / den, 2) if den else 0.0

    # 服务端 duration_ms 才是产品 SLO；result.ms 还包含 JSON/Base64 传输与客户端解析。
    cases.append(case("CAP-01", "区域截图 500 次无性能劣化（服务端 P95 ≤50ms）",
                      stats(region_server_ms)["p95"] <= 50,
                      server_stats=stats(region_server_ms), client_stats=stats(region_ms),
                      rss_slope_mb_h=slope(region_samples, "rss_mb"),
                      gdi_slope=slope(region_samples, "gdi"), user_slope=slope(region_samples, "user"),
                      gdi_start=region_samples[0].get("gdi"), gdi_end=region_samples[-1].get("gdi"),
                      user_start=region_samples[0].get("user"), user_end=region_samples[-1].get("user")))

    # 结论变化：复用资源 + 去 CAPTUREBLT + 大图不内联后，全屏不再出现 1.9s 悬崖。
    # 判定看服务端 P95（客户端还包含预览图传输）。
    cases.append(case("CAP-02", "全屏截图无悬崖（服务端 P95 ≤80ms）",
                      onset is None and stats(full_server_ms)["p95"] <= 80,
                      onset_call=onset, client_stats=stats(full_ms),
                      server_stats=stats(full_server_ms), slow_flags=sum(1 for item in slow_flags if item),
                      peak_rss_mb=max((s.get("rss_mb") or 0) for s in full_samples) if full_samples else 0,
                      rss_slope_mb_h=slope(full_samples, "rss_mb"),
                      gdi_start=full_samples[0].get("gdi"), gdi_end=full_samples[-1].get("gdi")))

    # 全屏之后小区域仍快（服务端耗时）
    tail_region = []
    for _ in range(10):
        result = client.call(
            "capture_screen",
            {"region": {"x": origin["x"], "y": origin["y"], "width": 320, "height": 180}}, 60)
        tail_region.append(float((result.data or {}).get("duration_ms") or 0))
    cases.append(case("CAP-03", "全屏之后区域截图仍保持低延迟（服务端 P95 ≤50ms）",
                      stats(tail_region)["p95"] <= 50, stats=stats(tail_region)))

    final = client.sample()
    final.update(gui_resources(client.proc.pid))
    client.call("release_environment", {"keep_display": False, "on_dirty": "discard", "allow_data_loss": True}, 120)
    client.stop()

    write_json(os.path.join(cap_dir, "summary.json"), {
        "cases": cases, "region_samples": region_samples, "full_samples": full_samples,
        "full_ms": full_ms, "final": final,
    })
    print_summary(cases)
    return 0


if __name__ == "__main__":
    sys.exit(main())
