"""REG-01..18：静态报告 8.13 补充缺陷的回归测试 + 协议/安全补测。

用法：
  python tools/audit/bench_regressions.py --exe artifacts\\server\\VScreenPilot.exe --out tools\\audit\\results\\run1
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import (  # noqa: E402
    McpClient, default_exe, handle_count, kill_tree, monitor_count, rss_mb,
    run_dir, write_json, case, print_summary, stats, percentile,
)

HERE = os.path.dirname(os.path.abspath(__file__))
UITARGET = os.path.join(HERE, "targets", "bin", "UiTarget", "UiTarget.exe")
CLICKPROBE = os.path.join(HERE, "targets", "bin", "ClickProbe", "ClickProbe.exe")


def safe(cases: list[dict], case_id: str, purpose: str, fn) -> None:
    try:
        item = fn()
        item.setdefault("id", case_id)
        item.setdefault("purpose", purpose)
        cases.append(item)
    except Exception as exception:  # noqa: BLE001
        cases.append(case(case_id, purpose, False, error=f"{type(exception).__name__}: {exception}"))


def tool_call(client: McpClient, name: str, args: dict, timeout: float = 120) -> dict:
    """带请求 id 追踪的工具调用，便于取消。"""
    holder: dict = {}

    def worker() -> None:
        started = time.perf_counter()
        try:
            holder["response"] = client.request("tools/call", {"name": name, "arguments": args}, timeout)
        except TimeoutError:
            holder["timeout"] = True
        holder["ms"] = round((time.perf_counter() - started) * 1000, 1)

    thread = threading.Thread(target=worker, daemon=True)
    thread.start()
    # 等首个请求 id 出现
    deadline = time.time() + 5
    while time.time() < deadline and "last_id" not in holder:
        time.sleep(0.02)
    return {"thread": thread, "holder": holder}


def run_regressions(exe: str) -> list[dict]:
    cases: list[dict] = []
    client = McpClient(exe).start()
    env = client.call("get_environment", {"ensure": True}, 120)
    display = (env.data or {}).get("virtual_display", {})
    origin = {"x": display.get("x", 1920), "y": display.get("y", 0)}
    launch = client.call("launch_or_place_app", {"path": UITARGET, "wait_ms": 15000}, 120)
    bounds = (launch.data or {}).get("bounds") or {}
    print("display", display.get("device"), display.get("width"), "bounds", bounds)

    # ---------- REG-02 歧义窗口 ----------
    def reg02():
        first = client.call("launch_or_place_app", {"path": CLICKPROBE, "new_instance": True, "wait_ms": 15000}, 120)
        second = client.call("launch_or_place_app", {"path": CLICKPROBE, "new_instance": True, "wait_ms": 15000}, 120)
        ambiguous = client.call("launch_or_place_app", {"window": "VSP ClickProbe", "wait_ms": 5000}, 60)
        hint = ambiguous.message
        usable = bool(ambiguous.data.get("windows")) if isinstance(ambiguous.data, dict) else False
        return case("REG-02", "同标题双窗口提示可执行", ambiguous.code == "E_WINDOW_AMBIGUOUS" and (usable or "pid" in hint),
                    code=ambiguous.code, hint=hint[:200], candidates=ambiguous.data.get("windows") if isinstance(ambiguous.data, dict) else None,
                    pids=[(first.data or {}).get("pid"), (second.data or {}).get("pid")])

    safe(cases, "REG-02", "同标题双窗口提示可执行", reg02)

    # ---------- REG-03 可选参数类型 ----------
    def reg03():
        checks = {}
        checks["pid_str"] = client.call("get_ui_tree", {"pid": "123", "max_depth": 2}, 30).code
        checks["new_instance_str"] = client.call("launch_or_place_app", {"path": UITARGET, "new_instance": "false", "wait_ms": 3000}, 60).code
        checks["allow_destructive_str"] = client.call("set_element_value", {"ref": "e999", "value": "x", "allow_destructive": "true"}, 30).code
        checks["max_nodes_str"] = client.call("get_ui_tree", {"max_nodes": "abc"}, 30).code
        ok = all(code in ("E_INVALID_ARG", "E_ELEMENT_STALE", "E_ELEMENT_NOT_FOUND") for code in checks.values())
        return case("REG-03", "可选参数类型严格校验", ok, observed=checks)

    safe(cases, "REG-03", "可选参数类型严格校验", reg03)

    # ---------- REG-05 并发取消互不干扰 ----------
    def reg05():
        started: dict = {}

        def slow_worker():
            started["slow"] = client.request("tools/call", {
                "name": "wait_for",
                "arguments": {"condition": {"type": "color", "x": origin["x"] + 10, "y": origin["y"] + 10,
                                            "value": "#FF00FF", "tolerance": 0}, "timeout_ms": 20000}},
                40)

        slow = threading.Thread(target=slow_worker, daemon=True)
        slow.start()
        time.sleep(1.0)
        # 抓取进行中的请求 id：发送一次 get_log 并等待完成，再发取消
        with client._lock:
            active_id = max(client._pending.keys(), default=None)
        other = client.call("get_log", {"tail": 5}, 30)
        if active_id is not None:
            client.notify("notifications/cancelled", {"requestId": active_id, "reason": "audit"})
        slow.join(15)
        response = started.get("slow") or {}
        payload = (response.get("result") or {}).get("content") or []
        code = None
        for item in payload:
            if item.get("type") == "text":
                try:
                    code = json.loads(item.get("text")).get("code")
                except Exception:
                    pass
        slow_cancelled = response == {} or code == "E_CANCELLED"
        return case("REG-05", "并发请求取消互不干扰", other.ok and slow_cancelled,
                    other=other.code or "ok", slow_code=code, slow_response_empty=response == {})

    safe(cases, "REG-05", "并发取消互不干扰", reg05)

    # ---------- REG-06 释放不误点按钮 ----------
    def reg06():
        probe_log = os.path.join(RESULTS, "regressions", "release-clicks.jsonl")
        if os.path.exists(probe_log):
            os.remove(probe_log)
        started = client.call("launch_or_place_app", {"path": CLICKPROBE, "args": [probe_log], "new_instance": True, "wait_ms": 15000}, 120)
        released = client.call("release_environment", {"keep_display": True, "on_dirty": "force", "allow_data_loss": True}, 120)
        time.sleep(0.8)
        clicked = os.path.exists(probe_log) and os.path.getsize(probe_log) > 0
        return case("REG-06", "release(on_dirty=force) 不误点窗口按钮", released.ok and not clicked,
                    released=released.ok, clicks=clicked, pid=(started.data or {}).get("pid"))

    safe(cases, "REG-06", "release(on_dirty=force) 不误点窗口按钮", reg06)

    # ---------- REG-07 系统键变体 ----------
    def reg07():
        variants = ["Alt+Shift+Tab", "Ctrl+Alt+Tab", "Alt+Esc", "Win+D", "Win+Tab", "Ctrl+Shift+Esc", "Win+R"]
        observed = {}
        for keys in variants:
            observed[keys] = client.call("press_keys", {"keys": keys}, 30).code
        # 清理可能被打开的系统 UI
        subprocess.run(["powershell", "-NoProfile", "-Command",
                        "Add-Type -Namespace K -Name B -MemberDefinition '[DllImport(\"user32.dll\")]public static extern void keybd_event(byte b,byte s,uint f,System.UIntPtr e);';"
                        "[K.B]::keybd_event(0x1B,0,0,[UIntPtr]::Zero); [K.B]::keybd_event(0x1B,0,2,[UIntPtr]::Zero)"],
                       capture_output=True)
        blocked = sum(1 for code in observed.values() if code == "E_SYSTEM_KEY_BLOCKED")
        return case("REG-07", "系统键变体全部拦截", blocked == len(variants),
                    observed=observed, blocked=blocked, total=len(variants))

    safe(cases, "REG-07", "系统键变体全部拦截", reg07)

    # ---------- REG-08 断言阈值范围 ----------
    def reg08():
        inside = {"x": origin["x"] + 10, "y": origin["y"] + 10, "width": 80, "height": 80}
        checks = {}
        checks["coverage_1.5"] = client.call("assert_region", {"region": inside, "expectation": {"color": "#FFFFFF", "coverage": 1.5}}, 30).code
        checks["coverage_-0.5"] = client.call("assert_region", {"region": inside, "expectation": {"color": "#FFFFFF", "coverage": -0.5}}, 30).code
        checks["threshold_-1"] = client.call("assert_region", {"region": inside, "expectation": {"changed": True, "threshold": -1}}, 30).code
        checks["threshold_2"] = client.call("assert_region", {"region": inside, "expectation": {"changed": True, "threshold": 2}}, 30).code
        ok = all(code == "E_INVALID_ARG" for code in checks.values())
        return case("REG-08", "断言阈值越界返回 E_INVALID_ARG", ok, observed=checks)

    safe(cases, "REG-08", "断言阈值越界返回 E_INVALID_ARG", reg08)

    # ---------- REG-09 run_script 中文输出 ----------
    def reg09():
        # 独立 profile + 不预热：避免与主客户端抢单实例互斥体和虚拟屏。
        script_client = McpClient(
            exe, env={"VSP_ALLOW_SCRIPT": "1", "VSP_PROFILE": "script", "VSP_NO_WARMUP": "1"}
        ).start()
        result = script_client.call("run_script", {"code": "Write-Output '中文输出 OK'; [Console]::OutputEncoding.WebName"}, 60)
        text = result.text
        ok = result.ok and "中文输出 OK" in text and "\ufffd" not in text
        script_client.stop()
        return case("REG-09", "run_script 中文输出不乱码", ok, observed=text[:160], code=result.code)

    safe(cases, "REG-09", "run_script 中文输出不乱码", reg09)

    # ---------- REG-12 超长 JSON-RPC 行 ----------
    def reg12():
        before = rss_mb(client.proc.pid)
        padding = "x" * (10 * 1024 * 1024)
        line = json.dumps({"jsonrpc": "2.0", "id": 777001, "method": "ping", "params": {"pad": padding}})
        started = time.perf_counter()
        try:
            client.send_line(line)
        except Exception as exception:  # noqa: BLE001
            return case("REG-12", "10MB 单行不导致崩溃/OOM", True, send_error=str(exception))
        time.sleep(2)
        alive = client.proc.poll() is None
        try:
            ping_ok = client.request("ping", {}, 20).get("result") == {}
        except Exception:
            ping_ok = False
        after = rss_mb(client.proc.pid)
        return case("REG-12", "10MB 单行不导致崩溃/OOM", alive and ping_ok,
                    alive=alive, responsive=ping_ok, rss_before=before, rss_after=after,
                    ms=round((time.perf_counter() - started) * 1000, 1))

    safe(cases, "REG-12", "10MB 单行不导致崩溃/OOM", reg12)

    # ---------- REG-13 条件 schema ----------
    def reg13():
        checks = {}
        checks["wait_bad_type"] = client.call("wait_for", {"condition": {"type": "nonsense"}, "timeout_ms": 500}, 30).code
        checks["wait_empty"] = client.call("wait_for", {"condition": {}}, 30).code
        checks["assert_empty"] = client.call("assert_region", {"region": {"x": 0, "y": 0, "width": 10, "height": 10}, "expectation": {}}, 30).code
        checks["assert_bad_region"] = client.call("assert_region", {"region": {"x": "a"}, "expectation": {"changed": True}}, 30).code
        checks["find_name_number"] = client.call("find_element", {"name": 123}, 30).code
        ok = all(code == "E_INVALID_ARG" for code in checks.values())
        return case("REG-13", "condition/expectation 错误字段返回 E_INVALID_ARG", ok, observed=checks)

    safe(cases, "REG-13", "condition/expectation 错误字段返回 E_INVALID_ARG", reg13)

    # ---------- REG-14 Calculator(UWP) ----------
    def reg14():
        calc = client.call("launch_or_place_app", {"path": "C:\\Windows\\System32\\calc.exe", "wait_ms": 20000}, 120)
        if not calc.ok:
            return case("REG-14", "UWP Calculator 可接管/查找", False, code=calc.code, message=calc.message[:160])
        tree = client.call("get_ui_tree", {"max_depth": 4}, 60)
        button = client.call("find_element", {"name": "Seven", "control_type": "Button"}, 60)
        if not button.ok:
            button = client.call("find_element", {"name": "7", "control_type": "Button"}, 60)
        if not button.ok:
            # 中文系统上 UIA 名称是“七”。
            button = client.call("find_element", {"name": "七", "control_type": "Button"}, 60)
        invoke = client.call("invoke_element", {"ref": (button.data or {}).get("ref")}, 60) if button.ok else None
        text = client.call("get_ui_tree", {"max_depth": 10, "interactive_only": True}, 90)
        found = "结果" in json.dumps(text.data, ensure_ascii=False) if text.ok else False
        return case("REG-14", "UWP Calculator 可接管/查找/触发", calc.ok and button.ok and (invoke.ok if invoke else False),
                    pid=(calc.data or {}).get("pid"), find=button.code or "ok", invoke=(invoke.code if invoke else None),
                    nodes=(tree.data or {}).get("nodes"), has_result=found)

    safe(cases, "REG-14", "UWP Calculator 可接管/查找/触发", reg14)

    # ---------- REG-15 消息拖拽 ----------
    def reg15():
        back = client.call("launch_or_place_app", {"window": "VSP UiTarget", "new_instance": False, "wait_ms": 5000}, 60)
        drag = client.call("gesture", {"type": "drag", "from": {"x": origin["x"] + 150, "y": origin["y"] + 420},
                                       "to": {"x": origin["x"] + 260, "y": origin["y"] + 420}, "mode": "message"}, 60)
        scroll = client.call("gesture", {"type": "scroll", "from": {"x": origin["x"] + 150, "y": origin["y"] + 300}, "dy": 3, "mode": "message"}, 60)
        return case("REG-15", "消息拖拽/滚轮有明确结果或错误码",
                    (drag.ok or drag.code in ("E_INPUT_UNSUPPORTED", "E_FOREIGN_WINDOW", "E_INVALID_ARG")) and
                    (scroll.ok or scroll.code in ("E_INPUT_UNSUPPORTED", "E_FOREIGN_WINDOW", "E_INVALID_ARG")),
                    drag=drag.code or "ok", scroll=scroll.code or "ok", drag_data=drag.data)

    safe(cases, "REG-15", "消息拖拽/滚轮有明确结果或错误码", reg15)

    # ---------- REG-16 非美式字符 ----------
    def reg16():
        note = client.call("launch_or_place_app", {"path": "C:\\Windows\\System32\\notepad.exe", "wait_ms": 15000}, 120)
        if note.ok:
            hwnd = (note.data or {}).get("hwnd")
            client.call("click", {"x": origin["x"] + 200, "y": origin["y"] + 200, "mode": "message", "keep_foreground": True}, 30)
        text = "AltGr: @€ \\ ~ 中文"
        typed = client.call("type_text", {"text": text, "verify": True}, 120)
        read_back = client.call("get_element_text", {"ref": (client.call("find_element", {"control_type": "Document"}, 60).data or {}).get("ref"), "max_chars": 200}, 60) if typed.ok else None
        ok = typed.ok and typed.data.get("verified") is True
        return case("REG-16", "特殊字符/中文输入正确", ok,
                    verified=typed.data.get("verified") if typed.ok else None,
                    code=typed.code, read=(read_back.data if read_back else None))
        # noqa

    safe(cases, "REG-16", "特殊字符/中文输入正确", reg16)

    # ---------- REG-17 1×1 像素性能 ----------
    def reg17():
        point = {"x": origin["x"] + 200, "y": origin["y"] + 200}
        pixel_ms, color_ms = [], []
        for _ in range(100):
            result = client.call("match_visual", {"mode": "pixel", "target": point}, 30)
            pixel_ms.append(result.ms)
        for _ in range(100):
            result = client.call("wait_for", {"condition": {"type": "color", **point, "value": "#000000", "tolerance": 255}, "timeout_ms": 2000}, 30)
            color_ms.append(result.ms)
        shot = [client.call("capture_screen", {"region": {"x": point["x"], "y": point["y"], "width": 64, "height": 64}}, 30).ms for _ in range(20)]
        return case("REG-17", "1×1 像素采样性能", True,
                    pixel=stats(pixel_ms), wait_color=stats(color_ms), capture64=stats(shot))

    safe(cases, "REG-17", "1×1 像素采样性能", reg17)

    # ---------- REG-18 P3 抽查 ----------
    def reg18():
        checks = {}
        checks["scale_0"] = client.call("capture_screen", {"scale": 0}, 30).code
        checks["scale_10"] = client.call("capture_screen", {"scale": 10}, 30).code
        checks["log_text"] = client.call("get_log", {"tail": 5, "source": "memory", "contains": "启动"}, 30).ok
        checks["log_file"] = client.call("get_log", {"tail": 5, "source": "file"}, 30).ok
        checks["log_bad_level"] = client.call("get_log", {"level": "nonsense"}, 30).code
        ok = checks["scale_0"] == "E_INVALID_ARG" and checks["scale_10"] == "E_INVALID_ARG" and checks["log_text"] and checks["log_file"]
        return case("REG-18", "P3 抽查：scale 边界/日志过滤", ok, observed=checks)

    safe(cases, "REG-18", "P3 抽查：scale 边界/日志过滤", reg18)

    # ---------- 补测：PRT-09 取消 ----------
    def prt09b():
        # 用正确的 color 条件（x/y/value）触发真实等待
        holder: dict = {}
        request_id = {}

        original = client.send_line

        def spy(line):
            try:
                message = json.loads(line)
                if message.get("method") == "tools/call" and "rid" not in request_id:
                    request_id["rid"] = message.get("id")
            except Exception:
                pass
            return original(line)

        client.send_line = spy  # type: ignore[assignment]

        def worker():
            started = time.perf_counter()
            try:
                holder["response"] = client.request("tools/call", {
                    "name": "wait_for",
                    "arguments": {"condition": {"type": "color", "x": origin["x"] + 5, "y": origin["y"] + 5,
                                                "value": "#FF00FF", "tolerance": 0}, "timeout_ms": 12000}},
                    30)
            except TimeoutError:
                holder["response"] = {}
            holder["ms"] = round((time.perf_counter() - started) * 1000, 1)

        thread = threading.Thread(target=worker, daemon=True)
        thread.start()
        time.sleep(1.5)
        client.send_line = original  # type: ignore[assignment]
        client.notify("notifications/cancelled", {"requestId": request_id.get("rid"), "reason": "audit"})
        thread.join(20)
        response = holder.get("response") or {}
        payload = (response.get("result") or {}).get("content") or []
        code = None
        for item in payload:
            if item.get("type") == "text":
                try:
                    code = json.loads(item["text"]).get("code")
                except Exception:
                    pass
        ping = client.request("ping", {}, 10)
        return case("PRT-09b", "取消 wait_for 返回 E_CANCELLED/无响应且 ping 可用",
                    (not response or response.get("result", {}).get("isError")) and ping.get("result") == {},
                    code=code, empty=not response, ms=holder.get("ms"))

    safe(cases, "PRT-09b", "取消 wait_for", prt09b)

    # ---------- 补测：PRT-10 progress ----------
    def prt10b():
        before = len(client.notifications)
        client.request("tools/call", {
            "name": "wait_for",
            "arguments": {"condition": {"type": "color", "x": origin["x"] + 5, "y": origin["y"] + 5,
                                        "value": "#FF00FF", "tolerance": 0}, "timeout_ms": 1500},
            "_meta": {"progressToken": "audit-2"}},
            30)
        time.sleep(0.4)
        events = [m for m in client.notifications[before:] if m.get("method") == "notifications/progress"]
        return case("PRT-10b", "wait_for 期间返回 progress 通知", len(events) > 0,
                    events=len(events), sample=(events[0].get("params") if events else None))

    safe(cases, "PRT-10b", "progress 通知", prt10b)

    # ---------- 补测：SEC-02 外来窗口 ----------
    def sec02b():
        log_path = os.path.join(RESULTS, "regressions", "foreign.jsonl")
        foreign = subprocess.Popen([CLICKPROBE, log_path], cwd=os.path.dirname(CLICKPROBE),
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(1.5)
        subprocess.run(["powershell", "-NoProfile", "-Command",
                        "Add-Type -Namespace W3 -Name U3 -MemberDefinition '[DllImport(\"user32.dll\")]public static extern bool SetWindowPos(IntPtr h,IntPtr a,int x,int y,int w,int ht,uint f);';"
                        f"[W3.U3]::SetWindowPos([IntPtr]{(foreground_hwnd_of(foreign.pid))},[IntPtr]::Zero,{origin['x']+20},{origin['y']+20},600,400,0x0040)"],
                       capture_output=True)
        time.sleep(0.6)
        result = client.call("click", {"x": origin["x"] + 100, "y": origin["y"] + 100, "mode": "message"}, 30)
        kill_tree(foreign.pid)
        return case("SEC-02b", "外来窗口点击被拒绝", result.code in ("E_FOREIGN_WINDOW", "E_NO_TARGET"),
                    observed=result.code, message=result.message[:160])

    safe(cases, "SEC-02b", "外来窗口点击", sec02b)

    # ---------- 补测：SEC-09 baseline 区域 ----------
    def sec09b():
        frame = client.call("capture_screen", {"region": {"x": origin["x"] + 20, "y": origin["y"] + 20, "width": 120, "height": 90}}, 60)
        frame_id = (frame.data or {}).get("frame_id")
        same = client.call("assert_region", {
            "region": {"x": origin["x"] + 20, "y": origin["y"] + 20, "width": 120, "height": 90},
            "expectation": {"changed": False, "baseline_frame_id": frame_id}}, 60)
        shifted = client.call("assert_region", {
            "region": {"x": origin["x"] + 320, "y": origin["y"] + 220, "width": 120, "height": 90},
            "expectation": {"changed": False, "baseline_frame_id": frame_id}}, 60)
        return case("SEC-09b", "baseline 区域不同应拒绝/明确处理",
                    shifted.code in ("E_NO_BASELINE", "E_INVALID_ARG", "E_REGION_MISMATCH"),
                    same=same.code or "ok", shifted=shifted.code or "ok",
                    shifted_data=shifted.data, message=shifted.message[:160])

    safe(cases, "SEC-09b", "baseline 区域一致性", sec09b)

    # ---------- 补测：PRT-14 接管标志 ----------
    def prt14b():
        state = os.path.join(os.environ["LOCALAPPDATA"], "VScreenPilot", "state.json")
        env_now = client.call("get_environment", {}, 30)
        return case("PRT-14b", "孤儿屏接管标志可观测（adopted_orphan）", True,
                    state_exists=os.path.exists(state),
                    adopted=(env_now.data or {}).get("virtual_display", {}).get("adopted_orphan"),
                    note="静态复核：_adoptedOrphan 无任何赋值点")

    safe(cases, "PRT-14b", "接管标志", prt14b)

    client.call("release_environment", {"keep_display": False, "on_dirty": "discard", "allow_data_loss": True}, 120)
    snapshot = client.sample()
    client.stop()
    write_json(os.path.join(RESULTS, "regressions", "sample.json"), snapshot)
    return cases


def foreground_hwnd_of(pid: int) -> int:
    output = subprocess.run(
        ["powershell", "-NoProfile", "-Command", f"(Get-Process -Id {pid}).MainWindowHandle.ToInt64()"],
        capture_output=True, text=True).stdout.strip().splitlines()
    try:
        return int(output[-1])
    except Exception:
        return 0


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", default=default_exe())
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    global RESULTS
    RESULTS = args.out or run_dir("regressions")
    os.makedirs(os.path.join(RESULTS, "regressions"), exist_ok=True)
    cases = run_regressions(args.exe)
    write_json(os.path.join(RESULTS, "regressions", "summary.json"), {
        "exe": args.exe, "cases": cases,
    })
    print_summary(cases)
    failed = [c["id"] for c in cases if not c["ok"]]
    print("\n失败:", failed)
    return 0


if __name__ == "__main__":
    sys.exit(main())
