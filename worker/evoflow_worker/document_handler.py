"""Maintained document operations. JSON in/out; no model code, templates or executable expressions.

Launched by the native host in the shipped isolated Python runtime. Paths are already
approved by the native policy; links and unsafe document containers are rejected again.
"""

from __future__ import annotations

import hashlib
import json
import math
import os
import sys
import tempfile
import zipfile
from io import BytesIO
from pathlib import Path
from typing import Literal
from xml.sax.saxutils import escape

from pydantic import BaseModel, ConfigDict, Field


class Strict(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)


class Slide(Strict):
    title: str = Field(default="", max_length=300)
    text: str = Field(default="", max_length=8000)


class Sheet(Strict):
    name: str = Field(default="Sheet1", min_length=1, max_length=31)
    rows: list[list[str | int | float | bool | None]] = Field(default_factory=list, max_length=500)


class Edit(Strict):
    operation: Literal[
        "replace_text",
        "append_paragraph",
        "set_slide",
        "append_slide",
        "set_cell",
        "append_page",
        "set_title",
        "rotate_page",
        "delete_page",
        "insert_page",
        "crop_page",
        "stamp_text",
        "merge_pdf",
        "set_field",
    ]
    text: str = Field(default="", max_length=8000)
    find: str = Field(default="", max_length=1000)
    title: str = Field(default="", max_length=300)
    index: int = Field(default=0, ge=0, le=100)
    sheet: str = Field(default="Sheet1", max_length=31)
    cell: str = Field(default="A1", pattern=r"^[A-Z]{1,2}[1-9][0-9]{0,3}$")
    value: str | int | float | bool | None = None
    degrees: int = Field(default=90, ge=-360, le=360)
    rectangle: list[float] = Field(default_factory=list, max_length=4)
    x: float = Field(default=36, ge=0, le=10000)
    y: float = Field(default=36, ge=0, le=10000)
    font_size: int = Field(default=12, ge=6, le=72)
    source: str | None = Field(default=None, max_length=1000)
    field: str = Field(default="", max_length=300)


class DocumentRequest(Strict):
    operation: Literal["create", "inspect", "edit"]
    format: Literal["docx", "pptx", "xlsx", "pdf"]
    path: str = Field(min_length=1, max_length=1000)
    title: str = Field(default="", max_length=300)
    paragraphs: list[str] = Field(default_factory=list, max_length=100)
    slides: list[Slide] = Field(default_factory=list, max_length=50)
    sheets: list[Sheet] = Field(default_factory=list, max_length=20)
    edits: list[Edit] = Field(default_factory=list, max_length=100)


def check_container(path, format):
    if path.stat().st_size > 16_000_000:
        raise ValueError("Document exceeds 16 MB input limit")
    if format == "pdf":
        if path.read_bytes()[:5] != b"%PDF-":
            raise ValueError("Invalid PDF")
        return
    with zipfile.ZipFile(path) as archive:
        info = archive.infolist()
        if len(info) > 5000 or sum(x.file_size for x in info) > 32_000_000:
            raise ValueError("Office container exceeds expansion limit")
        for item in info:
            if "vbaproject" in item.filename.lower() or ".." in Path(item.filename).parts:
                raise ValueError("Macros and invalid container paths are unavailable")
            if item.filename.endswith(".rels") and b'TargetMode="External"' in archive.read(item):
                raise ValueError("External document relationships are unavailable")


def literal(value):
    if isinstance(value, float) and not math.isfinite(value):
        raise ValueError("Nonfinite number")
    if isinstance(value, str) and value.startswith(("=", "+", "-", "@")):
        return "'" + value  # XLSX text must never become an executable formula.
    return value


