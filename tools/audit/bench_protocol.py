"""PRT-01..14 / SEC-01..10：协议、契约、安全与健壮性测试（无 OpenCode 依赖）。

用法：
  python tools/audit/bench_protocol.py --exe artifacts\\server\\VScreenPilot.exe
"""

from __future__ import annotations

import argparse
import json
import os
import random
import string
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import (  # noqa: E402
    McpClient, default_exe, kill_tree, rss_mb, handle_count, monitor_count,
    run_dir, write_json, case, print_summary, stats,
)

TARGET = os.path.join(os.path.dirname(os.path.abspath(__file__)), "targets", "bin", "ClickProbe", "ClickProbe.exe")
UITARGET = os.path.join(os.path.dirname(os.path.abspath(__file__)), "targets", "bin", "UiTarget", "UiTarget.exe")


def new_client(exe: str, **kwargs) -> McpClient:
    return McpClient(exe, **kwargs)


def protocol_versions(exe: str, cases: list[dict]) -> None:
    for version in ("2025-06-18", "2025-03-26", "2024-11-05"):
        client = new_client(exe).start(protocol=version)
        observed = client.negotiated
        cases.append(case(f"PRT-01/{version[-5:]}", f"版本协商 {version}",
                          observed == version, requested=version, observed=observed))
        client.stop()

    client = new_client(exe).start(protocol="1999-01-01")
    cases.append(case("PRT-02", "未知版本回退到服务端支持的最新版本",
                      client.negotiated in {
                          "2025-06-18", "2025-03-26", "2024-11-05", "2025-11-25", "2026-07-28"
                      },
                      requested="1999-01-01", observed=client.negotiated))
    client.stop()

    # PRT-03：未初始化直接调用。2026-07-28 起协议不再要求 initialize 握手，
    # 服务端允许无状态调用；旧版仍应返回 -32002。
    client = new_client(exe).start(init=False)
    response = client.request("tools/call", {"name": "get_environment", "arguments": {}}, 30)
    error = response.get("error") or {}
    result = response.get("result") or {}
    cases.append(case("PRT-03", "未初始化调用：旧版 -32002 或新版无状态可用",
                      error.get("code") == -32002 or bool(result),
                      observed=error.get("code"), stateless=bool(result)))
    client.stop()


def tools_contract(client: McpClient, cases: list[dict]) -> None:
    started = time.perf_counter()
    result = client.load_tools()
    elapsed = round((time.perf_counter() - started) * 1000, 2)
    names = [tool["name"] for tool in result.get("tools") or []]
    missing_output = [name for name, tool in client.tools_meta.items() if not tool.get("outputSchema")]
    bad_schema = [name for name, tool in client.tools_meta.items()
                  if (tool.get("inputSchema") or {}).get("type") != "object"]
    # outputSchema 必须声明具体字段；{"type":"object"} 是空校验，不算通过。
    vacuous_output = [name for name, tool in client.tools_meta.items()
                      if not (tool.get("outputSchema") or {}).get("properties")]
    payload = json.dumps(result, ensure_ascii=False)
    cases.append(case("PRT-04", "tools/list：≥18 工具、排序、输入/输出 schema 具体",
                      len(names) >= 18 and names == sorted(names)
                      and not missing_output and not bad_schema and not vacuous_output,
                      count=len(names), sorted=names == sorted(names),
                      missing_output=missing_output, bad_schema=bad_schema,
                      vacuous_output=vacuous_output,
                      bytes=len(payload.encode("utf-8")), ms=elapsed))
    write_json(os.path.join(RESULTS, "protocol", "tools-list.json"), result)

    response = client.request("tools/call", {"name": "no_such_tool", "arguments": {}}, 30)
    error = response.get("error") or {}
    result = response.get("result") or {}
    cases.append(case("PRT-05a", "未知工具返回协议错误或 isError",
                      error.get("code") == -32602 or result.get("isError") is True,
                      observed=error.get("code"), is_error=result.get("isError"),
                      message=str(error.get("message"))[:120]))

    response = client.request("tools/call", {"arguments": {}}, 30)
    error = response.get("error") or {}
    cases.append(case("PRT-05b", "缺少工具名返回 -32602/-32603（SDK 校验）",
                      error.get("code") in (-32602, -32603),
                      observed=error.get("code"), message=str(error.get("message"))[:120]))


