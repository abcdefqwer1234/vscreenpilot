"""pptx 产物校验：页数、比例、文本、字号、图片、表格、越界与重叠。

用法：
  python tools/audit/ppt/ppt_oracle.py artifacts/ppt/VScreenPilot-PPT-测评.pptx
"""

from __future__ import annotations

import json
import os
import sys
import zipfile
import xml.etree.ElementTree as ET

NS = {
    "p": "http://schemas.openxmlformats.org/presentationml/2006/main",
    "a": "http://schemas.openxmlformats.org/drawingml/2006/main",
    "r": "http://schemas.openxmlformats.org/officeDocument/2006/relationships",
}
EMU_PER_INCH = 914400


def text_of(node) -> str:
    return "".join(item.text or "" for item in node.findall(".//a:t", NS))


def shape_bounds(shape):
    xfrm = shape.find(".//a:xfrm", NS)
    if xfrm is None:
        return None
    off = xfrm.find("a:off", NS)
    ext = xfrm.find("a:ext", NS)
    if off is None or ext is None:
        return None
    return (int(off.get("x")), int(off.get("y")), int(ext.get("cx")), int(ext.get("cy")))


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    path = sys.argv[1]
    checks = []

    def check(cid, desc, ok, **extra):
        checks.append({"id": cid, "purpose": desc, "ok": bool(ok), **extra})

    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        presentation = ET.fromstring(archive.read("ppt/presentation.xml"))
        size = presentation.find("p:sldSz", NS)
        width = int(size.get("cx"))
        height = int(size.get("cy"))
        slides = sorted(
            [n for n in names if n.startswith("ppt/slides/slide") and n.endswith(".xml")],
            key=lambda n: int("".join(ch for ch in os.path.basename(n) if ch.isdigit())),
        )

        check("PPT-01", "页数 = 4", len(slides) == 4, slides=len(slides))
        ratio = width / height
        check("PPT-02", "16:9 画布", abs(ratio - 16 / 9) < 0.02,
              sldSz=f"{width}x{height}", ratio=round(ratio, 4))

        media = [n for n in names if n.startswith("ppt/media/")]
        check("PPT-03", "至少 1 张嵌入图片", len(media) >= 1, media=media)

        texts = []
        font_sizes = []
        tables = []
        out_of_bounds = []
        title_bounds = {}
        body_bounds = {}

        for index, name in enumerate(slides, start=1):
            root = ET.fromstring(archive.read(name))
            tree = root.find(".//p:cSld/p:spTree", NS)
            if tree is None:
                continue
            slide_text = []
            for shape in tree.findall("p:sp", NS):
                value = text_of(shape)
                if value.strip():
                    slide_text.append(value)
                bounds = shape_bounds(shape)
                for run in shape.findall(".//a:rPr", NS):
                    if run.get("sz"):
                        font_sizes.append(int(run.get("sz")) / 100)
                if bounds:
                    x, y, cx, cy = bounds
                    if x < 0 or y < 0 or x + cx > width or y + cy > height:
                        out_of_bounds.append({"slide": index, "text": value[:20], "bounds": bounds})
                if value.strip() and bounds:
                    if index in (2, 3, 4) and len(value) < 12:
                        title_bounds.setdefault(index, bounds)
                    elif index in (2, 3, 4):
                        body_bounds.setdefault(index, bounds)
            for table in tree.findall("p:graphicFrame", NS):
                tbl = table.find(".//a:tbl", NS)
                if tbl is None:
                    continue
                rows = tbl.findall("a:tr", NS)
                cols = rows[0].findall("a:tc", NS) if rows else []
                cell_texts = [
                    text_of(cell) for row in rows for cell in row.findall("a:tc", NS)
                ]
                tables.append({"slide": index, "rows": len(rows), "cols": len(cols),
                               "cells": cell_texts})
                slide_text.extend([value for value in cell_texts if value.strip()])
            texts.append({"slide": index, "texts": slide_text})

        check("PPT-04", "存在 1 个表格", len(tables) == 1, tables=tables)
        if tables:
            table = tables[0]
            check("PPT-05", "表格 3x4", table["rows"] == 3 and table["cols"] == 4,
                  rows=table["rows"], cols=table["cols"])
            filled = [c for c in table["cells"] if c.strip()]
            check("PPT-06", "表格 12 格均有内容", len(filled) == 12,
                  filled=len(filled), cells=table["cells"])

        expected = {
            1: ["VScreenPilot 能力测评", "Windows 桌面自动化能力实测"],
            2: ["核心能力", "虚拟屏隔离", "MSAA", "快照代际", "JSONL"],
            3: ["实测截图"],
            4: ["工具面覆盖", "类别", "snapshot"],
        }
        missing = []
        for slide_no, needles in expected.items():
            joined = "".join(
                "".join(item["texts"]) for item in texts if item["slide"] == slide_no
            )
            for needle in needles:
                if needle.lower() not in joined.lower():
                    missing.append(f"{slide_no}:{needle}")
        check("PPT-07", "关键文本齐全", not missing, missing=missing, texts=texts)

        check("PPT-08", "元素不越界", not out_of_bounds, out_of_bounds=out_of_bounds)
        check("PPT-09", "无占位提示残留",
              not any("单击此处" in t for item in texts for t in item["texts"]),
              texts=texts)

        overlaps = []
        for slide_no in title_bounds:
            if slide_no in body_bounds:
                tx, ty, tw, th = title_bounds[slide_no]
                bx, by, bw, bh = body_bounds[slide_no]
                if tx < bx + bw and bx < tx + tw and ty < by + bh and by < ty + th:
                    overlaps.append(slide_no)
        check("PPT-10", "标题与正文不重叠", not overlaps, overlaps=overlaps)

        biggest = max(font_sizes) if font_sizes else 0
        check("PPT-11", "标题字号 >= 32", biggest >= 32, max_pt=biggest,
              distinct=sorted(set(font_sizes), reverse=True)[:6])

    summary = {
        "deck": path,
        "bytes": os.path.getsize(path),
        "checks": checks,
        "passed": sum(1 for c in checks if c["ok"]),
        "total": len(checks),
    }
    out = os.path.join(os.path.dirname(path), "oracle.json")
    with open(out, "w", encoding="utf-8") as handle:
        json.dump(summary, handle, ensure_ascii=False, indent=2)
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
