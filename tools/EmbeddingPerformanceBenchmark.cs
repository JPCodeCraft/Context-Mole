#:property TargetFramework=net10.0
#:property PublishAot=false
#:property NuGetLockFilePath=../artifacts/EmbeddingPerformanceBenchmark.packages.lock.json
#:project ../src/Infrastructure/ContextMole.Infrastructure.csproj

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ContextMole.Core;
using ContextMole.Infrastructure;
using Microsoft.ML.OnnxRuntime;
using Tokenizers.HuggingFace.Tokenizer;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("Run from the repository root: dotnet run --file tools/EmbeddingPerformanceBenchmark.cs -- [Granite97M|Granite311M] [model.onnx|model_quint8_avx2.onnx] [--relevance]");
    Console.WriteLine("Uses installed pinned assets from CONTEXTMOLE_DATA_DIR (or the normal application data directory), without downloading or modifying them. Compares 64 mixed-length English/Portuguese/Spanish passages on 4 CPU threads, grouping both FP32 and quantized profiles. Model loading and warm-up are excluded; two timings run in reverse order to reduce order bias. Emits timing, padding and vector-difference diagnostics; use --relevance to measure retrieval. Compare on an otherwise idle machine; these timings are workload-specific.");
    Console.WriteLine("--relevance instead uses benchmarks/embeddings/relevance.json: 10 labeled topics, 60 mixed-length passages and 30 English/Portuguese/Spanish queries. Related topics act as distractors. It compares ungrouped and length-grouped retrieval with top-1 accuracy, MRR, Recall@6 and NDCG@10, including per-query results. Queries use the production BOS token and 256-token limit; passages retain 512 tokens. Vector differences are diagnostic, not a quality gate.");
    return;
}

var relevanceEvaluation = args.Contains("--relevance", StringComparer.Ordinal);
args = args.Where(argument => argument != "--relevance").ToArray();
var dataDirectory = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable);
var root = Path.Combine(string.IsNullOrWhiteSpace(dataDirectory)
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContextMole")
    : Path.GetFullPath(dataDirectory), "assets", "granite");
var batchMethod = typeof(GraniteEmbeddingGenerator).GetMethod("RunBatch", BindingFlags.NonPublic | BindingFlags.Static)!;
var groupedMethod = typeof(GraniteEmbeddingGenerator).GetMethod("RunBatches", BindingFlags.NonPublic | BindingFlags.Static)!;
var selected = args.Length == 0 ? EmbeddingModelChoice.Granite97M : Enum.Parse<EmbeddingModelChoice>(args[0]);
var model = GraniteEmbeddingModels.Get(selected);
var directory = Path.Combine(root, model.Revision);
var profiles = args.Skip(1).DefaultIfEmpty("model.onnx");
string[] sentences = [
    "A service agreement establishes payment schedules, confidentiality rules, renewal dates and termination conditions.",
    "O relatório financeiro apresenta receitas, despesas e fluxo de caixa para o conselho de administração.",
    "La política de privacidad protege los datos personales y define las responsabilidades del proveedor.",
    "The maintenance handbook describes industrial equipment inspections, safety checks, replacement parts and service intervals.",
    "A ata da reunião registra decisões, participantes, prazos e ações para a próxima revisão do projeto.",
    "El contrato de servicios incluye condiciones de pago, plazos de entrega y obligaciones de las partes."
];
using var tokenizer = Tokenizer.FromFile(Path.Combine(directory, "tokenizer.json"));
LabeledQuery[] queries = [];
LabeledPassage[] passages;
string? relevanceSha256 = null;
if (relevanceEvaluation)
{
    var datasetBytes = File.ReadAllBytes("benchmarks/embeddings/relevance.json");
    relevanceSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(datasetBytes));
    using var dataset = JsonDocument.Parse(datasetBytes);
    var topics = dataset.RootElement.GetProperty("topics").EnumerateArray().ToArray();
    string[] languages = ["en", "pt", "es"];
    queries = topics.SelectMany(topic => topic.GetProperty("queries").EnumerateArray().Select((query, index) =>
        new LabeledQuery(topic.GetProperty("id").GetString()!, languages[index], query.GetString()!))).ToArray();
    int[] repeats = [1, 8, 2, 10, 3, 6];
    var ordered = Enumerable.Range(0, 6).SelectMany(variant => topics.Select(topic =>
        new LabeledPassage(topic.GetProperty("id").GetString()!,
            string.Join(" ", Enumerable.Repeat(topic.GetProperty("passages")[variant % 3].GetString()!, repeats[variant])))))
        .ToArray();
    // Fixed permutation prevents grouping by topic or language from accidentally deciding the baseline batches.
    passages = Enumerable.Range(0, ordered.Length).Select(index => ordered[index * 17 % ordered.Length]).ToArray();
}
else
{
    passages = Enumerable.Range(0, 64).Select(index =>
    {
        var repeats = new[] { 1, 20, 3, 12, 2, 16, 5, 8 }[index % 8];
        return new LabeledPassage("unlabeled", string.Join(" ", Enumerable.Repeat(sentences[index % sentences.Length], repeats)));
    }).ToArray();
}
long[] Tokenize(string text, int maximumTokens)
{
    var ids = tokenizer.Encode(text, false).First().Ids;
    return new[] { model.BosTokenId }.Concat(ids.Take(maximumTokens - 1).Select(id => (long)id)).ToArray();
}
var encoded = passages.Select(passage => Tokenize(passage.Text, 512)).ToArray();
foreach (var profile in profiles)
{
    if (profile is not ("model.onnx" or "model_quint8_avx2.onnx"))
        throw new ArgumentException("Select model.onnx or model_quint8_avx2.onnx.");
    using var options = new SessionOptions { ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, InterOpNumThreads = 1,
        IntraOpNumThreads = 4, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
    using var session = new InferenceSession(Path.Combine(directory, profile), options);
    IReadOnlyList<float[]> Run(IReadOnlyList<long[]> batch) => (IReadOnlyList<float[]>)batchMethod.Invoke(null,
        [session, batch, model.SourceDimensions, model.Dimensions, CancellationToken.None])!;
    IReadOnlyList<float[]> Original() => encoded.Chunk(8).SelectMany(batch => Run(batch)).ToArray();
    IReadOnlyList<float[]> Grouped() => (IReadOnlyList<float[]>)groupedMethod.Invoke(null,
        [encoded, (Func<IReadOnlyList<long[]>, IReadOnlyList<float[]>>)Run, 8, CancellationToken.None])!;
    Run(encoded.Take(8).ToArray());
    var watch = Stopwatch.StartNew();
    var before = Original();
    var originalMs = watch.Elapsed.TotalMilliseconds;
    watch.Restart();
    var after = Grouped();
    var groupedMs = watch.Elapsed.TotalMilliseconds;
    // Repeat in reverse order to reduce warm-up/order bias.
    watch.Restart(); Grouped(); groupedMs += watch.Elapsed.TotalMilliseconds;
    watch.Restart(); Original(); originalMs += watch.Elapsed.TotalMilliseconds;
    var cosines = before.Zip(after).Select(pair => Cosine(pair.First, pair.Second)).ToArray();
    var maxDifference = before.Zip(after).Max(pair => pair.First.Zip(pair.Second).Max(values => Math.Abs(values.First - values.Second)));
    var top10Overlap = Enumerable.Range(0, 8).Average(query =>
    {
        var reference = Enumerable.Range(0, passages.Length).OrderByDescending(index => Cosine(before[query], before[index])).Take(10).ToHashSet();
        var actual = Enumerable.Range(0, passages.Length).OrderByDescending(index => Cosine(before[query], after[index])).Take(10).ToHashSet();
        return reference.Intersect(actual).Count() / 10d;
    });
    var relevance = queries.Select(query =>
    {
        // Production queries are embedded individually, with no instruction prefix or passage batch mixing.
        var vector = Run([Tokenize(query.Text, 256)])[0];
        var original = Rank(vector, before, passages, query.Topic);
        var grouped = Rank(vector, after, passages, query.Topic);
        return new { topic = query.Topic, language = query.Language, query = query.Text, original, grouped };
    }).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new { model = selected.ToString(), revision = model.Revision, profile,
        group_by_length = true, passages = passages.Length, threads = 4, measured_iterations = 2,
        runtime = Environment.Version.ToString(), cpu = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        original_ms = originalMs / 2, grouped_ms = groupedMs / 2, speedup = originalMs / groupedMs,
        mean_cosine = cosines.Average(), min_cosine = cosines.Min(), max_abs_difference = maxDifference,
        top10_overlap = top10Overlap, original_padded_tokens = encoded.Chunk(8).Sum(batch => batch.Length * batch.Max(ids => ids.Length)),
        grouped_padded_tokens = encoded.OrderBy(ids => ids.Length)
            .Chunk(8).Sum(batch => batch.Length * batch.Max(ids => ids.Length)),
        relevance_dataset_sha256 = relevanceSha256,
        original_relevance = relevance.Length == 0 ? null : Summarize(relevance.Select(row => row.original).ToArray()),
        grouped_relevance = relevance.Length == 0 ? null : Summarize(relevance.Select(row => row.grouped).ToArray()),
        query_results = relevance }));
}

