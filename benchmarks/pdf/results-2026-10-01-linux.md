# Local PDF extraction and retrieval evaluation, 2026-10-01–02

**Status:** the frozen-v4/current 97M extraction and retrieval evaluation is complete,
including the untouched CS panel and read-only HR replays. The completed 311M
experiments are retained as historical exploration; further model work has stopped.
Subsequent CS-driven repairs must treat this panel as reused feedback.

The later current97M-only follow-up and reused regression are documented in
[the October2 report](results-2026-10-02-current97m.md).

## Conclusions supported so far

- Geometric/native table extraction improves the locked development corpus and
  three successive fresh 70-PDF panels. Early iterations also caused genuine
  losses; their original scores are preserved below, alongside subsequent repairs.
- Frozen layout-v3 retrieval improved evidence-area coverage, but its fresh HR
  page-ranking gains were small and uncertain. Larger block boxes also became
  less selective. Higher area recall does not establish more precise locators.
- The original 97M embedding inputs omitted the publisher-required EOS token.
  The corrected token/input pipeline improves development nDCG@10 by 0.0261,
  with paired descriptive uncertainty excluding zero. This is not an EOS-only
  causal experiment: complete token accounting can change context trimming/chunks.
- Preview diversification, equal-weight optional lexical fusion and the tested
  excerpt-only reranker did not provide a consistently defensible improvement.
  These experimental options remain off. Automatic answer accuracy was not tested.

Raw reports, snapshots and original public PDFs remain outside the checkout.
[The provenance ledger](results-2026-10-01-linux-provenance.json) records report
SHA-256 values; the pinned corpus manifests retain document hashes and licenses.
No paid API, private document upload, deployment or user-computer access was used.

## Inputs, selection and score meanings

The pristine source baseline is commit `9970ef8`. Runs use Linux x64,
.NET 10.0.11 and at most four inference threads. Selection was fixed from pinned
public identities before inspecting scores; it was not cherry-picked for quality.

### Native extraction

olmOCR-bench revision `54a96a6fb6a2bd3b297e59869491db4d3625b711`.
The `ContextMole-heldout-v1` protocol hashes seed, category and original PDF ID;
excludes the seven smoke PDFs; takes five/category for development; then the
next ten/category for each of three sequential panels. All checks from every
selected PDF are retained. Panels contain 35 development or 70 validation PDFs.
An inspected panel becomes regression feedback for subsequent changes.

The local consumer-output adapter is documented in
[the olmOCR adapter](../olmocr/README.md). It evaluates canonical extracted text
and explicit rectangular TSV table projections. Unsupported mathematical-rendering
checks remain visible, including in conservative denominators. These adapted
scores are not official olmOCR leaderboard scores. Counts below give supported
checks explicitly rather than pretending all upstream checks were evaluated.

A full-corpus download was attempted; 356/1,403 original PDFs were retained before
the proxy returned a tunnel HTTP 403. That denied route was not bypassed. All
reported panels are complete; no full-1,403-PDF quality claim is made.

### Retrieval

ViDoRe v3 HR revision `0cdf0979f2c5a0fd3e335e6373b9da48a9fe3bc3`:
14 complete PDFs, 1,110 physical pages, 636 English/Portuguese texts (318 pairs).
After excluding six smoke pairs, hash ranking selected 104 pairs/208 development
texts and 208 pairs/416 initial validation texts. Every partition keeps all 14
PDFs and every page; complete qrel sets and verified translations are retained.
The 416 questions are **reused diagnostic evaluation after the input/model
iteration**, not a newly untouched test for choosing a model.

A second, independently curated domain is ViDoRe computer science revision
`d5cc75883d92e294f0c0fc2662551c9708a06ebc`: two complete OpenStax PDFs,
1,360 pages, 48 hash-frozen English/Portuguese pairs/96 texts, 440 positive qrel
rows and 189 distinct positive pages. No extraction/retrieval outputs informed
selection. Its manifest SHA-256 is
`794ea81c5c0808ae623ec2a6e2adfcff93894ace4d0c18763e924e02b05e012f`.
See [second-domain reproduction](vidore_computer_science/README.md).

The [ViDoRe adapter](../vidore/README.md) deduplicates exposed passages to physical
pages before K; uses linear graded nDCG and all positive relevance labels; and
checks preview identity, page, generation and exact UTF-16 substring against
canonical stored text. Reference answers and supplied OCR Markdown are not indexed
or used to retrieve. Results are actual production-service consumer output, capped
at 50 groups, 10 previews/group, 50 groups/document and 10,000 branch candidates.
Semantic threshold is −1 with no strict threshold, root attachments only.

