#!/usr/bin/env python3
"""Generate the independently authored CC0 mixed-format benchmark, offline.

Python 3.10+ standard library only. Gold and query splits below were authored
before rendering or extraction. No product parser, formula engine, network, font
file, package install, or time-dependent metadata is used.
"""
from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import io
import json
from pathlib import Path
import struct
import textwrap
import zipfile
import zlib
from xml.sax.saxutils import escape, quoteattr

VERSION = 1
ZIP_EPOCH = (2000, 1, 1, 0, 0, 0)
DEFAULT_OUTPUT = Path(__file__).resolve().parents[1] / "benchmarks" / "mixed-formats"


def fact(fid, root, anchors, kind, chain=(), heading=None, notes=None, **location):
    item = {"id": fid, "root": root, "chain": list(chain), "anchors": anchors,
            "location": {"kind": kind, **location}}
    if heading is not None:
        item["heading"] = heading
    if notes is not None:
        item["notes"] = notes
    return item


# AUTHOR GOLD: declared independently, before corpus construction or extraction.
# These labels are semantic expectations, including explicitly identified gaps.
FACTS = [
    fact("launch_owner", "launch_docx", ["Sofia Mendonça", "Aurora field launch"], "structure", structure_path="document/paragraph[2]", heading="Aurora field launch"),
    fact("launch_date", "launch_docx", ["2026-11-19"], "structure", structure_path="document/paragraph[3]", heading="Aurora field launch"),
    fact("launch_budget", "launch_docx", ["Field installation", "18400"], "structure", structure_path="document/table[1]", heading="Aurora field launch"),
    fact("launch_long_paragraph", "launch_docx", ["LANTERN-82"], "structure", structure_path="document/paragraph[5]", heading="Closing checklist", notes="Answer is near the end of a single long paragraph, after independently authored distractor sentences."),
    fact("launch_long_table", "launch_docx", ["MARLIN-440"], "structure", structure_path="document/table[2]", heading="Closing checklist", notes="Answer is in the final row of a 61-row table including its header."),
    fact("coimbra_owner", "coimbra_docx", ["Inês Gonçalves", "2026-12-03"], "structure", structure_path="document/paragraph[2]", heading="Entrega em Coimbra"),
    fact("coimbra_room", "coimbra_docx", ["Sala Açucena", "28"], "structure", structure_path="document/table[1]", heading="Entrega em Coimbra"),
    fact("order_quantity", "operations_xlsx", ["Maré sensor", "48"], "sheet", sheet="Orders", cell_range="A2:F2"),
    fact("order_iso_date", "operations_xlsx", ["2026-11-07", "João Ribeiro"], "sheet", sheet="Orders", cell_range="A2:F2"),
    fact("formula_cached", "operations_xlsx", ["Published total", "1250"], "sheet", sheet="Totals", cell_range="A2:B2", notes="B2 stores formula SUM(Orders!F2:F3) and author-supplied numeric cached value 1250. Do not evaluate formulas to generate gold."),
    fact("formula_uncached", "operations_xlsx", ["Uncached audit", "SUM(B2:B3)"], "sheet", sheet="Totals", cell_range="A4:B4", notes="B4 stores formula SUM(B2:B3), without a v element. Only the authored formula string is gold; no numeric result is asserted."),
    fact("styled_date_iso", "schedule_xlsx", ["2026-11-19"], "sheet", sheet="Schedules", cell_range="A2:B2", notes="B2 is raw numeric 46345 with number format yyyy-mm-dd in the Excel 1900 system. Human calendar interpretation is 2026-11-19. It is intentionally absent as literal text, exposing date-format gaps."),
    fact("styled_date_raw", "schedule_xlsx", ["46345"], "sheet", sheet="Schedules", cell_range="A2:B2", notes="Raw-serial control for the same styled date as styled_date_iso; a raw-only extractor should pass this and fail the human-date anchor."),
    fact("sparse_latch", "schedule_xlsx", ["Emergency latch", "17"], "sheet", sheet="Sparse", cell_range="A40:G40", notes="Only A40 and F40 have values; G40 is an explicit blank. Rows and intermediate cells are sparse, not filled with zeros."),
    fact("mail_headers_unicode", "mixed_eml", ["Sofia Mendonça", "Revisão Azulejo"], "email_part", email_part="headers", notes="From display name and Subject are encoded RFC 2047 UTF-8 headers."),
    fact("mail_body_meeting", "mixed_eml", ["Jadebrief", "9:20"], "email_part", email_part="body", notes="Plain and HTML alternatives express the same root fact; attachment answers are excluded from both alternatives."),
    fact("attachment_cabinet", "mixed_eml", ["Azulejo", "AX-204"], "structure", chain=["annex-brief.docx"], structure_path="document/paragraph[2]", heading="Cabinet allocation"),
    fact("attachment_filters", "mixed_eml", ["Shipment filters", "73"], "sheet", chain=["annex-stock.xlsx"], sheet="Dispatch", cell_range="A2:B2"),
    fact("attachment_reserve", "mixed_eml", ["R$ 6.840", "reserve"], "page", chain=["annex-approval.pdf"], page=1),
    fact("attachment_phrase", "mixed_eml", ["NIGHTJAR-52"], "document", chain=["annex-note.txt"]),
    fact("html_mail_room", "html_only_eml", ["Sala Ipê"], "email_part", email_part="body", notes="HTML-only MIME body, with no plain-text alternative."),
    fact("html_mail_action", "html_only_eml", ["levar o medidor azul"], "email_part", email_part="body"),
    fact("duplicate_first", "duplicate_eml", ["Birch-314"], "document", chain=["status.txt"], notes="First MIME attachment named status.txt, Content-ID <birch-status@fixtures.invalid>. The second has the identical filename; content anchors distinguish occurrences."),
    fact("duplicate_second", "duplicate_eml", ["Willow-271"], "document", chain=["status.txt"], notes="Second MIME attachment named status.txt, Content-ID <willow-status@fixtures.invalid>. Do not collapse children by filename."),
    fact("pdf_shift", "native_pdf", ["Cedar vessel", "06:40"], "page", page=1),
    fact("pdf_pressure_pt", "native_pdf", ["São Tomé", "4.2 bar"], "page", page=2),
    fact("text_radio", "radio_txt", ["channel 14"], "document"),
    fact("markdown_torque", "maintenance_md", ["Torquímetro Coral", "9 N m"], "structure", structure_path="html/block[2]", heading="Ajustes da oficina", notes="Authored Markdown H1 is block 1; the following torque paragraph is block 2 in the stable HTML block representation."),
    fact("html_capacity", "venue_html", ["Juniper", "36"], "structure", structure_path="html/table[1]", heading="Workshop seating"),
    fact("csv_stock", "inventory_csv", ["Viseu", "65"], "sheet", sheet="CSV", cell_range="A2:D2", notes="CSV header is row 1; Viseu is row 2. All column positions are authored. CSV is the stable sheet label for this file format."),
    fact("rtf_signoff", "signoff_rtf", ["Carla Araújo", "2026-10-27"], "document"),
    fact("scan_code", "scan_png", ["ORCHID-619"], "image_frame", image_frame=1, notes="Image-only exclusive fact; no text metadata and no sidecar transcript is indexed. Root requires OCR."),
    fact("long_email_seal", "long_eml", ["KITE-907"], "email_part", email_part="body", notes="Answer near the end of a single long plain-text email body, after dispatch-log distractors."),
    fact("attachment_scan_code", "scanned_eml", ["ORCHID-690"], "image_frame", chain=["dispatch-scan.png"], image_frame=1, notes="Image-only exclusive attachment fact, distinct from the standalone scan. Root requires OCR; neither headers nor body repeat the code."),
]

