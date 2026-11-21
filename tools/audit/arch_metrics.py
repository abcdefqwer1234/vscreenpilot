#!/usr/bin/env python3
"""架构度量与棘轮校验。

只依赖标准库。解析口径：
- 行数：read_text().splitlines()，含空行，排除 obj/bin；
- 方法：排除注释与字符串，按花括号深度 1 的成员签名计算到匹配闭括号；
- 无类型 catch：`catch {` / `catch when (...) {`，其中不含日志调用记为 silent。

用法：
  python tools/audit/arch_metrics.py --write    # 重写 arch_baseline.json
  python tools/audit/arch_metrics.py --check    # CI：只允许变好
  python tools/audit/arch_metrics.py --selftest # 解析器 golden 断言
  python tools/audit/arch_metrics.py --json     # 打印当前度量
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from datetime import datetime, timezone

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SRC = os.path.join(REPO, "src")
TESTS = os.path.join(REPO, "tests")
BASELINE = os.path.join(REPO, "tools", "audit", "arch_baseline.json")

TYPE_KEYWORDS = (
    "class", "record", "struct", "interface", "enum", "delegate",
    "namespace", "using", "event", "operator",
)
CONTROL = {
    "if", "for", "foreach", "while", "switch", "catch", "using", "lock",
    "return", "new", "throw", "base", "this", "when", "nameof", "typeof",
    "sizeof", "checked", "unchecked",
}
LOG_TOKENS = ("log.", "_log", "Log(", "Log.", "Trace.", "Debug.", "Console.", "Warn(", "Error(")


def source_files(root: str):
    for dirpath, dirnames, filenames in os.walk(root):
        parts = dirpath.split(os.sep)
        if "obj" in parts or "bin" in parts:
            continue
        for name in filenames:
            if name.endswith(".cs"):
                yield os.path.join(dirpath, name)


def strip_code(text: str) -> str:
    """去掉注释、字符串与字符字面量，保留换行以维持行号。"""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                if text[i] == "\n":
                    out.append("\n")
                i += 1
            i += 2
            continue
        if c == "@" and i + 1 < n and text[i + 1] == '"':
            i += 2
            while i < n:
                if text[i] == '"' and i + 1 < n and text[i + 1] == '"':
                    i += 2
                    continue
                if text[i] == '"':
                    i += 1
                    break
                if text[i] == "\n":
                    out.append("\n")
                i += 1
            continue
        if c == '"':
            i += 1
            while i < n:
                if text[i] == "\\":
                    i += 2
                    continue
                if text[i] == '"':
                    i += 1
                    break
                if text[i] == "\n":
                    out.append("\n")
                i += 1
            continue
        if c == "'":
            i += 1
            while i < n:
                if text[i] == "\\":
                    i += 2
                    continue
                if text[i] == "'":
                    i += 1
                    break
                i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def parse_methods(text: str):
    """返回 [(length, start_line, name)]；只含类型成员（花括号深度 1）。"""
    lines = strip_code(text).splitlines()
    depths, depth = [], 0
    for line in lines:
        depths.append(depth)
        depth += line.count("{") - line.count("}")
    rows = []
    i = 0
    while i < len(lines):
        line = lines[i]
        if depths[i] != 1 or not re.match(r"^\s*(public|private|internal|protected)\b", line):
            i += 1
            continue
        joined, j = line, i
        while j + 1 < len(lines) and j - i < 15:
            if "(" in joined or "{" in joined or ";" in joined or "=>" in joined:
                break
            j += 1
            joined += " " + lines[j].strip()
        header = joined.split("(")[0]
        if any(re.search(r"\b" + kw + r"\b", header) for kw in TYPE_KEYWORDS):
            i += 1
            continue
        pp = joined.find("(")
        pb = joined.find("{")
        ps = joined.find(";")
        if pp < 0 or (pb >= 0 and pb < pp) or (ps >= 0 and ps < pp):
            i += 1
            continue
        match = re.search(r"([A-Za-z_]\w*)\s*\($", joined[: pp + 1])
        if not match or match.group(1) in CONTROL:
            i += 1
            continue
        if "=>" in joined[:pb if pb >= 0 else len(joined)]:
            i += 1
            continue
        k = i
        while k < len(lines) and "{" not in lines[k]:
            if ";" in lines[k]:
                break
            k += 1
        if k >= len(lines) or "{" not in lines[k]:
            i += 1
            continue
        end, level = k, 0
        while end < len(lines):
            level += lines[end].count("{") - lines[end].count("}")
            if level <= 0:
                break
            end += 1
        rows.append((end - i + 1, i + 1, match.group(1)))
        i = end + 1
    return rows


def assembly_of(path: str) -> str:
    rel = os.path.relpath(path, SRC)
    return rel.split(os.sep)[0]


def read_namespace(text: str) -> str | None:
    match = re.search(r"^\s*namespace\s+([A-Za-z0-9_.]+)\s*[;{]", text, re.MULTILINE)
    return match.group(1) if match else None


def count_ivt():
    rows = []
    for dirpath, dirnames, filenames in os.walk(SRC):
        for name in filenames:
            if not name.endswith(".csproj"):
                continue
            path = os.path.join(dirpath, name)
            text = open(path, encoding="utf-8").read()
            source = re.search(r"<AssemblyName>([^<]+)</AssemblyName>", text)
            assembly = source.group(1) if source else os.path.splitext(name)[0]
            for target in re.findall(r'<InternalsVisibleTo\s+Include="([^"]+)"', text):
                rows.append({"from": assembly, "to": target})
    return sorted(rows, key=lambda row: (row["from"], row["to"]))


def count_hosts(files):
    hosts = []
    for path in files:
        text = strip_code(open(path, encoding="utf-8").read())
        for match in re.finditer(r"interface\s+(I\w*Host)\b", text):
            name = match.group(1)
            brace = text.find("{", match.end())
            if brace < 0:
                continue
            depth, index = 0, brace
            while index < len(text):
                if text[index] == "{":
                    depth += 1
                elif text[index] == "}":
                    depth -= 1
                    if depth == 0:
                        break
                index += 1
            body = text[brace + 1:index]
            members = 0
            for line in body.splitlines():
                stripped = line.strip()
                if not stripped or stripped.startswith("//"):
                    continue
                if re.match(r"^[A-Za-z_][\w<>,?\[\]. ]*\s+[A-Za-z_]\w*\s*(\(|\{)", stripped):
                    members += 1
            hosts.append({"name": name, "members": members})
    return hosts


def count_catches(files):
    typed_wide = typed_narrow = untyped = silent = debug = 0
    for path in files:
        text = strip_code(open(path, encoding="utf-8").read())
        debug += text.count("Debug.WriteLine")
        for match in re.finditer(r"catch\s*(\()?", text):
            paren = match.group(1)
            if paren:
                close = text.find(")", match.end())
                clause = text[match.end():close] if close > 0 else ""
                if re.match(r"\s*Exception\b", clause):
                    typed_wide += 1
                else:
                    typed_narrow += 1
                continue
            untyped += 1
            brace = text.find("{", match.end())
            if brace < 0:
                continue
            depth, index = 0, brace
            while index < len(text):
                if text[index] == "{":
                    depth += 1
                elif text[index] == "}":
                    depth -= 1
                    if depth == 0:
                        break
                index += 1
            body = text[brace + 1:index]
            if not any(token in body for token in LOG_TOKENS):
                silent += 1
    return {
        "typed_wide": typed_wide,
        "typed_narrow": typed_narrow,
        "untyped": untyped,
        "untyped_silent": silent,
        "debug_only": debug,
    }


def count_tests():
    fact = theory = inline = 0
    files = 0
    lines = 0
    for path in source_files(TESTS):
        text = open(path, encoding="utf-8").read()
        files += 1
        lines += len(text.splitlines())
        fact += len(re.findall(r"\[Fact\]", text))
        theory += len(re.findall(r"\[Theory\]", text))
        inline += len(re.findall(r"\[InlineData", text))
    return {"fact": fact, "theory": theory, "methods": fact + theory,
            "inline_data": inline, "files": files, "lines": lines}


def collect():
    files = sorted(source_files(SRC))
    file_rows = []
    method_rows = []
    namespace_map = {}
    for path in files:
        text = open(path, encoding="utf-8").read()
        length = len(text.splitlines())
        rel = os.path.relpath(path, REPO).replace(os.sep, "/")
        file_rows.append((length, rel))
        ns = read_namespace(text)
        if ns:
            namespace_map.setdefault(ns, set()).add(assembly_of(path))
        for length_m, line, name in parse_methods(text):
            method_rows.append((length_m, rel, line, name))

    split = sorted(ns for ns, asm in namespace_map.items() if len(asm) > 1)
    hosts = count_hosts(files)
    return {
        "generated_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "source": {
            "files": len(file_rows),
            "lines": sum(row[0] for row in file_rows),
            "max_file": max(file_rows)[0] if file_rows else 0,
            "files_gt_600": sum(1 for row in file_rows if row[0] > 600),
            "files_gt_800": sum(1 for row in file_rows if row[0] > 800),
            "top_files": [{"lines": n, "path": p} for n, p in sorted(file_rows, reverse=True)[:12]],
        },
        "methods": {
            "max": max((row[0] for row in method_rows), default=0),
            "gt_150": sum(1 for row in method_rows if row[0] > 150),
            "ge_200": sum(1 for row in method_rows if row[0] >= 200),
            "ge_120": sum(1 for row in method_rows if row[0] >= 120),
            "ge_80": sum(1 for row in method_rows if row[0] >= 80),
            "top": [{"lines": n, "path": p, "line": l, "name": name}
                    for n, p, l, name in sorted(method_rows, reverse=True)[:12]],
        },
        "hosts": {
            "count": len(hosts),
            "members": sum(host["members"] for host in hosts),
            "max_members": max((host["members"] for host in hosts), default=0),
            "items": sorted(hosts, key=lambda host: -host["members"]),
        },
        "namespaces": {
            "split": split,
            "map": {ns: sorted(asm) for ns, asm in sorted(namespace_map.items())},
        },
        "ivt": count_ivt(),
        "catches": count_catches(files),
        "tests": count_tests(),
    }


GOLDEN = {
    "Launch": 542,
    "SnapshotCore": 385,
    "Ensure": 330,
    "ExecuteAsync": 295,
    "GetEnvironment": 294,
    "Gesture": 289,
    "EvaluateCondition": 277,
    "Run": 246,
    "Release": 210,
    "MatchTemplate": 205,
    "TypeText": 201,
    "Click": 201,
    "Find": 200,
    "SetElementValue": 198,
    "Main": 176,
}


def selftest() -> int:
    rows = []
    for path in source_files(SRC):
        for length, line, name in parse_methods(open(path, encoding="utf-8").read()):
            rows.append((length, os.path.relpath(path, REPO), line, name))
    failures = []
    for name, expected in GOLDEN.items():
        hits = [row for row in rows if row[3] == name]
        if not hits:
            failures.append(f"{name}: 未解析到")
        elif not any(row[0] == expected for row in hits):
            failures.append(f"{name}: 期望 {expected}，实际 {[row[0] for row in hits]}")
    print(f"selftest: {len(GOLDEN) - len(failures)}/{len(GOLDEN)} 通过")
    for failure in failures:
        print("  FAIL", failure)
    return 1 if failures else 0


def check(current: dict, baseline: dict) -> list[str]:
    failures = []
    cur, base = current["source"], baseline["source"]
    if cur["max_file"] > base["max_file"]:
        failures.append(f"最大文件 {cur['max_file']} > 基线 {base['max_file']}")
    if cur["files_gt_600"] > base["files_gt_600"]:
        failures.append(f">600 行文件 {cur['files_gt_600']} > 基线 {base['files_gt_600']}")
    cm, bm = current["methods"], baseline["methods"]
    for key, label in (("max", "最大方法"), ("gt_150", ">150 行方法"),
                       ("ge_200", ">=200 行方法"), ("ge_120", ">=120 行方法")):
        if cm[key] > bm[key]:
            failures.append(f"{label} {cm[key]} > 基线 {bm[key]}")
    ch, bh = current["hosts"], baseline["hosts"]
    if ch["max_members"] > bh["max_members"]:
        failures.append(f"宿主接口最大成员 {ch['max_members']} > 基线 {bh['max_members']}")
    extra = set(current["namespaces"]["split"]) - set(baseline["namespaces"]["split"])
    if extra:
        failures.append(f"新增 SPLIT 命名空间: {sorted(extra)}")
    cc, bc = current["catches"], baseline["catches"]
    if cc["untyped_silent"] > bc["untyped_silent"]:
        failures.append(f"无类型静默 catch {cc['untyped_silent']} > 基线 {bc['untyped_silent']}")
    if current["tests"]["methods"] < baseline["tests"]["methods"]:
        failures.append(f"测试方法 {current['tests']['methods']} < 基线 {baseline['tests']['methods']}")
    cur_ivt = {(row["from"], row["to"]) for row in current["ivt"]}
    base_ivt = {(row["from"], row["to"]) for row in baseline["ivt"]}
    if cur_ivt - base_ivt:
        failures.append(f"新增 IVT: {sorted(cur_ivt - base_ivt)}")
    return failures


def summarize(data: dict) -> str:
    src, methods = data["source"], data["methods"]
    hosts, catches, tests = data["hosts"], data["catches"], data["tests"]
    return (
        f"files={src['files']} lines={src['lines']} max_file={src['max_file']} "
        f"gt600={src['files_gt_600']} | methods max={methods['max']} "
        f"gt150={methods['gt_150']} ge200={methods['ge_200']} ge120={methods['ge_120']}\n"
        f"hosts={hosts['count']} members={hosts['members']} max={hosts['max_members']} | "
        f"split={data['namespaces']['split']}\n"
        f"catch untyped={catches['untyped']} silent={catches['untyped_silent']} "
        f"debug={catches['debug_only']} | tests={tests['methods']} ({tests['fact']}f/{tests['theory']}t)"
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()

    if args.selftest:
        return selftest()

    current = collect()
    print(summarize(current))

    if args.write:
        os.makedirs(os.path.dirname(BASELINE), exist_ok=True)
        with open(BASELINE, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(current, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
        print(f"baseline -> {os.path.relpath(BASELINE, REPO)}")
        return 0
    if args.json:
        print(json.dumps(current, ensure_ascii=False, indent=2))
        return 0
    if args.check:
        if not os.path.exists(BASELINE):
            print("缺少 arch_baseline.json；先运行 --write")
            return 1
        baseline = json.load(open(BASELINE, encoding="utf-8"))
        failures = check(current, baseline)
        for failure in failures:
            print("  FAIL", failure)
        print("arch_metrics: OK" if not failures else f"arch_metrics: {len(failures)} 项退化")
        return 1 if failures else 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