Four HR gold rectangles have zero area. Their original diagnostics and page labels
are retained, but they cannot contribute geometric area. Area coverage unions
exposed source boxes against positive-area gold rectangles; region recall requires
at least 50% coverage. Citation validity proves literal traceability, not that the
requested fact is inside the excerpt. Excerpts can be clipped to 800 characters
while inheriting a larger block box. Geometry and factual usefulness are therefore
reported separately.

Intervals below use 2,000 fixed-seed paired bootstrap samples: original PDF
clusters stratified by category for extraction, verified translation-pair clusters
for retrieval. They are descriptive, uncorrected for repeated comparisons, and
not official leaderboard uncertainty estimates.

## Extraction iterations, including losses

| Scope and frozen candidate | Baseline supported | First candidate result | Pass gains / losses | Table checks |
| --- | ---: | ---: | ---: | ---: |
| Development 35, final v3 | 47/145 | 57/145 | 10 / 0 | 0/22 → 10/22 |
| First fresh 70, v1 | 113/318 | 118/318 | 8 / 3 | 12/63 → 18/63 |
| Second fresh 70, v2 | 96/298 | 110/298 | 15 / 1 | 10/52 → 24/52 |
| Third fresh 70, v3 | 88/297 | 103/297 | 15 / 0 | 3/40 → 14/40 |

Unsupported math checks were respectively 92, 163, 220 and 139. Total check
counts, including automatic PDF baselines, were 237, 481, 518 and 436. No extraction
errors occurred in these complete panels. Third-panel supported pass rate was
29.63% → 34.68%; the table PDF-cluster interval was wide (0 to +56.8 percentage
points), reflecting the small number of independent table PDFs.

V1 losses exposed false bibliography tables and merged-header grid shortcomings.
V2 repaired those while preserving the first result; reused first-panel feedback
became 125/318. V2's fresh-panel loss came from a 7.2-point textual row-label indent
exceeding the previous alignment tolerance. V3 allows a bounded 1.25-em textual
label indent while retaining strict numeric/data-column alignment. Reused second
panel became 111/298. These repaired-panel scores are feedback, not fresh validation.

Final layout-v4/native reruns preserved every final-v3 check status on development,
first/second feedback panels and the third validation panel. Native smoke is
9/40 versus pristine 8/40; no earlier v3 smoke-native report exists to isolate that
one gain to v4 alone. Real PP-OCR development passed 61/145 → 71/145, with ten gains,
no losses and table checks 2/22 → 12/22. The 92 unsupported checks remain visible.

## Frozen-v3 retrieval, before the input correction

**Historical input defect:** both pristine and frozen-v3 97M runs below used legacy
BOS-only inputs. The original Windows smoke summary also did so. Those numbers are
preserved as historical measurements and are not relabeled as publisher-faithful.

On the 416 questions first unblinded only after rank-only policy decisions froze:

| @10 metric | Pristine | Frozen v3 | Paired change, 95% interval |
| --- | ---: | ---: | --- |
| Page Recall | 0.47131 | 0.47739 | +0.00608 [−0.0066, +0.0185] |
| Graded nDCG | 0.42882 | 0.43019 | +0.00137 [−0.0070, +0.0099] |
| MRR | 0.54950 | 0.54345 | −0.00605 |
| Gold evidence-area coverage | 0.23406 | 0.25848 | +0.02441 [+0.0079, +0.0429] |
| Gold evidence-region recall | 0.25364 | 0.28155 | +0.02791 [+0.0095, +0.0487] |

All exposed citations passed literal identity/location checks. A post-hoc
source-box diagnostic reproduced the existing area scores, then measured annotated
intersection divided by predicted area. At @10, mean all-page precision decreased
0.12949 → 0.12151; relevant-page conditional precision 0.56674 → 0.55170 (335 versus
334 available queries). Predicted area increased 1.46996 → 1.65937. Gold annotations
may be incomplete; this is annotation-relative box selectivity, not a definitive
crop-quality score. It nevertheless prevents claiming that more inclusive boxes
became sharper locators.

New temporary passage GUIDs can affect exact ties before preview caps. The exposed
fused ranks had no exact score ties, but v3 has 1,143 duplicate-vector groups
covering 4,380/19,713 vectors and 187/636 queries with possible duplicate-similarity
cap boundaries. Pristine vectors were not retained. This diagnostic cannot prove
small changes are independent of all tie-order effects. One concrete gain, English
Q62, uses a unique target vector and newly exposes the actual five-to-three-point
fixed-term-contract gap on physical page 61; it is stronger evidence than a marginal
aggregate page-rank improvement.

Other reviewed development cases illustrate the limits: Portuguese Q1761 newly
exposes two Estonia research-workforce challenges but clips the third; Q1641 ranks
an already returned 2.4% wage-growth fact better; and Q36/Q1541 gain a relevant A1
page but expose a regulatory footnote rather than the requested country-specific
cause. A relevant page gain is not automatically an answer-bearing gain.

