# Retrieval quality checks

[SearchQualityBenchmark.cs](../../tools/SearchQualityBenchmark.cs) indexes the
repository-authored [labeled corpus](corpus.json) through the real SQLite writer
and runs `HybridSearchService`. It measures the complete search response, including
grouping, structured clause evaluation, literal excerpts and the compact/full wire
mapping. It creates and removes its own temporary index; it uses existing model
assets and does not change application settings or download models.

```powershell
dotnet run --file tools/SearchQualityBenchmark.cs -- --output artifacts/search-quality-lexical.json
dotnet run --file tools/SearchQualityBenchmark.cs -- --semantic --model Granite97M --output artifacts/search-quality-semantic.json
dotnet run --file tools/SearchQualityBenchmark.cs -- --semantic --model Granite311M --output artifacts/search-quality-311m.json
```

The default run requires no model. Semantic runs require the selected pinned
Granite assets in the normal application data directory, or an existing directory
selected with `CONTEXTMOLE_DATA_DIR`. The corpus has 10 documents, 13 logical
sections and 13 labeled queries. It covers distributed section requirements,
whole-section exclusions, repeated headings, related distractors, accents,
underscore identifiers, compatibility characters, late matches, metadata-only
matches, signatures and boilerplate. Labels select the intended actionable
content/section; the obsolete draft is a deliberate distractor. Semantic-only
runs omit lexical clauses, so they do not enforce the exact structured requirements.

Reported metrics are Recall@5, MRR@5, binary nDCG@5, compact/full UTF-8 JSON bytes,
and three warm search samples for each query. Summary latency percentiles use the
per-query medians. Model loading, indexing, warm-up and passage-read verification
are excluded from search latency; query embedding and vector retrieval remain
included. Every returned preview must equal the literal substring of its own
generation-guarded passage read and retain the same location, or the runner fails.
Lexical runs also fail if any labeled evidence is missed. A semantic branch that
does not complete fails rather than producing fallback results labeled semantic.

Semantic runs compare three explicit representations on identical chunk partitions:

- `raw`: metadata first, uncleaned body and filename context.
- `body_context`: body first and bounded title/heading context, with no cleanup.
- `prepared`: production signature/disclaimer/body cleanup, body first, bounded
  title/heading context, and semantic-ineligible boilerplate.

Both complete raw and prepared representations must fit the production 512-token
limit including BOS. The runner splits a long fixture at shared model-aware source
boundaries, retains complete section text and passage offsets, and rejects inputs
that exceed the limit. This isolates representation changes from silent truncation.
The raw representation is a constructed baseline, not a replay of an earlier build.
The harness exercises search and storage; extraction layout quality is measured
separately by the [PDF/OCR corpus](../extraction/README.md).

## Local measurements, 2026-10-01

Windows x64, .NET 10.0.11, installed Granite 97M FP32, four inference threads.
Corpus SHA-256: `e9577b846967c7b01c2a302ba5113de73d649f5f8e736b469bdc6716080956d9`.
The [saved measurement summary](results-2026-10-01.json) includes the model policy,
all profile summaries and individual rank regressions. Complete per-query reports
are saved under `artifacts/` by the commands above. This small synthetic corpus is a regression workload; its
latency and ranking measurements do not establish general model quality.

Recall@5 was 1 in every row. Bytes are mean serialized UTF-8 response sizes.

| Profile | Mode | MRR@5 | nDCG@5 | Median / p95 ms | Compact / full bytes |
| --- | --- | ---: | ---: | ---: | ---: |
| No model | Keyword | 1 | 1 | 1.1629 / 11.7283 | 2407.15 / 2892.23 |
| Raw | Semantic | 1 | 1 | 13.9130 / 18.4030 | 9443.31 / 12282.46 |
| Body/context | Semantic | 0.961538 | 0.971610 | 11.4180 / 13.9904 | 9470.46 / 12319.38 |
| Prepared | Semantic | 0.961538 | 0.971610 | 11.6146 / 16.0101 | 9271.54 / 12048.69 |
| Raw | Hybrid | 1 | 1 | 13.9273 / 20.8685 | 2474.46 / 2953.31 |
| Body/context | Hybrid | 1 | 1 | 11.7395 / 14.8306 | 2471.31 / 2953.00 |
| Prepared | Hybrid | 1 | 1 | 11.9013 / 13.8284 | 2471.31 / 2953.08 |

The model-free lexical run verified 17 preview anchors. Semantic runs verified
110 raw, 111 body/context and 107 prepared anchors; every lexical/hybrid profile
verified 17. All complete embedding representations fit 512 tokens including BOS.

All 13 lexical and hybrid queries recovered the labeled first result, with
Recall@5, MRR@5 and nDCG@5 equal to 1. Semantic Recall@5 remained 1, while changing
the representation moved the valid `restore/schedule` section from first to second
for `distributed-must`, behind `excluded/draft`: MRR@5 for that query became 0.5
and nDCG@5 became 0.6309297536. The draft combines backup and restore in one
passage; the valid procedure distributes them across two passages. Section
semantic ranking currently uses its best individual vector. The hybrid search
enforces the exact `every night` requirement and keeps the valid procedure first.

Neither competing section has removable signature/disclaimer content. The
`body_context` ablation isolates the change in body/context placement and filename
context from cleanup and reproduced the same regression. Therefore these results do not support a claim that
preparation universally improves semantic relevance. Continue to track the
per-query regression before changing context or section vector aggregation.
