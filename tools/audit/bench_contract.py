"""CONT-01..04：outputSchema 一致性、错误响应形状、save_path 语义、文本/图片字节。

用法：
  python tools/audit/bench_contract.py --exe artifacts\\server\\VScreenPilot.exe --out tools\\audit\\results\\run2
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import McpClient, default_exe, run_dir, write_json, case, print_summary  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
UITARGET = os.path.join(HERE, "targets", "bin", "UiTarget", "UiTarget.exe")

TYPE_MAP = {
    "object": dict, "array": list, "string": str, "boolean": bool,
    "number": (int, float), "integer": int, "null": type(None),
}


def validate(schema: dict, value, path: str, errors: list[str]) -> None:
    if not isinstance(schema, dict):
        return
    expected = schema.get("type")
    if expected:
        types = expected if isinstance(expected, list) else [expected]
        if not any(isinstance(value, TYPE_MAP.get(t, object)) and not (t in ("number", "integer") and isinstance(value, bool)) for t in types):
            errors.append(f"{path}: 期望 {expected}，实际 {type(value).__name__}")
            return
    if "enum" in schema and value not in schema["enum"]:
        errors.append(f"{path}: {value!r} 不在 enum")
    if isinstance(value, dict):
        for key in schema.get("required") or []:
            if key not in value:
                errors.append(f"{path}: 缺少必填 {key}")
        props = schema.get("properties") or {}
        for key, child in props.items():
            if key in value:
                validate(child, value[key], f"{path}.{key}", errors)
        if schema.get("additionalProperties") is False:
            extra = [k for k in value if k not in props]
            if extra:
                errors.append(f"{path}: 多余字段 {extra[:5]}")
    if isinstance(value, list) and "items" in schema:
        for index, item in enumerate(value[:20]):
            validate(schema["items"], item, f"{path}[{index}]", errors)


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", default=default_exe())
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    out_dir = args.out or run_dir("contract")
    contract_dir = os.path.join(out_dir, "contract")
    os.makedirs(contract_dir, exist_ok=True)
    cases: list[dict] = []

    client = McpClient(args.exe).start()
    tools = client.load_tools().get("tools") or []
    env = client.call("get_environment", {"ensure": True}, 120)
    display = (env.data or {}).get("virtual_display", {})
    origin = {"x": display.get("x", 1920), "y": display.get("y", 0)}
    client.call("launch_or_place_app", {"path": UITARGET, "wait_ms": 15000}, 120)

    calls = [
        ("get_environment", {}),
        ("get_log", {"tail": 5}),
        ("capture_screen", {}),
        ("find_element", {"automation_id": "input"}),
        ("get_ui_tree", {"max_depth": 4}),
        ("snapshot", {"max_depth": 3, "max_nodes": 200}),
        ("launch_or_place_app", {"window": "VSP UiTarget", "wait_ms": 5000}),
        ("click", {"x": origin["x"] + 200, "y": origin["y"] + 200, "mode": "message"}),
        ("type_text", {"text": "contract", "verify": False}),
        ("press_keys", {"keys": "End"}),
        ("set_element_value", {"ref": (client.call("find_element", {"automation_id": "input"}, 30).data or {}).get("ref"), "value": "x", "allow_destructive": True}),
        ("get_element_text", {"ref": (client.call("find_element", {"automation_id": "input"}, 30).data or {}).get("ref")}),
        ("invoke_element", {"ref": (client.call("find_element", {"automation_id": "opt"}, 30).data or {}).get("ref"), "pattern": "toggle"}),
        ("wait_for", {"condition": {"type": "color", "x": origin["x"] + 5, "y": origin["y"] + 5, "value": "#000000", "tolerance": 255}, "timeout_ms": 1000}),
        ("assert_region", {"region": {"x": origin["x"] + 10, "y": origin["y"] + 10, "width": 50, "height": 50}, "expectation": {"changed": False}}),
        ("match_visual", {"mode": "pixel", "target": {"x": origin["x"] + 5, "y": origin["y"] + 5}}),
        ("gesture", {"type": "move", "to": {"x": origin["x"] + 100, "y": origin["y"] + 100}, "mode": "message"}),
        ("run_script", {"code": "Write-Output 1"}),
    ]

    violations: list[dict] = []
    schema_map = {tool["name"]: tool.get("outputSchema") or {} for tool in tools}
    for name, payload in calls:
        result = client.call(name, payload, 120)
        structured = None
        if isinstance(result.raw, dict):
            structured = result.raw.get("structuredContent")
        if structured is None:
            violations.append({"tool": name, "issue": "缺少 structuredContent"})
            continue
        errors: list[str] = []
        validate(schema_map.get(name, {}), structured, name, errors)
        if errors:
            violations.append({"tool": name, "issues": errors[:8]})

    cases.append(case("CONT-01", "18 个有返回工具（含 snapshot）outputSchema 与 structuredContent 一致（release 见 CONT-05）",
                      not violations, violations=violations[:10],
                      checked=len(calls)))

    # 错误响应形状
    errors: list[dict] = []
    for name, payload in (("find_element", {"name": "__nope__"}),
                          ("capture_screen", {"region": {"x": 99999, "y": 0, "width": 10, "height": 10}}),
                          ("type_text", {"text": "a" * 100001}),
                          ("run_script", {"code": "1"})):
        result = client.call(name, payload, 60)
        text = result.text.strip()
        try:
            data = json.loads(text)
        except Exception:
            data = {}
        if result.ok or not isinstance(data, dict) or not data.get("code") or not data.get("message"):
            errors.append({"tool": name, "ok": result.ok, "text": text[:120]})
    cases.append(case("CONT-02", "错误响应包含 isError + code + message",
                      not errors, violations=errors))

    # save_path：相对路径不再按服务端 cwd 猜测，而是明确拒绝（E_INVALID_ARG）。
    rel_name = f"rel-audit-{int(time.time())}.png"
    server_root = os.path.dirname(os.path.abspath(args.exe))
    rel = client.call("capture_screen", {"save_path": rel_name}, 60)
    server_file = os.path.join(server_root, rel_name)
    client_file = os.path.join(os.getcwd(), rel_name)
    cases.append(case("CONT-03", "相对 save_path 被明确拒绝，不产生歧义落盘",
                      rel.code == "E_INVALID_ARG"
                      and not os.path.exists(server_file) and not os.path.exists(client_file),
                      code=rel.code,
                      server_file=server_file, server_exists=os.path.exists(server_file),
                      client_cwd=os.getcwd(), client_file_exists=os.path.exists(client_file)))

    # 文本/图片字节
    shot = client.call("capture_screen", {}, 60)
    text_bytes = env.text_bytes
    cases.append(case("CONT-04", "返回内容包含文本与图片（字节可统计）",
                      bool(shot.images) and shot.text_bytes > 0,
                      text_bytes=text_bytes, image_bytes=shot.image_bytes,
                      png_bytes=(shot.data or {}).get("png_bytes")))

    # CONT-06 get_ui_tree 默认不推进代际：find_element 的 ref 仍然可用。
    found = client.call("find_element", {"automation_id": "input"}, 60)
    found_ref = (found.data or {}).get("ref")
    tree = client.call("get_ui_tree", {"max_depth": 3}, 60)
    after_tree = client.call("get_element_text", {"ref": found_ref}, 60) if found_ref else None
    cases.append(case(
        "CONT-06",
        "get_ui_tree 默认复用当前快照，不使 find_element 的 ref 失效",
        bool(found_ref) and bool(tree.ok) and after_tree is not None and after_tree.ok,
        snapshot_before=(found.data or {}).get("snapshot_id"),
        snapshot_after=(tree.data or {}).get("snapshot_id"),
        code=after_tree.code if after_tree else None))

    # CONT-07 嵌套参数类型错误必须显式失败，而不是回退默认值。
    nested = client.call(
        "assert_region",
        {"region": {"x": origin["x"], "y": origin["y"], "width": 10, "height": 10},
         "expectation": {"color": "#000000", "coverage": "x"}},
        30)
    nested2 = client.call(
        "wait_for",
        {"condition": {"type": "color", "x": origin["x"], "y": origin["y"],
                       "value": "#000000", "tolerance": 999}, "timeout_ms": 100},
        30)
    cases.append(case(
        "CONT-07",
        "嵌套参数类型/范围错误返回 E_INVALID_ARG",
        nested.code == "E_INVALID_ARG" and nested2.code == "E_INVALID_ARG",
        coverage=nested.code, tolerance=nested2.code))

    # CONT-08 显式 save_path + inline=false：只写一个文件，不产生 artifact。
    explicit = os.path.join(tempfile.gettempdir(), f"vsp-contract-{int(time.time())}.png")
    try:
        os.remove(explicit)
    except FileNotFoundError:
        pass
    saved = client.call("capture_screen", {"save_path": explicit, "inline": False}, 60)
    saved_data = saved.data or {}
    cases.append(case(
        "CONT-08",
        "save_path + inline=false 只落指定文件",
        saved.ok and saved_data.get("path") == explicit
        and not saved_data.get("artifact_path")
        and os.path.exists(explicit),
        path=saved_data.get("path"), artifact=saved_data.get("artifact_path")))

    # CONT-09 资源列表带 size，preview 可读。
    artifact = client.call("capture_screen", {"inline": False}, 60)
    artifact_uri = (artifact.data or {}).get("artifact_uri")
    listing = client.request("resources/list", {}, 30)
    resources = (listing.get("result") or {}).get("resources") or []
    preview_read = client.request(
        "resources/read", {"uri": f"{artifact_uri}?preview=1"}, 30) if artifact_uri else {}
    blob = ((preview_read.get("result") or {}).get("contents") or [{}])[0]
    cases.append(case(
        "CONT-09",
        "resources/list 带 size，artifacts?preview=1 可读",
        bool(artifact_uri) and bool(resources) and all("size" in item for item in resources)
        and bool(blob.get("blob")),
        count=len(resources), uri=artifact_uri))

    # CONT-10 max_nodes 上限与 schema 一致。
    too_many = client.call("snapshot", {"max_nodes": 5000}, 30)
    cases.append(case("CONT-10", "snapshot max_nodes 超过 1024 返回 E_INVALID_ARG",
                      too_many.code == "E_INVALID_ARG", code=too_many.code))

    # CONT-05 release_environment 的 structuredContent
    release = client.call("release_environment", {"keep_display": False, "on_dirty": "discard", "allow_data_loss": True}, 120)
    structured = (release.raw or {}).get("structuredContent")
    release_errors: list[str] = []
    if structured is None:
        release_errors.append("缺少 structuredContent")
    else:
        validate(schema_map.get("release_environment", {}), structured, "release_environment", release_errors)
    cases.append(case("CONT-05", "release_environment 的 outputSchema 一致",
                      release.ok and not release_errors, violations=release_errors[:6]))
    client.stop()

    write_json(os.path.join(contract_dir, "summary.json"), {"cases": cases})
    print_summary(cases)
    return 0


if __name__ == "__main__":
    sys.exit(main())
