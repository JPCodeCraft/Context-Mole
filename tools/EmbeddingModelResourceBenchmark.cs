#:property TargetFramework=net10.0
#:property PublishAot=false
#:property NuGetLockFilePath=../artifacts/EmbeddingModelResourceBenchmark.packages.lock.json
#:project ../src/Infrastructure/ContextMole.Infrastructure.csproj

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextMole.Core;
using ContextMole.Infrastructure;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("dotnet run --file tools/EmbeddingModelResourceBenchmark.cs -- --model Granite97M --output <report.json> [--threads 4]");
    Console.WriteLine("Uses installed assets only. Run one fresh process per model on an otherwise idle host. Reports process-cold load, warm per-query latency, fixed mixed-length passage throughput, process CPU time, managed allocations and peak RSS. It does not measure full search quality, disk-cold loading or application-wide memory.");
    return;
}
string? Option(string name)
{
    var position = Array.IndexOf(args, name);
    if (position < 0) return null;
    if (position + 1 == args.Length || args[position + 1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"{name} needs a value.");
    return args[position + 1];
}
foreach (var option in args.Where(value => value.StartsWith("--", StringComparison.Ordinal)))
    if (option is not ("--model" or "--output" or "--threads")) throw new ArgumentException($"Unknown option: {option}");
var choice = Enum.Parse<EmbeddingModelChoice>(Option("--model") ?? throw new ArgumentException("--model required."));
if (!Enum.IsDefined(choice)) throw new ArgumentException("Unknown model.");
var output = Path.GetFullPath(Option("--output") ?? throw new ArgumentException("--output required."));
var threads = int.Parse(Option("--threads") ?? "4");
if (threads is < 1 || threads > Environment.ProcessorCount) throw new ArgumentException("Unsupported thread count.");
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable)))
    throw new ArgumentException("Set CONTEXTMOLE_DATA_DIR to the controlled installed-assets directory.");

// Repository-authored text. Fixed inputs and repetition order are identical for both models.
string[] sentences = [
    "The service agreement requires written approval before changing the delivery schedule and specifies a thirty day notice period for termination.",
    "O contrato de serviços exige aprovação por escrito antes de alterar o cronograma de entrega e estabelece trinta dias de aviso para rescisão.",
    "The recovery procedure verifies the latest backup checksum, restores the database to a separate server, and checks records before reopening access.",
    "O procedimento de recuperação verifica a soma de controle do backup, restaura o banco em outro servidor e confere os registros antes de liberar o acesso.",
    "The quarterly report compares employee retention, training hours, and operating costs across departments, with separate totals for each reporting period.",
    "O relatório trimestral compara retenção de funcionários, horas de treinamento e custos operacionais por departamento, com totais separados para cada período.",
    "The equipment manual recommends monthly inspections, replacement of damaged seals, and recording maintenance dates in the shared service register.",
    "O manual do equipamento recomenda inspeções mensais, substituição de vedações danificadas e registro das datas de manutenção no histórico de serviços."
];
var passages = new[] { 1, 8, 2, 4 }.SelectMany(repeats => sentences.Select(sentence =>
    string.Join(' ', Enumerable.Repeat(sentence, repeats)))).ToArray();
string[] queries = [
    "What approval is needed before changing the delivery schedule?",
    "Qual aprovação é necessária para mudar o cronograma de entrega?",
    "How much notice is required to terminate the service agreement?",
    "Qual é o prazo de aviso para rescindir o contrato?",
    "What must be checked before restoring the database?",
    "O que deve ser verificado antes de restaurar o banco de dados?",
    "Where should the backup be restored before reopening access?",
    "Onde o backup deve ser restaurado antes de liberar o acesso?",
    "Which employee metrics are compared across departments?",
    "Quais indicadores de funcionários são comparados entre departamentos?",
    "How often should the equipment be inspected?",
    "Com que frequência o equipamento deve ser inspecionado?",
    "What should be done when a seal is damaged?",
    "O que fazer quando uma vedação estiver danificada?",
    "Where are maintenance dates recorded?",
    "Onde são registradas as datas de manutenção?"
];
var inputHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
    JsonSerializer.Serialize(new { passages, queries }))));
var cpuSettings = new ResourceCpuSettings(threads);
using var cpu = new GlobalCpuBudget(cpuSettings);
await using var embeddings = new GraniteEmbeddingGenerator(new AppPaths(), cpuSettings,
    new ResourceModelSettings(choice), cpu);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
