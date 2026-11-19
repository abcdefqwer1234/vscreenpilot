"""抓取重构基线：协议面（initialize/tools/resources）、错误包、CLI 面。

不挂虚拟屏（VSP_NO_WARMUP=1），因此可在无 VDD、无 Office 的机器上复现。
纯标准库，输出为可直接入库的 JSON。

用法：
  python tools/audit/capture_baseline.py --exe artifacts/server/VScreenPilot.exe --out tools/audit/baseline/3ddb85b
  python tools/audit/capture_baseline.py --exe ... --out ... --tag refactor-p0 --note "现成 0.2.2 二进制"
"""

from __future__ import annotations

import argparse
import hashlib
import json
import locale
import os
import platform
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vsp_client import McpClient, repo_root  # noqa: E402

INIT_PARAMS = {
    "protocolVersion": "2025-06-18",
    "capabilities": {},
    "clientInfo": {"name": "vsp-audit-py", "version": "1.0"},
}

# 错误包基线使用的稳定触发点：不依赖显示、不依赖 Office。
ERROR_CASES = [
    ("unknown_tool", {"name": "no_such_tool", "arguments": {}}),
    ("invalid_control_type", {"name": "find_element", "arguments": {"control_type": "NotAControlType"}}),
    ("unknown_argument", {"name": "capture_screen", "arguments": {"timeout": 1}}),
    ("script_disabled", {"name": "run_script", "arguments": {"code": "Write-Output hi"}}),
]


def sha256_of(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def decode_output(raw: bytes) -> str:
    """CLI 输出在被重定向时按控制台代码页编码（中文 Windows 为 936）。"""
    for encoding in ("utf-8", locale.getpreferredencoding(False), "gbk"):
        try:
            return raw.decode(encoding)
        except (UnicodeDecodeError, LookupError):
            continue
    return raw.decode("utf-8", errors="replace")


def normalize_newlines(text: str) -> str:
    return text.replace("\r\n", "\n").replace("\r", "\n")


def run_cli(exe: str, args: list[str], env: dict[str, str], cwd: str, timeout: float = 180.0) -> dict:
    proc = subprocess.run(
        [exe, *args], capture_output=True, cwd=cwd, env=env, timeout=timeout,
        creationflags=0x08000000,  # CREATE_NO_WINDOW
    )
    return {
        "args": args,
        "exit_code": proc.returncode,
        "stdout": normalize_newlines(decode_output(proc.stdout)),
        "stderr": normalize_newlines(decode_output(proc.stderr)),
    }


def git_commit(cwd: str) -> str | None:
    try:
        proc = subprocess.run(
            ["git", "rev-parse", "HEAD"], capture_output=True, cwd=cwd, timeout=30, text=True,
        )
        return proc.stdout.strip() if proc.returncode == 0 else None
    except (OSError, subprocess.SubprocessError):
        return None


def write_json(path: str, payload) -> None:
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=False)
        handle.write("\n")


def capture_protocol(exe: str, profile: str) -> tuple[dict, dict, dict]:
    env = {"VSP_NO_WARMUP": "1", "VSP_PROFILE": profile}
    client = McpClient(exe, env=env).start(init=False)
    try:
        initialize = client.request("initialize", INIT_PARAMS, 60)
        client.notify("notifications/initialized")
        listed = client.request("tools/list", {}, 60)
        resources = client.request("resources/list", {}, 60)
    finally:
        client.stop()

    server = {
        "protocol_version": (initialize.get("result") or {}).get("protocolVersion"),
        "server_info": (initialize.get("result") or {}).get("serverInfo"),
        "capabilities": (initialize.get("result") or {}).get("capabilities"),
        "instructions": (initialize.get("result") or {}).get("instructions"),
        "tools": (listed.get("result") or {}).get("tools"),
        "tool_names_sorted": sorted(t["name"] for t in ((listed.get("result") or {}).get("tools") or [])),
        "resources": (resources.get("result") or {}).get("resources"),
        "next_cursor": (resources.get("result") or {}).get("nextCursor"),
    }
    return server, listed, resources


def capture_errors(exe: str, profile: str) -> dict:
    env = {"VSP_NO_WARMUP": "1", "VSP_PROFILE": profile}
    client = McpClient(exe, env=env).start()
    cases = {}
    try:
        for case_id, params in ERROR_CASES:
            response = client.request("tools/call", params, 60)
            result = response.get("result") or {}
            payload_text = ""
            for item in result.get("content") or []:
                if item.get("type") == "text":
                    payload_text = item.get("text") or ""
                    break
            try:
                payload = json.loads(payload_text) if payload_text else None
            except json.JSONDecodeError:
                payload = payload_text
            cases[case_id] = {
                "is_error": bool(result.get("isError")),
                "payload": payload,
                "structured_content": result.get("structuredContent"),
            }
    finally:
        client.stop()
    return cases


def capture_cli(exe: str, profile: str) -> dict:
    cwd = repo_root()
    env = {**os.environ, "VSP_PROFILE": profile, "VSP_NO_WARMUP": "1"}
    # status 使用不存在的 profile，确保命中 E_NO_INSTANCE 而不是接上真实会话。
    return {
        "version": run_cli(exe, ["--version"], env, cwd),
        "help": run_cli(exe, ["--help"], env, cwd),
        "doctor": run_cli(exe, ["--doctor"], env, cwd),
        "status_no_instance": run_cli(exe, ["--cli", "status"], env, cwd),
        "stop_no_instance": run_cli(exe, ["--cli", "stop"], env, cwd),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--profile", default="baseline-probe",
                        help="抓取用 profile；必须没有活跃实例")
    parser.add_argument("--tag", default="baseline")
    parser.add_argument("--note", default="")
    args = parser.parse_args()

    exe = os.path.abspath(args.exe)
    if not os.path.isfile(exe):
        print(f"找不到二进制：{exe}", file=sys.stderr)
        return 2

    os.makedirs(args.out, exist_ok=True)
    started = time.strftime("%Y-%m-%dT%H:%M:%S%z")

    server, listed, resources = capture_protocol(exe, args.profile)
    write_json(os.path.join(args.out, "server.json"), server)

    errors = capture_errors(exe, args.profile)
    write_json(os.path.join(args.out, "errors.json"), errors)

    cli = capture_cli(exe, args.profile)
    write_json(os.path.join(args.out, "cli.json"), cli)

    manifest = {
        "tag": args.tag,
        "note": args.note,
        "commit": git_commit(repo_root()),
        "exe": exe,
        "exe_sha256": sha256_of(exe),
        "exe_bytes": os.path.getsize(exe),
        "captured_at": started,
        "os": f"{platform.system()} {platform.release()} {platform.version()}",
        "python": platform.python_version(),
        "profile": args.profile,
        "tool_count": len(server.get("tools") or []),
        "protocol_version": server.get("protocol_version"),
        "server_info": server.get("server_info"),
    }
    write_json(os.path.join(args.out, "manifest.json"), manifest)

    print(f"tool_count={manifest['tool_count']} protocol={manifest['protocol_version']}")
    print(f"exe_sha256={manifest['exe_sha256']}")
    print(f"out={os.path.abspath(args.out)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
