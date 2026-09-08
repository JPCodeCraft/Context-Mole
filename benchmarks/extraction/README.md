# Extraction performance fixtures

A small, checked-in corpus for repeatable extraction and OCR measurements. Tests
never download these documents. The fixtures cover native PDF text, Word
structure, an illustrated historical scan through both the PDF and image
paths, and clean multilingual TIFF text. This is a smoke benchmark, not a
comprehensive document-quality dataset.

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
process working set, GC counts, text/section counts and quality checks. Working
set includes native ONNX/model memory; managed allocations do not. Compare on
the same machine, build configuration, CPU profile and model assets, without
other indexing or model workloads running. Use `--baseline path/to/prior.json`
to report timing ratios. Fixed elapsed-time limits are deliberately omitted from
ordinary tests because shared CI machines and CPU profiles vary.

Ordinary regression tests verify fixture hashes and native extraction quality.
The opt-in OCR run also fails on extraction errors or missing quality anchors,
so an empty or failed extraction cannot appear as a performance improvement.
