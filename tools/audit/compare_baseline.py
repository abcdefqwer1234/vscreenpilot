"""对比两份基线目录，输出逐字段差异。纯标准库。

规则：
  * 逐 JSON 文件对比（相对路径一致）；
  * 易变字段（request_id/耗时/PID 等）按 key 名掩码；
  * 字符串中的绝对路径、日期、时间戳统一替换为占位符后再比较；
  * 只在一侧存在的文件也算差异。

用法：
  python tools/audit/compare_baseline.py tools/audit/baseline/3ddb85b tools/audit/results/current
  python tools/audit/compare_baseline.py A B --report tools/audit/results/diff.json --max-diffs 200
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import os
import re
import sys
from typing import Any

# 这些 key 的值天然每次不同：按 key 掩码，不参与比较。
MASK_KEYS = {
    "request_id",
    "snapshot_id",
    "frame_id",
    "client_id",
    "pid",
    "proxy_pid",
    "elapsed_ms",
    "duration_ms",
    "encode_ms",
    "decode_ms",
    "cleanup_ms",
    "startup_ms",
    "ms",
    "t",
    "rss_mb",
    "peak_rss_mb",
    "handles",
    "threads",
    "thread_count",
    "started_at",
    "captured_at",
    "generated_at",
    "timestamp",
    "uptime_ms",
    "working_set_mb",
    "binary_sha256",
    "exe_sha256",
    "exe",
    "commit",
    # doctor 的运行时快照（同机复跑也会变）：按 key 掩码，结构差异仍会被发现。
    "log_file",
    "registry",
    "instances",
    "process",
    "state",
    "started",
    "duration_s",
}

# 这些文件只是溯源信息（tag/note/时间/二进制哈希），本就不该逐字节比较。
DEFAULT_SKIP = ["manifest.json"]

# 字符串中需要归一化的片段。
PATH_PATTERNS = [
    (re.compile(r"[A-Za-z]:\\\\[^\"'\s]*"), "<ABS_PATH>"),
    (re.compile(r"[A-Za-z]:\\[^\"'\s]*"), "<ABS_PATH>"),
]
VALUE_PATTERNS = [
    (re.compile(r"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{4}"), "<TIMESTAMP>"),
    (re.compile(r"\b\d{4}-\d{2}-\d{2}\b"), "<DATE>"),
    (re.compile(r"\b\d{2}:\d{2}:\d{2}(?:\.\d+)?\b"), "<TIME>"),
    (re.compile(r"\b\d{8}-\d{6}-[0-9a-fA-F]{8}\b"), "<STAMP>"),
    (re.compile(r"\b\d{8}-\d{6}\b"), "<STAMP>"),
    (re.compile(r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b"), "<GUID>"),
]


def normalize_string(text: str, roots: list[str]) -> str:
    for root in roots:
        if root:
            text = text.replace(root, "<ROOT>")
    for pattern, replacement in PATH_PATTERNS:
        text = pattern.sub(replacement, text)
    for pattern, replacement in VALUE_PATTERNS:
        text = pattern.sub(replacement, text)
    return text


def collect_files(root: str, ignores: list[str]) -> dict[str, str]:
    found: dict[str, str] = {}
    for directory, _dirs, files in os.walk(root):
        for name in files:
            if not name.endswith(".json"):
                continue
            absolute = os.path.join(directory, name)
            relative = os.path.relpath(absolute, root).replace("\\", "/")
            if any(fnmatch.fnmatch(relative, pattern) for pattern in ignores):
                continue
            found[relative] = absolute
    return found


def diff_value(base: Any, cur: Any, path: str, out: list[dict], max_diffs: int, roots: list[str]) -> None:
    if len(out) >= max_diffs:
        return

    # CLI 的 stdout/stderr 是"JSON 装成字符串"：先解包再按结构比较，否则内部的易变字段无法掩码。
    if isinstance(base, str) and isinstance(cur, str):
        base_text, cur_text = base.strip(), cur.strip()
        if base_text[:1] in "{[" and cur_text[:1] in "{[":
            try:
                diff_value(json.loads(base_text), json.loads(cur_text), path, out, max_diffs, roots)
                return
            except json.JSONDecodeError:
                pass

    if isinstance(base, dict) and isinstance(cur, dict):
        for key in sorted(set(base) | set(cur)):
            if len(out) >= max_diffs:
                return
            child = f"{path}.{key}" if path else key
            if key.lower() in MASK_KEYS:
                continue
            if key not in base:
                out.append({"path": child, "kind": "added", "baseline": None, "current": summarize(cur[key])})
            elif key not in cur:
                out.append({"path": child, "kind": "removed", "baseline": summarize(base[key]), "current": None})
            else:
                diff_value(base[key], cur[key], child, out, max_diffs, roots)
        return

    if isinstance(base, list) and isinstance(cur, list):
        if len(base) != len(cur):
            out.append({"path": f"{path}.length", "kind": "changed", "baseline": len(base), "current": len(cur)})
        for index in range(min(len(base), len(cur))):
            if len(out) >= max_diffs:
                return
            diff_value(base[index], cur[index], f"{path}[{index}]", out, max_diffs, roots)
        return

    if isinstance(base, str) and isinstance(cur, str):
        base, cur = normalize_string(base, roots), normalize_string(cur, roots)

    if base != cur:
        out.append({"path": path, "kind": "changed", "baseline": summarize(base), "current": summarize(cur)})


def summarize(value: Any, limit: int = 400) -> Any:
    if isinstance(value, (dict, list)):
        text = json.dumps(value, ensure_ascii=False)
        return text if len(text) <= limit else text[:limit] + "..."
    if isinstance(value, str) and len(value) > limit:
        return value[:limit] + "..."
    return value


def case_signature(payload: Any) -> dict[str, bool] | None:
    """从审计结果里抽出"用例 id → 是否通过"，用于跨轮次稳定对比。

    审计脚本的 summary 把耗时、字节数、临时路径都写进了详情，
    这些数值不应进入契约基线；真正要守的是"哪些用例通过"。
    """
    found: dict[str, bool] = {}

    def walk(node: Any) -> None:
        if isinstance(node, dict):
            if "id" in node and isinstance(node.get("ok"), bool):
                found[str(node["id"])] = bool(node["ok"])
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(payload)
    return found or None


def diff_cases(base: Any, cur: Any, relative: str, out: list[dict], max_diffs: int) -> bool:
    base_cases = case_signature(base)
    cur_cases = case_signature(cur)
    if base_cases is None or cur_cases is None:
        return False
    for case_id in sorted(set(base_cases) | set(cur_cases)):
        if len(out) >= max_diffs:
            break
        if case_id not in cur_cases:
            out.append({"file": relative, "path": case_id, "kind": "case_removed",
                        "baseline": base_cases[case_id], "current": None})
        elif case_id not in base_cases:
            # 新增用例是覆盖面变好，不算回归。
            continue
        elif base_cases[case_id] and not cur_cases[case_id]:
            # 只把“通过 → 失败”当回归；失败 → 通过是改进，不阻塞。
            out.append({"file": relative, "path": case_id, "kind": "case_regressed",
                        "baseline": True, "current": False})
    return True


def load_json(path: str) -> Any:
    # PowerShell 的 Set-Content -Encoding UTF8 在 5.1 下会带 BOM。
    with open(path, "r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("baseline")
    parser.add_argument("current")
    parser.add_argument("--report", default="")
    parser.add_argument("--max-diffs", type=int, default=200)
    parser.add_argument("--ignore", action="append", default=[],
                        help="相对路径 glob，例如 'perf/*'")
    parser.add_argument("--only", action="append", default=[],
                        help="只比较匹配的相对路径（可多次）；跨机器对比建议 --only server.json --only errors.json")
    parser.add_argument("--skip", action="append", default=[],
                        help="跳过匹配的相对路径（可多次），优先级高于 --only")
    parser.add_argument("--quiet", action="store_true")
    args = parser.parse_args()

    if not os.path.isdir(args.baseline) or not os.path.isdir(args.current):
        print("基线或当前目录不存在", file=sys.stderr)
        return 2

    roots = [os.path.abspath(args.baseline), os.path.abspath(args.current), os.path.abspath(os.getcwd())]
    base_files = collect_files(args.baseline, args.ignore + DEFAULT_SKIP)
    cur_files = collect_files(args.current, args.ignore + DEFAULT_SKIP)

    def keep(relative: str) -> bool:
        if any(fnmatch.fnmatch(relative, pattern) for pattern in args.skip):
            return False
        if args.only and not any(fnmatch.fnmatch(relative, pattern) for pattern in args.only):
            return False
        return True

    base_files = {name: path for name, path in base_files.items() if keep(name)}
    cur_files = {name: path for name, path in cur_files.items() if keep(name)}

    diffs: list[dict] = []
    checked = 0
    for relative in sorted(set(base_files) | set(cur_files)):
        if len(diffs) >= args.max_diffs:
            break
        if relative not in base_files:
            diffs.append({"file": relative, "path": "", "kind": "file_added", "baseline": None, "current": "<new file>"})
            continue
        if relative not in cur_files:
            diffs.append({"file": relative, "path": "", "kind": "file_removed", "baseline": "<missing>", "current": None})
            continue
        try:
            base = load_json(base_files[relative])
            cur = load_json(cur_files[relative])
        except json.JSONDecodeError as error:
            diffs.append({"file": relative, "path": "", "kind": "invalid_json", "baseline": str(error), "current": None})
            continue
        checked += 1
        # 子目录里是审计运行记录：只对比"用例通过集"，不对比耗时/字节/临时路径。
        # 没有用例集的文件（原始指标）直接忽略，避免把噪声当契约。
        if "/" in relative:
            diff_cases(base, cur, relative, diffs, args.max_diffs)
            continue
        local: list[dict] = []
        diff_value(base, cur, "", local, args.max_diffs - len(diffs), roots)
        for item in local:
            item["file"] = relative
        diffs.extend(local)

    report = {
        "baseline": os.path.abspath(args.baseline),
        "current": os.path.abspath(args.current),
        "checked_files": checked,
        "diff_count": len(diffs),
        "diffs": diffs,
    }

    if args.report:
        os.makedirs(os.path.dirname(os.path.abspath(args.report)), exist_ok=True)
        with open(args.report, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(report, handle, ensure_ascii=False, indent=2)
            handle.write("\n")

    if not args.quiet:
        print(f"对比 {checked} 个 JSON 文件，差异 {len(diffs)} 处")
        for item in diffs[:40]:
            print(f"[{item['kind']}] {item.get('file')} :: {item['path']}")
            print(f"    基线={item['baseline']!r}")
            print(f"    当前={item['current']!r}")
        if len(diffs) > 40:
            print(f"... 其余 {len(diffs) - 40} 处见 --report")

    return 0 if not diffs else 1


if __name__ == "__main__":
    sys.exit(main())