# Each tuple is (id, natural question, exact lexical terms, relevant facts,
# language, scope, negative). Split labels are assigned by authored row parity,
# giving 23 development and 23 holdout queries before any measurement.
QUERY_ROWS = [
    ("q01", "Who owns the Aurora field launch?", ["Aurora", "launch"], ["launch_owner"], "en", "any", False),
    ("q02", "On what date will the Aurora installation happen?", ["Aurora", "installation"], ["launch_date"], "en", "any", False),
    ("q03", "What is the budget for field installation?", ["Field", "installation"], ["launch_budget"], "en", "any", False),
    ("q04", "Which rendezvous code closes the Aurora checklist?", ["rendezvous", "Aurora"], ["launch_long_paragraph"], "en", "any", False),
    ("q05", "Which serial identifies the final calibration crate?", ["final", "calibration", "crate"], ["launch_long_table"], "en", "any", False),
    ("q06", "Quem recebe a entrega em Coimbra e em que data?", ["Coimbra", "recebe"], ["coimbra_owner"], "pt", "any", False),
    ("q07", "Quantos lugares tem a Sala Açucena?", ["Sala", "Açucena"], ["coimbra_room"], "pt", "any", False),
    ("q08", "How many Maré sensors were ordered?", ["Maré", "sensor"], ["order_quantity"], "en", "any", False),
    ("q09", "Qual a data ISO do pedido de João Ribeiro?", ["João", "Ribeiro"], ["order_iso_date"], "pt", "any", False),
    ("q10", "What cached number is saved for the published total?", ["Published", "total"], ["formula_cached"], "en", "any", False),
    ("q11", "Which formula is stored for the uncached audit?", ["Uncached", "audit"], ["formula_uncached"], "en", "any", False),
    ("q12", "What calendar date is Cedar calibration scheduled?", ["Cedar", "calibration"], ["styled_date_iso"], "en", "any", False),
    ("q13", "What raw Excel serial is stored for Cedar calibration?", ["Cedar", "calibration"], ["styled_date_raw"], "en", "any", False),
    ("q14", "How many emergency latches are listed in the sparse sheet?", ["Emergency", "latch"], ["sparse_latch"], "en", "any", False),
    ("q15", "Quem enviou o email Revisão Azulejo?", ["Revisão", "Azulejo"], ["mail_headers_unicode"], "pt", "root_only", False),
    ("q16", "When does the Jadebrief meeting begin?", ["Jadebrief", "meeting"], ["mail_body_meeting"], "en", "root_only", False),
    ("q17", "Which cabinet is allocated to the Azulejo team in the attachment?", ["Azulejo", "cabinet"], ["attachment_cabinet"], "en", "attachments_only", False),
    ("q18", "How many filters are in the attachment's shipment?", ["Shipment", "filters"], ["attachment_filters"], "en", "attachments_only", False),
    ("q19", "Qual é o valor da reserva aprovada no PDF anexo?", ["reserve", "approved"], ["attachment_reserve"], "pt", "attachments_only", False),
    ("q20", "What is the access phrase in the attached note?", ["access", "phrase"], ["attachment_phrase"], "en", "attachments_only", False),
    ("q21", "Em que sala será a sessão descrita no email só HTML?", ["sessão", "sala"], ["html_mail_room"], "pt", "root_only", False),
    ("q22", "O que é preciso levar para a sessão da Sala Ipê?", ["Sala", "Ipê", "levar"], ["html_mail_action"], "pt", "root_only", False),
    ("q23", "What docket belongs to Birch in the first status attachment?", ["Birch", "docket"], ["duplicate_first"], "en", "attachments_only", False),
    ("q24", "What docket belongs to Willow in the second status attachment?", ["Willow", "docket"], ["duplicate_second"], "en", "attachments_only", False),
    ("q25", "When does the Cedar vessel shift start?", ["Cedar", "vessel", "shift"], ["pdf_shift"], "en", "any", False),
    ("q26", "Qual a pressão de teste em São Tomé?", ["São", "Tomé"], ["pdf_pressure_pt"], "pt", "any", False),
    ("q27", "Which radio channel does the harbor watch use?", ["harbor", "watch", "radio"], ["text_radio"], "en", "any", False),
    ("q28", "Qual o ajuste do Torquímetro Coral?", ["Torquímetro", "Coral"], ["markdown_torque"], "pt", "any", False),
    ("q29", "What is the Juniper workshop seating capacity?", ["Juniper", "Seats"], ["html_capacity"], "en", "any", False),
    ("q30", "Quantas unidades tem o depósito de Viseu?", ["Viseu"], ["csv_stock"], "pt", "any", False),
    ("q31", "Em que data Carla Araújo assinou a revisão?", ["Carla", "Araújo"], ["rtf_signoff"], "pt", "any", False),
    ("q32", "What dispatch code is printed on the standalone scanned card?", ["DISPATCH", "CODE"], ["scan_code"], "en", "root_only", False),
    ("q33", "Which emergency seal is specified at the end of the quay dispatch email?", ["emergency", "seal"], ["long_email_seal"], "en", "root_only", False),
    ("q34", "Quem é responsável pelo lançamento de campo Aurora?", ["Aurora"], ["launch_owner"], "pt", "any", False),
    ("q35", "Qual é o código de encontro no fim da lista de encerramento?", ["rendezvous"], ["launch_long_paragraph"], "pt", "any", False),
    ("q36", "What day does João Ribeiro expect delivery?", ["João", "Ribeiro"], ["order_iso_date"], "en", "any", False),
    ("q37", "Qual armário foi reservado à equipa Azulejo no anexo?", ["Azulejo", "cabinet"], ["attachment_cabinet"], "pt", "attachments_only", False),
    ("q38", "How much money was set aside in the approval annex?", ["reserve"], ["attachment_reserve"], "en", "attachments_only", False),
    ("q39", "Que senha de acesso está na nota anexa?", ["access", "phrase"], ["attachment_phrase"], "pt", "attachments_only", False),
    ("q40", "Which quartzotter permit appears in the records?", ["quartzotter"], [], "en", "any", True),
    ("q41", "Qual relatório menciona nebulafinch?", ["nebulafinch"], [], "pt", "any", True),
    ("q42", "What is the NIGHTJAR access phrase in root documents only?", ["NIGHTJAR"], [], "en", "root_only", True),
    ("q43", "Qual anexo menciona a reunião Jadebrief?", ["Jadebrief"], [], "pt", "attachments_only", True),
    ("q44", "Which root document supplies the Willow docket?", ["Willow"], [], "en", "root_only", True),
    ("q45", "What dispatch code is printed in the attached scan?", ["DISPATCH", "CODE"], ["attachment_scan_code"], "en", "attachments_only", False),
    ("q46", "Qual é o código de despacho na imagem anexa?", ["DISPATCH", "CODE"], ["attachment_scan_code"], "pt", "attachments_only", False),
]
QUERIES = [{"id": r[0], "text": r[1], "terms": r[2], "relevant": r[3],
            "language": r[4], "scope": r[5], "negative": r[6],
            "split": "development" if i % 2 == 0 else "holdout"}
           for i, r in enumerate(QUERY_ROWS)]


