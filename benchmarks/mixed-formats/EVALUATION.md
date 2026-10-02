# Mixed-format extraction and search evaluation

## What this covers

The [CC0 authored corpus](README.md) has 16 root files, 34 independent gold facts,
46 English/Portuguese questions (41 positive, 5 negative), and preassigned
23-question development/holdout splits. Fourteen native-only roots contain 32
facts; two OCR roots contain two facts. Formats include DOCX, XLSX, EML, native
PDF, TXT, Markdown, HTML, CSV, RTF, and PNG. EML cases include encoded Unicode
headers, plain/HTML alternatives, HTML-only bodies, DOCX/XLSX/PDF/TXT attachments,
distinct same-name attachments, and an image-only attachment answer.

Dates, numbers, multilingual names, sparse cells, multiple worksheets, supplied
formula caches, uncached formulas, tables and long paragraph/email answers are
included. Gold facts and source locations are authored independently; the
extractor is not used to create expected answers. Files and questions are pinned
by manifest SHA-256 bd308223a0a30ba2de92efd67da2d951ea750a7c15b0ea809d153e70529aa59e.
The generator reproduces all 20 corpus files byte-for-byte.

This is a small synthetic structural regression corpus. The question splits
share documents/facts; they are not an unseen-document test or proof of
real-world generalization. No LLM-generated answers are graded. Fixtures were
not edited in response to model results. Early author-location/lexical-control
annotation corrections are documented in the corpus provenance notes; the
canonical before/after runs use identical frozen inputs and labels.

## Reproduce

```sh
python3 tools/GenerateMixedFormatFixtures.py --verify
# Native-only keyword validation: no models or network needed
dotnet run --file tools/MixedFormatSearchBenchmark.cs -- --output artifacts/mixed-native.json
# Actual 97M inference and cached PP-OCR; the runner does not download models
dotnet run --file tools/MixedFormatSearchBenchmark.cs -- --semantic --ocr \
  --assets /absolute/path/to/installed/assets --iterations 3 --output artifacts/mixed-real.json
```

The runner verifies hashes, copies only fixture bytes into an owned temporary
source directory, creates an isolated SQLite project, and uses the actual
DocumentExtractionRegistry, IndexingCoordinator, 97M embedding generator,
HybridSearchService, compact/full wire responses and passage reading. Document
identities are source-derived, controlling tied-rank identity noise. All copied
fixture source hashes are checked before identity assignment. It does not alter
an application index/settings or use fake semantic vectors.

Production limits are retained: ten groups, two groups per root document,
800-character excerpts, and candidate limit 1000. One/two/three previews are
compared explicitly. Keyword/hybrid runs use author-fixed MUST anchor terms;
semantic runs use the unmodified natural question. Therefore keyword/hybrid
results are not a natural-language keyword-parser benchmark.

Primary Recall@k requires that a ranked compact-response group exposes readable
passage IDs with every exact gold anchor at the correct source, attachment chain
and sheet/cell/structure location. Legitimate merged DOCX paragraph ranges may
contain an author-specified paragraph. Numeric/identifier boundaries prevent
`48` from matching `148`, `-48`, or `48.5`. Same-name attachments must retain
distinct content IDs. The looser source-content metric is conditioned on a
complete extractable gold fact and is not independent of extraction coverage.

Excerpt coverage measures literal visible gold anchors. Compact-read coverage
uses only IDs exposed by compact output; full-read coverage is reported
separately. Every preview is checked against its own passage substring, offsets,
source path, attachment chain and location. Semantically irrelevant sources may
still have valid literal citations. Semantic negative-query abstention is not
asserted; all five lexical negatives must return no groups.

## Measured 2026-10-02, Linux x64 / .NET 10

Pinned Granite Multilingual 97M FP32 and verified cached PP-OCRv6 were used.
The official installer validated the model assets and rejected the quantized
profile on native parity (mean cosine 0.962897; top-10 overlap 88.3%), selecting
FP32. Model setup and indexing are excluded from warm search timings; real
query embedding is included. Three timed searches follow one warm-up per case.
`p95_query_median_ms` is the p95 of per-query median timings, not a pooled p95.
These small local measurements are not a speed guarantee.

Detailed reports: [before](results/2026-10-02-before.json) and
[after](results/2026-10-02-after.json), including passage audits, all queries,
source locations, branch caps, splits, payload and timing.

