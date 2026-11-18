import re
import sys

from docx import Document
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.oxml.ns import qn
from docx.shared import Cm, Pt, RGBColor

EA = "Microsoft YaHei"
LATIN = "Segoe UI"
CODE = "Consolas"
ACCENT = RGBColor(0x1F, 0x36, 0x64)


def set_font(run, ea=EA, latin=LATIN):
    run.font.name = latin
    rpr = run._element.get_or_add_rPr()
    fonts = rpr.find(qn("w:rFonts"))
    if fonts is None:
        fonts = rpr.makeelement(qn("w:rFonts"), {})
        rpr.append(fonts)
    fonts.set(qn("w:ascii"), latin)
    fonts.set(qn("w:hAnsi"), latin)
    fonts.set(qn("w:eastAsia"), ea)


INLINE = re.compile(r"(`[^`]+`|\*\*[^*]+\*\*)")


def add_inline(par, text, size=None, bold=False):
    for piece in INLINE.split(text):
        if not piece:
            continue
        if piece.startswith("`") and piece.endswith("`") and len(piece) > 2:
            run = par.add_run(piece[1:-1])
            set_font(run, ea=CODE, latin=CODE)
            run.font.size = Pt((size or 10.5) - 0.5)
            run.font.color.rgb = RGBColor(0xA6, 0x1B, 0x1B)
        elif piece.startswith("**") and piece.endswith("**") and len(piece) > 4:
            run = par.add_run(piece[2:-2])
            set_font(run)
            run.bold = True
            if size:
                run.font.size = Pt(size)
        else:
            run = par.add_run(piece)
            set_font(run)
            run.bold = bold
            if size:
                run.font.size = Pt(size)


def split_row(line):
    line = line.strip().strip("|")
    cells, buf, i = [], [], 0
    while i < len(line):
        if line[i] == "\\" and i + 1 < len(line) and line[i + 1] == "|":
            buf.append("|")
            i += 2
            continue
        if line[i] == "|":
            cells.append("".join(buf).strip())
            buf = []
        else:
            buf.append(line[i])
        i += 1
    cells.append("".join(buf).strip())
    return cells


def is_sep(line):
    return "-" in line and re.match(r"^\|?[\s:\-\|]+\|[\s:\-\|]*$", line.strip()) is not None


def text_len(cell):
    return len(re.sub(r"[`*]", "", cell))


def layout(table, rows, header, usable=16.6):
    from math import sqrt
    cols = len(header)
    weights = []
    for c in range(cols):
        longest = max([text_len(header[c])] + [text_len(r[c]) if c < len(r) else 0 for r in rows])
        weights.append(sqrt(max(longest, 4)))
    total = sum(weights)
    widths = [max(1.3, usable * w / total) for w in weights]
    scale = usable / sum(widths)
    widths = [w * scale for w in widths]

    tbl = table._tbl
    tblPr = tbl.tblPr
    tblPr.append(tblPr.makeelement(qn("w:tblLayout"), {qn("w:type"): "fixed"}))
    table.autofit = False

    grid = tbl.find(qn("w:tblGrid"))
    if grid is not None:
        for gc, w in zip(grid.findall(qn("w:gridCol")), widths):
            gc.set(qn("w:w"), str(int(round(w * 567))))

    for idx, row in enumerate(table.rows):
        trPr = row._tr.get_or_add_trPr()
        trPr.append(trPr.makeelement(qn("w:cantSplit"), {}))
        if idx == 0:
            trPr.append(trPr.makeelement(qn("w:tblHeader"), {}))
        for i, cell in enumerate(row.cells):
            cell.width = Cm(widths[i])