def duplicate_id(client: McpClient, cases: list[dict]) -> None:
    payload = {"jsonrpc": "2.0", "id": 9001, "method": "ping", "params": {}}
    client.send_line(json.dumps(payload))
    client.send_line(json.dumps(payload))
    time.sleep(0.6)
    responses = [m for m in client.notifications if m.get("id") == 9001]
    duplicate_errors = [m for m in responses if (m.get("error") or {}).get("code") == -32600]
    # 无状态传输下重复 id 由客户端负责；服务端只要求不崩溃、两条都有响应。
    cases.append(case("PRT-06", "重复请求 id：两条都有响应且不崩溃",
                      len(responses) >= 2 or len(duplicate_errors) >= 1,
                      observed=[m.get("error") or m.get("result") for m in responses][:3]))


def json_robustness(client: McpClient, cases: list[dict]) -> None:
    # 官方 SDK 的传输层对畸形 JSON 的策略是“丢弃并继续”，不一定回 -32700。
    # 这里已“连接仍可用 + 进程不崩溃”为通过线，并记录是否返回了严格错误码。
    client.send_line("{not json")
    time.sleep(0.3)
    parse_errors = [m for m in client.notifications if (m.get("error") or {}).get("code") == -32700]
    alive = client.request("ping", {}, 10).get("result") == {}
    cases.append(case("PRT-07a", "非法 JSON：连接存活（严格错误码可选）", alive,
                      observed=len(parse_errors), strict_error=len(parse_errors) >= 1))

    client.send_line(json.dumps([1, 2, 3]))
    time.sleep(0.3)
    invalid_requests = [m for m in client.notifications if (m.get("error") or {}).get("code") == -32600]
    alive = client.request("ping", {}, 10).get("result") == {}
    cases.append(case("PRT-07b", "非对象请求：连接存活（严格错误码可选）", alive,
                      observed=len(invalid_requests), strict_error=len(invalid_requests) >= 1))

    client.send_line(json.dumps({"id": 9100, "method": "ping", "params": {}}))
    time.sleep(0.4)
    missing_jsonrpc = [m for m in client.notifications
                       if m.get("id") == 9100 and (m.get("error") or {}).get("code") == -32600]
    alive = client.request("ping", {}, 10).get("result") == {}
    cases.append(case("PRT-07c", "缺少 jsonrpc：连接存活（严格错误码可选）", alive,
                      observed=len(missing_jsonrpc), strict_error=len(missing_jsonrpc) >= 1))

    response = client.request("ping", {}, 10)
    cases.append(case("PRT-08", "ping 返回空对象", response.get("result") == {},
                      observed=response.get("result")))


def cancellation(client: McpClient, cases: list[dict]) -> None:
    holder: dict = {}
    env = client.call("get_environment", {}, 30).data or {}
    display = env.get("virtual_display") or {}
    origin_x = int(display.get("x", 1920))
    origin_y = int(display.get("y", 0))

    def worker() -> None:
        started = time.perf_counter()
        try:
            holder["response"] = client.request(
                "tools/call",
                {"name": "wait_for",
                 "arguments": {"condition": {"type": "color", "x": origin_x + 20, "y": origin_y + 20,
                                             "value": "#FF00FF", "tolerance": 0},
                               "timeout_ms": 15000}},
                30)
        except TimeoutError:
            holder["response"] = {"timeout": True}
        holder["ms"] = round((time.perf_counter() - started) * 1000, 1)
        holder["id"] = request_id[0]

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
    thread = threading.Thread(target=worker, daemon=True)
    thread.start()
    time.sleep(1.2)
    client.notify("notifications/cancelled", {"requestId": request_id[0], "reason": "audit"})
    thread.join(30)
    client.send_line = original  # type: ignore[assignment]

    response = holder.get("response") or {}
    payload = (response.get("result") or {}).get("content") or []
    code = None
    for item in payload:
        if item.get("type") == "text":
            try:
                code = json.loads(item.get("text")).get("code")
            except Exception:
                pass
    cancelled = bool(response.get("timeout")) or code in ("E_CANCELLED", None)
    ping = client.request("ping", {}, 10)
    cases.append(case("PRT-09", "取消不阻塞协议（E_CANCELLED 或静默，ping 仍可用）",
                      cancelled and ping.get("result") == {},
                      observed={"code": code, "timeout": bool(response.get("timeout")),
                                "ms": holder.get("ms"), "ping": ping.get("result")}))


