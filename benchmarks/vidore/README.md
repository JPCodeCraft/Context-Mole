# ViDoRe v3 HR: original PDF retrieval and evidence

This benchmark indexes the original HR PDFs through `DocumentExtractionRegistry`, `IndexingCoordinator`, SQLite and `HybridSearchService`. Supplied OCR markdown and reference answers are excluded from indexing and scoring. It uses installed model assets, makes no model downloads, and creates an owned temporary index instead of using the application index. No LLM or paid service is involved.

Dataset: [`vidore/vidore_v3_hr`](https://huggingface.co/datasets/vidore/vidore_v3_hr/tree/0cdf0979f2c5a0fd3e335e6373b9da48a9fe3bc3), pinned to `0cdf0979f2c5a0fd3e335e6373b9da48a9fe3bc3`. The downloader verifies each viewer response's revision and locked normalized annotation hashes, plus PDF SHA-256 hashes. The runner verifies the supported dataset/revision, each original and copied PDF hash, physical page counts, and a complete page-to-corpus mapping. Zero-based upstream page numbers become one-based physical PDF pages; pixel evidence rectangles become normalized top-left coordinates using each corpus image's dimensions.

The smoke selection contains two complete original PDFs, 45 pages, and 12 closed queries: six English and six Portuguese. IDs are locked in `benchmarks/pdf/datasets.lock.json`. Queries retain every positive page judgment; a relevance label outside the selected PDFs is rejected. The full selection contains all 14 PDFs / 1,110 pages and the locked English/Portuguese query languages (636 queries). It takes substantially longer to index and evaluate. This selection is not the official English-only leaderboard submission protocol.

## Run

Run from the repository root with Python 3 (standard library only) and .NET 10. Choose an external cache directory. Granite97M must already be installed for the default semantic run.

```powershell
$cache = Join-Path $env:TEMP 'context-mole-pdf-benchmarks'
python tools/DownloadPdfBenchmarks.py --dataset vidore --subset smoke --cache $cache
$manifest = Join-Path $cache 'vidore/0cdf0979f2c5a0fd3e335e6373b9da48a9fe3bc3/smoke/manifest.json'
dotnet run --file tools/VidorePdfBenchmark.cs -- --manifest $manifest --output artifacts/vidore-smoke-semantic.json
```

To rebuild the normalized manifest and verify cached inputs without network access:

```powershell
python tools/DownloadPdfBenchmarks.py --dataset vidore --subset smoke --cache $cache --offline
```

For the full selection, change the downloader to `--subset full` and the manifest path to `.../full/manifest.json`. Increase `--timeout-minutes` when needed. `--assets <directory>` selects an existing assets directory without changing application settings. `--model Granite311M` requires that model to be installed already. `--ocr` enables the application's cached PP-OCR assets after all three pinned files are verified; missing assets fail without downloading. Without this flag, native PDF extraction runs with OCR unavailable, and extraction warnings remain visible. Native-only mode avoids preparing page rasters when recognition is unavailable.

`--mode keyword` works without embeddings; its fallback token counting/chunking follows the application's embedding-unavailable path. `--mode hybrid` requires installed embeddings. Both lexical modes use an explicit baseline: up to 64 distinct Unicode letter/number query tokens, optional clauses across body/title/heading, and the application's default minimum-should-match behavior. This is not a hand-tuned multilingual query rewrite. `--k 5,10` and `--iterations 3` are the defaults. Use `--help` for every option.

## What the results measure

This is an adaptation for the output available to a Context Mole consumer. It ranks only `SearchResponse.Results.Previews`, sorted globally by production fused score, then semantic/keyword ranks and deterministic document/page/passage tie-breaks. It deduplicates `(original document ID, physical page)` **before** each top-K cutoff. Repeated chunks cannot inflate page recall or consume extra ranks. Embedded attachments are indexed normally but excluded from this original-page evaluation through the root-only search filter.

The runner requests the maximum supported budgets: 10,000 candidates, 50 groups, 10 previews per group and 50 groups per document. Semantic/hybrid similarity filtering is disabled with threshold -1. Candidate caps, suppressed groups, groups reaching the preview cap, and unique exposed pages are reported. Ten previews from a content group can cover fewer than ten pages. Hidden ranked groups, full evidence IDs, raw vector candidates and relevance labels are never used to improve the ranked page pool.

- **Recall@K** is the number of unique positive pages retrieved divided by every positive page in the selected query.
- **Graded nDCG@K** uses the qrel values themselves as gains: grade 1 is critically relevant and grade 2 is fully relevant. This is the linear-gain `trec_eval` convention used by the [ViDoRe evaluator](https://github.com/illuin-tech/vidore-benchmark/blob/main/src/vidore_benchmark/pipeline_evaluation/evaluator.py). The ideal ranking includes all selected positive pages before truncation to K. Duplicate page judgments use the maximum grade.
- **MRR@K** uses the first positive unique page.
- **Geometric source-region coverage@K** is the union area of gold rectangles covered by valid, exposed source regions on top-K pages, divided by the union area of all gold rectangles for the query. Unretrieved gold pages contribute zero coverage. Rectangle unions avoid double counting overlap and duplicate chunks.
- **Evidence region recall@K** is the fraction of distinct gold rectangles with at least 50% of their area covered. Geometry metrics are null when no gold rectangles are present, and zero when gold exists but no valid regions cover it.
- **Citation validity** checks generation-guarded `ReadPassagesAsync` rows by passage ID, requested-row status, document/content identity, root PDF location and source path. The preview must equal the exact UTF-16 substring at its excerpt start/length. Invalid citations cannot contribute geometric evidence coverage.

Source regions are inherited from extracted blocks and can extend beyond the visible excerpt. Geometry overlap is therefore a source-location check, not proof that the excerpt answers the question. PDF/image crop or rotation differences can affect geometric comparisons. Native extraction can miss information in charts and images. The report includes root pages with indexed text, relevant indexed pages, extraction methods/errors, and query metrics split by language. It does not score reference-answer correctness, completeness, reasoning or faithfulness.

The full English/Portuguese selection has four zero-area upstream evidence rectangles. They retain their page relevance labels and original pixel-coordinate diagnostics, while their zero area is excluded from geometric scoring. `unusable_bounding_box_count` and `annotation_warnings` in the manifest and report make this explicit. The smoke selection has none of these rectangles.

Warm timings include query embedding and retrieval, with one excluded warm-up and three measured searches per query by default. Indexing, citation reads, and output serialization are excluded. The reported p95 is over per-query medians, not pooled request latencies. Compact/full byte counts serialize the actual `SearchWireResponse` projections from the same search result. Machine/runtime, manifest hash, model policy, budgets and raw query timings are included to make comparisons inspectable.

Dataset annotations are CC BY 4.0; original PDF publisher licenses are retained per document in the manifest. The smoke PDFs both declare CC BY 4.0. See [shared attribution](../pdf/ATTRIBUTION.md) and the pinned dataset card. Downloaded PDFs and source annotations remain in the external cache.
