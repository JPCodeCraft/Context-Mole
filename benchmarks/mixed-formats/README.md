# Authored mixed-format evidence corpus

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