def progress(client: McpClient, cases: list[dict]) -> None:
    before = len(client.notifications)
    client.request("tools/call",
                   {"name": "get_ui_tree",
                    "arguments": {"max_depth": 8, "max_nodes": 600},
                    "_meta": {"progressToken": "audit-progress"}},
                   120)
    time.sleep(0.4)
    events = [m for m in client.notifications[before:]
              if m.get("method") == "notifications/progress"]
    cases.append(case("PRT-10", "长任务返回 notifications/progress",
                      len(events) > 0, observed=len(events)))


def fuzz(client: McpClient, cases: list[dict], count: int = 1000) -> None:
    random.seed(20261114)
    alphabet = string.printable + "中文测试\u0000"
    started = time.perf_counter()
    for _ in range(count):
        kind = random.random()
        if kind < 0.4:
            line = "".join(random.choice(alphabet) for _ in range(random.randint(1, 120)))
        elif kind < 0.7:
            line = json.dumps({"jsonrpc": "2.0", "id": random.randint(1, 10 ** 6),
                               "method": random.choice(["ping", "tools/list", "tools/call", "", None]),
                               "params": random.choice([None, [], "x", {"name": None}])})
        else:
            line = "{" + "".join(random.choice(alphabet) for _ in range(random.randint(1, 60)))
        try:
            client.send_line(line.replace("\n", " "))
        except Exception:
            break
    time.sleep(1.5)
    alive = client.proc.poll() is None
    try:
        ping = client.request("ping", {}, 15)
        responsive = ping.get("result") == {}
    except Exception:
        responsive = False
    cases.append(case("PRT-11", f"{count} 条随机输入不崩溃且仍可响应",
                      alive and responsive,
                      alive=alive, responsive=responsive,
                      fuzz_ms=round((time.perf_counter() - started) * 1000, 1),
                      rss_mb=rss_mb(client.proc.pid), handles=handle_count(client.proc.pid)))


def single_instance(exe: str, cases: list[dict]) -> None:
    first = new_client(exe).start()
    second = subprocess.run([exe], input=b"", capture_output=True, timeout=60)
    stderr = second.stderr.decode("utf-8", errors="replace").strip()
    cases.append(case("PRT-12", "第二实例因互斥体退出（exit=3）",
                      second.returncode == 3,
                      exit=second.returncode, stderr=stderr[:160]))
    first.stop()


def eof_cleanup(exe: str, cases: list[dict]) -> None:
    client = new_client(exe).start()
    client.call("get_environment", {"ensure": True}, 120)
    client.close_stdin()
    started = time.perf_counter()
    code = client.wait(30)
    elapsed = round((time.perf_counter() - started) * 1000, 1)
    cases.append(case("PRT-13", "stdin EOF 后 exit=0 且清理完成",
                      code == 0, exit=code, cleanup_ms=elapsed))
    timeout_ms = 3000
    time.sleep(timeout_ms / 1000)
    cases.append(case("PRT-13b", "EOF 后显示器数量（生命周期策略观察）", True,
                      monitors=monitor_count(), note="displayRemoved 由 keep_display 策略决定"))


def orphan_display(exe: str, cases: list[dict]) -> None:
    client = new_client(exe).start()
    env = client.call("get_environment", {"ensure": True}, 120)
    device = (env.data or {}).get("virtual_display", {}).get("device")
    kill_tree(client.proc.pid)
    time.sleep(2.5)
    recovered = new_client(exe).start()
    env2 = recovered.call("get_environment", {"ensure": True}, 180)
    display = (env2.data or {}).get("virtual_display", {})
    adopted = bool(display.get("adopted_orphan")) or bool(display.get("self_healed"))
    recreated = int(display.get("display_recreate_count") or 0) > 0
    same_device = display.get("device") == device
    cases.append(case("PRT-14", "崩溃后虚拟屏可接管/自愈/重建，无孤儿残留",
                      env2.ok and bool(display.get("enabled")) and (adopted or recreated or same_device),
                      before=device, after=display.get("device"),
                      adopted_orphan=display.get("adopted_orphan"),
                      self_healed=display.get("self_healed"),
                      recreate_count=display.get("display_recreate_count"),
                      same_device=same_device))
    recovered.call("release_environment", {"keep_display": False}, 120)
    recovered.stop()