## Corrected 97M token/input pipeline

Pinned 97M configurations require BOS + text + EOS. `CountTokens` and generation
now account for the complete model template; tokenization policy is `bos-eos-v1`.
The preparation policy is `layout-v4/spans-v3/body-context-v3`. A native source audit
verified all 14 original HR PDF hashes, zero U+00AD glyphs and zero changed canonical
sections from the new discretionary-hyphen display prepass. Full reindexing was
still required for corrected embedding inputs and token-aware preparation.

The old report contains 636 questions and the new report 208. Comparison explicitly
verified each original report hash against its own manifest, identical complete
PDF/page corpora, and exact shared query/annotation/qrel inputs; it did not silently
relax manifest equality.

| Development @10 metric | Frozen v3, legacy inputs | Corrected 97M | Paired change, 95% interval |
| --- | ---: | ---: | --- |
| Recall | 0.46032 | 0.47666 | +0.01633 [−0.01500, +0.04551] |
| nDCG | 0.38351 | 0.40965 | +0.02614 [+0.00319, +0.04741] |
| MRR | 0.49172 | 0.52681 | +0.03509 [−0.00374, +0.07135] |
| Evidence area | 0.26643 | 0.29287 | +0.02644 [+0.00782, +0.04667] |
| Evidence region recall | 0.29367 | 0.32473 | +0.03105 [+0.00802, +0.05386] |

English/Portuguese nDCG deltas are +0.02302/+0.02925; Recall deltas
+0.02518/+0.00749. Corrected index: 21,073 root passages, 19,715 vectors,
1,091/1,110 pages, 100% literal citations. Complete prepared token maximum405 and
query maximum60 are below their 512/256 budgets. Of 20,368 canonically aligned
old/new chunks, 309 prepared hashes changed; 703 old and 705 new chunks did not
align after token-aware partitioning. Unmatched chunks are not proof of source
text loss. Full-index elapsed638.4s had possible build contention and is not a
controlled speed claim.

### Identity/tie sensitivity of the aggregate change

Fresh temporary indexes assign new document GUIDs: all 14 differ, with no shared
passage IDs. Production same-document reindexing normally preserves document scope
and stable structural passage identity, so this benchmark's identity changes are
an additional confound. A separate raw-vector audit found duplicate-vector cap
risks in 60/208 legacy-v3 questions and 65/208 corrected questions; 102 had a detected
risk on either side. Corrected output has raw similarity ties in 131 questions even
though exposed fused/RRF scores have no exact ties. Two collisions also occur
between byte-distinct vectors, including semantic ranks 9/10 for Q1724. Unique
vector bytes do not imply unique raw similarity.

On the diagnostic no-detected-cap-risk 106-question subset, nDCG@10 improves 0.02065,
with a translation-cluster interval −0.00528 to +0.04789. On its complete-pair
70-question/35-pair subset, the delta is +0.02600, interval −0.00353 to +0.06002.
Direction persists, but uncertainty includes zero; the subset is not a new
validation panel and some page-anchor ties remain. The whole-sample interval should
therefore not be described as conclusive source-only causal evidence.

The three concrete EN262/PT1898/EN1821 newly returned fact-bearing passages are
byte-unique. Their corrected same-document positions 4/6/3 and raw-similarity
margins above the tenth-preview cutoff 0.00858/0.00222/0.00473 make their inclusion
invariant to known GUID tie ordering. Baseline raw query scores are unavailable,
so accidental equality between its distinct vectors cannot be ruled out fully.
The audited factual gains are stronger practical evidence than tiny aggregate
rank changes, without proving universal improvement.

### Practical corrected-input examples and regressions

A separate development-only audit verified 4,382 exposed records across 16 selected
questions against both immutable snapshots: exact passage/content/document IDs,
active revisions, original PDF hashes, physical pages and UTF-16 excerpt slices.
These selected examples are not an exhaustive answer-accuracy score.

- English Q262 newly returns the Hungary youth-employment row: 28.5% in 2019 versus
  27.2% in 2020, on physical page 126. The baseline returned the page but none of its
  returned full passages contained the answer row. The new row is semantic rank 20;
  page rank improves 5 → 4. Source table headers supply the country/year context.
  Its Portuguese counterpart already returned that fact, so it is not a second
  independent recovery.
- Portuguese Q1898 newly returns France's 465,000 cross-border-worker figure in
  the page 46 full passage (absent → page rank 5). The number begins at UTF-16
  offset 1289, beyond the unchanged 800-character preview. It is a useful subsequent
  `read_passages` result, not an answer-bearing first excerpt or a complete answer
  to the query's additional demographic comparison.
- English Q1821 newly returns the GNDI 5% table passage on physical page 13 (absent
  → rank 3). The percentage begins at offset 1270, also beyond the 800-character
  preview. Portuguese Q1906 already returned the full table; its page 4 → 1 gain
  is better ranking of existing evidence. The opt-in semantic-preview flag is off.