### Extraction and useful fixes

- Before: 33/34 complete facts: 31/32 native, 2/2 real OCR; the styled human-date fact is missing while its raw-serial control passes
- After: 34/34 complete facts: 32/32 native, 2/2 real OCR; no extraction errors
- Styled XLSX date serial `46345` now gives `2026-11-19 (Excel serial: 46345)`
- XLSX respects 1900/1904 date systems and recognized single-section date/time
  styles. Conditional/multiple-section or unusable styles remain raw, rather
  than inventing calendar values
- Formula results are never evaluated. Existing cached values are read; missing,
  empty or whitespace caches retain the formula with explicit `no cached value`
- Short adjacent image OCR word/label/value blocks now share bounded same-line/
  same-column context and unioned regions. `DISPATCH CODE` searches return
  `ORCHID-619` in a root scan and `ORCHID-690` in the image attachment. PDF
  grouping and the OCR models are unchanged
- The extraction preparation marker advances to `layout-v5`; source-backed
  re-extraction is queued. Existing active evidence stays readable until an
  atomic successful replacement, including failed/pending v4→v5 upgrades

### Previous default (one preview) versus new default (two)

Averages over the 41 positive questions:

| Mode | Compact evidence Recall@5 before → after | Visible anchor coverage before → after | Compact-read coverage before → after | Mean compact bytes before → after |
|---|---:|---:|---:|---:|
| Keyword | 82.9% → 100% | 85.4% → 100% | 85.4% → 100% | 2,407 → 2,605 |
| Semantic | 70.7% → 92.7% | 70.7% → 91.5% | 75.6% → 96.3% | 11,730 → 15,830 |
| Hybrid | 82.9% → 100% | 85.4% → 100% | 85.4% → 100% | 2,457 → 2,670 |

The before/after table combines extraction fixes and the preview-default change.
Same-extraction comparisons are retained in the reports. With final extraction,
semantic compact-read coverage is 85.4%/96.3%/97.6% for 1/2/3 previews; mean
positive-query compact payload is 11,745/15,830/19,728 bytes. Keyword/hybrid
achieve complete answer coverage at two previews; a third gives no further gain.

Two previews are selected from the development comparison: semantic read
coverage is identical for two and three (95.2%), while a third raises semantic
payload by about 25%. On the preassigned holdout, two previews produce 97.5%
semantic visible/read anchor coverage; three produce 100%. The global
800-character excerpt length is not increased. Clients may still request 1–10
previews explicitly.

Warm positive-query median-of-query-median times before→after: keyword
1.00→0.75 ms, semantic 13.08→12.38 ms, hybrid 18.45→18.46 ms. These differences
are descriptive, not a causal latency improvement claim. All 4,625 returned
previews across both reports pass literal/source citation checks. All five
keyword negatives pass across preview configurations.

### Remaining misses and boundaries

Pure semantic retrieval does not expose sender headers for the Portuguese
sender question (q15); headers remain searchable lexically while being excluded
from semantic vectors. At two previews, the approval-PDF reserve fact (q38) has
only half its gold anchors visible/readable; a third preview restores it. The
96.3% semantic compact-read measure therefore means 39 complete answers, one
partial answer and one missing answer out of 41, not 96.3% fully correct answers.
The João Ribeiro delivery-date question (q36) places
the correct workbook eighth, so Recall@5 fails although Recall@10/full evidence
succeeds. The hybrid anchor controls retrieve all three correctly. Semantic negatives
still return nearest neighbors; citation validity does not imply relevance.

MSG has a production extractor but no new representative licensed sample here.
Legacy binary DOC/XLS are outside the supported catalog. Password-protected,
macros, unusual merged office cells, arbitrary email nesting, handwriting,
low-quality/scanned real-world documents, Windows/macOS native behavior and
external-corpus generalization remain unproven by this suite.

The standard aggregate `dotnet test` launcher was blocked by named-pipe IPC
permission in this cloud environment. The supported direct test executable runs
non-IPC tests; broker/named-pipe native protocol tests were excluded, not passed.

Final Release build: zero warnings/errors. Supported non-IPC aggregate:
824/824 passed, zero failures/skips. Native MCP stdio initialize/tools-list smoke
also passed (nine tools, clean protocol stdout, exit zero); its live schema
advertises `previews_per_group` default 2. This discovery smoke does not invoke
broker-backed tools or establish named-pipe broker coverage.