def xml(text):
    return ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n' + text).encode("utf-8")


def stable_zip(parts):
    """ZIP_STORED also avoids compression-library version dependence."""
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_STORED) as archive:
        for name, data in sorted(parts.items()):
            info = zipfile.ZipInfo(name, date_time=ZIP_EPOCH)
            info.compress_type = zipfile.ZIP_STORED
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data)
    return output.getvalue()


def docx(elements, title):
    w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
    body = []
    def para(text, heading=0):
        style = f'<w:pPr><w:pStyle w:val="Heading{heading}"/></w:pPr>' if heading else ""
        return f'<w:p>{style}<w:r><w:t xml:space="preserve">{escape(text)}</w:t></w:r></w:p>'
    for kind, value in elements:
        if kind in ("p", "h1", "h2"):
            body.append(para(value, 1 if kind == "h1" else 2 if kind == "h2" else 0))
        elif kind == "table":
            rows = ''.join('<w:tr>' + ''.join('<w:tc><w:tcPr><w:tcW w:w="2500" w:type="dxa"/></w:tcPr>' + para(str(cell)) + '</w:tc>' for cell in row) + '</w:tr>' for row in value)
            body.append('<w:tbl><w:tblPr><w:tblW w:w="0" w:type="auto"/><w:tblBorders><w:top w:val="single" w:sz="4"/><w:left w:val="single" w:sz="4"/><w:bottom w:val="single" w:sz="4"/><w:right w:val="single" w:sz="4"/><w:insideH w:val="single" w:sz="4"/><w:insideV w:val="single" w:sz="4"/></w:tblBorders></w:tblPr><w:tblGrid><w:gridCol w:w="2500"/><w:gridCol w:w="2500"/><w:gridCol w:w="2500"/></w:tblGrid>' + rows + '</w:tbl>')
        else:
            raise ValueError(kind)
    body.append('<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1134" w:right="1134" w:bottom="1134" w:left="1134"/></w:sectPr>')
    styles = '<w:styles xmlns:w="' + w + '"><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Liberation Sans" w:hAnsi="Liberation Sans"/><w:sz w:val="22"/><w:color w:val="000000"/></w:rPr></w:rPrDefault></w:docDefaults><w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:pPr><w:spacing w:after="120"/></w:pPr></w:style>'
    for level in (1, 2):
        styles += f'<w:style w:type="paragraph" w:styleId="Heading{level}"><w:name w:val="Heading {level}"/><w:basedOn w:val="Normal"/><w:pPr><w:keepNext/><w:spacing w:before="160" w:after="120"/><w:outlineLvl w:val="{level-1}"/></w:pPr><w:rPr><w:b/><w:sz w:val="{32 if level == 1 else 26}"/><w:color w:val="000000"/></w:rPr></w:style>'
    styles += '</w:styles>'
    parts = {
        "[Content_Types].xml": xml('<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/></Types>'),
        "_rels/.rels": xml('<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>'),
        "word/document.xml": xml(f'<w:document xmlns:w="{w}"><w:body>' + ''.join(body) + '</w:body></w:document>'),
        "word/styles.xml": xml(styles),
        "word/_rels/document.xml.rels": xml('<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>'),
        "docProps/core.xml": xml('<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:title>' + escape(title) + '</dc:title><dc:creator>Context Mole synthetic fixture author</dc:creator><dcterms:created xsi:type="dcterms:W3CDTF">2000-01-01T00:00:00Z</dcterms:created><dcterms:modified xsi:type="dcterms:W3CDTF">2000-01-01T00:00:00Z</dcterms:modified></cp:coreProperties>'),
    }
    return stable_zip(parts)