- Portuguese Q1791's explicit demographic/green/digital transition answer drops
  from page rank 10 → 29 (semantic 49 → 304), leaving the first ten pages but still
  available deeper. English Q53's explicit 48%/16% job-strain comparison moves
  page 4 → 6, and Portuguese Q1745's 17.5% → 15.7% financial-distress comparison
  page 4 → 9. These are real discoverability regressions, not complete deletion.
- English Q129's judged bibliography page 8 → 1 gain does not itself answer the
  Principle17 question. An explanatory passage was already returned separately.
  Conversely, Q172's caption-only age-table page 1 → 8 loss does not establish loss
  of age-group numbers: neither caption carried them.

The corrected-input @10 box diagnostic on all 208 questions raises mean
annotation-relative all-page precision 0.10190 → 0.10468, while predicted area rises
1.78723 → 2.00217 and irrelevant-page area 1.46962 → 1.65460. Conditional relevant-page
precision on the same 159 available queries improves 0.50837 → 0.52818. This remains
an inclusive-area tradeoff, with incomplete annotation and full-block box caveats.

## Historical 311M model exploration (completed before current-model restriction)

The 311M run uses the same 208 development texts and complete 1,110-page corpus,
FP32 and 384 output dimensions. Recorded extraction/search/indexing/infrastructure
MVIDs match the 97M run. The launch directory was subsequently rebuilt while 311M
was live; matching MVIDs alone do not prove complete immutable-binary equivalence.
Both snapshots subsequently reproduced all 208 saved development rows exactly
under the copied final production-service replay, including ranks, scores, literal
excerpts and citations. Snapshot SHA-256 values were unchanged; the native HR
source audit found no soft-hyphen-sensitive content. This establishes observed
query-output equivalence on the saved development rows, not retroactive
byte-for-byte immutability of the earlier launch directory.

| Development @10 metric | Corrected 97M | 311M | Paired change, 95% interval |
| --- | ---: | ---: | --- |
| Recall |0.47666 |0.49505 |+0.01839 [−0.01819,+0.05534] |
| nDCG |0.40965 |0.42590 |+0.01626 [−0.02042,+0.05299] |
| MRR |0.52681 |0.53963 |+0.01281 [−0.04155,+0.06460] |
| Evidence area |0.29287 |0.31081 |+0.01794 [−0.00192,+0.04230] |
| Evidence region recall |0.32473 |0.33854 |+0.01381 [−0.00864,+0.04167] |

English/Portuguese Recall changes are −0.00114/+0.03792 and nDCG
+0.00416/+0.02836. English region recall decreases 0.01126 while Portuguese improves
0.03888. Mean gains are uncertain on this small development sample. The post-hoc
all-page annotated-box precision improves 0.10468 →0.12633 (paired interval
+0.00643 to +0.03790), with predicted area 2.00217 →1.78124 and irrelevant-page area
1.65460 →1.42514. Conditional relevant-page precision on paired available queries
changes 0.54135 →0.56822 with uncertainty including zero. The geometric result is
encouraging but does not grade completeness of the literal answer.

311M produced 21,298 root passages/19,940 vectors and indexed 1,091 pages, with 100%
literal citation validity. The observed index took 35.0 minutes versus 10.6 for 97M;
shared builds/native analysis can affect elapsed time. Whole-index CPU/RSS was not
captured. Prepared inputs have 19,980 unchanged canonical chunk matches; 385 of these
have changed prepared hashes, while 1,093 old/1,318 new chunks do not align. Different
publisher tokenizers change chunking and 64-token context trimming. This compares
models with their legitimate tokenization/preparation, not two embeddings over
byte-identical prepared text. 311M complete prepared maximum 440 is below 512.

The selected cloud benchmark profile is 97M. Product defaults were not changed.
Completed 311M measurements are historical records, with no further 311M work. Optional ranking flags remain off. A pristine 9970ef8/legacy 97M profile was also frozen before any CS results, to
measure whole-pipeline before/after. The reused 416 HR questions and frozen 96-text
CS panel will not be used to tune
weights, preview policy, model input format or query selection.

### Reused 416-question HR diagnostic

The prior 416 questions were evaluated only after policies/model profiles froze,
using read-only snapshots. They are explicitly reused after earlier HR feedback,
not a fresh generalization panel or data for tuning. Each replay first exactly
reproduced all 208 saved development rows with the final source. Every exposed
citation passed identity/location/literal checks; neither snapshot changed.