def pdf_page(title, paragraphs):
    from reportlab.lib.styles import getSampleStyleSheet
    from reportlab.platypus import Paragraph, SimpleDocTemplate, Spacer

    buffer = BytesIO()
    styles = getSampleStyleSheet()
    blocks = [Paragraph(escape(title), styles["Title"]), Spacer(1, 12)]
    for text in paragraphs:
        blocks.extend([Paragraph(escape(text), styles["BodyText"]), Spacer(1, 8)])
    SimpleDocTemplate(buffer, title=title).build(blocks)
    return buffer.getvalue()


def inspect_document(path, format):
    check_container(path, format)
    if format == "docx":
        from docx import Document

        doc = Document(path)
        data = {"paragraphs": [p.text[:2000] for p in doc.paragraphs[:100]], "tables": len(doc.tables)}
    elif format == "pptx":
        from pptx import Presentation

        doc = Presentation(path)
        data = {
            "slides": [
                {"text": [s.text[:2000] for s in slide.shapes if s.has_text_frame][:20]}
                for slide in list(doc.slides)[:50]
            ]
        }
    elif format == "xlsx":
        from openpyxl import load_workbook

        doc = load_workbook(path, read_only=True, data_only=False, keep_links=False)
        data = {
            "sheets": [
                {
                    "name": s.title,
                    "rows": [
                        list(r)
                        for r in s.iter_rows(
                            max_row=min(s.max_row or 1, 30),
                            max_col=min(s.max_column or 1, 20),
                            values_only=True,
                        )
                    ],
                }
                for s in list(doc)[:20]
            ]
        }
        doc.close()
    else:
        import pdfplumber

        with pdfplumber.open(path) as doc:
            if len(doc.pages) > 100:
                raise ValueError("PDF page limit exceeded")
            data = {
                "pages": len(doc.pages),
                "text": [(p.extract_text() or "")[:4000] for p in doc.pages[:10]],
            }
        from pypdf import PdfReader

        fields = PdfReader(path).get_fields() or {}
        data["fields"] = {
            name: str(field.get("/V", ""))[:1000]
            for name, field in list(fields.items())[:100]
            if not int(field.get("/Ff", 0)) & 8192
        }
    return data