def cell(reference, value=None, style=None, formula=None, cached=None):
    attributes = ' r=' + quoteattr(reference)
    if style is not None:
        attributes += f' s="{style}"'
    if formula is not None:
        return '<c' + attributes + '><f>' + escape(formula) + '</f>' + (f'<v>{cached}</v>' if cached is not None else '') + '</c>'
    if value is None:
        return '<c' + attributes + '/>'
    if isinstance(value, (int, float)):
        return '<c' + attributes + f'><v>{value}</v></c>'
    return '<c' + attributes + ' t="inlineStr"><is><t xml:space="preserve">' + escape(value) + '</t></is></c>'


def xlsx(sheets):
    s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
    overrides = ['<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>', '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>']
    sheet_entries, relations, parts = [], [], {}
    for index, (name, rows) in enumerate(sheets, 1):
        sheet_entries.append(f'<sheet name={quoteattr(name)} sheetId="{index}" r:id="rId{index}"/>')
        relations.append(f'<Relationship Id="rId{index}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{index}.xml"/>')
        overrides.append(f'<Override PartName="/xl/worksheets/sheet{index}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>')
        content = ''.join(f'<row r="{number}">' + ''.join(cells) + '</row>' for number, cells in rows)
        parts[f"xl/worksheets/sheet{index}.xml"] = xml(f'<worksheet xmlns="{s}"><sheetViews><sheetView workbookViewId="0"/></sheetViews><sheetFormatPr defaultRowHeight="18"/><cols><col min="1" max="1" width="28" customWidth="1"/><col min="2" max="7" width="22" customWidth="1"/></cols><sheetData>{content}</sheetData></worksheet>')
    parts.update({
        "[Content_Types].xml": xml('<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>' + ''.join(overrides) + '</Types>'),
        "_rels/.rels": xml('<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>'),
        "xl/workbook.xml": xml(f'<workbook xmlns="{s}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><workbookPr date1904="0"/><sheets>' + ''.join(sheet_entries) + '</sheets><calcPr calcId="0" fullCalcOnLoad="0" forceFullCalc="0"/></workbook>'),
        "xl/_rels/workbook.xml.rels": xml('<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' + ''.join(relations) + '<Relationship Id="rIdStyles" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>'),
        "xl/styles.xml": xml(f'<styleSheet xmlns="{s}"><numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy-mm-dd"/></numFmts><fonts count="1"><font><sz val="11"/><name val="Liberation Sans"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>'),
    })
    return stable_zip(parts)


def pdf(pages):
    """Native, two-dimensional PDF using standard Helvetica and WinAnsi text."""
    def pdf_string(value):
        encoded = value.encode("cp1252")
        return ''.join(chr(c) if 32 <= c <= 126 and c not in (40, 41, 92) else f'\\{c:03o}' for c in encoded)
    objects = [b'', b'', b'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>']
    page_ids = []
    for lines in pages:
        page_id, stream_id = len(objects) + 1, len(objects) + 2
        page_ids.append(page_id)
        objects.append(f'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {stream_id} 0 R >>'.encode("ascii"))
        commands = ['BT /F1 14 Tf 48 778 Td']
        for index, line in enumerate(lines):
            if index:
                commands.append('0 -30 Td')
            commands.append(f'({pdf_string(line)}) Tj')
        commands.append('ET')
        stream = '\n'.join(commands).encode("ascii")
        objects.append(f'<< /Length {len(stream)} >>\nstream\n'.encode("ascii") + stream + b'\nendstream')
    objects[0] = b'<< /Type /Catalog /Pages 2 0 R >>'
    objects[1] = f'<< /Type /Pages /Count {len(page_ids)} /Kids ['.encode("ascii") + ' '.join(f'{p} 0 R' for p in page_ids).encode("ascii") + b'] >>'
    output = bytearray(b'%PDF-1.4\n%\xe2\xe3\xcf\xd3\n')
    offsets = [0]
    for index, obj in enumerate(objects, 1):
        offsets.append(len(output))
        output.extend(f'{index} 0 obj\n'.encode("ascii") + obj + b'\nendobj\n')
    startxref = len(output)
    output.extend(f'xref\n0 {len(offsets)}\n0000000000 65535 f \n'.encode("ascii"))
    output.extend(''.join(f'{o:010} 00000 n \n' for o in offsets[1:]).encode("ascii"))
    output.extend(f'trailer\n<< /Size {len(offsets)} /Root 1 0 R /ID [<4d69786564466978747572655630303031><4d69786564466978747572655630303031>] >>\nstartxref\n{startxref}\n%%EOF\n'.encode("ascii"))
    return bytes(output)