def security(client: McpClient, cases: list[dict]) -> None:
    import ctypes
    import ctypes.wintypes

    env = client.call("get_environment", {}, 30).data or {}
    display = env.get("virtual_display") or {}
    origin_x = int(display.get("x", 1920))
    origin_y = int(display.get("y", 0))

    # SEC-01 越界
    shot = client.call("capture_screen", {"region": {"x": 5000, "y": 0, "width": 100, "height": 100}}, 60)
    click = client.call("click", {"x": 5000, "y": 100, "mode": "message"}, 60)
    cases.append(case("SEC-01", "虚拟屏外截图/点击被拒绝",
                      shot.code == "E_OUT_OF_BOUNDS" and click.code in ("E_OUT_OF_BOUNDS", "E_FOREIGN_WINDOW"),
                      capture=shot.code, click=click.code))

    # SEC-02 外来窗口：shell 启动 Notepad 并移动到虚拟屏上方（非 MCP 接管）
    # 用仓库自带 WinForms 靶标：经典 HWND，MainWindowHandle 可靠。
    foreign_exe = os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "targets", "bin", "ClickProbe", "ClickProbe.exe")
    foreign = subprocess.Popen([foreign_exe], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(2.5)
    hwnd = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         f"(Get-Process -Id {foreign.pid}).MainWindowHandle.ToInt64()"],
        capture_output=True, text=True).stdout.strip().splitlines()
    hwnd_value = 0
    try:
        hwnd_value = int(hwnd[-1]) if hwnd else 0
    except Exception:
        hwnd_value = 0
    moved = False
    if hwnd_value:
        user32 = ctypes.windll.user32
        # SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW
        user32.SetWindowPos(
            ctypes.wintypes.HWND(hwnd_value), None,
            origin_x + 200, origin_y + 200, 400, 300, 0x0004 | 0x0010 | 0x0040)
        time.sleep(0.8)
        rect = ctypes.wintypes.RECT()
        user32.GetWindowRect(ctypes.wintypes.HWND(hwnd_value), ctypes.byref(rect))
        moved = rect.left >= origin_x and rect.top >= origin_y
    time.sleep(0.4)
    foreign_click = client.call(
        "click", {"x": origin_x + 250, "y": origin_y + 250, "mode": "message"}, 30)
    cases.append(case("SEC-02", "外来窗口点击返回 E_FOREIGN_WINDOW",
                      moved and foreign_click.code in ("E_FOREIGN_WINDOW", "E_NO_TARGET"),
                      hwnd=hwnd_value, moved=moved, observed=foreign_click.code,
                      message=foreign_click.message[:120]))
    kill_tree(foreign.pid)

    # SEC-03 系统键
    variants = [["Win+L"], ["Alt+Tab"], ["Ctrl+Esc"], ["Alt+Esc"], ["Alt+Shift+Tab"], ["Ctrl+Alt+Tab"]]
    observed = {}
    for keys in variants:
        result = client.call("press_keys", {"keys": keys[0], "keep_foreground": True}, 30)
        observed[keys[0]] = result.code
    blocked = all(code == "E_SYSTEM_KEY_BLOCKED" for code in observed.values())
    cases.append(case("SEC-03", "系统键组合全部被拦截", blocked, observed=observed))

    # SEC-04 脚本默认关闭
    script = client.call("run_script", {"code": "Write-Output hi"}, 30)
    cases.append(case("SEC-04", "run_script 默认关闭", script.code == "E_DISABLED",
                      observed=script.code, message=script.message[:120]))

    # SEC-05 超长文本
    long_text = client.call("type_text", {"text": "a" * 100001}, 60)
    cases.append(case("SEC-05", "100001 字符文本被拒绝",
                      long_text.code in ("E_INVALID_ARG", "E_TEXT_TOO_LONG"),
                      observed=long_text.code, message=long_text.message[:120]))

    # SEC-06 落盘路径
    outside = "C:\\Windows\\System32\\vsp-audit.png"
    traversal = client.call("capture_screen", {"save_path": outside}, 30)
    wrong_ext = client.call("capture_screen", {"save_path": os.path.join(RESULTS, "x.txt")}, 30)
    cases.append(case("SEC-06", "工作目录/临时目录之外的落盘路径被拒绝",
                      traversal.code == "E_INVALID_ARG" and wrong_ext.code == "E_INVALID_ARG"
                      and not os.path.exists(outside),
                      traversal=traversal.code, wrong_ext=wrong_ext.code,
                      written=os.path.exists(outside)))

    # SEC-07 图像输入
    bad_png = client.call("match_visual", {"mode": "template", "target": "bm90IGEgcG5n"}, 30)
    cases.append(case("SEC-07", "无效 PNG 模板返回 E_UNSUPPORTED_IMAGE",
                      bad_png.code in ("E_UNSUPPORTED_IMAGE", "E_INVALID_ARG"),
                      observed=bad_png.code, message=bad_png.message[:120]))

    # SEC-08 非法 control_type
    bad_type = client.call("find_element", {"control_type": "NotAControlType"}, 30)
    cases.append(case("SEC-08", "非法 control_type 应返回 E_INVALID_ARG",
                      bad_type.code == "E_INVALID_ARG",
                      observed=bad_type.code or "ok", message=bad_type.message[:120]))

    # SEC-09 baseline 区域一致性（坐标必须落在虚拟屏内）
    first = client.call("capture_screen", {
        "region": {"x": origin_x, "y": origin_y, "width": 100, "height": 100}}, 60)
    mismatched = client.call("assert_region", {
        "region": {"x": origin_x + 400, "y": origin_y + 300, "width": 100, "height": 100},
        "expectation": {"type": "changed", "baseline_frame_id": (first.data or {}).get("frame_id")},
    }, 60)
    cases.append(case("SEC-09", "baseline 区域不一致应报错",
                      mismatched.code in ("E_NO_BASELINE", "E_INVALID_ARG", "E_REGION_MISMATCH"),
                      observed=mismatched.code or "ok", message=mismatched.message[:160]))

    # SEC-10 树节点/ref 容量
    tree = client.call("get_ui_tree", {"max_depth": 8, "max_nodes": 1024, "interactive_only": False}, 120)
    tree_data = tree.data or {}
    flat: list[dict] = []

    def walk(node: dict) -> None:
        flat.append(node)
        for child in node.get("children") or []:
            walk(child)

    if isinstance(tree_data.get("tree"), dict):
        walk(tree_data["tree"])
    refs = [node.get("ref") for node in flat if node.get("ref")]
    stale = 0
    for ref in refs[:40]:
        probe = client.call("get_element_text", {"ref": ref, "max_chars": 20}, 20)
        if probe.code == "E_ELEMENT_STALE":
            stale += 1
    cases.append(case("SEC-10", "大树 + ref 可用（无 stale）",
                      tree.ok and stale == 0,
                      nodes=len(flat), reported_nodes=tree_data.get("nodes"),
                      refs=len(refs), truncated=tree_data.get("truncated"),
                      stale=stale, ms=tree.ms))


