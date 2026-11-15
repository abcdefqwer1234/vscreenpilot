"""Word/docx 独立 oracle：不依赖 Office，直接解压 docx 读取 XML。

用法：
  python tools/audit/word_oracle.py <file.docx> [--expect "文本1" "文本2"] [--json out.json]

输出：段落文本、加粗 run、标题样式、表格单元格、命中/缺失的期望文本。
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import zipfile
from pathlib import Path

NS = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"


def read_docx(path: Path) -> dict:
    with zipfile.ZipFile(path) as archive:
        xml = archive.read("word/document.xml").decode("utf-8", errors="replace")

    paragraphs = []
    bold_runs = []
    headings = []
    for para in re.findall(r"<w:p[ >].*?</w:p>", xml, flags=re.S):
        texts = re.findall(r"<w:t(?: [^>]*)?>(.*?)</w:t>", para, flags=re.S)
        text = "".join(texts)
        paragraphs.append(text)
        style = re.search(r'<w:pStyle w:val="([^"]+)"', para)
        if style:
            raw = style.group(1)
            if raw in ("1", "Heading1") or "Heading" in raw or "标题" in raw:
                headings.append({"text": text, "style": "Heading1" if raw == "1" else raw})
        for run in re.findall(r"<w:r[ >].*?</w:r>", para, flags=re.S):
            if "<w:b/>" in run or '<w:b w:val="1"' in run or '<w:b w:val="true"' in run:
                run_text = "".join(re.findall(r"<w:t(?: [^>]*)?>(.*?)</w:t>", run, flags=re.S))
                if run_text.strip():
                    bold_runs.append(run_text)

    tables = []
    for table in re.findall(r"<w:tbl>.*?</w:tbl>", xml, flags=re.S):
        rows = []
        for row in re.findall(r"<w:tr[ >].*?</w:tr>", table, flags=re.S):
            cells = []
            for cell in re.findall(r"<w:tc>.*?</w:tc>", row, flags=re.S):
                cells.append("".join(re.findall(r"<w:t(?: [^>]*)?>(.*?)</w:t>", cell, flags=re.S)))
            rows.append(cells)
        tables.append(rows)

    return {
        "file": str(path),
        "bytes": path.stat().st_size,
        "paragraphs": paragraphs,
        "text": "\n".join(paragraphs),
        "bold_runs": bold_runs,
        "headings": headings,
        "tables": tables,
        "table_count": len(tables),
    }


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    parser = argparse.ArgumentParser()
    parser.add_argument("docx")
    parser.add_argument("--expect", nargs="*", default=[])
    parser.add_argument("--json", default="")
    args = parser.parse_args()

    path = Path(args.docx)
    if not path.exists():
        print(json.dumps({"ok": False, "error": "file_not_found", "file": str(path)}, ensure_ascii=False))
        return 2

    data = read_docx(path)
    missing = [item for item in args.expect if item not in data["text"]]
    result = {"ok": not missing, "missing": missing, **data}
    text = json.dumps(result, ensure_ascii=False, indent=1)
    if args.json:
        Path(args.json).write_text(text, encoding="utf-8")
    print(text)
    return 0 if not missing else 1


if __name__ == "__main__":
    sys.exit(main())