def encoded_header(value):
    return '=?utf-8?B?' + base64.b64encode(value.encode("utf-8")).decode("ascii") + '?='


def encoded_payload(data):
    return '\r\n'.join(textwrap.wrap(base64.b64encode(data).decode("ascii"), 76))


def mime_part(content_type, data, filename=None, content_id=None):
    headers = [f'Content-Type: {content_type}', 'Content-Transfer-Encoding: base64']
    if filename:
        headers.append(f'Content-Disposition: attachment; filename="{filename}"')
    if content_id:
        headers.append(f'Content-ID: <{content_id}>')
    return '\r\n'.join(headers) + '\r\n\r\n' + encoded_payload(data) + '\r\n'


def eml(identity, subject, plain=None, html=None, attachments=()):
    headers = [f'From: {encoded_header("Sofia Mendonça")} <sofia@fixtures.invalid>',
               'To: Ellis Carter <ellis@fixtures.invalid>',
               'Date: Mon, 05 Oct 2026 08:00:00 +0000',
               f'Message-ID: <{identity}@fixtures.invalid>',
               f'Subject: {encoded_header(subject)}', 'MIME-Version: 1.0']
    if plain is not None and html is not None:
        boundary = f'cm-{identity}-alternative-v1'
        body = f'Content-Type: multipart/alternative; boundary="{boundary}"\r\n\r\n'
        for kind, data in [('text/plain; charset=utf-8', plain), ('text/html; charset=utf-8', html)]:
            body += f'--{boundary}\r\n' + mime_part(kind, data.encode("utf-8"))
        body += f'--{boundary}--\r\n'
    elif html is not None:
        body = mime_part('text/html; charset=utf-8', html.encode("utf-8"))
    elif plain is not None:
        body = mime_part('text/plain; charset=utf-8', plain.encode("utf-8"))
    else:
        raise ValueError("A body is required")
    if attachments:
        boundary = f'cm-{identity}-mixed-v1'
        container = f'Content-Type: multipart/mixed; boundary="{boundary}"\r\n\r\n--{boundary}\r\n' + body
        for filename, content_type, data, content_id in attachments:
            container += f'--{boundary}\r\n' + mime_part(content_type, data, filename, content_id)
        body = container + f'--{boundary}--\r\n'
    return ('\r\n'.join(headers) + '\r\n' + body).encode("ascii")


# Independently drawn 5x7 pixel glyphs: no fonts or third-party assets embedded.
FONT = {
    'A':[14,17,17,31,17,17,17], 'C':[14,17,16,16,16,17,14],
    'D':[30,17,17,17,17,17,30], 'E':[31,16,16,30,16,16,31],
    'H':[17,17,17,31,17,17,17], 'I':[31,4,4,4,4,4,31],
    'O':[14,17,17,17,17,17,14], 'P':[30,17,17,30,16,16,16],
    'R':[30,17,17,30,20,18,17], 'S':[15,16,16,14,1,1,30],
    'T':[31,4,4,4,4,4,4], '0':[14,17,19,21,25,17,14],
    '1':[4,12,4,4,4,4,14], '6':[14,16,16,30,17,17,14],
    '9':[14,17,17,15,1,1,14], '-':[0,0,0,31,0,0,0],
    ' ':[0]*7,
}


def scan_png(code="ORCHID-619"):
    width, height, scale = 1120, 390, 8
    image = bytearray([255] * (width * height))
    for line, y in [('DISPATCH CODE', 65), (code, 215)]:
        x = 72
        for char in line:
            for row, pattern in enumerate(FONT[char]):
                for col in range(5):
                    if pattern & (1 << (4-col)):
                        for dy in range(scale):
                            offset = (y + row*scale + dy) * width + x + col*scale
                            image[offset:offset+scale] = b'\0' * scale
            x += 6 * scale
    raw = b''.join(b'\0' + image[r*width:(r+1)*width] for r in range(height))
    # Handwritten uncompressed DEFLATE stream: no zlib compression variability.
    def stored_zlib(data):
        blocks = bytearray(b'\x78\x01')
        for pos in range(0, len(data), 65535):
            block = data[pos:pos+65535]
            final = pos + len(block) == len(data)
            blocks.extend(bytes([1 if final else 0]) + struct.pack('<HH', len(block), 65535-len(block)) + block)
        blocks.extend(struct.pack('>I', zlib.adler32(data) & 0xffffffff))
        return bytes(blocks)
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', binascii.crc32(kind+data) & 0xffffffff)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 0, 0, 0, 0))
            + chunk(b'pHYs', struct.pack('>IIB', 11811, 11811, 1)) + chunk(b'IDAT', stored_zlib(raw)) + chunk(b'IEND', b''))


def rtf(text):
    body = ''.join('\\' + c if c in '{}\\' else '\\par\n' if c == '\n' else c if ord(c) < 128 else f'\\u{ord(c)}?' for c in text)
    return ('{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0 Liberation Sans;}}\\uc1\\fs22\n' + body + '}\n').encode("ascii")