var token = timeout.Token;
using var process = Process.GetCurrentProcess();
process.Refresh();
var initialRss = process.WorkingSet64;
var loadClock = Stopwatch.StartNew();
await embeddings.ReloadAsync(token);
loadClock.Stop();
if (!embeddings.IsAvailable) throw new InvalidOperationException(embeddings.UnavailableReason);
var passageTokens = passages.Select(embeddings.CountTokens).ToArray();
var queryTokens = queries.Select(embeddings.CountTokens).ToArray();
if (passageTokens.Any(count => count > 512) || queryTokens.Any(count => count > 256))
    throw new InvalidOperationException("Fixed benchmark inputs exceed a complete model token budget; do not truncate them.");
process.Refresh();
var loadedRss = process.WorkingSet64;
await embeddings.EmbedQueryAsync(queries[0], token);
await embeddings.EmbedPassagesAsync(passages.Take(8).ToArray(), token);
var queryRows = new List<object>();
var queryMedians = new List<double>();
for (var index = 0; index < queries.Length; index++)
{
    var timings = new List<double>();
    for (var iteration = 0; iteration < 3; iteration++)
    {
        var clock = Stopwatch.StartNew();
        var result = await embeddings.EmbedQueryAsync(queries[index], token);
        clock.Stop();
        if (result.Vector.Length != 384 || result.Vector.Any(value => !float.IsFinite(value)))
            throw new InvalidOperationException("Invalid query vector.");
        timings.Add(clock.Elapsed.TotalMilliseconds);
    }
    queryMedians.Add(Quantile(timings, 0.5));
    queryRows.Add(new { index, tokens = queryTokens[index], milliseconds = timings, median_ms = queryMedians[^1] });
}
var passageRows = new List<object>();
for (var iteration = 0; iteration < 3; iteration++)
{
    var allocated = GC.GetTotalAllocatedBytes(precise: true);
    process.Refresh();
    var cpuBefore = process.TotalProcessorTime;
    var clock = Stopwatch.StartNew();
    var result = await embeddings.EmbedPassagesAsync(passages, token);
    clock.Stop();
    process.Refresh();
    if (result.Vectors.Count != passages.Length || result.Vectors.Any(vector => vector.Length != 384 || vector.Any(value => !float.IsFinite(value))))
        throw new InvalidOperationException("Invalid passage vectors.");
    passageRows.Add(new { iteration, milliseconds = clock.Elapsed.TotalMilliseconds,
        process_cpu_ms = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
        passages_per_second = passages.Length / clock.Elapsed.TotalSeconds,
        cumulative_managed_allocated_bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated });
}
process.Refresh();
var report = new
{
    benchmark = "fixed-bilingual-embedding-resources-v1", measured_utc = DateTimeOffset.UtcNow,
    runtime = Environment.Version.ToString(), platform = Environment.OSVersion.ToString(),
    processor_count = Environment.ProcessorCount, threads, model = choice.ToString(),
    embedding_policy = embeddings.Policy, input_sha256 = inputHash,
    passages = passages.Length, passage_complete_token_counts = passageTokens,
    queries = queries.Length, query_complete_token_counts = queryTokens,
    process_cold_model_load_ms = loadClock.Elapsed.TotalMilliseconds,
    warm_query_median_ms = Quantile(queryMedians, 0.5), warm_query_p95_median_ms = Quantile(queryMedians, 0.95),
    query_measurements = queryRows, passage_measurements = passageRows,
    initial_process_rss_bytes = initialRss, loaded_process_rss_bytes = loadedRss,
    final_process_rss_bytes = process.WorkingSet64, peak_process_rss_bytes = process.PeakWorkingSet64,
    note = "Fresh process, installed assets, fixed identical EN/PT inputs; process-cold is not OS-cache-cold. Warm query embedding and batched passage throughput only, not full search/indexing. Peak RSS includes runtime/tokenizer/model/native workspace; allocations are cumulative managed bytes. Caller must record host contention."
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, json + Environment.NewLine, token);
Console.WriteLine(json);

static double Quantile(IEnumerable<double> values, double quantile)
{
    var sorted = values.Order().ToArray();
    return sorted[Math.Clamp((int)Math.Ceiling(quantile * sorted.Length) - 1, 0, sorted.Length - 1)];
}
sealed class ResourceCpuSettings(int threads) : ICpuUsageSettings
{
    public CpuUsageProfile Profile => CpuUsageProfile.Normal;
    public int LogicalProcessorCount => Environment.ProcessorCount;
    public int ThreadLimit => threads;
    public int MaximumThreadLimit => threads;
    public event EventHandler? Changed { add { } remove { } }
    public void SetProfile(CpuUsageProfile profile) => throw new NotSupportedException();
}
sealed class ResourceModelSettings(EmbeddingModelChoice model) : IEmbeddingModelSettings
{
    public EmbeddingModelChoice Model => model;
    public event EventHandler? Changed { add { } remove { } }
    public void SetModel(EmbeddingModelChoice value) => throw new NotSupportedException();
    public bool RefreshFromDisk() => false;
}