| Reused HR @10 | Frozen v3 legacy 97M | Corrected 97M | 311M |
| --- | ---: | ---: | ---: |
| Recall |0.47739 |0.48482 |0.53789 |
| nDCG |0.43019 |0.45450 |0.49277 |
| MRR |0.54345 |0.58105 |0.61787 |
| Evidence area |0.25847 |0.27715 |0.31034 |
| Evidence region recall |0.28155 |0.30200 |0.34832 |

Corrected 97M versus v3 nDCG delta is +0.02431 [paired 95% interval +0.00719,+0.04123];
Recall +0.00744 [−0.01194,+0.02590], area +0.01868 [+0.00693,+0.03112].
311M versus corrected 97M Recall improves +0.05307 [+0.02403,+0.08480],
nDCG +0.03826 [+0.01098,+0.06657], area +0.03319 [+0.01225,+0.05810] and region recall
+0.04632 [+0.02252,+0.07338]. English/Portuguese Recall deltas are +0.04476/+0.06138,
nDCG +0.03980/+0.03673. Intervals are conditional on this fixed HR corpus and the
fresh-index identity namespace caveat; translation-pair resampling does not model
all shared-topic/document correlations or establish out-of-domain behavior.

The paired all-page box-precision diagnostic is 0.11918 →0.12464 for corrected 97M
→311M, interval−0.00570 to +0.01609. Predicted area changes 1.80306 →1.77021;
irrelevant-page area 1.43101 →1.34246. Conditional relevant-page precision on the
same 326 available queries changes 0.55477 →0.55101, interval including zero. Region
coverage improves, but a definitive sharper-locator claim is not supported.

The fixed sixteen-query practical panel was carried forward unchanged. All 4,357
records across the two model pools passed snapshot/active-revision/source/page/
UTF-16 checks, and all 14 original PDF hashes were verified again. 311M promotes the
Hungary youth-employment answer row from page rank 4 →1, the 48%/16% job-strain
comparison 6 →2, and Portuguese financial-distress figures 9 →1. It moves the France
465,000-worker passage 5 →14 and GNDI 5% passage 3 →6 in English/1 →7 in Portuguese.
The adult-learning 65.7%/25.1% fact shifts 9 →13 and crosses the 800-character preview
cutoff in its differently chunked passage. These tradeoffs remain visible;
311M does not improve every factual question.

Whole replay processes include 208 verification searches plus 416 reused searches,
all citation reads/scoring, model startup and serialization. 97M peaked at 1.794 GiB
RSS, using 554.9 CPU seconds over 436.1 elapsed seconds; 311M peaked at 3.023 GiB,
using 614.9 CPU seconds over 450.2 elapsed seconds. 311M may overlap a 2-CPU baseline
harness build. These are full-process workload measurements, not warmed search
latency or pure embedding CPU. Neither run overlapped another model inference.

## Untouched computer-science validation: current 97M pipeline

All three profiles were locked before any CS scores: pristine 9970ef8/legacy 97M,
corrected 97M and 311M. They use the same complete 2-PDF/1,360-page corpus and 96 frozen
texts/48 pairs. A common benchmark-only `source-document-guid-v1` fixture assigns
initial document IDs from SHA-256 of fixed seed, dataset/revision, original PDF ID
and source hash, before any indexing. Production derives content/section/passage
IDs normally. It cannot modify running/nonempty/private indexes, verifies source
bytes and foreign keys, and preserves all non-identity fields transactionally.
Eight focused tests passed. Both candidate profiles use one copied, hash-locked
runner; the pristine adapter changes only harness/scorer files while all 141
pristine product source files remain byte-identical to 9970ef8. The comparison tool
rejects differing/missing stable protocols, maps, source hashes or IDs; an old
report with no identity field is treated as generated-v7, not as a stable fixture.

Stable source IDs are `be30f4e5-5414-61ac-443c-aaf4bf7bdf32` for the computer-science
book and `70521972-ff41-f5a5-87c2-600064b10154` for the Python book. Fixture maps,
source/binary hashes and model policies are in each report/snapshot provenance.
This removes the random independent-document namespace confound for this fresh
comparison; different extraction/tokenizer chunks can still have different derived
identities and true similarity ties remain possible.

The first corrected 97M attempt was interrupted by an environment reset before any
query scored: generation 0, two staging revisions, no derived content/passages/
vectors. Its SQLite backup/log are preserved, with the original execution handle
confirmed missing. It is not counted as a completed quality/resource run. A new
attempt used the same frozen corpus/policies/runner and completed normally.
Independent shell calls have separate PID namespaces; progress is checked through
owned running handles, SQLite and namespace-local wrapper sampling.

Completed corrected 97M @10: Recall 0.63819, nDCG 0.63461, MRR 0.82876, area 0.31080,
region recall 0.31509; every literal citation passed. English/Portuguese Recall is
0.65201/0.62437 and nDCG 0.64677/0.62244. It indexed 1,355/1,360 pages,
29,719 root passages and 28,208 vectors in 528.1s. Complete prepared-token mean 44.95,
maximum 386 and zero over 512. The full process took 607.7s, 2,150.4 CPU seconds and
1.142 GiB peak RSS. These are one complete indexing+quality/citation/scoring process
observations, with model/startup and OS cache effects included.