README = """# Authored mixed-format evidence corpus

This compact corpus is independently authored synthetic content dedicated to
CC0-1.0. Every person, organisation, address, amount, code and operational plan is
fictional. The `.invalid` email domain is reserved for non-deliverable examples.
No private documents, downloaded sources, paid APIs or product parser were used.

## Regeneration and integrity

From the repository root, using Python 3.10 or newer:

```sh
python3 tools/GenerateMixedFormatFixtures.py
python3 tools/GenerateMixedFormatFixtures.py --verify
python3 tools/GenerateMixedFormatFixtures.py --output /tmp/mixed-copy
(cd benchmarks/mixed-formats && sha256sum -c SHA256SUMS)
```

Run the `sha256sum` command inside this directory, or add its path prefix to the
listed filenames. `--verify` reconstructs bytes independently in memory and checks
the complete generated set without changing files. The generator needs only the
Python standard library. It deliberately does not calculate workbook formulas.
The cached formula result was supplied by the author. OOXML packages are sorted
uncompressed ZIP entries with fixed 2000 timestamps and permissions. MIME dates,
IDs, boundaries, header encoding and CRLF are fixed. PDFs use Helvetica/WinAnsi
and contain no time-dependent metadata. The scan uses an independently drawn
bitmap alphabet, no font files or text metadata, and stored DEFLATE blocks.

`SHA256SUMS` freezes every fixture, manifest, README and license. It cannot list
its own hash; report that file's hash separately when preserving provenance.

## Corpus and label semantics

There are 16 root fixtures, 34 facts and 46 natural-language questions. Four
attachments inside `mixed.eml` exercise DOCX, XLSX, native PDF and TXT extraction.
`duplicate.eml` includes two different TXT attachments both named `status.txt`;
never collapse those children by filename. Their Content-IDs and anchor strings
identify the first and second authored occurrence. Attachments are not also saved
as roots, preventing accidental leakage into root-only tests.

The native set covers DOCX headings, paragraphs and tables; multi-sheet XLSX
numbers, ISO text dates, styled numeric dates, cached and uncached formulas,
explicit blanks and absent cells; encoded Unicode EML headers, equivalent
plain/HTML alternatives and HTML-only mail; native PDF, TXT, Markdown, HTML, CSV
and RTF. English and Portuguese preserve accents in names and prose. A long
paragraph, 61-row table and long email put exclusive answers near their ends,
checking excerpt answer coverage rather than merely passage hit rates.
`dispatch-card.png` stores an exclusive image-only code. The second OCR root,
`scanned-mail.eml`, contains an image-only `dispatch-scan.png` attachment with a
different code, testing OCR through email attachment retrieval and citation.
Normal native `mixed.eml` does not depend on OCR.

`manifest.json` uses snake_case throughout:

- `version`: integer 1
- `fixtures`: `id`, relative `file`, `sha256`, `requires_ocr`
- `facts`: `id`, root fixture ID in `root`, attachment filename `chain` excluding
  the root, literal gold `anchors`, and `location` with a `kind` plus only the
  applicable `structure_path`, `sheet`, `cell_range`, `email_part`, `page` or
  `image_frame`; optional `heading` and `notes`
- `queries`: `id`, natural `text`, simple exact lexical `terms`, relevant fact IDs
  in `relevant`, `language` (`en`/`pt`), `scope` (`any`/`root_only`/
  `attachments_only`), `negative`, and preassigned `split`

Fact locations describe the source's authored identity, not an extraction result.
DOCX paragraph ordinals count root body paragraphs independently of table cells.
XLSX ranges cover the complete authored row, including explicit blank endpoint
cells. PDF pages are 1-based; exact PDF block structures are intentionally omitted.
CSV is represented as sheet `CSV`, row ranges A:D. Plain TXT and RTF use
document-level evidence. Markdown's torque paragraph is `html/block[2]`, following
its H1 block. HTML's authored table is `html/table[1]`.

The human-date fact `styled_date_iso` expects `2026-11-19` from the numeric cell
46345 with format `yyyy-mm-dd`; the separate `styled_date_raw` fact expects 46345.
Only the raw serial appears literally in the package. A parser exposing only the
serial should pass the raw control and fail human-date coverage. Do not weaken the
human expectation by accepting the raw number. An uncached formula has no expected
numeric result: its stored formula text is gold. Blank cells are not zeros.

All facts and queries were declared before constructing files or extracting any
text. Development/holdout labels are fixed by authored query row parity, 23 each,
and have not been tuned on scores. This splits query formulations, not independent
source families; shared documents/facts may occur in both partitions. It is a
small regression corpus, not a statistically independent model-quality test.

The two absent-token negatives contain invented keywords absent from all source
content. Three scope negatives use real terms present only outside the requested
scope. Gold lists are empty for all negatives. Terms are a diagnostic lexical
baseline; paraphrase questions may intentionally use semantically related wording
while their baseline terms are exact source words.

CC0 dedication is in `LICENSE.txt`. Repository code outside this authored fixture
corpus keeps its existing license. This corpus contains no third-party assets.
"""
LICENSE = """CC0 1.0 Universal dedication
SPDX-License-Identifier: CC0-1.0

The authors dedicate the independently authored synthetic mixed-format fixture
content, gold labels, queries, README and generation tool to the public domain.
To the extent possible under law, the authors waive all copyright and related
or neighboring rights to these works. The works are provided without warranty.

You may copy, modify, distribute and perform these works, even for commercial
purposes, without asking permission. All names, data and scenarios are fictional.

The governing CC0 1.0 Universal legal code is available at:
https://creativecommons.org/publicdomain/zero/1.0/legalcode

No third-party documents, fonts, artwork or private data are included. Standard
file-format identifiers and names describe compatibility, not endorsement.
"""


