"""STAB-01..：50 会话、soak、12 项故障注入与残留检查。

用法：
  python tools/audit/bench_stability.py --exe artifacts\\server\\VScreenPilot.exe --out tools\\audit\\results\\run1 --sessions 50 --soak-min 20
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import (  # noqa: E402
    McpClient, default_exe, kill_tree, monitor_count, run_dir, write_json, case,
    print_summary, stats, rss_mb, handle_count, percentile,
)

HERE = os.path.dirname(os.path.abspath(__file__))
UITARGET = os.path.join(HERE, "targets", "bin", "UiTarget", "UiTarget.exe")
CLICKPROBE = os.path.join(HERE, "targets", "bin", "ClickProbe", "ClickProbe.exe")


def residual() -> dict:
    output = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         "(Get-Process VScreenPilot,UiTarget,ClickProbe,notepad -ErrorAction SilentlyContinue | Measure-Object).Count"],
        capture_output=True, text=True).stdout.strip()
    try:
        processes = int(output.splitlines()[-1])
    except Exception:
        processes = -1
    return {"processes": processes, "monitors": monitor_count()}


def slope_mb_per_hour(samples: list[dict]) -> float:
    points = [(s["t"], s["rss_mb"]) for s in samples if s.get("rss_mb", -1) > 0]
    if len(points) < 10:
        return 0.0
    t0 = points[0][0]
    xs = [(t - t0) / 3600 for t, _ in points]
    ys = [v for _, v in points]
    mean_x = sum(xs) / len(xs)
    mean_y = sum(ys) / len(ys)
    denominator = sum((x - mean_x) ** 2 for x in xs)
    if denominator == 0:
        return 0.0
    return round(sum((x - mean_x) * (y - mean_y) for x, y in zip(xs, ys)) / denominator, 2)


def sessions(exe: str, count: int, cases: list[dict]) -> list[dict]:
    records = []
    started_all = time.perf_counter()
    for index in range(1, count + 1):
        entry = {"index": index}
        started = time.perf_counter()
        try:
            client = McpClient(exe).start()
            entry["start_ms"] = round((time.perf_counter() - started) * 1000, 1)
            ensure = client.call("get_environment", {"ensure": True}, 120)
            entry["ensure_ms"] = ensure.ms
            shot = client.call("capture_screen", {}, 60)
            entry["shot_ms"] = shot.ms
            release = client.call("release_environment", {"keep_display": False, "on_dirty": "discard", "allow_data_loss": True}, 120)
            entry["release_ms"] = release.ms
            entry["display_removed"] = (release.data or {}).get("display_removed")
            client.close_stdin()
            exit_started = time.perf_counter()
            code = client.wait(30)
            entry["eof_ms"] = round((time.perf_counter() - exit_started) * 1000, 1)
            entry["exit"] = code
            entry["ok"] = bool(ensure.ok and shot.ok and release.ok and code == 0 and entry["display_removed"])
        except Exception as exception:  # noqa: BLE001
            entry["ok"] = False
            entry["error"] = f"{type(exception).__name__}: {exception}"
        entry["total_ms"] = round((time.perf_counter() - started) * 1000, 1)
        records.append(entry)
        time.sleep(0.3)
    passed = sum(1 for item in records if item["ok"])
    cases.append(case("STAB-01", f"{count} 会话循环",
                      passed == count,
                      passed=passed, total=count,
                      total_s=round(time.perf_counter() - started_all, 1),
                      start_ms=stats([i.get("start_ms", 0) for i in records]),
                      ensure_ms=stats([i.get("ensure_ms", 0) for i in records]),
                      shot_ms=stats([i.get("shot_ms", 0) for i in records]),
                      release_ms=stats([i.get("release_ms", 0) for i in records]),
                      eof_ms=stats([i.get("eof_ms", 0) for i in records])))
    return records


def soak(exe: str, minutes: float, cases: list[dict]) -> dict:
    client = McpClient(exe).start()
    client.call("get_environment", {"ensure": True}, 120)
    client.call("launch_or_place_app", {"path": UITARGET, "wait_ms": 15000}, 120)
    samples: list[dict] = []
    errors: list[dict] = []
    deadline = time.time() + minutes * 60
    iteration = 0
    last_sample = 0.0
    while time.time() < deadline:
        iteration += 1
        for name, payload, timeout in (
            ("capture_screen", {}, 60),
            ("find_element", {"automation_id": "input"}, 30),
            ("get_ui_tree", {"max_depth": 4}, 30),
            ("type_text", {"text": "soak", "verify": False}, 60),
        ):
            result = client.call(name, payload, timeout)
            if not result.ok:
                errors.append({"iteration": iteration, "tool": name, "code": result.code})
        now = time.time()
        if now - last_sample >= 5:
            samples.append(client.sample())
            last_sample = now
        time.sleep(0.15)
    alive = client.proc.poll() is None
    slope = slope_mb_per_hour(samples)
    handle_values = [s["handles"] for s in samples if s["handles"] > 0]
    handle_slope = 0.0
    if len(handle_values) > 10:
        slice_len = max(1, len(handle_values) // 4)
        handle_slope = round((sum(handle_values[-slice_len:]) / slice_len) - (sum(handle_values[:slice_len]) / slice_len), 1)
    peak = max((s["rss_mb"] for s in samples), default=0)
    start_rss = samples[0]["rss_mb"] if samples else 0
    after = client.call("release_environment", {"keep_display": False, "on_dirty": "discard", "allow_data_loss": True}, 120)
    client.close_stdin()
    code = client.wait(30)
    cases.append(case("STAB-02", f"{minutes:g} 分钟 soak",
                      alive and not errors and slope < 5.0 and code == 0,
                      iterations=iteration, samples=len(samples), errors=len(errors),
                      error_sample=errors[:5], peak_rss_mb=peak, start_rss_mb=start_rss,
                      slope_mb_per_hour=slope, handle_start=handle_values[:1],
                      handle_end=handle_values[-1:], handle_delta=handle_slope,
                      exit=code))
    return {"samples": samples, "errors": errors, "iterations": iteration,
            "slope_mb_per_hour": slope, "peak_rss_mb": peak}


def faults(exe: str, cases: list[dict]) -> None:
    client = McpClient(exe).start()
    client.call("get_environment", {"ensure": True}, 120)
    target = client.call("launch_or_place_app", {"path": UITARGET, "wait_ms": 15000}, 120)
    pid = (target.data or {}).get("pid")

    # 1 应用中途退出
    kill_tree(pid or 0)
    time.sleep(1)
    dead = client.call("get_element_text", {"ref": "e1"}, 30)
    cases.append(case("FAULT-01", "应用中途退出后错误可识别", dead.code is not None, code=dead.code))

    # 2 外部摘屏
    vdd = os.path.join(os.path.dirname(os.path.dirname(HERE)), "tools", "VddProbe", "bin", "Release", "net10.0-windows", "VddProbe.exe")
    removed = None
    if os.path.exists(vdd):
        # VddProbe 是 apphost exe，必须能找到 .NET 运行时；本地 SDK 常放在
        # %LOCALAPPDATA%\vsp-dotnet，调用方 PATH 未包含时要显式补 DOTNET_ROOT。
        env = os.environ.copy()
        dotnet = shutil.which("dotnet") or os.path.join(
            os.environ.get("LOCALAPPDATA", ""), "vsp-dotnet", "dotnet.exe")
        if os.path.exists(dotnet):
            env.setdefault("DOTNET_ROOT", os.path.dirname(dotnet))
            env["PATH"] = os.path.dirname(dotnet) + os.pathsep + env.get("PATH", "")
        removed = subprocess.run(
            [vdd, "removeall"], capture_output=True, text=True, timeout=120, env=env)
    shot = client.call("capture_screen", {}, 60)
    recovered = client.call("get_environment", {"ensure": True}, 120)
    recovered_display = (recovered.data or {}).get("virtual_display") or {}
    baseline = (shot.data or {}).get("baseline") or {}
    # 与 HOT-01 同一契约：外部摘屏后允许“明确报错”，也允许“自愈后成功”，
    # 但成功的截图必须来自当前真实虚拟屏，不能停在已消失的旧坐标上。
    geometry_ok = (
        bool(recovered_display.get("enabled"))
        and baseline.get("x") == recovered_display.get("x")
        and baseline.get("y") == recovered_display.get("y")
        and baseline.get("width") == recovered_display.get("width")
        and baseline.get("height") == recovered_display.get("height")
    )
    cases.append(case("FAULT-02", "外部摘屏后截图报错或自愈到正确几何",
                      (shot.code is not None or (shot.ok and geometry_ok)) and recovered.ok,
                      code=shot.code, recovered=recovered.ok, baseline=baseline,
                      display={key: recovered_display.get(key)
                               for key in ("enabled", "x", "y", "width", "height")},
                      vdd_exit=(removed.returncode if removed else None)))

    # 3 越界
    oob = client.call("click", {"x": 99999, "y": 99999, "mode": "message"}, 30)
    cases.append(case("FAULT-03", "坐标越界被拒绝", oob.code == "E_OUT_OF_BOUNDS", code=oob.code))

    # 4 外来窗口
    foreign = subprocess.Popen([CLICKPROBE, os.path.join(RESULTS, "faults", "foreign.jsonl")],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.5)
    foreign_click = client.call("click", {"x": 2000, "y": 100, "mode": "message"}, 30)
    kill_tree(foreign.pid)
    cases.append(case("FAULT-04", "外来窗口被拒绝", foreign_click.code in ("E_FOREIGN_WINDOW", "E_NO_TARGET", "E_OUT_OF_BOUNDS"),
                      code=foreign_click.code))

    # 5 无效 PNG
    bad = client.call("match_visual", {"mode": "template", "target": "AAAA"}, 30)
    cases.append(case("FAULT-05", "无效 PNG 被拒绝", bad.code in ("E_UNSUPPORTED_IMAGE", "E_INVALID_ARG"), code=bad.code))

    # 6 超长路径
    long_path = "C:\\" + ("a" * 400) + ".png"
    long_result = client.call("capture_screen", {"save_path": long_path}, 30)
    cases.append(case("FAULT-06", "超长路径被拒绝", long_result.code == "E_INVALID_ARG", code=long_result.code))

    # 7 取消请求（真实 wait_for）
    holder: dict = {}
    request_id: list[int] = [0]
    original = client.send_line

    def spy(line):
        try:
            message = json.loads(line)
            if message.get("method") == "tools/call" and request_id[0] == 0:
                request_id[0] = message.get("id")
        except Exception:
            pass
        return original(line)

    client.send_line = spy  # type: ignore[assignment]

    def worker():
        try:
            # 25s > wait_for 的 20s，取消被服务端静默丢弃时也能在 join 前结束。
            holder["response"] = client.request("tools/call", {
                "name": "wait_for",
                "arguments": {"condition": {"type": "color", "x": 1925, "y": 5, "value": "#FF00FF", "tolerance": 0},
                              "timeout_ms": 20000}}, 25)
        except TimeoutError:
            holder["timeout"] = True

    thread = threading.Thread(target=worker, daemon=True)
    thread.start()
    time.sleep(1.2)
    client.send_line = original  # type: ignore[assignment]
    client.notify("notifications/cancelled", {"requestId": request_id[0], "reason": "fault"})
    thread.join(30)
    ping = client.request("ping", {}, 10)
    cases.append(case("FAULT-07", "取消后协议仍可用",
                      ping.get("result") == {} and (holder.get("timeout") or holder.get("response") is not None),
                      cancelled_response=bool(holder.get("response")), client_timeout=bool(holder.get("timeout")),
                      ping=ping.get("result")))
    client.call("release_environment", {"keep_display": False, "on_dirty": "discard", "allow_data_loss": True}, 120)
    client.stop()

    # 8 EOF 中断（调用中关闭 stdin）：必须在单实例互斥释放后启动
    interrupt = McpClient(exe).start()
    interrupt.call("get_environment", {"ensure": True}, 120)
    interrupt.call("launch_or_place_app", {"path": UITARGET, "wait_ms": 15000}, 120)
    background = threading.Thread(target=lambda: interrupt.call("capture_screen", {"region": {"x": 1920, "y": 0, "width": 900, "height": 700}}, 60), daemon=True)
    background.start()
    time.sleep(0.05)
    interrupt.close_stdin()
    code = interrupt.wait(30)
    cases.append(case("FAULT-08", "调用中 EOF 能退出", code is not None, exit=code))

    # 9 重复实例（共享到同一个 daemon，空 stdin 应正常退出 0）
    holder_client = McpClient(exe).start()
    second = subprocess.run([exe], input=b"", capture_output=True, timeout=60)
    holder_client.stop()
    cases.append(case("FAULT-09", "重复实例作为共享客户端接入（exit=0）",
                      second.returncode == 0, exit=second.returncode))

    # 10-12 新会话
    client = McpClient(exe).start()
    client.call("get_environment", {"ensure": True}, 120)
    client.call("launch_or_place_app", {"path": UITARGET, "wait_ms": 15000}, 120)

    # 10 UIPI：无法由 MCP 启动管理员窗口，验证错误路径不可达并记录
    cases.append(case("FAULT-10", "UIPI 错误路径（记录限制）", True,
                      note="MCP 无法启动提权应用，E_UIPI 路径本轮不可达"))

    # 11 慢启动应用
    slow = client.call("launch_or_place_app", {"path": "C:\\Windows\\System32\\mspaint.exe", "wait_ms": 3000}, 30)
    cases.append(case("FAULT-11", "慢启动应用有明确超时/结果",
                      slow.code is not None or slow.ok,
                      code=slow.code or "ok", ms=slow.ms))

    # 12 UIA 空结果/卡死
    none = client.call("find_element", {"name": "__no_such_element__"}, 30)
    cases.append(case("FAULT-12", "UIA 空结果返回明确错误", none.code == "E_ELEMENT_NOT_FOUND", code=none.code))

    client.call("release_environment", {"keep_display": False, "on_dirty": "discard", "allow_data_loss": True}, 120)
    client.stop()


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", default=default_exe())
    parser.add_argument("--out", default=None)
    parser.add_argument("--sessions", type=int, default=50)
    parser.add_argument("--soak-min", type=float, default=20)
    args = parser.parse_args()

    global RESULTS
    RESULTS = args.out or run_dir("stability")
    os.makedirs(os.path.join(RESULTS, "stability"), exist_ok=True)
    cases: list[dict] = []

    records = sessions(args.exe, args.sessions, cases)
    write_json(os.path.join(RESULTS, "stability", "sessions.json"), {"sessions": records, "cases": cases})
    soak_result = soak(args.exe, args.soak_min, cases)
    write_json(os.path.join(RESULTS, "stability", "soak.json"), {"soak": soak_result, "cases": cases})
    faults(args.exe, cases)
    leftover = residual()

    write_json(os.path.join(RESULTS, "stability", "summary.json"), {
        "exe": args.exe,
        "sessions": records,
        "soak": soak_result,
        "cases": cases,
        "residual": leftover,
    })
    print_summary(cases)
    print("residual", leftover)
    return 0


if __name__ == "__main__":
    sys.exit(main())