The pristine/current 97M comparison retains the same FP32 model assets, corpus,
questions, output budgets and stable document map. The baseline product sources
remain byte-identical to 9970ef8 and retain the historical BOS-only defect. This is
an end-to-end pipeline comparison across extraction, preparation and corrected
inputs, not a pure EOS-only or isolated layout ablation.

| Fresh CS @10 metric | Pristine legacy 97M | Current corrected 97M | Paired change, 95% interval |
| --- | ---: | ---: | --- |
| Recall |0.59661 |0.63819 |+0.04158 [+0.00330,+0.08186] |
| nDCG |0.57982 |0.63461 |+0.05479 [+0.02455,+0.08666] |
| MRR |0.76209 |0.82876 |+0.06667 [+0.02795,+0.10851] |
| Evidence area |0.24108 |0.31080 |+0.06971 [+0.03012,+0.11698] |
| Evidence region recall |0.24011 |0.31509 |+0.07498 [+0.03219,+0.12474] |

English/Portuguese Recall gains are +0.05816/+0.02500 and nDCG
+0.05495/+0.05462. This fixed 48-pair/two-textbook panel supports a pipeline gain,
with conditional descriptive uncertainty; it does not establish all-domain answer
accuracy or account for every shared-topic/document dependence.

All 96 texts completed in every frozen profile, with 100% literal citations and
identical 1,355/1,360 indexed-page coverage. Pristine created 32,156 passages/30,636
vectors, versus current 29,719/28,208. Pristine index time 546.0s versus 528.1s current;
complete process time 640.9s versus 607.7s, CPU 2,235.4s versus 2,150.4s, peak RSS
1.137 GiB versus 1.142 GiB. These are single full-process observations, not warmed
search or a controlled repeated throughput claim. All snapshot hashes, complete
manifest/query sets, successful exits and fixture maps were independently checked.

At @10, all-page annotation-relative box precision is 0.20619→0.20658 (interval
−0.03124 to +0.02722). Conditional relevant-page precision on 90 paired available
questions is 0.69162→0.67178 (interval includes zero). Predicted area increases
0.50990→0.68941 and irrelevant-page area 0.34972→0.46952. More inclusive evidence
raises gold-area recall, without demonstrating sharper locators.

The fixed first-four-pair current 97M review verified 320 before/after records,
active revisions, both source-PDF hashes, pages/locations and exact UTF-16 slices;
snapshots remained unchanged. Its artifact hash is recorded in the ledger.

- English Q67 newly exposes the explicit `round()` instruction on physical Python
  page 58 (semantic rank 9/page rank 1) and examples on page 392 (semantic 4).
  None of the twenty legacy returned full passages contains `round(` even though
  the relevant page 58 was already returned. This is an answer-bearing passage
  gain beyond relevant-page retention. Portuguese already had the instruction;
  semantic rank 8→2 is better ranking, not a second fact recovery.
- English Q42 newly exposes the complete Mac/Linux/Windows path and `open()` table
  on physical page 346 (semantic 10/page 1). Legacy returns its header/caption,
  without the full table in any returned passage. The Portuguese Windows caption
  regresses page 2→5/semantic 2→8, and neither Portuguese pool returns the table body.
- Binary-search halving/O(logN) explanations are already present in both systems:
  English physical page 130 improves page 3→2; Portuguese stays page 3. The first-ranked
  exercise page does not itself explain the algorithm. MongoDB contextual material
  stays on page 407/page 1, while excerpts do not directly supply the reference's
  categorical answer. These are limits of literal returned evidence, not automatic
  answer-accuracy scores.

Historical model comparison already completed before the scope restriction:

The two candidate profiles have identical recorded build IDs and stable maps:

| Fresh CS @10 | Corrected 97M | 311M | Paired change, 95% interval |
| --- | ---: | ---: | --- |
| Recall |0.63819 |0.63958 |+0.00139 [−0.06572,+0.06091] |
| nDCG |0.63461 |0.64014 |+0.00553 [−0.03857,+0.05101] |
| MRR |0.82876 |0.81095 |−0.01781 [−0.06163,+0.02417] |
| Evidence area |0.31080 |0.31980 |+0.00901 [−0.04214,+0.04897] |
| Evidence region recall |0.31509 |0.33359 |+0.01850 [−0.03417,+0.06287] |