def build_corpus():
    roots = []
    files = {}
    def root(identity, filename, data, requires_ocr=False):
        files[filename] = data
        roots.append({"id": identity, "file": filename,
                      "sha256": hashlib.sha256(data).hexdigest(), "requires_ocr": requires_ocr})
    long_para = ' '.join(f'Inspection {i:02d} checks the weather cover, records the noncritical packing colour, and leaves the provisional route open for review.' for i in range(1, 43))
    long_para += ' The final Aurora rendezvous code is LANTERN-82; the obsolete draft code is COPPER-11.'
    long_table = [["Crate", "Purpose", "Serial"]]
    long_table += [[f"Crate {i:02d}", f"Routine spare kit for dock sector {i:02d}", f"SPARE-{100+i}"] for i in range(1, 60)]
    long_table += [["Crate 60", "Final calibration crate", "MARLIN-440"]]
    root("launch_docx", "launch.docx", docx([
        ("h1", "Aurora field launch"),
        ("p", "Sofia Mendonça owns the Aurora field launch. Ellis Carter coordinates the separate warehouse painting trial."),
        ("p", "The Aurora installation date is 2026-11-19. The draft review takes place on 2026-11-12 and does not move installation."),
        ("table", [["Activity", "Budget EUR", "Owner"], ["Field installation", "18400", "Sofia Mendonça"], ["Warehouse painting", "7200", "Ellis Carter"]]),
        ("h2", "Closing checklist"), ("p", long_para), ("table", long_table),
    ], "Aurora field launch"))
    root("coimbra_docx", "coimbra.docx", docx([
        ("h1", "Entrega em Coimbra"),
        ("p", "Inês Gonçalves recebe a entrega em Coimbra em 2026-12-03. Luís Nóbrega acompanha a visita de Braga em 2026-12-04."),
        ("table", [["Sala", "Lugares", "Uso"], ["Sala Açucena", "28", "Oficina de montagem"], ["Sala Dália", "16", "Reunião de arquivo"]]),
    ], "Entrega em Coimbra"))
    root("operations_xlsx", "operations.xlsx", xlsx([
        ("Orders", [(1, [cell("A1", "Item"), cell("B1", "Quantity"), cell("C1", "Unit price"), cell("D1", "Delivery ISO"), cell("E1", "Owner"), cell("F1", "Line amount")]),
                    (2, [cell("A2", "Maré sensor"), cell("B2", 48), cell("C2", 37.5), cell("D2", "2026-11-07"), cell("E2", "João Ribeiro"), cell("F2", 800)]),
                    (3, [cell("A3", "Vale gasket"), cell("B3", 12), cell("C3", 18), cell("D3", "2026-11-09"), cell("E3", "Marta Leão"), cell("F3", 450)])]),
        ("Totals", [(1, [cell("A1", "Metric"), cell("B1", "Value")]),
                    (2, [cell("A2", "Published total"), cell("B2", formula="SUM(Orders!F2:F3)", cached=1250)]),
                    (3, [cell("A3", "Adjustment"), cell("B3", 25)]),
                    (4, [cell("A4", "Uncached audit"), cell("B4", formula="SUM(B2:B3)")])]),
    ]))
    root("schedule_xlsx", "schedule.xlsx", xlsx([
        ("Schedules", [(1, [cell("A1", "Task"), cell("B1", "Scheduled date")]),
                       (2, [cell("A2", "Cedar calibration"), cell("B2", 46345, style=1)]),
                       (3, [cell("A3", "Larch inspection"), cell("B3", 46352, style=1)])]),
        ("Sparse", [(1, [cell("A1", "Inventory label"), cell("F1", "Count")]),
                    (7, [cell("A7", "Reserve rack"), cell("C7"), cell("E7", "Pine-9")]),
                    (40, [cell("A40", "Emergency latch"), cell("F40", 17), cell("G40")])]),
    ]))
    annex_docx = docx([("h1", "Cabinet allocation"), ("p", "The Azulejo team uses cabinet AX-204. The Junco team uses cabinet JC-118.")], "Cabinet allocation")
    annex_xlsx = xlsx([("Dispatch", [(1, [cell("A1", "Item"), cell("B1", "Count")]), (2, [cell("A2", "Shipment filters"), cell("B2", 73)]), (3, [cell("A3", "Spare washers"), cell("B3", 21)])])])
    annex_pdf = pdf([["Approved reserve", "The approved reserve is R$ 6.840 for the east pier.", "The unfunded west-pier proposal remains R$ 2.900."]])
    annex_txt = b'The east-pier access phrase is NIGHTJAR-52.\nThe revoked draft phrase was DAYBIRD-18.\n'
    attachments = [
        ("annex-brief.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", annex_docx, "brief@fixtures.invalid"),
        ("annex-stock.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", annex_xlsx, "stock@fixtures.invalid"),
        ("annex-approval.pdf", "application/pdf", annex_pdf, "approval@fixtures.invalid"),
        ("annex-note.txt", "text/plain; charset=utf-8", annex_txt, "note@fixtures.invalid"),
    ]
    root("mixed_eml", "mixed.eml", eml("mixed", "Revisão Azulejo",
        plain="Jadebrief meeting begins at 9:20. Please read the four annexes for the allocation, shipment, reserve and access details. The earlier 8:10 review is cancelled.\n",
        html='<!doctype html><html lang="en"><body><p>Jadebrief meeting begins at <strong>9:20</strong>.</p><p>Please read the four annexes for the allocation, shipment, reserve and access details. The earlier 8:10 review is cancelled.</p></body></html>',
        attachments=attachments))
    root("html_only_eml", "html-only.eml", eml("html-only", "Sessão de montagem",
        html='<!doctype html><html lang="pt"><body><h1>Sessão de montagem</h1><p>A sessão será na Sala Ipê. É preciso levar o medidor azul.</p><p>A Sala Cedro recebe apenas a equipa de limpeza; o medidor verde fica guardado.</p></body></html>'))
    root("duplicate_eml", "duplicate.eml", eml("duplicate", "Two status notices",
        plain="The two attached status notices have the same filename. Treat them as distinct records.\n",
        attachments=[("status.txt", "text/plain; charset=utf-8", b'The Birch docket is Birch-314. The Birch review is complete.\n', "birch-status@fixtures.invalid"),
                     ("status.txt", "text/plain; charset=utf-8", b'The Willow docket is Willow-271. The Willow review is pending.\n', "willow-status@fixtures.invalid")]))
    root("native_pdf", "harbor.pdf", pdf([
        ["Harbor shift briefing", "The Cedar vessel shift starts at 06:40.", "The Larch vessel shift starts at 07:15.", "The two shifts use different teams and independent manifests."],
        ["Teste de pressão", "O teste em São Tomé exige pressão de 4.2 bar.", "O ensaio de Faro usa 3.8 bar e não altera São Tomé.", "A técnica responsável é Íris Falcão."],
    ]))
    root("radio_txt", "radio.txt", 'Harbor watch radio plan\nThe harbor watch uses radio channel 14 during the night patrol.\nThe maintenance crew uses channel 6 during daytime repairs.\nContact Noémie Silva for roster corrections.\n'.encode("utf-8"))
    root("maintenance_md", "maintenance.md", '# Ajustes da oficina\n\nO Torquímetro Coral deve ser ajustado para 9 N m.\n\n## Equipamento separado\nO Torquímetro Âmbar usa 12 N m. Ana Sá verifica ambos antes da oficina.\n'.encode("utf-8"))
    root("venue_html", "venue.html", '<!doctype html>\n<html lang="en"><head><meta charset="utf-8"><title>Workshop venue</title></head><body><h1>Workshop seating</h1><p>The following capacities apply to seated workshops.</p><table><tr><th>Room</th><th>Seats</th><th>Host</th></tr><tr><td>Juniper</td><td>36</td><td>Zoë Martin</td></tr><tr><td>Maple</td><td>24</td><td>André Costa</td></tr></table><p>Standing exhibitions have a different safety review.</p></body></html>\n'.encode("utf-8"))
    root("inventory_csv", "inventory.csv", 'Depot,Item,Units,Owner\r\nViseu,Brass connector,65,Beatriz Simões\r\nÉvora,Copper connector,41,Álvaro Reis\r\nLeiria,Steel clip,90,Renée Vidal\r\n'.encode("utf-8"))
    root("signoff_rtf", "signoff.rtf", rtf('Revisão da passarela\nCarla Araújo assinou a revisão em 2026-10-27.\nA inspeção preliminar de Rui Mourão ocorreu em 2026-10-21.\n'))
    root("scan_png", "dispatch-card.png", scan_png(), requires_ocr=True)
    long_body = 'Quay dispatch control log\n\n' + ' '.join(f'Dispatch entry {i:02d} records routine crate handling, dry weather, a completed cover check and a provisional loading sequence for the next quay lane.' for i in range(1, 45))
    long_body += ' Final instruction: the emergency seal is KITE-907. The retired sample seal SWIFT-103 is only a training prop.\n'
    root("long_eml", "long.eml", eml("long", "Quay dispatch control", plain=long_body))
    root("scanned_eml", "scanned-mail.eml", eml("scanned-mail", "Scanned instruction",
        plain="See the attached image for its printed instruction.\n",
        attachments=[("dispatch-scan.png", "image/png", scan_png("ORCHID-690"), "scan@fixtures.invalid")]), requires_ocr=True)
    manifest = {"version": VERSION, "fixtures": roots, "facts": FACTS, "queries": QUERIES}
    files["manifest.json"] = (json.dumps(manifest, ensure_ascii=False, indent=2) + '\n').encode("utf-8")
    files["README.md"] = README.encode("utf-8")
    files["LICENSE.txt"] = LICENSE.encode("utf-8")
    sums = ''.join(f'{hashlib.sha256(data).hexdigest()}  {name}\n' for name, data in sorted(files.items()))
    files["SHA256SUMS"] = sums.encode("ascii")
    validate_authoring(manifest, files)
    return manifest, files


