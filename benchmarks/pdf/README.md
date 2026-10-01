# External PDF benchmarks

These opt-in benchmarks complement the checked-in extraction and synthetic search
regressions. They use original PDFs and the existing local extraction/index/search
pipeline. There is no paid model, answer generation, external upload, or change to
the application index or settings.

The small datasets are fixed in [datasets.lock.json](datasets.lock.json). PDFs and
annotations are downloaded into an explicitly chosen directory outside the
checkout, using Python's standard library. Do not commit that cache.

```powershell
$cache = 'C:\benchmarks\ContextMole'
python tools/DownloadPdfBenchmarks.py --cache $cache --subset smoke
# Rebuild/verify the same manifests without network:
python tools/DownloadPdfBenchmarks.py --cache $cache --subset smoke --offline
```

The default smoke workload has seven olmOCR PDFs (one lexicographically first PDF
per category, 43 supplied checks), plus two complete ViDoRe PDFs (45 physical
pages). ViDoRe selects six English and six corresponding Portuguese questions in
ID order whose complete relevance sets lie inside these documents. Its reduced
corpus is easier than the full benchmark: do not compare smoke scores with public
leaderboards or use these selected questions as a training set.

Dataset caches include pinned dataset cards for attribution. All annotation batches
must report the exact locked revision; source PDFs and annotations are hash
verified. A new cache refuses viewer data if upstream serves another revision.
Batch caches allow interrupted metadata downloads to resume. Temporary rate limits
receive bounded retry; the tool never installs a dataset or Parquet package.

## Extraction

```powershell
$olm = "$cache/olmocr/54a96a6fb6a2bd3b297e59869491db4d3625b711/smoke/manifest.json"
dotnet run --file tools/OlmOcrBenchmark.cs -- --manifest $olm --mode native --output artifacts/olmocr-native.json
dotnet run --file tools/OlmOcrBenchmark.cs -- --manifest $olm --mode ocr --output artifacts/olmocr-ocr.json
```

`--assets <existing-assets-directory>` selects an already installed model directory,
for example `$env:LOCALAPPDATA/ContextMole/assets`. OCR verifies the three pinned
PP-OCR files and fails if any is missing or corrupt. It never prepares downloads or
updates their policy files. Default native mode does not load or render OCR pages.

Read [the extraction adapter](../olmocr/README.md) before interpreting category
scores. Canonical source evidence (including boilerplate) and rectangular TSV
table structure are explicit projections. Math rendering checks
are unsupported without upstream KaTeX/browser dependencies; unsupported checks
remain visible in coverage and conservative score denominators. This is an adapted
olmOCR evaluation, not an official leaderboard score. A nonzero exit can represent
measured quality failures or incomplete check coverage; inspect the report.

## Retrieval and source evidence

```powershell
$vidore = "$cache/vidore/0cdf0979f2c5a0fd3e335e6373b9da48a9fe3bc3/smoke/manifest.json"
dotnet run --file tools/VidorePdfBenchmark.cs -- --manifest $vidore --mode semantic --model Granite97M --output artifacts/vidore-semantic.json
dotnet run --file tools/VidorePdfBenchmark.cs -- --manifest $vidore --mode hybrid --model Granite97M --output artifacts/vidore-hybrid.json
dotnet run --file tools/VidorePdfBenchmark.cs -- --manifest $vidore --mode keyword --output artifacts/vidore-keyword.json
```

Semantic/hybrid runs require existing, validated Granite assets; they do not install
models. `--ocr` optionally uses existing verified PP-OCR assets. Without it,
unreadable/scanned page warnings are retained, with absent pages counted in coverage.
The runner indexes full original PDFs through `IndexingCoordinator`, freezes its
owned temporary SQLite index, and evaluates the actual exposed search previews.

Read [the retrieval adapter](../vidore/README.md) for page deduplication, linear
graded nDCG, Recall/MRR, geometric evidence coverage, output caps and literal
citation checks. Supplied OCR Markdown is discarded; it is neither gold extraction
nor indexed text. Supplied reference answers are retained only as annotations and
are not scored. These checks evaluate retrieval and source evidence, not generated
answer correctness.

## Larger runs

```powershell
python tools/DownloadPdfBenchmarks.py --cache $cache --dataset olmocr --subset full
python tools/DownloadPdfBenchmarks.py --cache $cache --dataset vidore --subset full
```

Use `full/manifest.json` in the same runner commands. Full olmOCR has 1,403 PDFs,
7,010 non-baseline checks and nine supplied baseline checks; the adapter separately
identifies any automatic per-PDF baselines. The full ViDoRe manifest has 14 PDFs,
1,110 pages and 636 queries (318 English originals plus Portuguese translations).
All six languages' annotations are cached, but the default evaluation languages
are explicitly English and Portuguese. Four supplied rectangles in that full
selection have zero area. Their original coordinates remain in manifest
diagnostics; every page relevance label is retained, and only positive-area
rectangles contribute geometric coverage. Full runs are optional and may take hours;
PDF downloads are approximately 357 MB for olmOCR and 53 MB for ViDoRe. Real OCR,
indexing/model startup and warmed search timings are reported separately. Use an
idle machine, identical assets, thread limits, and the same subset for comparisons.

Ordinary tests remain offline and contain no corpus downloads or inference.

```powershell
python -m unittest discover -s tests/tools -v
dotnet run --project tests/ContextMole.Tests -- --filter-class '*OlmOcrBenchmark*' --filter-class '*VidorePdfBenchmarkTests*'
```

See [ATTRIBUTION.md](ATTRIBUTION.md) for datasets, licenses, and pinned references.

## First smoke measurements, 2026-10-01

Windows x64, .NET 10.0.11, four inference threads, existing PP-OCRv6 and Granite
97M FP32 assets. [The saved summary](results-2026-10-01.json) includes category and
language results, input/report hashes, model policy, output limits, and limitations.

| Workload | Measured result |
| --- | --- |
| olmOCR native | 8/40 supported checks passed (20%); 10 math checks unsupported |
| olmOCR with real OCR | 18/40 supported checks passed (45%); 10 math checks unsupported |
| ViDoRe semantic @5 | Recall 0.5000; graded nDCG 0.5029; gold evidence area 0.3148 |
| ViDoRe semantic @10 | Recall 0.6991; graded nDCG 0.5477; gold evidence area 0.4632 |
| Literal source citations | 240/240 valid |

Extraction totals include 43 supplied checks and seven added per-PDF baselines.
The conservative category macro is 20.89% native / 31.34% OCR; both reports are
explicitly incomplete, with zero extraction errors. Tiny-text checks improved to
9/12 with OCR, but historical-scan checks passed 0/5 and table checks 0/9.

ViDoRe indexed native text from 42/45 pages and retained 19 OCR-disabled extraction
diagnostics. All relevant pages had some indexed text; image/chart contents can
still be missing. Every result group reached its ten-preview cap. Literal source
matching therefore does not imply the answer's required evidence was retrieved.
These are first smoke observations; larger runs and an OCR-enabled retrieval
comparison remain to be measured.