static QueryMetrics Rank(float[] query, IReadOnlyList<float[]> vectors, LabeledPassage[] passages, string topic)
{
    var ranked = Enumerable.Range(0, vectors.Count).OrderByDescending(index => Cosine(query, vectors[index]))
        .ThenBy(index => index).ToArray();
    var relevantCount = passages.Count(passage => passage.Topic == topic);
    var firstRelevant = Array.FindIndex(ranked, index => passages[index].Topic == topic);
    var recall = ranked.Take(6).Count(index => passages[index].Topic == topic) / (double)relevantCount;
    var dcg = ranked.Take(10).Select((index, rank) => passages[index].Topic == topic ? 1 / Math.Log2(rank + 2) : 0).Sum();
    var ideal = Enumerable.Range(0, Math.Min(10, relevantCount)).Sum(rank => 1 / Math.Log2(rank + 2));
    return new QueryMetrics(firstRelevant == 0, firstRelevant < 0 ? 0 : 1d / (firstRelevant + 1), recall,
        dcg / ideal, passages[ranked[0]].Topic);
}

static RelevanceMetrics Summarize(QueryMetrics[] queries) => new(queries.Length,
    queries.Count(query => query.Top1Correct) / (double)queries.Length, queries.Average(query => query.ReciprocalRank),
    queries.Average(query => query.RecallAt6), queries.Average(query => query.NdcgAt10));

static double Cosine(float[] left, float[] right)
{
    double sum = 0, l = 0, r = 0;
    for (var index = 0; index < left.Length; index++) { sum += (double)left[index] * right[index]; l += (double)left[index] * left[index]; r += (double)right[index] * right[index]; }
    return sum / Math.Sqrt(l * r);
}

sealed record LabeledPassage(string Topic, string Text);
sealed record LabeledQuery(string Topic, string Language, string Text);
sealed record QueryMetrics(bool Top1Correct, double ReciprocalRank, double RecallAt6, double NdcgAt10, string TopTopic);
sealed record RelevanceMetrics(int Queries, double Top1Accuracy, double MeanReciprocalRank, double RecallAt6, double NdcgAt10);