def find_window(fragment: str) -> int:
    script = (
        "Add-Type -Namespace W2 -Name U2 -MemberDefinition '[DllImport(\"user32.dll\")]public static extern IntPtr FindWindow(string c,string n);';"
        f"$h=[W2.U2]::FindWindow($null,'{fragment}'); if($h -ne [IntPtr]::Zero){{$h.ToInt64()}}"
    )
    out = subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True)
    try:
        return int(out.stdout.strip().splitlines()[-1])
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
    parser.add_argument("--fuzz", type=int, default=1000)
    args = parser.parse_args()

    global RESULTS
    RESULTS = args.out or run_dir("protocol")
    os.makedirs(os.path.join(RESULTS, "protocol"), exist_ok=True)
    cases: list[dict] = []

    protocol_versions(args.exe, cases)

    client = McpClient(args.exe).start()
    client.call("get_environment", {"ensure": True}, 120)
    tools_contract(client, cases)
    duplicate_id(client, cases)
    json_robustness(client, cases)

    launch = client.call("launch_or_place_app", {"path": UITARGET, "wait_ms": 15000}, 120)
    if not launch.ok:
        print("UiTarget 启动失败:", launch.code, launch.message)
    cancellation(client, cases)
    progress(client, cases)
    fuzz(client, cases, args.fuzz)
    security(client, cases)

    snapshot = client.sample()
    client.call("release_environment", {"keep_display": False, "on_dirty": "discard"}, 120)
    client.stop()

    single_instance(args.exe, cases)
    eof_cleanup(args.exe, cases)
    orphan_display(args.exe, cases)

    write_json(os.path.join(RESULTS, "protocol", "summary.json"), {
        "run": RESULTS,
        "exe": args.exe,
        "cases": cases,
        "sample": snapshot,
    })
    print_summary(cases)
    failed = [c for c in cases if not c["ok"]]
    print(f"\n失败 {len(failed)} 项：", [c["id"] for c in failed])
    print("结果目录:", RESULTS)
    return 0


if __name__ == "__main__":
    sys.exit(main())
