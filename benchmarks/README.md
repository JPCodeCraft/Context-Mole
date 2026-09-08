# Performance checks

The [extraction corpus](extraction/README.md) contains small, licensed public
documents and quality checks for native text and OCR. Its runner records warm
extraction times, allocations and sampled process memory. Ordinary tests verify
fixture integrity and native extraction without downloading models. Real OCR
measurements are opt-in and fail if text checks fail.

The embedding runner uses the installed, pinned model assets without changing
application settings:

```powershell
dotnet run --file tools/EmbeddingPerformanceBenchmark.cs -- Granite97M model.onnx
dotnet run --file tools/EmbeddingPerformanceBenchmark.cs -- Granite311M model.onnx
dotnet run --file tools/EmbeddingPerformanceBenchmark.cs -- Granite97M model_quint8_avx2.onnx --relevance
dotnet run --file tools/EmbeddingPerformanceBenchmark.cs -- Granite311M model_quint8_avx2.onnx --relevance
```

Set `CONTEXTMOLE_DATA_DIR` explicitly to select another existing model directory.
Run on an otherwise idle machine. The runner compares the previous batching
order with the production implementation, excludes model loading and warm-up,
then measures both orders twice with the order reversed for the second pass.
It reports timing, vector differences and top-10 neighbor overlap as JSON.
With `--relevance`, it uses [explicitly labeled synthetic cases](embeddings/relevance.json)
and also reports top-1 accuracy, mean reciprocal rank, Recall@6 and NDCG@10,
including individual query results. Vector equality is not a quality requirement.

## Local measurements, 2026-09-07

Windows x64, .NET 10, four inference threads. The embedding workload contains
64 mixed-length English, Portuguese and Spanish passages, capped at the same
512 tokens as production.

| FP32 model | Previous batching | Length grouping | Speedup | Largest vector difference |
| --- | ---: | ---: | ---: | ---: |
| Granite 97M | 8.22 s | 3.76 s | 2.19× | 0 |
| Granite 311M | 32.31 s | 13.51 s | 2.39× | 0 |

Both FP32 measurements had cosine agreement of 1 and 100% top-10 overlap.
Uniformly long passages will benefit less.

Quantized batching was evaluated separately using 60 mixed-length passages
across 10 related topics and 30 English, Portuguese and Spanish queries. Other
topics act as distractors, including supplier payments versus payroll and
lease termination versus subscription cancellation. This workload differs from
the FP32 timing workload above; the tables do not compare model precisions.

| Quantized model | Previous batching | Length grouping | Speedup | Recall@6 before → after | NDCG@10 before → after |
| --- | ---: | ---: | ---: | ---: | ---: |
| Granite 97M | 11.86 s | 7.70 s | 1.54× | 98.33% → 99.44% | 0.99546 → 0.99928 |
| Granite 311M | 35.74 s | 23.61 s | 1.51× | 100% → 100% | 1 → 1 |

Both quantized models returned a relevant first result for all 30 queries before
and after grouping. For 97M, the Portuguese supplier-payment query improved
from four to six relevant results in its top six. The Portuguese database-restore
query kept the same top result and recall, with NDCG moving from 0.98767 to
0.97840. Its other 28 queries and all 311M queries retained their relevance
metrics. Grouping is enabled for both precisions: different quantized vectors
are acceptable, and the measured retrieval results support the throughput gain.

The historical scan also exposed an OCR correctness problem: the old fixed
320-pixel recognition width extracted only 112 characters from its PDF. Using
the pinned model's dynamic width recovers 1,455 characters and passes the text
checks. This extra recognition takes time, so the original incomplete output
is not a valid speed baseline. The final implementation preserves the OCR
model, detection settings and page resolution while removing PNG round trips,
duplicate pixel buffers and per-line tensor copies.

Final extraction measurements used three warm samples per fixture. All five
passed their text, section-count and error checks:

| Fixture | Median extraction | Managed allocations | Extracted characters |
| --- | ---: | ---: | ---: |
| Native PDF | 12.1 ms | 2.2 MiB | 1,802 |
| Word document | 9.7 ms | 0.9 MiB | 1,908 |
| Scanned PDF | 7.67 s | 52.2 MiB | 1,455 |
| Scanned JPEG | 3.91 s | 11.0 MiB | 1,458 |
| Multilingual TIFF | 1.97 s | 17.5 MiB | 422 |

Managed allocation figures exclude native model memory. The JSON reports also
record sampled process working set, which includes that memory and earlier
fixtures. Model preparation and fixture warm-up are excluded from these times.

These are local smoke measurements, not a throughput guarantee or a complete
transcription-accuracy evaluation. Keep quality checks enabled when comparing
performance, and compare the same inputs, model assets and CPU configuration.
