# olmOCR extraction evaluation adapter

This runner evaluates the original PDFs and original JSONL checks from
[allenai/olmOCR-bench](https://huggingface.co/datasets/allenai/olmOCR-bench/tree/54a96a6fb6a2bd3b297e59869491db4d3625b711)
at revision `54a96a6fb6a2bd3b297e59869491db4d3625b711` against Context Mole's production PDF extractor.
The dataset is licensed under ODC-BY; retain the downloader's source and license notices.
The upstream evaluator used to define check semantics is
[allenai/olmocr](https://github.com/allenai/olmocr/tree/f7cfe4c22098b154c76b6ec950d1c0a464eecf8d/olmocr/bench)
at `f7cfe4c22098b154c76b6ec950d1c0a464eecf8d` (Apache-2.0).
The local adapter was independently implemented and does not vendor its Python evaluator.

Download a selected cache with `tools/DownloadPdfBenchmarks.py` first, then run from the repository root:

```powershell
$manifest = 'C:\benchmark-cache\olmocr\54a96a6fb6a2bd3b297e59869491db4d3625b711\smoke\manifest.json'
dotnet run --file tools/OlmOcrBenchmark.cs -- --manifest $manifest --mode native --output artifacts\olmocr-native.json
dotnet run --file tools/OlmOcrBenchmark.cs -- --manifest $manifest --mode ocr --assets C:\existing-context-mole-data\assets --output artifacts\olmocr-ocr.json
```

The runner does not download datasets or models. OCR requires the existing detector, recognizer and dictionary with the production pinned SHA-256 hashes.
`PrepareCachedAssetsAsync` makes the engine instance stay cached-only, including later internal preparation calls after an OCR failure.
Missing or mismatched models abort before extraction. Model cache directories and policy files are not created or changed.
Temporary source copies and state are isolated under the system temporary directory and removed after the run.
Each copied PDF and the JSONL input are hash-verified; extraction reads the verified copy.
Application indexes, settings and model caches are not modified. Report output is the requested file.

Native mode disables OCR and returns empty OCR evidence without rendering requested pages.
OCR mode uses the actual production native text selection, fallback and native/OCR reconciliation; it does not force OCR on every page.
Canonical root PDF sections are concatenated in their extraction order per physical page, including annotated boilerplate.
Embedded attachments are excluded. This deliberately measures the evidence the product retains: header/footer absence checks may fail because Context Mole keeps these source passages.

Every selected upstream row is retained, including its original PDF ID, check ID, page, type and category.
Categories follow JSONL filenames, rather than inferred PDF directory names.
The pinned dataset contains **7,019 checks** across seven source categories; the dataset card's older count should not be used as an evaluation denominator.
An additional page-1 baseline is added only for PDFs with no upstream baseline on any page, matching upstream default behavior.
`--no-auto-baseline` disables only generated baselines; original baseline rows remain evaluated.

| Check | Implementation and limitation |
| --- | --- |
| present / absent | NFC, upstream punctuation/Markdown/whitespace normalization, case option and Unicode prefix/suffix slices. Zero differences uses symmetric exact partial matching. Nonzero differences uses bounded Levenshtein substring matching instead of RapidFuzz partial Indel similarity. |
| order | Case-sensitive normalized substring start ordering with Levenshtein tolerance and consolidated approximate occurrences. Exact matches retain overlaps. |
| table | Whole-cell normalized Indel similarity, all requested relations on the same candidate cell, immediate directional neighbors including occupied empty cells. Explicit table sections become rectangular TSV grids; first row/column are heading candidates. HTML spans and marked header graphs are unavailable. |
| baseline | Unicode alphanumeric content, optional maximum length/image-alt exclusion, contiguous suffix repetition and upstream disallowed Unicode ranges. |
| format: heading | Explicit extracted heading metadata. Body text that merely inherits heading context is not treated as a heading. This is an adaptation from upstream Markdown/HTML headings. |
| math | Unsupported: no LaTeX/KaTeX rendering-equivalence stack is installed. The adapter never substitutes plain equation text presence. |
| footnote / bold / italic | Unsupported: the PDF projection does not retain the required formatting/marker association. |
| future unknown types | Explicitly unsupported. |

Missing pages, extractor errors, malformed checks and matching work-limit failures are reported as errors, never passing absence checks.
Math remains in category denominators. The output includes passed/failed/unsupported/error counts, supported coverage, supported-only pass rates and a conservative equal-category macro with every unsupported/error check counted as not passed.
An unsupported-only category has null supported pass rate and zero conservative score.
Verdict is `incomplete` when any unsupported/error check exists, `failed` for complete evaluations with failures, and `passed` only when all checks pass.
Exit code is 0 only for `passed`, otherwise 1; a selected math subset is expected to report incomplete coverage.

This is a **Context Mole consumer-output adaptation, not the official olmOCR score**.
There is one extraction per PDF, without upstream repeated-run majority voting or bootstrap confidence intervals.
Approximate text matching, table/header projection and heading detection differ as listed above.
The report embeds page text, tables, headings, extraction errors, source pins, model revisions, build identity and timings so scores can be inspected.
Use `--threads` to set the benchmark CPU limit and `--timeout-minutes` to cancel extraction and I/O after the deadline.
Evaluation observes cancellation between checks and table candidates; an in-progress bounded matching operation completes before cancellation is observed.
Matching has an explicit 100-million-cell work limit and max-difference limit of 256 to bound malformed or unusually large checks.