def build(src, dst):
    lines = open(src, encoding="utf-8").read().split("\n")
    doc = Document()

    sec = doc.sections[0]
    sec.top_margin = Cm(2.0)
    sec.bottom_margin = Cm(2.0)
    sec.left_margin = Cm(2.2)
    sec.right_margin = Cm(2.0)

    normal = doc.styles["Normal"]
    normal.font.name = LATIN
    normal.font.size = Pt(10.5)
    normal.element.rPr.rFonts.set(qn("w:eastAsia"), EA)
    normal.paragraph_format.space_after = Pt(4)
    normal.paragraph_format.line_spacing = 1.25

    for name, size in (("Heading 1", 18), ("Heading 2", 14), ("Heading 3", 12)):
        st = doc.styles[name]
        st.font.name = LATIN
        st.font.size = Pt(size)
        st.font.color.rgb = ACCENT
        st.element.rPr.rFonts.set(qn("w:eastAsia"), EA)
        st.paragraph_format.space_before = Pt(10)
        st.paragraph_format.space_after = Pt(5)

    i, n = 0, len(lines)
    title_done = False

    while i < n:
        line = lines[i].rstrip()
        s = line.strip()

        if not s or s == "---":
            i += 1
            continue

        if s.startswith("```"):
            i += 1
            block = []
            while i < n and not lines[i].strip().startswith("```"):
                block.append(lines[i].rstrip())
                i += 1
            i += 1
            for row in block:
                par = doc.add_paragraph()
                par.paragraph_format.space_after = Pt(0)
                par.paragraph_format.line_spacing = 1.0
                par.paragraph_format.left_indent = Cm(0.4)
                run = par.add_run(row if row else " ")
                set_font(run, ea=CODE, latin=CODE)
                run.font.size = Pt(8.5)
            doc.add_paragraph().paragraph_format.space_after = Pt(2)
            continue

        m = re.match(r"^(#{1,4})\s+(.*)$", s)
        if m:
            level, text = len(m.group(1)), m.group(2)
            if level == 1 and not title_done:
                p = doc.add_paragraph()
                p.paragraph_format.space_after = Pt(2)
                run = p.add_run(text)
                set_font(run)
                run.font.size = Pt(22)
                run.bold = True
                run.font.color.rgb = ACCENT
                title_done = True
            else:
                h = doc.add_heading(level=min(level, 3))
                add_inline(h, text, bold=True)
                for run in h.runs:
                    run.font.color.rgb = ACCENT
            i += 1
            continue

        if s.startswith("|") and i + 1 < n and is_sep(lines[i + 1]):
            header = split_row(lines[i])
            i += 2
            rows = []
            while i < n and lines[i].strip().startswith("|"):
                rows.append(split_row(lines[i]))
                i += 1
            table = doc.add_table(rows=1, cols=len(header))
            table.style = "Light Grid Accent 1"
            table.alignment = WD_TABLE_ALIGNMENT.CENTER
            for c, text in enumerate(header):
                cell = table.rows[0].cells[c]
                cell.text = ""
                add_inline(cell.paragraphs[0], text, size=9, bold=True)
            for row in rows:
                cells = table.add_row().cells
                for c in range(len(header)):
                    cells[c].text = ""
                    par = cells[c].paragraphs[0]
                    par.paragraph_format.space_after = Pt(0)
                    par.paragraph_format.line_spacing = 1.1
                    add_inline(par, row[c] if c < len(row) else "", size=9)
            layout(table, rows, header)
            doc.add_paragraph().paragraph_format.space_after = Pt(2)
            continue

        if s == ">":
            i += 1
            continue

        if s.startswith("> "):
            text = s[2:].strip()
            if text.startswith("- "):
                text = text[2:]
            par = doc.add_paragraph(style="Intense Quote")
            add_inline(par, text, size=10)
            i += 1
            continue

        m = re.match(r"^(\s*)[-*]\s+(.*)$", line)
        if m:
            par = doc.add_paragraph(style="List Bullet 2" if len(m.group(1)) >= 2 else "List Bullet")
            add_inline(par, m.group(2))
            i += 1
            continue

        m = re.match(r"^(\s*)\d+\.\s+(.*)$", line)
        if m:
            par = doc.add_paragraph(style="List Number")
            add_inline(par, m.group(2))
            i += 1
            continue

        par = doc.add_paragraph()
        add_inline(par, s)
        i += 1

    doc.save(dst)
    print("saved", dst)


if __name__ == "__main__":
    build(sys.argv[1], sys.argv[2])
