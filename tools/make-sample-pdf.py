#!/usr/bin/env python3
"""Generate a minimal multi-page PDF used for samples and tests.

Usage: python tools/make-sample-pdf.py [output.pdf]
"""
from __future__ import annotations

import sys
from pathlib import Path

PAGE_W, PAGE_H = 612, 792  # US Letter, in points


def _content(text: str, page_no: int) -> bytes:
    title = f"PDFReader X - Page {page_no + 1}"
    body = text.replace("\\", r"\\").replace("(", r"\(").replace(")", r"\)")
    return (
        f"BT /F1 24 Tf 72 720 Td ({title}) Tj ET\n"
        f"BT /F1 12 Tf 72 680 Td 14 TL ({body}) Tj ET\n"
        "72 650 m 540 650 l S\n"
    ).encode("ascii")


def _add_outline(objects: list[bytes], page_obj: list[int]) -> None:
    """在 objects 末尾追加 PDF 大纲（书签）对象，供侧边栏书签树测试。"""
    entries: list[tuple[bytes, int, list[tuple[bytes, int]]]] = []
    titles = [
        b"Page 1 - Introduction",
        b"Page 2 - Bookmarks and Ink",
        b"Page 3 - LLM Bookmarks",
    ]
    for i in range(min(3, len(page_obj))):
        kids = [(b"LLM Generation Test", i)] if i == 2 else []
        entries.append((titles[i], i, kids))

    root_id = len(objects)
    total_count = sum(1 + len(kids) for _, _, kids in entries)
    first_id = root_id + 1
    last_id = first_id + len(entries) - 1
    objects.append(
        b"<< /Type /Outlines /First %d 0 R /Last %d 0 R /Count %d >>"
        % (first_id, last_id, total_count)
    )

    next_child_id = last_id + 1
    for i, (title, page_idx, kids) in enumerate(entries):
        parent = b"/Parent %d 0 R" % root_id
        if i > 0:
            parent += b" /Prev %d 0 R" % (first_id + i - 1)
        if i < len(entries) - 1:
            parent += b" /Next %d 0 R" % (first_id + i + 1)
        if kids:
            parent += b" /First %d 0 R /Last %d 0 R /Count %d" % (
                next_child_id, next_child_id + len(kids) - 1, len(kids)
            )
        objects.append(
            b"<< /Title (%s) %s /Dest [%d 0 R /Fit] >>"
            % (title, parent, page_obj[page_idx])
        )
        for kid_title, kid_page in kids:
            objects.append(
                b"<< /Title (%s) /Parent %d 0 R /Dest [%d 0 R /Fit] >>"
                % (kid_title, first_id + i, page_obj[kid_page])
            )
        next_child_id += len(kids)


def build_pdf(pages: list[str]) -> bytes:
    n = len(pages)
    content_obj = [4 + i * 2 for i in range(n)]
    page_obj = [5 + i * 2 for i in range(n)]

    objects: list[bytes] = [b""]
    objects.append(b"<< /Type /Catalog /Pages 2 0 R /Outlines %d 0 R >>" % (4 + 2 * n))
    kids = b" ".join(b"%d 0 R" % p for p in page_obj)
    objects.append(b"<< /Type /Pages /Kids [%s] /Count %d >>" % (kids, n))
    objects.append(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    for i, text in enumerate(pages):
        stream = _content(text, i)
        objects.append(b"<< /Length %d >>\nstream\n%s\nendstream" % (len(stream), stream))
        objects.append(
            b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %d %d] "
            b"/Resources << /Font << /F1 3 0 R >> >> /Contents %d 0 R >>"
            % (PAGE_W, PAGE_H, content_obj[i])
        )
    _add_outline(objects, page_obj)

    out = bytearray(b"%PDF-1.4\n")
    offsets = [0]
    for i in range(1, len(objects)):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % i
        out += objects[i]
        out += b"\nendobj\n"
    xref_pos = len(out)
    out += b"xref\n0 %d\n" % len(objects)
    out += b"0000000000 65535 f \n"
    for off in offsets[1:]:
        out += b"%010d 00000 n \n" % off
    out += (
        b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n"
        % (len(objects), xref_pos)
    )
    return bytes(out)


def main() -> None:
    out = Path(sys.argv[1] if len(sys.argv) > 1 else "samples/sample.pdf")
    out.parent.mkdir(parents=True, exist_ok=True)
    pages = [
        "This is the first page of the PDFReader X sample document.",
        "Second page: bookmarks and ink layers are coming in later phases.",
        "Third page: the LLM bookmark generator can be tested against this text.",
    ]
    out.write_bytes(build_pdf(pages))
    print(f"wrote {out} ({out.stat().st_size} bytes, {len(pages)} pages)")


if __name__ == "__main__":
    main()
