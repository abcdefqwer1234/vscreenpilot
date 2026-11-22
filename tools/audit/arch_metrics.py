#!/usr/bin/env python3
"""架构度量与硬规则校验（第三轮 v2）。

只依赖标准库。解析口径：
- 行数：read_text().splitlines()，含空行，排除 obj/bin；
- 类型跨度：顶层类型声明到匹配闭括号的行数；partial 按 namespace+name 合并；
- 方法：类型体内 brace depth 1 的成员，覆盖泛型、多行签名、表达式体、构造函数；
- 判断点：if/for/foreach/while/case/catch/when/&&/||/??/三元 `?`；
- 依赖边：文件 namespace + using/限定名，按 Server 模块归并。

用法：
  python tools/audit/arch_metrics.py --write      # 重写基线（带 ratchet 保护）
  python tools/audit/arch_metrics.py --check      # CI：只允许变好
  python tools/audit/arch_metrics.py --enforce    # 硬上限
  python tools/audit/arch_metrics.py --selftest   # fixtures golden
  python tools/audit/arch_metrics.py --json
  python tools/audit/arch_metrics.py --dump-fixtures
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
RULES = os.path.join(REPO, "tools", "audit", "arch_rules.json")
FIXTURES = os.path.join(REPO, "tools", "audit", "fixtures")

CONTROL = {
    "if", "for", "foreach", "while", "switch", "catch", "using", "lock",
    "return", "new", "throw", "base", "this", "when", "nameof", "typeof",
    "sizeof", "checked", "unchecked", "else", "do", "try", "finally",
}
MODIFIERS = (
    "public|private|internal|protected|static|async|sealed|override|virtual|"
    "abstract|extern|unsafe|new|partial|readonly|ref|file"
)
DECL_RE = re.compile(
    r"^\s*(?P<mods>(?:(?:" + MODIFIERS + r")\s+)*)"
    r"(?P<kind>record\s+struct|record\s+class|record|class|struct|interface|enum)\s+"
    r"(?P<name>[A-Za-z_]\w*)"
)
SIG_RE = re.compile(
    r"^\s*(?P<mods>(?:(?:" + MODIFIERS + r")\s+)*)"
    r"(?P<ret>(?:\([^)]*\)|[A-Za-z_][\w<>,\[\]\?\.]*)\s+)?"
    r"(?P<name>[A-Za-z_]\w*)\s*(?:<[^<>()]*>)?\s*\("
)
LOG_TOKENS = ("log.", "_log", "Log(", "Log.", "LogSink.", "Trace.", "Debug.", "Console.", "Warn(", "Error(")


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


def read_namespace(text: str) -> str | None:
    match = re.search(r"^\s*namespace\s+([A-Za-z0-9_.]+)\s*[;{]", text, re.MULTILINE)
    return match.group(1) if match else None


def assembly_of(path: str) -> str:
    return os.path.relpath(path, SRC).split(os.sep)[0]


def module_of(namespace: str) -> str | None:
    """把 VScreenPilot.* 命名空间归并为依赖图模块名。"""
    if namespace == "VScreenPilot":
        return "Program"
    if namespace.startswith("VScreenPilot.Server."):
        return namespace[len("VScreenPilot.Server."):].split(".")[0]
    if namespace.startswith("VScreenPilot."):
        return namespace.split(".")[1]
    return None


def parse_decls(text: str, rel_path: str):
    """解析顶层/嵌套类型声明，返回 span 与 partial 标记。"""
    lines = strip_code(text).splitlines()
    namespace = read_namespace(text) or ""
    decls = []
    for index, line in enumerate(lines):
        match = DECL_RE.match(line)
        if not match:
            continue
        kind = match.group("kind").replace("record ", "")
        name = match.group("name")
        mods = match.group("mods")
        # 找到第一个 `{` 或声明结束的 `;`（record 主构造函数）。
        brace = index
        while brace < len(lines) and "{" not in lines[brace] and ";" not in lines[brace]:
            brace += 1
        if brace >= len(lines) or "{" not in lines[brace]:
            end = index
        else:
            depth, end = 0, brace
            while end < len(lines):
                depth += lines[end].count("{") - lines[end].count("}")
                if depth <= 0:
                    break
                end += 1
        decls.append({
            "name": name,
            "kind": kind,
            "namespace": namespace,
            "file": rel_path,
            "start": index,
            "end": end,
            "span": end - index + 1,
            "partial": "partial" in mods.split(),
            "public": "public" in mods.split(),
        })
    for decl in decls:
        decl["top"] = not any(
            other is not decl and other["start"] < decl["start"] <= other["end"]
            for other in decls
        )
    return decls


def matching_paren(lines, start: int, column: int) -> int:
    """从 lines[start][column] 的 '(' 找到匹配 ')'，返回行号；失败返回 -1。"""
    depth = 0
    for index in range(start, len(lines)):
        line = lines[index]
        begin = column if index == start else 0
        for char in line[begin:]:
            if char == "(":
                depth += 1
            elif char == ")":
                depth -= 1
                if depth == 0:
                    return index
    return -1


def split_top_level(text: str, separator: str = ","):
    parts, depth, current = [], 0, []
    for char in text:
        if char in "(<[{":
            depth += 1
        elif char in ")>]}":
            depth -= 1
        if char == separator and depth == 0:
            parts.append("".join(current))
            current = []
        else:
            current.append(char)
    parts.append("".join(current))
    return parts


def count_parameters(signature: str) -> int:
    start = signature.find("(")
    if start < 0:
        return 0
    depth, end = 0, -1
    for index in range(start, len(signature)):
        if signature[index] == "(":
            depth += 1
        elif signature[index] == ")":
            depth -= 1
            if depth == 0:
                end = index
                break
    if end < 0:
        return 0
    inner = signature[start + 1:end].strip()
    if not inner:
        return 0
    return len([part for part in split_top_level(inner) if part.strip()])


def decision_points(body: str) -> int:
    count = len(re.findall(r"\b(?:if|for|foreach|while|case|catch|when)\b", body))
    count += body.count("&&") + body.count("||") + body.count("??")
    # 三元表达式按 ` ? ` 计；不把可空类型 `string?` 计入。
    count += len(re.findall(r"\s\?\s", body))
    return count


def parse_members(lines, decl):
    """解析类型体内 depth 1 的方法/构造函数/访问器。"""
    open_line = next((k for k in range(decl["start"], decl["end"] + 1) if "{" in lines[k]), None)
    if open_line is None:
        return []
    rows, depth = [], 0
    for index in range(open_line, decl["end"] + 1):
        before = depth
        depth += lines[index].count("{") - lines[index].count("}")
        if before != 1 or index == open_line:
            continue
        line = lines[index]
        if re.match(r"^\s*new\b", line):
            continue
        accessor = re.match(r"^\s*(get|set|init|add|remove)\s*(\{|=>)", line)
        name, kind, params_count = None, "method", 0
        if accessor:
            name, kind = accessor.group(1), "accessor"
            signature = line
        else:
            match = SIG_RE.match(line)
            if not match:
                continue
            name = match.group("name")
            if name in CONTROL:
                continue
            paren = match.end() - 1
            prefix = line[: paren]
            if re.search(r"\b(?:class|record|struct|interface|enum|delegate)\b", prefix):
                continue
            if "=" in prefix and "=>" not in prefix:
                continue
            close = matching_paren(lines, index, paren)
            if close < 0:
                continue
            params_signature = line[paren:] + " " + " ".join(
                part.strip() for part in lines[index + 1: close + 1])
            if name == decl["name"]:
                kind = "ctor"
            params_count = count_parameters(params_signature)
        # 找 body：`{` / `=>` / `;`。
        k = index
        while k <= decl["end"] and "{" not in lines[k] and "=>" not in lines[k] and ";" not in lines[k]:
            k += 1
        if k > decl["end"]:
            continue
        if ";" in lines[k] and "{" not in lines[k] and "=>" not in lines[k]:
            # abstract/extern/interface 成员，没有方法体。
            continue
        if "=>" in lines[k] and ( "{" not in lines[k] or lines[k].find("=>") < lines[k].find("{") ):
            end = k
            while end <= decl["end"] and ";" not in lines[end]:
                end += 1
            body = "\n".join(lines[index: end + 1])
            rows.append({
                "name": name, "kind": kind, "file": decl["file"], "type": decl["name"],
                "line": index + 1, "lines": end - index + 1,
                "decision": decision_points(body), "params": params_count,
                "public": bool(re.match(r"^\s*public\b", line)),
            })
            continue
        brace = next((j for j in range(index, decl["end"] + 1) if "{" in lines[j]), None)
        if brace is None:
            continue
        level, end = 0, brace
        while end <= decl["end"]:
            level += lines[end].count("{") - lines[end].count("}")
            if level <= 0:
                break
            end += 1
        body = "\n".join(lines[brace: end + 1])
        rows.append({
            "name": name, "kind": kind, "file": decl["file"], "type": decl["name"],
            "line": index + 1, "lines": end - index + 1,
            "decision": decision_points(body), "params": params_count,
            "public": bool(re.match(r"^\s*public\b", line)),
        })
    return rows


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
            members = []
            for line in body.splitlines():
                stripped = line.strip()
                if not stripped or stripped.startswith("//"):
                    continue
                member = re.match(r"^[A-Za-z_][\w<>,?\[\]. ]*\s+([A-Za-z_]\w*)\s*(\(|!|\{|=>)", stripped)
                if member:
                    members.append(member.group(1))
            hosts.append({"name": name, "members": members})
    return hosts


def count_catches(files):
    typed = untyped = silent = comment_only = 0
    for path in files:
        text = strip_code(open(path, encoding="utf-8").read())
        for match in re.finditer(r"catch\s*(\(([^)]*)\))?\s*\{", text):
            brace = text.find("{", match.start())
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
            if match.group(1):
                typed += 1
            else:
                untyped += 1
            if not any(token in body for token in LOG_TOKENS):
                if "throw" in body:
                    continue
                quiet = body.strip()
                if quiet:
                    silent += 1
                    if "//" in quiet or "/*" in quiet:
                        comment_only += 1
    return {"typed": typed, "untyped": untyped, "untyped_silent": silent,
            "comment_only": silent}


def count_tests():
    fact = theory = 0
    for path in source_files(TESTS):
        text = open(path, encoding="utf-8").read()
        fact += len(re.findall(r"\[Fact\]", text))
        theory += len(re.findall(r"\[Theory\]", text))
    return fact + theory, fact, theory


def component_tests(components):
    rows = []
    for component in components:
        name = component["name"]
        test = component.get("test", "")
        path = os.path.join(TESTS, test)
        count = 0
        if os.path.exists(path):
            text = open(path, encoding="utf-8").read()
            count = len(re.findall(r"\[(?:Fact|Theory)\]", text))
        rows.append({"name": name, "test": test, "methods": count})
    return rows


def collect():
    files = sorted(source_files(SRC))
    file_rows, decls, method_rows = [], [], []
    edges = set()
    for path in files:
        text = open(path, encoding="utf-8").read()
        rel = os.path.relpath(path, REPO).replace(os.sep, "/")
        length = len(text.splitlines())
        file_rows.append((length, rel))
        namespace = read_namespace(text) or ""
        for line in text.splitlines():
            match = re.match(r"^\s*(?:global\s+)?using\s+(?:static\s+)?([A-Za-z0-9_.]+)\s*;", line)
            if match:
                edges.add((module_of(namespace), module_of(match.group(1))))
            elif re.match(r"^\s*using\s+\w+\s*=", line):
                target = line.split("=", 1)[1].strip().rstrip(";")
                edges.add((module_of(namespace), module_of(target)))
        for token in re.findall(r"\bVScreenPilot(?:\.[A-Za-z_]\w*)+", text):
            edges.add((module_of(namespace), module_of(token)))
        for decl in parse_decls(text, rel):
            decls.append(decl)
            method_rows.extend(parse_members(strip_code(text).splitlines(), decl))

    top = [decl for decl in decls if decl["top"]]
    merged = {}
    for decl in top:
        key = (decl["namespace"], decl["name"])
        group = merged.setdefault(key, {"name": decl["name"], "namespace": decl["namespace"],
                                        "files": [], "lines": 0, "partial": False, "public": False})
        group["files"].append(decl["file"])
        group["lines"] += decl["span"]
        group["partial"] = group["partial"] or decl["partial"]
        group["public"] = group["public"] or decl["public"]

    by_file = {}
    for decl in top:
        by_file.setdefault(decl["file"], []).append(decl)
    mismatches, big_helpers = [], []
    for file, items in by_file.items():
        stem = os.path.basename(file)[:-3]
        main = max(items, key=lambda item: item["span"])
        if not stem.startswith(main["name"]):
            mismatches.append({"file": file, "main": main["name"], "lines": main["span"]})
        for item in items:
            if item is not main and item["span"] > 80:
                big_helpers.append({"file": file, "type": item["name"], "lines": item["span"]})

    hosts = count_hosts(files)
    unique_members = {member for host in hosts for member in host["members"]}
    catch = count_catches(files)
    test_methods, fact, theory = count_tests()

    namespaces = {}
    for path in files:
        text = open(path, encoding="utf-8").read()
        namespace = read_namespace(text)
        if namespace:
            namespaces.setdefault(namespace, set()).add(assembly_of(path))

    public_types = {}
    for decl in top:
        if decl["public"]:
            public_types[decl["namespace"].split(".")[0] if decl["namespace"].startswith("VScreenPilot") else "other"] = 1
    # 用程序集重新统计 public 类型，避免命名空间前缀误判。
    public_by_assembly = {}
    for path in files:
        text = strip_code(open(path, encoding="utf-8").read())
        count = len(re.findall(r"(?m)^\s*public\s+(?:sealed\s+|static\s+|abstract\s+|readonly\s+|partial\s+)*(?:class|record|struct|interface|enum)\s+\w+", text))
        public_by_assembly[assembly_of(path)] = public_by_assembly.get(assembly_of(path), 0) + count

    edges = {(source, target) for source, target in edges if source and target and source != target}
    file_types = {
        "mismatch": sorted(mismatches, key=lambda row: -row["lines"]),
        "helpers_gt_80": sorted(big_helpers, key=lambda row: -row["lines"]),
    }
    type_rows = sorted(merged.values(), key=lambda row: -row["lines"])
    session = next((row for row in type_rows if row["name"] == "SessionManager"), None)
    program_path = os.path.join(SRC, "VScreenPilot.Server", "Program.cs")
    program_lines = len(open(program_path, encoding="utf-8").read().splitlines()) if os.path.exists(program_path) else 0
    main_row = next((row for row in method_rows
                     if row["name"] == "Main" and row["file"].endswith("/Program.cs")), None)
    return {
        "schema": 2,
        "generated_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "source": {
            "files": len(file_rows),
            "lines": sum(row[0] for row in file_rows),
            "max_file": max(file_rows)[0] if file_rows else 0,
            "files_gt_450": sum(1 for row in file_rows if row[0] > 450),
            "top_files": [{"lines": n, "path": p} for n, p in sorted(file_rows, reverse=True)[:12]],
        },
        "types": {
            "count": len(type_rows),
            "max": type_rows[0]["lines"] if type_rows else 0,
            "gt_400": sum(1 for row in type_rows if row["lines"] > 400),
            "partial_declarations": sum(1 for decl in top if decl["partial"]),
            "partial_types": sum(1 for row in type_rows if row["partial"]),
            "top": [{"lines": row["lines"], "name": row["name"], "files": len(row["files"])}
                    for row in type_rows[:20]],
        },
        "file_types": file_types,
        "methods": {
            "count": len(method_rows),
            "max": max((row["lines"] for row in method_rows), default=0),
            "gt_80": sum(1 for row in method_rows if row["lines"] > 80),
            "max_decision": max((row["decision"] for row in method_rows), default=0),
            "gt_12_decision": sum(1 for row in method_rows if row["decision"] > 12),
            "max_params": max((row["params"] for row in method_rows), default=0),
            "gt_6_params": sum(1 for row in method_rows if row["params"] > 6),
            "top": sorted(method_rows, key=lambda row: -row["lines"])[:25],
        },
        "hosts": {
            "count": len(hosts),
            "members": sum(len(host["members"]) for host in hosts),
            "max_members": max((len(host["members"]) for host in hosts), default=0),
            "unique_members": len(unique_members),
            "items": sorted(({"name": host["name"], "members": len(host["members"])}
                             for host in hosts), key=lambda row: -row["members"]),
        },
        "namespaces": {
            "split": sorted(ns for ns, asms in namespaces.items() if len(asms) > 1),
            "edges": sorted(f"{source}->{target}" for source, target in edges),
        },
        "public_types": public_by_assembly,
        "catches": catch,
        "tests": {"methods": test_methods, "fact": fact, "theory": theory},
        "session_manager": {
            "lines": session["lines"] if session else 0,
            "public": sum(1 for row in method_rows
                          if row["type"] == "SessionManager" and row["public"]),
        },
        "program": {
            "lines": program_lines,
            "main_lines": main_row["lines"] if main_row else 0,
        },
        "components": [],
        "ivt": [],
    }


def load_rules():
    if not os.path.exists(RULES):
        return {}
    return json.load(open(RULES, encoding="utf-8"))


def threshold_failures(current, rules):
    failures = []
    src, types, methods = current["source"], current["types"], current["methods"]
    checks = [
        ("max_file_lines", src["max_file"], "最大文件"),
        ("max_type_lines", types["max"], "最大类型"),
        ("max_partial_types", types["partial_types"], "partial 类型"),
        ("max_method_lines", methods["max"], "最大方法"),
        ("max_decision_points", methods["max_decision"], "最大判断点"),
        ("max_parameters", methods["max_params"], "最大参数"),
        ("max_host_interfaces", current["hosts"]["count"], "I*Host"),
        ("max_session_manager_lines", current["session_manager"]["lines"], "SessionManager 行数"),
        ("max_session_manager_public", current["session_manager"]["public"], "SessionManager public 成员"),
        ("max_program_lines", current["program"]["lines"], "Program 行数"),
        ("max_program_main_lines", current["program"]["main_lines"], "Main 行数"),
        ("max_comment_only_catches", current["catches"]["comment_only"], "无日志 catch"),
        ("min_test_methods", -current["tests"]["methods"], "测试方法"),
    ]
    for key, value, label in checks:
        if key not in rules:
            continue
        limit = rules[key]
        if key == "min_test_methods":
            if current["tests"]["methods"] < limit:
                failures.append(f"{label} {current['tests']['methods']} < {limit}")
        elif value > limit:
            failures.append(f"{label} {value} > {limit}")
    for assembly, baseline in rules.get("public_types", {}).items():
        actual = current["public_types"].get(assembly, 0)
        if actual > baseline:
            failures.append(f"{assembly} public 类型 {actual} > 基线 {baseline}")
    for component in current["components"]:
        minimum = rules.get("min_tests_per_component", 1)
        if component["methods"] < minimum:
            failures.append(
                f"组件 {component['name']} 测试方法 {component['methods']} < {minimum}")
    return failures


def check(current, baseline):
    failures = []
    pairs = [
        ("source", "max_file", "最大文件"),
        ("types", "max", "最大类型"),
        ("types", "gt_400", ">400 类型"),
        ("types", "partial_types", "partial 类型"),
        ("methods", "max", "最大方法"),
        ("methods", "gt_80", ">80 行方法"),
        ("methods", "max_decision", "最大判断点"),
        ("methods", "gt_12_decision", ">12 判断点方法"),
        ("methods", "max_params", "最大参数"),
        ("methods", "gt_6_params", ">6 参数方法"),
        ("hosts", "count", "I*Host"),
    ]
    for section, key, label in pairs:
        if current[section][key] > baseline[section][key]:
            failures.append(f"{label} {current[section][key]} > 基线 {baseline[section][key]}")
    if current["tests"]["methods"] < baseline["tests"]["methods"]:
        failures.append(f"测试方法 {current['tests']['methods']} < 基线 {baseline['tests']['methods']}")
    extra = set(current["namespaces"]["split"]) - set(baseline["namespaces"]["split"])
    if extra:
        failures.append(f"新增 SPLIT 命名空间: {sorted(extra)}")
    return failures


def summarize(data: dict) -> str:
    src, types, methods = data["source"], data["types"], data["methods"]
    hosts, catch, tests = data["hosts"], data["catches"], data["tests"]
    return (
        f"files={src['files']} lines={src['lines']} max_file={src['max_file']} "
        f"gt450={src['files_gt_450']} | types max={types['max']} gt400={types['gt_400']} "
        f"partial={types['partial_types']} | methods max={methods['max']} gt80={methods['gt_80']} "
        f"dec={methods['max_decision']} params={methods['max_params']} | "
        f"hosts={hosts['count']}/{hosts['members']} | tests={tests['methods']} | "
        f"catch untyped={catch['untyped']} silent={catch['untyped_silent']}"
    )


# fixture golden：只断言 tools/audit/fixtures，不绑定生产方法长度。
GOLDEN = {
    "BasicCases.cs:Alpha": 3,
    "BasicCases.cs:Beta": 5,
    "BasicCases.cs:Gamma": 6,
    "BasicCases.cs:Delta": 4,
    "BasicCases.cs:Epsilon": 5,
    "BasicCases.cs:Zeta": 4,
    "BasicCases.cs:Eta": 4,
    "BasicCases.cs:Theta": 4,
    "BasicCases.cs:Mu": 8,
    "BasicCases.cs:Iota": 1,
    "BasicCases.cs:Inner": 3,
    "PartialCases.cs:P1": 3,
    "PartialCases.cs:P2": 4,
    "PartialCases.cs:P3": 5,
    "PartialCases.cs:P4": 4,
    "PartialCases.cs:P5": 5,
    "PartialCases.cs:P6": 6,
    "EdgeCases.cs:E1": 3,
    "EdgeCases.cs:E2": 4,
    "EdgeCases.cs:E3": 4,
    "EdgeCases.cs:E4": 4,
    "EdgeCases.cs:E5": 7,
    "EdgeCases.cs:E6": 6,
}
ABSENT = ("Kappa",)
GOLDEN_META = {
    "BasicCases.cs:Mu": {"params": 7, "decision": 3},
    "EdgeCases.cs:E6": {"params": 0, "decision": 1},
}


def fixture_rows():
    rows = []
    for path in source_files(FIXTURES):
        name = os.path.basename(path)
        text = open(path, encoding="utf-8").read()
        for decl in parse_decls(text, name):
            for row in parse_members(strip_code(text).splitlines(), decl):
                rows.append((name, row["name"], row["lines"], row["line"],
                             row["decision"], row["params"]))
    return rows


def selftest() -> int:
    rows = fixture_rows()
    failures, passed = [], 0
    for key, expected in GOLDEN.items():
        file_name, method = key.split(":")
        hits = [row for row in rows if row[0] == file_name and row[1] == method]
        if not hits:
            failures.append(f"{key}: 未解析到")
        elif not any(row[2] == expected for row in hits):
            failures.append(f"{key}: 期望 {expected}，实际 {[row[2] for row in hits]}")
        else:
            passed += 1
    for key, expected in GOLDEN_META.items():
        file_name, method = key.split(":")
        hits = [row for row in rows if row[0] == file_name and row[1] == method]
        if not hits:
            failures.append(f"{key}: 未解析到（meta）")
            continue
        row = hits[0]
        for field, index in (("decision", 4), ("params", 5)):
            if row[index] != expected[field]:
                failures.append(f"{key}.{field}: 期望 {expected[field]}，实际 {row[index]}")
    absent = sorted({row[1] for row in rows if row[1] in ABSENT})
    failures.extend(f"{name}: 不应被解析为方法" for name in absent)
    print(f"selftest: {passed}/{len(GOLDEN)} positive, "
          f"{len(GOLDEN_META)} meta, {len(ABSENT) - len(absent)}/{len(ABSENT)} negative")
    for failure in failures:
        print("  FAIL", failure)
    return 1 if failures else 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--enforce", action="store_true")
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--dump-fixtures", action="store_true")
    parser.add_argument("--inventory", action="store_true")
    args = parser.parse_args()

    if args.inventory:
        files = sorted(source_files(SRC))
        all_rows = []
        for path in files:
            text = open(path, encoding="utf-8").read()
            rel = os.path.relpath(path, REPO).replace(os.sep, "/")
            for decl in parse_decls(text, rel):
                all_rows.extend(parse_members(strip_code(text).splitlines(), decl))
        print("| 方法 | 行数 | 判断点 | 参数 | 文件 |")
        print("|---|---:|---:|---:|---|")
        for row in sorted(all_rows, key=lambda item: -item["lines"]):
            if row["lines"] > 80 or row["decision"] > 12 or row["params"] > 6:
                print(f"| `{row['name']}` | {row['lines']} | {row['decision']} | "
                      f"{row['params']} | `{os.path.basename(row['file'])}` |")
        return 0

    if args.dump_fixtures:
        for row in fixture_rows():
            print(f"{row[0]}:{row[1]} length={row[2]} line={row[3]}")
        return 0
    if args.selftest:
        return selftest()

    current = collect()
    rules = load_rules()
    current["components"] = component_tests(rules.get("components", []))
    if not args.json:
        print(summarize(current))

    if args.write:
        if os.path.exists(BASELINE):
            baseline = json.load(open(BASELINE, encoding="utf-8"))
            if baseline.get("schema") == 2:
                worse = check(current, baseline)
                if worse:
                    for failure in worse:
                        print("  FAIL", failure)
                    print("arch_metrics: 基线拒绝更新（指标升高）")
                    return 1
        with open(BASELINE, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(current, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
        print(f"baseline -> {os.path.relpath(BASELINE, REPO)}")
        return 0
    if args.json:
        print(json.dumps(current, ensure_ascii=False, indent=2))
        return 0
    if args.enforce:
        failures = threshold_failures(current, rules)
        for failure in failures:
            print("  FAIL", failure)
        print("arch_metrics: ENFORCE OK" if not failures else f"arch_metrics: {len(failures)} 项违反硬上限")
        return 1 if failures else 0
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
