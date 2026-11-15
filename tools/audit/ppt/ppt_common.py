"""PPT 轮测试公共层：启动 PowerPoint、定位幻灯片区域、判定文本是否可见。

约定：
  * 只用标准库；
  * 固定 profile `pptbench` + VSP_DISPLAY_OFFSET=0（避开 OpenCode 的 default 互斥体，
    也避开 S1：非默认偏移目前挂载会失败）；
  * 每个脚本结束都释放虚拟屏，失败也不留孤儿。
"""

from __future__ import annotations

import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
AUDIT = os.path.dirname(HERE)
sys.path.insert(0, AUDIT)

from vsp_client import McpClient  # noqa: E402

POWERPNT = r"C:\Program Files\Microsoft Office\root\Office16\POWERPNT.EXE"
PROFILE = "pptbench"
REPO = os.path.dirname(os.path.dirname(AUDIT))
DECK_DIR = os.path.join(REPO, "artifacts", "ppt")


def open_client(exe: str, profile: str = PROFILE) -> McpClient:
    client = McpClient(
        exe,
        env={"VSP_PROFILE": profile, "VSP_DISPLAY_OFFSET": "0"},
        args=["--profile", profile, "--parent-pid", str(os.getpid())],
    )
    client.start(timeout=60, warmup=False, protocol="2025-11-25")
    return client


def ensure_clean(client: McpClient) -> None:
    """清掉上次异常退出可能留下的虚拟屏（同 profile 才有效）。"""
    env = client.call("get_environment", {}, 30)
    display = (env.data or {}).get("virtual_display") or {}
    if display.get("enabled"):
        client.call("release_environment", {"keep_display": False, "on_dirty": "force"}, 90)


def display_rect(client: McpClient) -> dict:
    env = client.call("get_environment", {"ensure": True}, 120)
    if not env.ok:
        raise RuntimeError(f"ensure 失败: {env.code} {(env.data or {}).get('message')}")
    return (env.data or {}).get("virtual_display") or {}


def app_title(client: McpClient) -> str:
    data = client.call("get_environment", {}, 30).data or {}
    apps = data.get("apps") or []
    return str(apps[0].get("title") or "") if apps else ""


def safe_launch(client: McpClient) -> dict:
    """确保有一份空白演示文稿；失败时返回证据而不是抛异常。"""
    app = client.call("launch_or_place_app", {"path": POWERPNT, "wait_ms": 60000}, 150)
    record = {
        "ok": app.ok,
        "code": app.code,
        "ms": app.ms,
        "pid": (app.data or {}).get("pid"),
        "title": (app.data or {}).get("title"),
        "document_ready": (app.data or {}).get("document_ready"),
        "maximized": (app.data or {}).get("maximized"),
        "dialogs": (app.data or {}).get("dialogs"),
        "warnings": (app.data or {}).get("warnings"),
    }
    if not app.ok:
        return record

    # PowerPoint 默认停在开始屏幕（Backstage）。Ctrl+N 在该状态下无效（实测），
    # 必须点“空白演示文稿”磁贴：虚拟屏原点 (1920,0)，磁贴中心约 (200,225)。
    if " - PowerPoint" not in str((app.data or {}).get("title") or ""):
        display = (client.call("get_environment", {}, 30).data or {}).get("virtual_display") or {}
        origin_x = display.get("x", 1920)
        clicked = client.call(
            "click",
            {"x": origin_x + 200, "y": 225, "channel": "message", "keep_foreground": True},
            30,
        )
        record["blank_tile_click"] = clicked.ok and clicked.code is None
        time.sleep(3.0)
        # 首次运行可能弹出 OfficePLUS/AI 助手浮层；尽力关掉（Chromium 浮层只能 input）。
        client.call("click", {"x": origin_x + 1005, "y": 131, "channel": "input", "keep_foreground": True}, 30)
        time.sleep(0.5)

    snap = client.call("snapshot", {"max_depth": 2, "max_nodes": 60}, 60)
    dialogs = (snap.data or {}).get("dialogs") or []
    record["dialogs_after"] = [d.get("title") for d in dialogs]

    record["title_after"] = app_title(client)
    return record


def slide_area(client: McpClient) -> dict:
    """优先用 UIA 的 Document 边界；否则按窗口几何估算。"""
    found = client.call(
        "find_element",
        {"control_type": "Document", "include_offscreen": True, "prefer": "largest"},
        30,
    )
    bounds = (found.data or {}).get("bounds") if found.ok else None

    if isinstance(bounds, dict) and bounds.get("width", 0) > 200:
        return {"source": "document", **bounds}

    display = display_rect(client)
    # 1280x720 下的估算：左侧缩略图 ~235px、顶部功能区 ~180px、底部状态栏 ~45px。
    return {
        "source": "estimate",
        "x": display.get("x", 0) + 235,
        "y": display.get("y", 0) + 180,
        "width": max(320, display.get("width", 1280) - 260),
        "height": max(240, display.get("height", 720) - 225),
    }


def title_point(area: dict) -> tuple[int, int]:
    return (
        int(area["x"] + area["width"] * 0.5),
        int(area["y"] + area["height"] * 0.18),
    )


def body_point(area: dict) -> tuple[int, int]:
    return (
        int(area["x"] + area["width"] * 0.5),
        int(area["y"] + area["height"] * 0.62),
    )


def text_visible(client: McpClient, text: str) -> dict:
    """三重判定：find_element contains → snapshot.text → 无。"""
    found = client.call(
        "find_element",
        {"name": text, "name_match": "contains", "include_offscreen": True},
        30,
    )
    if found.ok:
        return {"visible": True, "via": "find_element", "ref": (found.data or {}).get("ref")}

    snap = client.call("snapshot", {"max_depth": 3, "max_nodes": 200}, 60)
    blob = str((snap.data or {}).get("text") or "")
    if text in blob:
        return {"visible": True, "via": "snapshot_text"}
    return {"visible": False, "via": "none", "snapshot_nodes": (snap.data or {}).get("nodes")}


def type_into(client: McpClient, x: int, y: int, text: str, channel: str = "message") -> dict:
    clicked = client.call("click", {"x": x, "y": y, "channel": channel, "keep_foreground": True}, 30)
    typed = client.call("type_text", {"text": text, "verify": False, "chunk_chars": 64}, 120)
    return {
        "click_ok": clicked.ok,
        "click_code": clicked.code,
        "click_channel": (clicked.data or {}).get("channel"),
        "type_ok": typed.ok,
        "type_code": typed.code,
        "typed": (typed.data or {}).get("typed"),
        "type_ms": typed.ms,
        "text_bytes": typed.text_bytes,
    }


def release(client: McpClient, keep_display: bool = False) -> dict:
    result = client.call(
        "release_environment", {"keep_display": keep_display, "on_dirty": "force"}, 120
    )
    return {
        "ok": result.ok,
        "code": result.code,
        "display_removed": (result.data or {}).get("display_removed"),
        "warnings": (result.data or {}).get("warnings"),
        "closed_pids": (result.data or {}).get("closed_pids"),
    }
