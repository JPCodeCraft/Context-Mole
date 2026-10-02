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

Current semantic/hybrid runs support only corrected Granite 97M and require its existing, validated assets; they do not install
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
These are historical first smoke observations. Both this Windows result and
the early Linux 97M results used legacy BOS-only inputs, omitting required EOS.
Later input correction, larger evaluation and OCR comparisons are recorded in
[the Linux iteration report](results-2026-10-01-linux.md), without rewriting these numbers.

## Reproducible development and held-out selections

For iterative improvements, keep tuning data separate from the final check:

```bash
python3 tools/PreparePdfEvaluationPartitions.py --cache /path/outside/checkout
```

The fixed `ContextMole-heldout-v1` seed ranks original identities with SHA-256,
without inspecting extraction quality or retrieval scores. olmOCR excludes the
seven smoke PDFs, then takes five development and ten held-out PDFs per category
(35 and 70 PDFs), retaining every upstream check for each selected PDF. Categories
with fewer than fifteen non-smoke PDFs fail rather than silently shrinking the test.

ViDoRe keeps all fourteen PDFs and all 1,110 physical pages in both selections.
English/Portuguese translations are paired by the pinned language-block ordinal,
verified using retained original answers and complete page-relevance sets. Pairing
validation does not select query difficulty or supply answers to retrieval. After
excluding the six smoke pairs, identity-hash ranking takes the first third for
development (104 pairs / 208 queries) and the remaining two thirds for held-out
validation (208 pairs / 416 queries). The manifests and `selection.json` freeze
these identities before any candidate results are inspected. Never tune against
the held-out results; report candidate changes, losses, unsupported coverage and
both languages, not just aggregate gains. This dataset remains limited to one HR
publication corpus and does not establish quality across every document domain.

`--offline` regenerates and verifies both partitions from the existing pinned
sources/PDFs. PDF copies can be reused from full/smoke cache directories, always
checking the original locked hash. Dataset licenses and attribution remain in the
cache and each manifest. The standard downloader now uses four concurrent PDF
downloads by default (`--workers 1` through `8`); selection, ordering and hashes
are unchanged. Viewer metadata batches still use their existing bounded retries.

The ViDoRe runner accepts `--include-evidence` to retain only the literal excerpts
already exposed by production search, with source identity, physical page and
citation validation. This enables a separate practical-usefulness review without
hidden passage expansion or answer-driven retrieval. It also writes a per-query
`<output>.progress.jsonl` checkpoint when `--output` is supplied. A checkpoint is
not a completed report, and its partial means must not be presented as full-corpus
scores.

`--preview-diversity compare` compares rank-only and the optional diversified
preview policy on one frozen index, identical model, extraction, query and budgets.
The primary summary remains rank-only; `summary_by_preview_policy` contains both
variants. Report per-language changes and evidence-region losses alongside page
recall: spreading previews across more pages need not preserve all needed evidence
on one page. The default `rank` policy retains the benchmark's original ranking.

`--quality-only` performs exactly one search per query and skips warm-up/repeated
latency measurement. Its warmed-latency fields are null, and the report labels the
single query observation explicitly (not a controlled cold or warmed latency measurement). Use it for broad quality evaluation when
repeated old-build searches are impractical. Never compare those single observations
with the default warmed latency benchmark.

```bash
python3 tools/ComparePdfBenchmarkReports.py --baseline /results/before.json \
  --candidate /results/after.json --pairs /cache/vidore/REV/heldout/selection.json \
  --output /results/paired-comparison.json
# Same-index policy comparison from a --preview-diversity compare report:
python3 tools/ComparePdfBenchmarkReports.py --baseline /results/ablation.json \
  --pairs /cache/vidore/REV/development/selection.json --output /results/ablation-comparison.json
```