def handle(request: DocumentRequest):
    path = Path(request.path)
    if not path.is_absolute() or path.suffix.lower() != "." + request.format:
        raise ValueError("Expected an absolute path with matching extension")
    for component in [path, *path.parents]:
        if component.is_symlink() or component.is_junction():
            raise ValueError("Linked paths are unavailable")
    if any(len(p) > 8000 for p in request.paragraphs):
        raise ValueError("Paragraph size exceeded")
    if request.operation == "inspect":
        return {
            "verified": True,
            "path": str(path),
            "format": request.format,
            "inspection": inspect_document(path, request.format),
        }
    if request.operation == "edit":
        check_container(path, request.format)
        if not request.edits:
            raise ValueError("Supply at least one structured edit")
    fmt = request.format
    if fmt == "docx":
        from docx import Document

        doc = Document(path) if request.operation == "edit" else Document()
        if request.operation == "create":
            if request.title:
                doc.add_heading(request.title, 0)
            for p in request.paragraphs:
                doc.add_paragraph(p)
        for edit in request.edits:
            if edit.operation == "append_paragraph":
                doc.add_paragraph(edit.text)
            elif edit.operation == "replace_text" and edit.find:
                changed = 0
                paragraphs = list(doc.paragraphs) + [
                    p for t in doc.tables for r in t.rows for c in r.cells for p in c.paragraphs
                ]
                for p in paragraphs:
                    if edit.find in p.text:
                        p.text = p.text.replace(edit.find, edit.text)
                        changed += 1
                if not changed:
                    raise ValueError("Requested text was not found")
            else:
                raise ValueError("Unsupported Word edit")
        save = doc.save
    elif fmt == "pptx":
        from pptx import Presentation

        doc = Presentation(path) if request.operation == "edit" else Presentation()

        def add_slide(title, text):
            slide = doc.slides.add_slide(doc.slide_layouts[1])
            slide.shapes.title.text = title
            slide.placeholders[1].text = text

        if request.operation == "create":
            for slide in request.slides or [Slide(title=request.title, text="\n".join(request.paragraphs))]:
                add_slide(slide.title, slide.text)
        for edit in request.edits:
            if edit.operation == "append_slide":
                add_slide(edit.title, edit.text)
            elif edit.operation == "set_slide" and edit.index < len(doc.slides):
                slide = doc.slides[edit.index]
                slide.shapes.title.text = edit.title
                body = next(
                    (s for s in slide.placeholders if s != slide.shapes.title and s.has_text_frame), None
                )
                if body is None:
                    raise ValueError("Slide has no supported body placeholder")
                body.text = edit.text
            else:
                raise ValueError("Unsupported PowerPoint edit")
        save = doc.save
    elif fmt == "xlsx":
        from openpyxl import Workbook, load_workbook

        doc = load_workbook(path, keep_links=False) if request.operation == "edit" else Workbook()
        if request.operation == "create":
            doc.remove(doc.active)
            for sheet in request.sheets or [Sheet()]:
                s = doc.create_sheet(sheet.name)
                for row in sheet.rows:
                    if len(row) > 100:
                        raise ValueError("Column limit exceeded")
                    s.append([literal(v) for v in row])
        for edit in request.edits:
            if edit.operation != "set_cell" or edit.sheet not in doc.sheetnames:
                raise ValueError("Unsupported Excel edit or missing sheet")
            doc[edit.sheet][edit.cell] = literal(edit.value)
        save = doc.save
    else:
        from pypdf import PdfReader, PdfWriter
        from pypdf.generic import ContentStream, TextStringObject

        doc = PdfWriter()
        if request.operation == "edit":
            source = PdfReader(path)
            if source.is_encrypted or len(source.pages) > 100:
                raise ValueError("Encrypted or oversized PDF")
            doc = PdfWriter(clone_from=str(path))
        else:
            doc.append(PdfReader(BytesIO(pdf_page(request.title, request.paragraphs))))
        for edit in request.edits:
            if edit.operation == "append_page":
                doc.append(PdfReader(BytesIO(pdf_page(edit.title, [edit.text]))))
            elif edit.operation == "set_title":
                doc.add_metadata({"/Title": edit.title})
            elif edit.operation == "merge_pdf" and edit.source:
                source_path = Path(edit.source)
                if (
                    not source_path.is_absolute()
                    or source_path.suffix.lower() != ".pdf"
                    or any(p.is_symlink() or p.is_junction() for p in [source_path, *source_path.parents])
                ):
                    raise ValueError("Expected a reviewed local PDF source")
                check_container(source_path, "pdf")
                source_doc = PdfReader(source_path)
                if source_doc.is_encrypted or len(source_doc.pages) + len(doc.pages) > 100:
                    raise ValueError("PDF merge page limit or encryption")
                doc.append(source_doc)
            elif edit.operation == "insert_page":
                if edit.index > len(doc.pages):
                    raise ValueError("Insert page index is outside the document")
                insert = PdfReader(BytesIO(pdf_page(edit.title, [edit.text])))
                doc.insert_page(insert.pages[0], edit.index)
            elif edit.operation in {
                "rotate_page",
                "delete_page",
                "crop_page",
                "stamp_text",
                "replace_text",
                "set_field",
            }:
                if edit.index >= len(doc.pages):
                    raise ValueError("PDF page index is outside the document")
                page = doc.pages[edit.index]
                if edit.operation == "rotate_page":
                    if edit.degrees % 90:
                        raise ValueError("Rotation must be a multiple of 90")
                    page.rotate(edit.degrees)
                elif edit.operation == "delete_page":
                    if len(doc.pages) <= 1:
                        raise ValueError("Cannot delete the final page")
                    del doc.pages[edit.index]
                elif edit.operation == "crop_page":
                    if len(edit.rectangle) != 4 or not all(math.isfinite(v) for v in edit.rectangle):
                        raise ValueError("Supply four finite crop coordinates")
                    x1, y1, x2, y2 = edit.rectangle
                    if not (
                        0 <= x1 < x2 <= float(page.mediabox.width)
                        and 0 <= y1 < y2 <= float(page.mediabox.height)
                    ):
                        raise ValueError("Crop rectangle must fit the page")
                    page.cropbox.lower_left, page.cropbox.upper_right = (x1, y1), (x2, y2)
                elif edit.operation == "stamp_text":
                    from reportlab.pdfgen import canvas

                    overlay = BytesIO()
                    stamp = canvas.Canvas(
                        overlay, pagesize=(float(page.mediabox.width), float(page.mediabox.height))
                    )
                    stamp.setFont("Helvetica", edit.font_size)
                    stamp.drawString(edit.x, edit.y, edit.text)
                    stamp.save()
                    page.merge_page(PdfReader(BytesIO(overlay.getvalue())).pages[0])
                elif edit.operation == "set_field":
                    if not edit.field or edit.field not in (doc.get_fields() or {}):
                        raise ValueError("PDF form field not found")
                    if int(doc.get_fields()[edit.field].get("/Ff", 0)) & 8192:
                        raise ValueError("Password PDF fields are protected")
                    doc.update_page_form_field_values(
                        page,
                        {edit.field: str(edit.value if edit.value is not None else "")},
                        auto_regenerate=False,
                    )
                else:
                    if not edit.find or not edit.text.isascii():
                        raise ValueError(
                            "PDF text replacement requires a find value and ASCII replacement compatible with the existing font"
                        )
                    stream = ContentStream(page.get_contents(), doc)
                    changed = False
                    for operands, operator in stream.operations:
                        if operator in (b"Tj", b"'", b'"'):
                            slot = len(operands) - 1
                            if isinstance(operands[slot], TextStringObject) and edit.find in operands[slot]:
                                operands[slot] = TextStringObject(
                                    operands[slot].replace(edit.find, edit.text)
                                )
                                changed = True
                        elif operator == b"TJ":
                            for index, part in enumerate(operands[0]):
                                if isinstance(part, TextStringObject) and edit.find in part:
                                    operands[0][index] = TextStringObject(part.replace(edit.find, edit.text))
                                    changed = True
                    if not changed:
                        raise ValueError(
                            "Text is split, encoded or not found; use stamp_text or edit the original source document"
                        )
                    page.replace_contents(stream)
            else:
                raise ValueError("Unsupported PDF edit")
        save = doc.write
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, suffix=path.suffix, delete=False) as stream:
            temporary = Path(stream.name)
        save(str(temporary))
        inspection = inspect_document(temporary, fmt)  # Reopen with a format reader before publishing.
        if fmt == "pdf":
            for edit in request.edits:
                if edit.operation == "replace_text" and edit.text not in "\n".join(inspection["text"]):
                    raise ValueError("PDF replacement did not pass independent text readback")
                if edit.operation == "set_field" and inspection["fields"].get(edit.field) != str(
                    edit.value if edit.value is not None else ""
                ):
                    raise ValueError("PDF form update did not pass independent readback")
        if temporary.stat().st_size > 16_000_000:
            raise ValueError("Output exceeds document size limit")
        os.replace(temporary, path)
        return {
            "verified": True,
            "path": str(path),
            "format": fmt,
            "bytes": path.stat().st_size,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "inspection": inspection,
        }
    finally:
        if temporary and temporary.exists():
            temporary.unlink()


if __name__ == "__main__":
    try:
        data = sys.stdin.read(110001)
        if len(data) > 110000:
            raise ValueError("Input budget exceeded")
        result = handle(DocumentRequest.model_validate_json(data))
        print(json.dumps(result, default=str))
    except Exception:
        print('{"verified":false,"error":"Document validation or operation failed"}')
        sys.exit(1)
