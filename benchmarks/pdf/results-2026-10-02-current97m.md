# Current 97M follow-up evaluation, 2026-10-02

This follows the preserved [frozen-v4 evaluation](results-2026-10-01-linux.md).
Granite97M is now the sole selectable/downloadable embedding model and default;
completed 311M measurements remain historical records. The local evaluation uses
97M FP32, 384 dimensions, four inference threads and publisher-correct BOS/text/EOS.
Historical benchmark assets, indexes and reports remain in their separate evaluation
cache. The native app now removes only its 12 known managed 311M cache leaves at
startup, as requested; benchmark runs do not invoke that cleanup.

## Controlled development change

The only retrieval-input rule tested here excludes standalone bullet markers from
semantic embedding. Canonical extraction, lexical/readable evidence and chunk
identity remain intact. The preparation policy is
`layout-v4/spans-v3/body-context-v4`, versus the control's body-context-v3.

Both profiles independently index the same 14 public HR PDFs/all 1,110 pages and
208 locked development texts/104 translation pairs, with the same deterministic
source-document GUID fixture, assets, root-only scope, semantic mode, 50 groups,
10 previews/group, 50 groups/document and 10,000 candidate cap. Preview diversity,
semantic-preview anchors and optional lexical fusion stay off. This control
removes the random independent-index document namespace confound described in the
older report. No query selection or scorer definition changed.

| Development @10 | Stable v4 control | Bullet guard | Paired change, 95% interval |
| --- | ---: | ---: | --- |
| Recall |0.47665770 |0.47858078 |+0.001923 [0,+0.005769] |
| nDCG |0.40966029 |0.41099333 |+0.001333 [0,+0.003999] |
| MRR |0.52681433 |0.52681433 |0 |
| Gold area coverage |0.29286935 |0.29538099 |+0.002512 [0,+0.007535] |
| Gold region recall |0.32472574 |0.32632831 |+0.001603 [0,+0.004808] |

These are small gains in the two members of one translation pair; no development
metric regressed. Fixed-seed 2,000-sample translation-cluster intervals touch zero,
so this is not evidence of a broad ranking advance. EN1818/PT1903 newly expose
short literal passages on physical pages 21/44 stating that 75% or more of workers
are aged 25–55. Neither profile previously exposed that specific age fact. It answers
only the age-range part of the compound question; the hours/occupation part is
not independently graded.

All 21,073 passage IDs and canonical display/body/title/heading/filename/content,
location and section-offset fields match. Exactly 506 pure `•` passages become
semantically ineligible; all 19,209 retained prepared strings and vector bytes match
exactly. Stored path strings differ only by temporary benchmark-workspace prefixes.
Page coverage remains 1,091/1,110 and every exposed literal citation passes.

## Reused CS regression

The complete two-textbook/1,360-page panel and 96 fixed texts were already inspected
in the previous evaluation. This follow-up is **reused regression feedback**, not
fresh validation, and was not used to tune another change.

Recall@5/@10, nDCG, MRR and region recall remain exactly equal across all 96 questions:
@10 Recall 0.63819254, nDCG 0.63460626, MRR 0.82876157 and region recall 0.31509102.
The mean area value changes 0.3107976611→0.3107976392 (−2.19e−8). One Portuguese
query replaces a pure bullet's tiny overlapping source box with a cache-miss
paragraph on the same page; page ordering and best-anchor boxes remain unchanged.
This negligible geometric difference is preserved, not dismissed as floating-point
noise or labeled a factual answer loss.

All 29,719 canonical passage identities/locations and lexical content remain intact.
Exactly 1,686 standalone `•`, `▪` or `◦` passages become semantically ineligible.
All 26,522 retained prepared strings and vector bytes match exactly. Indexed pages
remain 1,355 and literal citations 100%; the same 100 OCR-disabled page diagnostics
remain visible. Source boxes can be wider than excerpts, so area coverage does not
establish answer correctness or sharper locators.

## Resource observations and final-source verification

| Complete workload | Elapsed | CPU time | Peak child RSS |
| --- | ---: | ---: | ---: |
| HR v4 stable control |789.4s |2,670.4s |1.583GiB |
| HR bullet-guard candidate |777.2s |2,598.5s |1.518GiB |
| Reused CS bullet-guard candidate |601.4s |2,099.0s |1.166GiB |

These one-run observations include model startup, full indexing, quality searches,
citation reads/scoring and serialization; builds/test activity can affect elapsed
time. They are not controlled warmed-query speed measurements. CPU includes all
threads; RSS is a Linux child-process high-water mark, not managed allocation.

