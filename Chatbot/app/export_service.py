from __future__ import annotations

import os
import re
import uuid
from datetime import datetime
from pathlib import Path
from typing import Optional

from .config import get_settings


def _safe_filename_stem(title: str) -> str:
    stem = re.sub(r"[^\w\-]+", "_", (title or "bao_cao").strip(), flags=re.UNICODE)
    stem = stem.strip("_") or "bao_cao"
    return stem[:60]


def _timestamp() -> str:
    return datetime.now().strftime("%Y%m%d_%H%M%S")


def _ensure_export_path(ext: str, title: str = "LibraryHub_BaoCao") -> tuple[str, Path, str]:
    settings = get_settings()
    settings.ensure_directories()
    filename = f"{_safe_filename_stem(title)}_{_timestamp()}_{uuid.uuid4().hex[:6]}.{ext}"
    path = settings.export_path / filename
    base = settings.export_base_url.rstrip("/")
    url = f"{base}/{filename}"
    return filename, path, url


def export_xlsx(content: str, title: str = "Báo cáo LibraryHub") -> tuple[str, Path, str]:
    from openpyxl import Workbook
    from openpyxl.styles import Alignment, Font

    filename, path, url = _ensure_export_path("xlsx", title)
    wb = Workbook()
    ws = wb.active
    ws.title = "BaoCao"

    ws.merge_cells("A1:D1")
    ws["A1"] = title
    ws["A1"].font = Font(bold=True, size=14)
    ws["A1"].alignment = Alignment(horizontal="center", vertical="center")

    ws.merge_cells("A2:D2")
    ws["A2"] = f"Thời gian xuất: {datetime.now().strftime('%d/%m/%Y %H:%M:%S')} | Hệ thống: LibraryHub"
    ws["A2"].alignment = Alignment(horizontal="center")

    # Nội dung: đưa từng dòng vào cột A
    row_idx = 4
    for line in (content or "").splitlines():
        ws.cell(row=row_idx, column=1, value=line)
        ws.cell(row=row_idx, column=1).alignment = Alignment(wrap_text=True, vertical="top")
        row_idx += 1

    ws.column_dimensions["A"].width = 120
    ws.column_dimensions["B"].width = 25
    ws.column_dimensions["C"].width = 25
    ws.column_dimensions["D"].width = 25
    wb.save(path)
    return filename, path, url


def export_docx(content: str, title: str = "Báo cáo LibraryHub") -> tuple[str, Path, str]:
    from docx import Document
    from docx.enum.text import WD_ALIGN_PARAGRAPH
    from docx.shared import Pt

    filename, path, url = _ensure_export_path("docx", title)
    doc = Document()

    heading = doc.add_paragraph()
    run = heading.add_run("LibraryHub")
    run.bold = True
    run.font.size = Pt(12)
    heading.alignment = WD_ALIGN_PARAGRAPH.CENTER

    t = doc.add_paragraph()
    tr = t.add_run(title)
    tr.bold = True
    tr.font.size = Pt(16)
    t.alignment = WD_ALIGN_PARAGRAPH.CENTER

    meta = doc.add_paragraph(
        f"Thời gian xuất: {datetime.now().strftime('%d/%m/%Y %H:%M:%S')}"
    )
    meta.alignment = WD_ALIGN_PARAGRAPH.CENTER

    doc.add_paragraph("")
    for block in (content or "").split("\n\n"):
        p = doc.add_paragraph(block.strip())
        p_format = p.paragraph_format
        p_format.space_after = Pt(8)

    doc.save(path)
    return filename, path, url


def _find_vietnamese_font() -> Optional[str]:
    candidates = [
        r"C:\Windows\Fonts\arial.ttf",
        r"C:\Windows\Fonts\tahoma.ttf",
        r"C:\Windows\Fonts\times.ttf",
        r"C:\Windows\Fonts\segoeui.ttf",
        r"C:\Windows\Fonts\calibri.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
    ]
    for p in candidates:
        if os.path.exists(p):
            return p
    return None


def export_pdf(content: str, title: str = "Báo cáo LibraryHub") -> tuple[str, Path, str]:
    from reportlab.lib.pagesizes import A4
    from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
    from reportlab.lib.enums import TA_CENTER
    from reportlab.lib.units import cm
    from reportlab.platypus import Paragraph, SimpleDocTemplate, Spacer
    from reportlab.pdfbase import pdfmetrics
    from reportlab.pdfbase.ttfonts import TTFont

    filename, path, url = _ensure_export_path("pdf", title)
    font_name = "Helvetica"
    font_path = _find_vietnamese_font()
    if font_path:
        try:
            pdfmetrics.registerFont(TTFont("VNFont", font_path))
            font_name = "VNFont"
        except Exception:
            font_name = "Helvetica"

    doc = SimpleDocTemplate(
        str(path),
        pagesize=A4,
        leftMargin=2 * cm,
        rightMargin=2 * cm,
        topMargin=2 * cm,
        bottomMargin=2 * cm,
        title=title,
        author="LibraryHub",
    )
    styles = getSampleStyleSheet()
    title_style = ParagraphStyle(
        "VNTitle",
        parent=styles["Title"],
        fontName=font_name,
        fontSize=16,
        leading=20,
        alignment=TA_CENTER,
        spaceAfter=12,
    )
    meta_style = ParagraphStyle(
        "VNMeta",
        parent=styles["Normal"],
        fontName=font_name,
        fontSize=10,
        leading=14,
        alignment=TA_CENTER,
        spaceAfter=12,
    )
    body_style = ParagraphStyle(
        "VNBody",
        parent=styles["Normal"],
        fontName=font_name,
        fontSize=11,
        leading=15,
        spaceAfter=6,
    )

    story = [
        Paragraph("LibraryHub", title_style),
        Paragraph(_escape_xml(title), title_style),
        Paragraph(
            _escape_xml(f"Thời gian xuất: {datetime.now().strftime('%d/%m/%Y %H:%M:%S')}"),
            meta_style,
        ),
        Spacer(1, 0.3 * cm),
    ]

    for para in (content or "").split("\n\n"):
        text = _escape_xml(para.strip()).replace("\n", "<br/>")
        if text:
            story.append(Paragraph(text, body_style))
            story.append(Spacer(1, 0.2 * cm))

    doc.build(story)
    return filename, path, url


def _escape_xml(text: str) -> str:
    return (
        (text or "")
        .replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
    )


def create_export(export_type: str, content: str, title: str = "Báo cáo LibraryHub") -> dict[str, str]:
    et = (export_type or "").lower().strip()
    safe_title = (title or "Báo cáo LibraryHub").strip() or "Báo cáo LibraryHub"
    safe_content = (content or "").strip()
    # Giới hạn kích thước nội dung để tránh treo/OOM khi xuất file
    if len(safe_content) > 200_000:
        safe_content = safe_content[:200_000] + "\n\n...[Nội dung đã được rút gọn để xuất file]..."
    if not safe_content:
        safe_content = "Không có nội dung để xuất."
    if et == "xlsx":
        filename, _, url = export_xlsx(safe_content, safe_title)
    elif et == "docx":
        filename, _, url = export_docx(safe_content, safe_title)
    elif et == "pdf":
        filename, _, url = export_pdf(safe_content, safe_title)
    else:
        raise ValueError(f"Loại file xuất không hỗ trợ: {export_type}")
    return {"type": et, "filename": filename, "url": url}
