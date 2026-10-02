# Frozen Computer Science PDF panel

This directory makes the previously external, score-blind second-domain panel
reproducible from a checkout. It contains **only small public identity metadata
and fixed selection/provenance locks**, not the PDFs, image shards, OCR text,
query text, or answers. The complete corpus is two OpenStax textbooks with 1,360
physical pages; the selection is 48 English originals plus their 48 verified
Portuguese translations. Every upstream relevance judgment and usable evidence
rectangle for each selected query is retained.

## Reproduce

Use Python with normally importable **PyArrow 21.0.0** and **PyMuPDF 1.26.6**.
These are optional benchmark-only dependencies, not product dependencies. The
reference preparation used Python 3.12.14. The tool does not install anything;
missing imports produce explicit package/version guidance. An existing Python
installation can be supplied through `PYTHONPATH`.

From the repository root:

```bash
# Download only the pinned public card, three Parquet annotations, and two PDFs:
python tools/PrepareComputerSciencePdfBenchmark.py --cache /path/outside/checkout

# Verify/rebuild from those same inputs, with no network:
python tools/PrepareComputerSciencePdfBenchmark.py --cache /path/outside/checkout --offline

# Focused tests need no optional packages, downloads, PDFs, or model inference:
python -m unittest discover -s tests/tools -p test_computer_science_pdf_benchmark.py -v
```

`--cache` is required and must be outside the checkout. The tool discovers the
repository from its own script path, so invoking that path from another working
directory also works. The dataset cache is:

`CACHE/vidore_computer_science/d5cc75883d92e294f0c0fc2662551c9708a06ebc/`

The benchmark input is `heldout_v1/manifest.json`; original PDFs are beside it in
`heldout_v1/pdfs/`. An existing prepared external cache can use the same base
directory. Identical immutable inputs are only read; an altered existing input
or output fails rather than being overwritten or relocked. Offline mode still
requires the official dataset card and the five original binaries already in
that cache. The binary inputs total 65,459,104 bytes. Do not commit the cache.

Online mode uses immutable official Hugging Face resolve URLs and SHA-256
verification. Access denials stop; only transient HTTP errors receive the
existing bounded retry. No live viewer request is necessary, because the exact
previously verified page identity/dimension projection is retained here. This
avoids depending on a public viewer continuing to serve a historical revision.
The two large corpus-image Parquet shards are not downloaded.

## Immutable inputs and output equality

- `selection-protocol.json`: original pre-annotation protocol, unchanged bytes
- `selection.json`: original 48 translation-pair identities and all 215 original
  hash ranks; recomputed from verified Parquet annotations, never simply trusted
- `provenance.lock.json`: original source URLs, binary and normalized-annotation
  hashes, licenses, page/count validation, and historical dependency versions
- `hf-tree.json`: unchanged official immutable-revision artifact listing
- `corpus.json`: unchanged public corpus IDs, document IDs, zero-based physical
  page numbers, widths and heights; no image, OCR, query, or answer fields

All five checked-in files are themselves hash-pinned by the reproduction tool.
The canonical corpus projection (excluding its final newline) has SHA-256
`3ca1a4a1071d73e43f6545f1c9fc4a339f95127f72c1e6422ed157029e821c7e`.
Its full file hash is
`fc9b5c1b0555135d45c4007ba5f300ae5efafa378584d9692e615bfe00701413`.
The complete metadata bundle is approximately 176 KiB. No large data inputs are
vendored. Query/document/qrel projections are regenerated from byte-verified
Parquet inputs and must match their original normalized hashes.

Required output hashes, including canonical JSON's final newline:

| Output | SHA-256 |
| --- | --- |
| Manifest | `794ea81c5c0808ae623ec2a6e2adfcff93894ace4d0c18763e924e02b05e012f` |
| Selection | `fc02b3c46134d967be41cdc08fd9684253231677c6d13d8efcb7b82e759a2327` |
| Protocol | `ab9226fc5c67dddc2eb3b64b09edaca2531cec66eca5378543bbdc18d8532203` |

The tool also requires every immutable provenance field to match, including
2 PDFs / 1,360 pages, 440 positive qrels, 189 distinct positive pages, zero
zero-area selected rectangles, and the PDF publisher notices. It never changes
the seed, pair count, language selection, page candidates, labels, or boxes.

The original `provenance.lock.json` is copied byte-for-byte, retaining its original
dependency versions as historical evidence. Current Python/PyArrow/PyMuPDF
versions and reproduction/helper code hashes are written separately to
`reproduction-runtime.json`. Runtime differences do not permit any manifest,
selection, source, annotation, physical-validation, or other input field to drift.

Verified on 2026-10-01: two offline runs from a separate owned cache containing
only hardlinks to the original PDFs/Parquet/card reproduced the manifest,
selection, protocol, and provenance byte-for-byte. Network calls were explicitly
blocked during verification. The original locked cache and its input modification
times stayed unchanged. Preparation performs source/license validation, not
extraction-quality scoring or model inference.

## Attribution and licenses

- [ViDoRe v3 Computer Science, pinned revision](https://huggingface.co/datasets/vidore/vidore_v3_computer_science/tree/d5cc75883d92e294f0c0fc2662551c9708a06ebc)
- Revision: `d5cc75883d92e294f0c0fc2662551c9708a06ebc`
- Annotation and generated-metadata credit: ViDoRe / ILLUIN Technology and the
  contributors in the [pinned dataset card](https://huggingface.co/datasets/vidore/vidore_v3_computer_science/blob/d5cc75883d92e294f0c0fc2662551c9708a06ebc/README.md)
- Annotations and the vendored identity/dimension projection:
  [Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/)
- Textbooks: OpenStax / Rice University, copyright 2024, separately licensed
  CC BY 4.0 as recorded in source metadata and physical page 4 of each PDF
- Publisher sources: [Introduction to Computer Science](https://openstax.org/details/books/introduction-computer-science)
  and [Introduction to Python Programming](https://openstax.org/details/books/introduction-python-programming)
- Original PDFs remain byte-for-byte with their publisher attribution notices;
  the downloaded dataset card remains in the external cache

The frozen original curation script is adapted into
`tools/PrepareComputerSciencePdfBenchmark.py`. The existing
`tools/DownloadPdfBenchmarks.py` helper is reused without changing its behavior.
Normalized boxes remain top-left image coordinates and physical page numbers
become one-based in the manifest. Reference answers remain annotations only;
supplied OCR markdown is never indexed or used as scoring ground truth.

## Selection and interpretation

Original English query IDs are ranked by ascending SHA-256 of UTF-8 bytes of
`ContextMole-second-domain-v1 + NUL + vidore/vidore_v3_computer_science + NUL + English query ID`,
with integer ID breaking ties. The first 48 are retained. Portuguese partners
are paired by sorted language-block ordinal, requiring equal retained raw
answers and identical complete qrels, grades, content types and rectangles
except query ID. These checks validate grouping; observed difficulty or model
results cannot influence identity selection.

All 1,360 pages remain retrieval candidates, including the 1,171 pages without
selected positive judgments. Comparisons must cluster both translations by the
48 original identities. Textbook questions can still be correlated; this small
two-document panel is an adapted local text-retrieval/evidence evaluation, not
an official visual ViDoRe leaderboard or answer-correctness test. Public source
content might have appeared in model pretraining. After results are inspected,
repaired-candidate reruns on this panel are regression feedback; reproducing it
again does not create fresh held-out evidence.

The reproduction tool prepares inputs only. It does not broaden a benchmark
runner's supported-input allowlist or alter its scoring, extraction, model,
tokenizer, evidence projection, or search budgets.
