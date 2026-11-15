"""DIA-01..04：模态对话框可达性（Word 保存/查找、Notepad 另存为）与只读文档。

用法：
  python tools/audit/bench_dialogs.py --exe artifacts\\server\\VScreenPilot.exe --out tools\\audit\\results\\run2
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import json
import os
import shutil
import sys
import time
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import McpClient, default_exe, run_dir, write_json, case, print_summary  # noqa: E402

WORD = r"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE"
NOTE = r"C:\Windows\System32\notepad.exe"
HERE = os.path.dirname(os.path.abspath(__file__))


def windows_of(pid: int) -> list[dict]:
    found: list[dict] = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def callback(hwnd, _lparam):
        process_id = wt.DWORD()
        ctypes.windll.user32.GetWindowThreadProcessId(hwnd, ctypes.byref(process_id))
        # pid<=0 表示枚举全部可见窗口：单实例应用可能把窗口交给另一个进程。
        if pid > 0 and process_id.value != pid:
            return True
        length = ctypes.windll.user32.GetWindowTextLengthW(hwnd)
        title = ctypes.create_unicode_buffer(length + 2)
        ctypes.windll.user32.GetWindowTextW(hwnd, title, length + 2)
        klass = ctypes.create_unicode_buffer(256)
        ctypes.windll.user32.GetClassNameW(hwnd, klass, 256)
        visible = bool(ctypes.windll.user32.IsWindowVisible(hwnd))
        rect = wt.RECT()
        ctypes.windll.user32.GetWindowRect(hwnd, ctypes.byref(rect))
        if visible and title.value:
            found.append({"hwnd": int(hwnd), "title": title.value, "class": klass.value,
                          "owner": int(ctypes.windll.user32.GetWindow(hwnd, 4)),
                          "rect": [rect.left, rect.top, rect.right, rect.bottom]})
        return True

    ctypes.windll.user32.EnumWindows(callback, 0)
    return found


def tree_names(client: McpClient) -> set[str]:
    tree = client.call("get_ui_tree", {"max_depth": 5, "interactive_only": False, "max_nodes": 400}, 60)
    names: set[str] = set()

    def walk(node: dict) -> None:
        if node.get("name"):
            names.add(node["name"])
        for child in node.get("children") or []:
            walk(child)

    data = tree.data or {}
    if isinstance(data.get("tree"), dict):
        walk(data["tree"])
    return names


def controls_inside(client: McpClient, dialog: dict | None) -> int:
    """统计落在对话框矩形内的可交互控件；UIA 看不到时由 MSAA 后端补齐。"""
    if not dialog:
        return 0
    result = client.call("find_element", {"all": True, "max_results": 200}, 60)
    elements = (result.data or {}).get("elements") or []
    left, top, right, bottom = dialog.get("rect") or [0, 0, 0, 0]
    count = 0
    for element in elements:
        bounds = element.get("bounds") or {}
        cx = (bounds.get("x") or 0) + (bounds.get("width") or 0) // 2
        cy = (bounds.get("y") or 0) + (bounds.get("height") or 0) // 2
        if left < cx < right and top < cy < bottom:
            count += 1
    return count


def body_text(client: McpClient) -> str:
    found = client.call("find_element", {"role": "document_body"}, 30)
    if not found.ok:
        return ""
    ref = (found.data or {}).get("ref")
    text = client.call("get_element_text", {"ref": ref, "max_chars": 4000}, 30)
    return (text.data or {}).get("text") or ""


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", default=default_exe())
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    out_dir = args.out or run_dir("dialogs")
    dialog_dir = os.path.join(out_dir, "dialogs")
    os.makedirs(dialog_dir, exist_ok=True)
    cases: list[dict] = []

    client = McpClient(args.exe).start()
    client.call("get_environment", {"ensure": True}, 120)
    time.sleep(0.5)

    def word_case(case_id: str, purpose: str, open_keys: str, dialog_hint: str,
                  type_text: str, ui_hint: str) -> None:
        client.call("release_environment", {"keep_display": True, "on_dirty": "discard", "allow_data_loss": True}, 120)
        launch = client.call("launch_or_place_app", {"path": WORD, "wait_ms": 60000}, 120)
        if not launch.ok:
            cases.append(case(case_id, purpose, False, launch=launch.code, message=launch.message[:120]))
            return
        pid = (launch.data or {}).get("pid")
        client.call("click", {"x": 2420, "y": 300, "mode": "message", "keep_foreground": True}, 30)
        client.call("type_text", {"text": "SEED-FOR-DIALOG", "verify": False}, 30)
        time.sleep(1.0)
        before = body_text(client)
        names_before = tree_names(client)
        client.call("press_keys", {"keys": open_keys, "keep_foreground": True}, 30)
        time.sleep(2.0)
        windows = windows_of(pid or 0)
        names = tree_names(client)
        dialog = next((w for w in windows if dialog_hint in w["title"]), None)

        if dialog is None:
            # Word 单实例会把窗口交给另一个进程，按 pid 过滤会漏掉对话框。
            extra = windows_of(0)
            dialog = next((w for w in extra if dialog_hint in w["title"]), None)
            windows = windows + [w for w in extra if w not in windows]
        inside = controls_inside(client, dialog)
        typed = client.call("type_text", {"text": type_text, "verify": False}, 30)
        time.sleep(0.4)
        after = body_text(client)
        # 关掉 Word（对话框随进程结束）
        client.call("release_environment", {"keep_display": True, "on_dirty": "discard", "allow_data_loss": True}, 120)
        new_names = names - names_before
        ui_reachable = any(ui_hint in name for name in new_names)
        reachable = bool(dialog) and (dialog["title"] in names or inside > 0) or ui_reachable
        cases.append(case(case_id, purpose,
                          reachable and type_text not in after,
                          dialog=dialog, in_uia=bool(dialog and dialog["title"] in names),
                          controls_inside=inside, ui_reachable=ui_reachable,
                          windows=[w["title"] for w in windows][:6],
                          typed=typed.code or "ok", landed_in_document=type_text in after,
                          before_len=len(before), after_len=len(after)))

    word_case("DIA-01", "Word 另存为对话框：可被语义后端发现，文本不落入正文",
              "Ctrl+S", "另存", "DIALOG-SAVE-TEXT", "保存")
    word_case("DIA-02", "Word 查找替换：可被语义后端发现，文本不落入正文",
              "Ctrl+H", "查找", "DIALOG-FIND-TEXT", "替换")

    # Notepad 另存为
    client.call("release_environment", {"keep_display": True, "on_dirty": "discard", "allow_data_loss": True}, 120)
    note = client.call("launch_or_place_app", {"path": NOTE, "new_instance": True, "wait_ms": 15000}, 120)
    note_pid = (note.data or {}).get("pid")
    client.call("press_keys", {"keys": "Ctrl+N", "keep_foreground": True}, 30)
    time.sleep(0.8)
    client.call("click", {"x": 2420, "y": 300, "mode": "message", "keep_foreground": True}, 30)
    client.call("type_text", {"text": "NOTEPAD-SEED", "verify": True}, 30)
    client.call("press_keys", {"keys": "Ctrl+S", "keep_foreground": True}, 30)
    time.sleep(2.0)
    note_windows = windows_of(note_pid or 0)
    note_names = tree_names(client)
    note_dialog = next((w for w in note_windows if "另存为" in w["title"] or "Save As" in w["title"]), None)
    note_inside = controls_inside(client, note_dialog)
    typed_note = client.call("type_text", {"text": "DIALOG-NOTE-TEXT", "verify": False}, 30)
    time.sleep(0.4)
    cases.append(case("DIA-03", "Notepad 另存为：可被语义后端发现",
                      bool(note_dialog) and (note_dialog["title"] in note_names or note_inside > 0),
                      dialog=note_dialog, in_uia=bool(note_dialog and note_dialog["title"] in note_names),
                      controls_inside=note_inside,
                      windows=[w["title"] for w in note_windows][:6],
                      typed=typed_note.code or "ok"))
    client.call("release_environment", {"keep_display": True, "on_dirty": "discard", "allow_data_loss": True}, 120)

    # 只读/受保护视图：给 docx 加 Zone.Identifier（ZoneId=3）
    fixture = os.path.join(dialog_dir, "protected.docx")
    shutil.copyfile(os.path.join(HERE, "results", "run1", "word", "sample.docx"), fixture)
    os.chmod(fixture, 0o666)
    with open(fixture + ":Zone.Identifier", "w", encoding="utf-8") as handle:
        handle.write("[ZoneTransfer]\r\nZoneId=3\r\n")
    launch = client.call("launch_or_place_app", {"path": WORD, "args": fixture, "wait_ms": 60000}, 120)
    time.sleep(3)
    typed = client.call("type_text", {"text": "PROTECTED-EDIT", "verify": True}, 60)
    shot = client.call("capture_screen", {"save_path": os.path.join(dialog_dir, "protected-view.png")}, 60)
    cases.append(case("DIA-04", "受保护视图/只读：输入应失败或明确提示",
                      (not typed.ok) or typed.data.get("verified") is False,
                      launch=(launch.data or {}).get("title"), typed=typed.code or "ok",
                      verified=(typed.data or {}).get("verified"), message=typed.message[:160],
                      shot=shot.ok))

    client.call("release_environment", {"keep_display": False, "on_dirty": "discard", "allow_data_loss": True}, 120)
    client.stop()
    write_json(os.path.join(dialog_dir, "summary.json"), {"cases": cases})
    print_summary(cases)
    return 0


if __name__ == "__main__":
    sys.exit(main())