The comparison tool checks identical inputs/configuration, retains every query or
check, reports gains and regressions, and uses fixed-seed paired descriptive
bootstrap intervals. Retrieval resamples translation pairs together; extraction
resamples original PDFs within each category because their checks are correlated.
These intervals are not corrected for multiple comparisons and are not official
leaderboard confidence intervals. Embedding policy changes are displayed explicitly.

For subsequent extraction rounds after a panel has been inspected,
`--dataset olmocr --heldout-round 2` or `3` creates new `heldout_v2` / `heldout_v3`
manifests using the next ten identity-hash ranks per category. The original panels
and their results remain unchanged. A repaired candidate's rerun on an inspected
panel is regression feedback, not new held-out validation.

`--comparison-manifest <development/manifest.json>` restricts additional preview
policy searches to those verified development query IDs while a primary rank-only
full-corpus run covers every query. It checks an identical complete document/page
corpus and exact query identity/text/language. `--compare-natural-hybrid` optionally
adds a separately labeled `natural_hybrid_msm0` development variant on the same
semantic index, explicitly requesting `MinimumShouldMatch=0` for optional lexical
boosts. This does not change the structured API defaults or the original hybrid
baseline. Use `--selection <development/manifest.json>` with the comparison tool
when policy rows cover a smaller subset than the primary full-corpus report.

`tools/AuditPdfEvidenceRegions.py` provides a separate post-hoc source-box area
precision/overcoverage diagnostic from an `--include-evidence` report and its
manifest. It unions valid exposed block boxes on top-K pages, checks exact
reproduction of the original evidence-area coverage, and reports overlap divided
by predicted area on all/relevant pages. Provided rectangles may be incomplete,
and inherited block boxes can extend beyond excerpts. This diagnostic does not
change retrieval scores, establish pixel-level crop precision, or grade answer
correctness/completeness. Inspect literal content as well as geometry.

A deliberately controlled OCR on/off pair can be compared using
`ComparePdfBenchmarkReports.py --allow-ocr-change`; the report explicitly records
that changed setting. All input/model/budget equality checks remain active.

## Locked second-domain validation

The [Computer Science reproduction guide](vidore_computer_science/README.md)
defines a separate score-blind panel: both original OpenStax PDFs, all 1,360
physical pages, and 48 fixed English questions with their Portuguese translations.
`tools/PrepareComputerSciencePdfBenchmark.py` reconstructs its exact manifest in
an explicit external cache from pinned public files and checked-in identity-only
metadata. Its optional Python dependencies, source licenses, hashes, and offline
verification are documented in that guide. It does not download corpus images or
use supplied OCR as retrieval input. Once its results have been inspected, reruns
are regression checks rather than a new held-out test.


## Corrected inputs and immutable replay

[The Linux iteration report](results-2026-10-01-linux.md) preserves early BOS-only
97M scores separately from corrected BOS/text/EOS runs. Input limits include all
special tokens for the sole supported 97M model. The report's completed historical 311M comparisons used BOS/text and FP32 with 384 output dimensions; current runners cannot select or load 311M. `--allow-model-change` is explicit and preserves
input/output-budget equality, recording policy differences. Full/partition report
comparisons require each original manifest plus explicit selection verification.

`--index-snapshot-out <owned-index.db>` retains a finalized SQLite backup,
SHA-256/generation/source/model-policy provenance and prepared-text hash/token
ledger. `tools/VidoreSnapshotReplay.cs` opens that snapshot read-only, verifies all
identities and must reproduce every saved development result exactly before
scoring disjoint reused queries. It never extracts, migrates or re-embeds passages.
Use `--validate-only` to perform static identity checks without model inference.
`tools/RunMeasuredBenchmark.py --output <resources.json> -- <built-runner-command>`
records whole-child Linux CPU/RSS without changing quality or claiming warmed
search latency. Use a fresh wrapper process and avoid simultaneous inference.


The current 97M-only follow-up is recorded in [the October 2 evaluation](results-2026-10-02-current97m.md). Earlier 311M comparisons are historical; the application now selects/downloads only 97M.