The 01:49 production-source checkpoint exactly reproduced all 208 saved HR candidate
rows from a read-only snapshot. The final integrated source replay also exactly reproduced HR 208 plus CS 96
saved rows: all ranks, scores, excerpts, identities, citations and metrics, with
unchanged snapshot hashes. It performed no reindexing/new evaluation and never
invoked native UI startup. Completed receipts are in the provenance ledger.

Integrated Release verification before the version-provenance correction: 679 tests,
663 passed, 15 known sandbox IPC
`PermissionDenied` failures matching the previous environment baseline, one Windows
desktop skip; Python 47 passed; build zero warnings/errors. This also covers preserving pre-v8
canonical/FTS evidence through the repaired 007/008 migration path, while marking
old preparation explicitly stale. Fresh benchmark indexes contain no old data.

The final UI changes remove three `Consolas` overrides so the existing bundled
Inter fallback renders paused projects on Linux, and add a Settings version label
from the UI assembly's canonical version metadata. The label preserves prerelease
text and strips commit/build metadata; all 19 version tests pass. Actual isolated
Linux native execution initially showed **Version 1.0.0**, the sole **Granite Multilingual 97M**
model and **Document OCR Ready**. Core, Documents, Indexing, Infrastructure, Search,
Storage, Broker and Protocol Release binaries remain byte-identical to the pre-UI
verification checkpoint. Their source inputs and the separately copied replay
binaries are unchanged, so the exact 304-row replay still applies; these display
changes need no model rerun.

In the disposable native cleanup fixture, startup removed 11 of the 12 allowlisted
311M leaves and deferred the locked file. After lock release and restart, it removed
the final leaf. Protected 97M/shared assets, source files and stored literal/FTS
evidence remained intact; historical benchmark model weights were not cleaned.
Windows/macOS native cleanup and successful live IPC remain unexecuted. Fourteen
Windows-only test bodies also return early on Linux despite runner-reported passes.
The documented Unix limit remains: a concurrent same-user rename after the final
identity checks can race anchored deletion; the check does not establish an
atomic exact-handle unlink. Native proof, screenshots and final build/test hashes
are retained in the ledger.

## Version presentation correction

The initial screenshot's `Version 1.0.0` came from the .NET SDK default used by
an unversioned local Release build. It was not a published Context Mole release.
The release workflow already validates a `vMAJOR.MINOR.PATCH` tag, passes that
version into build/publish, and validates the packaged product version. The live
GitHub API reported published `v0.2.9` on October 2; that version was not assigned
to this modified local binary.

UI-only assembly provenance now distinguishes supplied version inputs from SDK
defaults. The corrected native Settings screen shows **Development build**.
Explicit versions, including genuine `1.0.0`, tagged versions and prerelease/dev
labels, remain visible without noisy commit metadata. The updater and other
projects' version semantics are unchanged. The focused Release build passed with
zero warnings/errors and all 35 version tests passed. The eight retrieval/broker
pipeline DLLs are byte-identical to the earlier verified batch; no long benchmark
was rerun. The prior 679-test totals describe the earlier integrated checkpoint,
not a newly executed full suite after this UI-only correction.

The cloud workspace was subsequently replaced. The final delivered source ZIP
was restored with its checksum and every one of its 331 file hashes verified.
Prior external raw snapshots/model caches were not included in that source ZIP;
the checked-in reports and provenance hashes remain available. Current build,
focused-test and corrected native-image receipts are recorded in the ledger.

## Reproduce and audit

Use the pinned manifests and already validated 97M/PP-OCR assets from the original
[benchmark instructions](README.md). The new candidate source is 97M-only.

```bash
dotnet run --file tools/VidorePdfBenchmark.cs -- \
  --manifest /cache/vidore/0cdf0979f2c5a0fd3e335e6373b9da48a9fe3bc3/development/manifest.json \
  --mode semantic --model Granite97M --assets /validated/assets \
  --include-evidence --quality-only --stable-document-ids \
  --index-snapshot-out /results/current97m-index.db --output /results/current97m-development.json
python3 tools/ComparePdfBenchmarkReports.py \
  --baseline /results/stable-v4-control.json --candidate /results/current97m-development.json \
  --pairs /cache/vidore/0cdf0979f2c5a0fd3e335e6373b9da48a9fe3bc3/development/selection.json \
  --output /results/paired-development.json
```

The [provenance ledger](results-2026-10-01-linux-provenance.json) retains raw-report,
input-equivalence, qualitative review, source/binary freeze and replay hashes.
Dataset/model identities, attribution and unsupported extraction denominators are
preserved in the original report and [ATTRIBUTION.md](ATTRIBUTION.md). This adapted
consumer-output evaluation is not an official leaderboard or generated-answer test.
