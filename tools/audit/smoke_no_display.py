"""无显示冒烟：CI 用。

不挂虚拟屏（VSP_NO_WARMUP=1），只验证协议、工具目录、schema、错误形状与资源列表。
用法：
  python tools/audit/smoke_no_display.py --exe artifacts/server/VScreenPilot.exe
"""

from __future__ import annotations

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import vsp_client  # noqa: E402
from vsp_client import McpClient, write_json  # noqa: E402

EXPECTED_TOOLS = {
    "assert_region", "capture_screen", "click", "click_ref", "click_text",
    "close_tab", "find_element", "gesture", "get_element_text", "get_environment",
    "get_log", "get_processes", "get_ui_tree", "invoke_element", "launch_or_place_app",
    "lease_input", "list_windows", "match_visual", "press_keys", "read_screen_text",
    "read_table", "release_environment", "run_actions", "run_script", "select_tab",
    "set_display", "set_element_value", "snapshot", "type_text", "wait_for",
}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--out", default=os.path.join("tools", "audit", "results", "smoke"))
    args = parser.parse_args()

    os.makedirs(args.out, exist_ok=True)
    failures: list[str] = []
    client = McpClient(args.exe, env={"VSP_NO_WARMUP": "1"}).start()

    try:
        tools = client.load_tools().get("tools") or []
        names = {tool["name"] for tool in tools}

        def check(name: str, ok: bool, detail=None) -> None:
            print(("PASS" if ok else "FAIL"), name, "" if detail is None else detail)
            if not ok:
                failures.append(name)

        check("tools==30", len(tools) == 30, len(tools))
        check("tool_set_matches", names == EXPECTED_TOOLS,
              sorted(names ^ EXPECTED_TOOLS))
        check("has_snapshot", "snapshot" in names)
        check("has_click_ref", "click_ref" in names)

        vacuous = [tool["name"] for tool in tools
                   if not (tool.get("outputSchema") or {}).get("properties")]
        check("output_schema_non_vacuous", not vacuous, vacuous)

        no_annotations = [tool["name"] for tool in tools
                          if not tool.get("annotations")]
        check("annotations_present", not no_annotations, no_annotations)

        snapshot = next(tool for tool in tools if tool["name"] == "snapshot")
        tree_schema = ((snapshot.get("outputSchema") or {}).get("properties") or {}).get("tree") or {}
        tree_type = tree_schema.get("type")
        check("snapshot_tree_object", tree_type == "object" or "object" in (tree_type or []), tree_type)

        unknown = client.call("find_element", {"control_type": "NotAControlType"}, 30)
        check("invalid_control_type", unknown.code == "E_INVALID_ARG", unknown.code)

        unknown_arg = client.call("capture_screen", {"timeout": 1}, 30)
        check("unknown_argument", unknown_arg.code == "E_INVALID_ARG", unknown_arg.code)

        docs = (unknown_arg.data or {}).get("docs") if isinstance(unknown_arg.data, dict) else None
        check("error_docs_url", isinstance(docs, str) and docs.startswith("https://"), docs)

        script = client.call("run_script", {"code": "Write-Output hi"}, 30)
        check("script_disabled", script.code == "E_DISABLED", script.code)

        missing = client.call("no_such_tool", {}, 30)
        check("unknown_tool_structured", missing.code == "E_UNKNOWN_TOOL", missing.code)

        resources = client.request("resources/list", {}, 30)
        result = resources.get("result") or {}
        check("resources_list", isinstance(result.get("resources"), list), resources.get("error"))
        cursor = result.get("nextCursor")
        check("resources_cursor_shape", cursor is None or isinstance(cursor, str), cursor)

        env = client.call("get_environment", {}, 30)
        check("environment_read", env.ok, env.code)
    finally:
        client.stop()

    # write_json 会补 binary_sha256。
    summary = {
        "failures": failures,
        "tools": len(tools) if "tools" in locals() else 0,
    }
    write_json(os.path.join(args.out, "summary.json"), summary)
    print("failures:", failures)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