def validate_authoring(manifest, files):
    roots = {r["id"] for r in manifest["fixtures"]}
    facts = {f["id"]: f for f in FACTS}
    assert len(roots) == 16 and len(facts) == 34 and len(QUERIES) == 46
    assert sum(q["split"] == "development" for q in QUERIES) == 23
    assert sum(q["negative"] for q in QUERIES) == 5
    assert all(f["root"] in roots and f["anchors"] for f in FACTS)
    for q in QUERIES:
        assert all(fid in facts for fid in q["relevant"])
        assert q["negative"] == (not q["relevant"])
        assert q["language"] in {"en", "pt"} and q["scope"] in {"any", "root_only", "attachments_only"}
        for fid in q["relevant"]:
            assert q["scope"] != "root_only" or not facts[fid]["chain"]
            assert q["scope"] != "attachments_only" or facts[fid]["chain"]
    assert all(hashlib.sha256(files[r["file"]]).hexdigest() == r["sha256"] for r in manifest["fixtures"])


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--verify", action="store_true", help="Reconstruct bytes in memory and verify all generated files without writing")
    args = parser.parse_args(argv)
    manifest, files = build_corpus()
    if args.verify:
        failures = []
        for name, expected in files.items():
            path = args.output / name
            if not path.is_file() or path.read_bytes() != expected:
                failures.append(name)
        if failures:
            parser.exit(1, "Fixture mismatch or missing file: " + ', '.join(failures) + '\n')
        print(f"Verified {len(files)} generated files; {len(manifest['fixtures'])} roots, {len(FACTS)} facts, {len(QUERIES)} queries.")
    else:
        args.output.mkdir(parents=True, exist_ok=True)
        for name, data in files.items():
            (args.output / name).write_bytes(data)
        print(f"Generated {len(files)} files in {args.output}")
    print("manifest_sha256=" + hashlib.sha256(files["manifest.json"]).hexdigest())
    print("hash_lock_sha256=" + hashlib.sha256(files["SHA256SUMS"]).hexdigest())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
