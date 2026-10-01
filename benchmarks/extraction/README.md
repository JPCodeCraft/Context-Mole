# Extraction performance fixtures

A small, checked-in corpus for repeatable extraction and OCR measurements. Tests
never download these documents. The fixtures cover native PDF text, Word
structure, an illustrated historical scan through both the PDF and image
paths, and clean multilingual TIFF text. This is a smoke benchmark, not a
comprehensive document-quality dataset.

The persisted preparation identity is `layout-v2/spans-v2/body-context-v2`,
defined by the extraction, chunking and semantic constants in
`src/Core/IndexPreparation.cs`. Existing revisions prepared with an older identity
are extracted and indexed again before they are treated as current. Extraction
retains canonical source text, logical heading occurrences and provenance;
boilerplate annotations affect semantic input while the original evidence remains
available to lexical search and reading.

The synthetic `quality/` corpus adds deliberate content-stream scrambling across
two columns, repeated page margins, numeric tables, native Portuguese accents and
line-break hyphenation, page rotation, Portuguese OCR, and a mixed native/scanned
PDF. Its content is repository-owned under MIT; it embeds no font files. Regenerate
it deliberately with `tools/GenerateExtractionQualityFixtures.cs` and update the
manifest hashes. Regeneration uses Windows Arial only to rasterize the synthetic
scan and may vary with the installed font/rendering version.

Quality cases carry ordered anchors, expected page anchors, exact table rows/cell
separators, required normalized text regions and expected boilerplate annotations.
OCR ground truth is measured as Unicode character error rate (CER) and word error
rate (WER), with whitespace normalized and accents preserved. The synthetic OCR
cases require CER ≤10%; existing upstream ground truth is reported without a new
threshold. Inferred PDF/OCR tables retain an explicit layout warning: a geometry
heuristic does not establish merged-cell or column meaning. Regions use normalized
coordinates with a top-left origin on the displayed page/image. Character counts
include canonical line separators between extracted sections, so located OCR
lines are comparable to earlier page-sized text. Rotated native text preserves
its content sequence and displayed bounds with an explicit warning that complex
column/table order is unverified.

Focused generated regression cases additionally exercise a ruled text-only PDF
table, aligned OCR cell boxes, inherited Word heading styles and a custom broken
PDF Unicode font mapping. Missing font geometry is marked as inferred; readable
native regions survive OCR reconciliation, while stronger OCR evidence can repair
an overlapping illegible region. These cases are generated inside tests and do
not require fonts or model downloads.

| File | Coverage | Origin and license |
| --- | --- | --- |
| `reading-order.pdf` | Native PDF with two columns, headings and lists | [W3C WAI PDF3 working example](https://www.w3.org/WAI/WCAG21/Techniques/pdf/PDF3), W3C Software and Document License |
| `reading-order.docx` | The same content in editable Word structure | Same W3C example and license |
| `huckleberry-finn-scan.pdf` | One scanned page with illustration, irregular text layout and historical type | OCRmyPDF `c03-29.pdf`, from Project Gutenberg's *Adventures of Huckleberry Finn*, page 29; public domain |
| `huckleberry-finn-scan.jpg` | The identical underlying JPEG, exercising direct image OCR | Losslessly extracted from the preceding PDF, without resizing or recompression; public domain |
| `eurotext.tif` | Clean TIFF with English, German, French, Italian, Spanish and Portuguese, including accents and punctuation | [Tesseract testing image](https://github.com/tesseract-ocr/test/blob/232ff181c66516116ec0e84c4963f70de15050fd/testing/eurotext.tif), Apache-2.0 |

The W3C files are unmodified copies downloaded on 2026-09-07. Copyright W3C
(World Wide Web Consortium). The complete applicable notice is retained in
[LICENSE-W3C.txt](LICENSE-W3C.txt); see the
[W3C license](https://www.w3.org/copyright/software-license-2023/).

The OCRmyPDF input is pinned to commit
`196072acef056aa11b63e60564d98762bd38f638`.
Its [license declaration](https://github.com/ocrmypdf/OCRmyPDF/blob/196072acef056aa11b63e60564d98762bd38f638/REUSE.toml)
identifies `tests/resources/c03-29.pdf` as public domain; its
[source notes](https://github.com/ocrmypdf/OCRmyPDF/blob/196072acef056aa11b63e60564d98762bd38f638/tests/resources/README.rst)
identify the [Project Gutenberg source](https://www.gutenberg.org/files/76/76-h/images/).
No OCRmyPDF application code is included. The JPEG preserves the PDF's original
image bytes. Every input's SHA-256, download URL and quality anchors are recorded
in [manifest.json](manifest.json).

The Tesseract TIFF and its [upstream ground truth](eurotext.txt) are unmodified
copies from `tesseract-ocr/test` commit
`232ff181c66516116ec0e84c4963f70de15050fd`. The image is 1024 × 800 pixels at
300 DPI and contains 12 printed lines. The complete repository license is
retained in [LICENSE-TESSERACT.txt](LICENSE-TESSERACT.txt). Upstream's
[image attribution notes](https://github.com/tesseract-ocr/test/blob/232ff181c66516116ec0e84c4963f70de15050fd/testing/README.md)
list source exceptions for other samples, with no eurotext exception. The
[ground-truth source](https://raw.githubusercontent.com/tesseract-ocr/test/232ff181c66516116ec0e84c4963f70de15050fd/testing/eurotext.txt)
is retained for reviewing recognition differences; the benchmark checks
multilingual anchors and a 350-character minimum rather than exact punctuation.

Run from the repository root:

```powershell
dotnet run --file tools/ExtractionPerformanceBenchmark.cs -- --iterations 5 --output artifacts/extraction-benchmark/native.json
dotnet run --file tools/ExtractionPerformanceBenchmark.cs -- --ocr --iterations 3 --output artifacts/extraction-benchmark/ocr.json
```

OCR uses the pinned PP-OCR models. If `CONTEXTMOLE_DATA_DIR` is unset, the runner
uses an isolated `artifacts/extraction-benchmark/data` directory and prepares the
models there. An existing data directory can be selected explicitly to reuse
downloaded models. Model setup is reported separately, and one warm-up per
fixture is excluded from measured extraction. Native-only runs do not require
model downloads.

Reports include median elapsed time, managed allocation totals, sampled peak
process working set, GC counts, text/section counts, available CER/WER, located
sections, layout warnings, boilerplate annotations and structural quality checks. Working
set includes native ONNX/model memory; managed allocations do not. Compare on
the same machine, build configuration, CPU profile and model assets, without
other indexing or model workloads running. Use `--baseline path/to/prior.json`
to report timing ratios. Fixed elapsed-time limits are deliberately omitted from
ordinary tests because shared CI machines and CPU profiles vary.

Ordinary regression tests verify fixture hashes and native extraction quality.
The opt-in OCR run also fails on extraction errors or missing quality anchors,
so an empty or failed extraction cannot appear as a performance improvement.