English/Portuguese Recall changes −0.00844/+0.01122 and nDCG +0.00883/+0.00223.
This 48-pair panel provides no clear overall advantage for 311M. Its index took
1,630.6s versus 528.1s; full-process CPU 6,582.1s versus 2,150.4s and peak RSS 2.291 GiB
versus 1.142 GiB. Total process elapsed 1,711.9s versus 607.7s includes query, citation,
startup and serialization work. Full-model mean token length 46.56/max 388 remains
below 512. There are 29,593 unchanged canonical chunks; only one matched chunk has
changed prepared text, 126 old/142 new chunks differ through tokenizer partitioning.
Both indexes cover 1,355 pages and retain identical 100 OCR-disabled diagnostics.

The separate box diagnostic raises all-page annotation precision 0.20658→0.24662
(paired interval +0.00994 to +0.07296), while conditional relevant-page precision
is 0.67339→0.67327, with uncertainty including zero. Predicted area 0.68941→0.68115
also has uncertainty including zero. This geometric signal must not be mistaken
for factual answer quality.

A preselected first-four-translation-pair review independently verified 320 candidate
records, both source PDFs, active revisions and exact UTF-16 slices. For Portuguese
Q1052, 311M retains the relevant Python page 58 at page rank 2 but none of its 20
returned full passages contains `round(`; corrected 97M returns the explicit
instruction at semantic rank 2/page rank 1. The English counterpart keeps and promotes
the instruction. This is a genuine answer-bearing returned-passage loss despite
page relevance. A Portuguese file-path example improves the actual Windows/full-OS
table at physical page 346 (page rank 5→2), with the table body newly returned.
Binary-search explanations remain available with differing pages/ranks; a MongoDB
passage provides context rather than a literal categorical answer. The review is
qualitative evidence on four fixed pairs, not a new answer-accuracy rate.

There are 100 explicit OCR-disabled page diagnostics, rather than zero extraction
warnings: some pages retain partial native text despite needing OCR for visual
content. Only five pages have no indexed native passage. All original corpus/qrel
pages remain in recall denominators. All three profiles were fixed before these validation outputs. The completed
311M results are historical only; continued work targets the current 97M/OCR
pipeline. Any further CS-driven repair is reused feedback, preserving these scores.

## Optional experiments retained separately

- Same-index development preview diversity raised Recall@10 0.46032 → 0.48268
  and nDCG 0.38351 → 0.39509, but area fell 0.26643 → 0.23731 and region recall
  0.29367 → 0.25585. It remains off.
- Equal-weight natural hybrid with optional-token `MinimumShouldMatch=0` reduced
  Recall 0.46032 → 0.36099, including Portuguese 0.43312 → 0.25177. The structured
  API defaults were not changed. No broad weight sweep was performed.
- A frozen 12-pair/24-query CPU reranker pilot improved a weak keyword pool but
  reduced the stronger pristine semantic pool's nDCG@10 0.3112 → 0.2890 and Recall
  0.3189 → 0.2791. It remains research-only; capped excerpts and full-passage
  diagnostics are separately retained outside the checkout.
- V3 real-OCR semantic smoke indexed 44/45 rather than 42/45 pages and added 242
  passages, but Recall@10 fell 0.69907 → 0.67130, nDCG 0.54774 → 0.53381, and area
  was unchanged. Observed index time was 16.2s → 308.2s under contention. This small
  OCR on/off test does not establish full-corpus gains or justify force-OCR.
- The 13-query authored synthetic regression retained the same quality in all
  compared profiles: raw semantic nDCG/MRR1; body-context/prepared semantic
  0.971610/0.961538; keyword/hybrid1. A corpus-hash change adds only explicit
  `email_body` metadata, with unchanged text, questions and relevance.

## Models, resources and precision

