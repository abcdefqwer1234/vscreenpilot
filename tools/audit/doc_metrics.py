#!/usr/bin/env python3
"""把 arch_metrics 的实测数字写入文档，避免手写漂移。

用法：
  python tools/audit/doc_metrics.py --write   # 生成并写入 markers 之间
  python tools/audit/doc_metrics.py --check   # CI：markers 内容必须与实测一致
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ARCH = os.path.join(REPO, "docs", "架构说明.md")
METRICS = os.path.join(REPO, "tools", "audit", "arch_metrics.py")
BEGIN = "<!-- arch-metrics:begin -->"
END = "<!-- arch-metrics:end -->"


def generate() -> str:
    result = subprocess.run(
        [sys.executable, METRICS, "--json"],
        capture_output=True, text=True, check=True)
    data = json.loads(result.stdout)
    src, types, methods = data["source"], data["types"], data["methods"]
    hosts, session, program = data["hosts"], data["session_manager"], data["program"]
    tests, catch = data["tests"], data["catches"]
    public = ", ".join(f"{key.split('.')[-1]}={value}"
                       for key, value in sorted(data["public_types"].items()))
    rows = [
        ("源文件 / 行数", f"{src['files']} / {src['lines']}", "-"),
        ("最大文件", f"{src['max_file']}", "≤450"),
        ("类型 >400", f"{types['gt_400']}", "0"),
        ("最大类型跨度", f"{types['max']}", "≤400"),
        ("partial 类型", f"{types['partial_types']}", "0"),
        ("方法 >80 行", f"{methods['gt_80']}（最大 {methods['max']}）", "0 / ≤80"),
        ("方法 >12 判断点", f"{methods['gt_12_decision']}（最大 {methods['max_decision']}）", "0 / ≤12"),
        ("方法 >6 参数", f"{methods['gt_6_params']}（最大 {methods['max_params']}）", "0 / ≤6"),
        ("I*Host", f"{hosts['count']} / {hosts['members']} 成员", "0"),
        ("SessionManager", f"{session['lines']} 行 / {session['public']} public", "≤350 / ≤12"),
        ("Program", f"{program['lines']} 行 / Main {program['main_lines']}", "≤200 / ≤80"),
        ("测试方法", f"{tests['methods']}", "≥230"),
        ("无日志 catch", f"{catch['comment_only']}", "≤66"),
        ("SPLIT 命名空间", f"{len(data['namespaces']['split'])}", "0"),
        ("public 类型", public, "不增加"),
    ]
    lines = ["| 指标 | 实测 | 硬上限 |", "|---|---:|---:|"]
    lines += [f"| {name} | {value} | {limit} |" for name, value, limit in rows]
    return "\n".join(lines)


def replace(text: str, block: str) -> str:
    start = text.find(BEGIN)
    end = text.find(END)
    if start < 0 or end < 0 or end < start:
        raise SystemExit(f"缺少 markers：{BEGIN} / {END}")
    return text[: start + len(BEGIN)] + "\n" + block + "\n" + text[end:]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    text = open(ARCH, encoding="utf-8").read()
    block = generate()
    updated = replace(text, block)

    if args.write:
        with open(ARCH, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(updated)
        print(f"metrics -> {os.path.relpath(ARCH, REPO)}")
        return 0

    if updated != text:
        print("doc_metrics: 文档数字与实测不一致；运行 --write")
        return 1
    print("doc_metrics: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