The historical model comparison used FP32 and 384 output dimensions. Both failed the existing
INT8 parity gate. Comparing 311M INT8 against 97M FP32 would confound model and
precision, so it is not done here. 311M tokenizer use followed explicit user
acceptance of [Gemma terms](https://ai.google.dev/gemma/terms).

| Pinned model | Revision | Complete input | Output |
| --- | --- | --- | --- |
| Granite97M multilingual r2 | `835ad14087e140460703cf0fae09f97d469d65c2` | BOS + text + EOS | CLS/L2, 384 |
| Granite311M multilingual r2 | `44399559930365213510b1ee2eb15ded83374f0e` | BOS + text | CLS, source768 → 384, L2 after Matryoshka reduction |

Model/tokenizer SHA-256 values are in every embedding policy and index provenance.
An isolated serial embedding-only probe used the same 32 authored EN/PT passages,
16 queries × three repeats and four threads:

| Measurement | Corrected 97M FP32 | 311M FP32 |
| --- | ---: | ---: |
| Process cold model load | 1.322s | 2.961s |
| Warm query median / p95 | 7.79 / 11.46ms | 19.40 / 27.97ms |
| 32-passage median batch | 1.084s | 3.434s |
| Probe process peak RSS | 0.788GiB | 2.082GiB |

These are embedding-only measurements, not end-to-end full-index memory or query
latency. Early full index runs were not resource-wrapped, so whole-process peak
RSS/CPU totals are missing. Subsequent runs use `RunMeasuredBenchmark.py` around
an already-built runner; Linux child-process CPU time includes all threads and
peak RSS is a process high-water mark. Quality-only searches use one observation,
with warmed-latency summary fields null. An observation can already benefit from
cache state and is not a cold or controlled warmed latency measurement.

The clause-cache stress test reduced repeated scanning/allocation while 1,000
fixed differential cases remained byte-identical. Its representative 280,015-char,
200-evaluation request observed 31,167ms → 66ms and 24.06GB → 40.20MB cumulative
managed allocations. A brief test build overlapped part of the baseline repeat;
these values are explicitly not controlled idle timing, full-search speed or RSS.

## Reproduction and integrity

Use an explicitly chosen external cache/result directory. Standard tests remain
network-free; the corpus tools are opt-in. Model assets must already be installed
and hash validated. The runners do not install/download models or change the app's
index/settings.

```bash
export CACHE=/path/outside/checkout/benchmarks RESULTS=/path/outside/checkout/results
python3 tools/DownloadPdfBenchmarks.py --cache "$CACHE" --dataset vidore --subset full
python3 tools/PreparePdfEvaluationPartitions.py --cache "$CACHE" --offline
HR="$CACHE/vidore/0cdf0979f2c5a0fd3e335e6373b9da48a9fe3bc3"
dotnet run --file tools/VidorePdfBenchmark.cs -- \
  --manifest "$HR/development/manifest.json" --mode semantic --model Granite97M \
  --assets /path/to/validated/assets --include-evidence --quality-only \
  --index-snapshot-out "$RESULTS/corrected97-index.db" --output "$RESULTS/corrected97-dev.json"
# Repeat with the same compiled/source runner, model Granite311M and separate outputs.
python3 tools/ComparePdfBenchmarkReports.py \
  --baseline "$RESULTS/corrected97-dev.json" --candidate "$RESULTS/311-dev.json" \
  --allow-model-change --pairs "$HR/development/selection.json" \
  --output "$RESULTS/model-development-comparison.json"
```

For full versus partition reports, pass both original manifests with
`--baseline-manifest`, `--candidate-manifest` and an explicit `--selection`; the
comparison fails closed on changed corpus, qrels or hashes. `--allow-model-change`
keeps precision, output dimensions, query inputs and consumer budgets equal while
recording model/preparation/tokenizer policy differences. This is a model-plus-
its-tokenizer production comparison; changed chunks/context are separately audited.

An index snapshot is SQLite-backed before temporary cleanup, with its hash,
project/search generation, active model/preparation policy, original source hashes
and prepared-input hash/token ledger. `VidoreSnapshotReplay.cs` opens it read-only,
verifies the identities, and must exactly reproduce saved development IDs, ranks,
scores, literal excerpts, citations and metrics before evaluating disjoint reused
questions with the unchanged scorer. No passage re-embedding or migration occurs.

```bash
dotnet run --file tools/VidoreSnapshotReplay.cs -- \
  --manifest "$HR/heldout/manifest.json" --snapshot "$RESULTS/corrected97-index.db" \
  --model Granite97M --assets /path/to/validated/assets \
  --verify-report "$RESULTS/corrected97-dev.json" --verify-manifest "$HR/development/manifest.json" \
  --output "$RESULTS/corrected97-reused 416.json"
python3 tools/AuditPdfEvidenceRegions.py --help
python3 -m unittest discover -s tests/tools -v
```

Linux constrained-build runs used disabled build servers, single-process MSBuild,
`UseSharedCompilation=false`, writable .NET/NuGet directories and an explicit
four-thread CPU budget. Pristine runners remain preserved. The HR97M/311M launch path was a file-based
build directory subsequently updated during indexing, so retroactive binary immutability cannot be asserted. Both models
subsequently reproduced every saved 208 development row exactly under the final
copied replay; build/input/source audits are retained. Future
CS runs use a separately copied, hash-locked runner for both models.
Final production-source verification after the TSV boundary safety tests:545 .NET
tests, 529 passed, 15 sandbox IPC failures matching the existing names, one
interactive Windows skip. Release build had zero warnings/errors. The original Linux Skia mismatch was repaired; the IPC restrictions
remain visible and were not bypassed. Eight benchmark-identity fixture tests passed separately from the 545-test
production suite (553 combined observations). Current Python harness tests: 47
passed; the earlier 46 count predates the resource-progress test.

Dataset attribution and license links are in [ATTRIBUTION.md](ATTRIBUTION.md).
No large copyrighted source excerpts or downloaded model/corpus files are added
to this repository report.
