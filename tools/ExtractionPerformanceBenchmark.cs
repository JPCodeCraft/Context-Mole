#:property TargetFramework=net10.0
#:property NuGetLockFilePath=../artifacts/ExtractionPerformanceBenchmark.packages.lock.json
#:project ../src/Core/ContextMole.Core.csproj
#:project ../src/Documents/ContextMole.Documents.csproj
#:project ../src/Infrastructure/ContextMole.Infrastructure.csproj

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Infrastructure;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("Run from the repository root: dotnet run --file tools/ExtractionPerformanceBenchmark.cs -- [--ocr] [--iterations 3] [--fixtures benchmarks/extraction/manifest.json] [--output artifacts/extraction-benchmark/report.json] [--baseline previous-report.json]");
    Console.WriteLine("OCR model preparation and one warm-up per file are excluded from timings. OCR uses the existing CONTEXTMOLE_DATA_DIR, or an isolated artifacts/extraction-benchmark/data directory. Baseline timing ratios are informational and should be compared on the same machine/profile.");
    return;
}

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    if (index < 0) return null;
    if (index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"{name} requires a value.");
    return args[index + 1];
}

var jsonContext = new BenchmarkJsonContext(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
var manifestPath = Path.GetFullPath(Option("--fixtures") ?? "benchmarks/extraction/manifest.json");
var outputPath = Path.GetFullPath(Option("--output") ?? "artifacts/extraction-benchmark/report.json");
var baselinePath = Option("--baseline");
var includeOcr = args.Contains("--ocr", StringComparer.Ordinal);
var iterations = int.Parse(Option("--iterations") ?? "3", System.Globalization.CultureInfo.InvariantCulture);
if (iterations is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(iterations), "Use 1 to 20 measured iterations.");
var manifest = JsonSerializer.Deserialize(await File.ReadAllTextAsync(manifestPath), jsonContext.FixtureManifest)
    ?? throw new InvalidDataException("Benchmark manifest is empty.");
if (manifest.Fixtures.Select(fixture => fixture.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Fixtures.Length)
    throw new InvalidDataException("Benchmark fixture IDs must be unique.");
foreach (var fixture in manifest.Fixtures)
    if (string.IsNullOrWhiteSpace(fixture.Id) || fixture.ExpectedText.Length == 0 ||
        fixture.ExpectedText.Any(string.IsNullOrWhiteSpace) || fixture.MinimumSections < 1 ||
        fixture.MinimumCharacters < 1 || fixture.Sha256 is not { Length: 64 } ||
        !fixture.Sha256.All(Uri.IsHexDigit))
        throw new InvalidDataException($"Fixture '{fixture.Id}' requires a SHA-256, nonempty text anchors, and positive section/character minimums.");
var selected = manifest.Fixtures.Where(fixture => includeOcr || !fixture.RequiresOcr).ToArray();
if (selected.Length == 0) throw new InvalidDataException("No benchmark fixtures match the requested mode.");

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable)))
    Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable,
        Path.GetFullPath("artifacts/extraction-benchmark/data"));
var paths = new AppPaths();
var settings = new CpuUsageSettings(paths);
using var cpuBudget = new GlobalCpuBudget(settings);
using var ocrEngine = new PpOcrV6Engine(paths, settings, cpuBudget);
var extractor = new DocumentExtractionRegistry(includeOcr ? ocrEngine : new DisabledOcrEngine());
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };

var cpuIdentity = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString();
var machineIdentity = Convert.ToHexStringLower(SHA256.HashData(
    System.Text.Encoding.UTF8.GetBytes(Environment.MachineName + "|" + cpuIdentity)));
var buildIdentity = $"{typeof(DocumentExtractionRegistry).Assembly.ManifestModule.ModuleVersionId:N}:" +
                    $"{typeof(PpOcrV6Engine).Assembly.ManifestModule.ModuleVersionId:N}";
BenchmarkReport? baseline = baselinePath is null ? null :
    JsonSerializer.Deserialize(await File.ReadAllTextAsync(baselinePath, cancellation.Token), jsonContext.BenchmarkReport);
if (baseline is not null &&
    (baseline.MachineIdentity != machineIdentity || baseline.Runtime != RuntimeInformation.FrameworkDescription ||
     baseline.OperatingSystem != RuntimeInformation.OSDescription || baseline.LogicalProcessors != Environment.ProcessorCount ||
     baseline.CpuProfile != settings.Profile.ToString() || baseline.ThreadLimit != settings.ThreadLimit ||
     baseline.IncludesOcr != includeOcr || baseline.DetectorRevision != PpOcrV6Engine.DetectorRevision ||
     baseline.RecognizerRevision != PpOcrV6Engine.RecognizerRevision))
    throw new InvalidDataException("Baseline comparisons require the same machine, runtime, OS, CPU profile, thread limit, OCR mode and model revisions. Run without --baseline to record a separate measurement.");

var preparation = Stopwatch.StartNew();
if (selected.Any(fixture => fixture.RequiresOcr))
{
    Console.WriteLine("Preparing the pinned OCR models (excluded from measured extraction time)...");
    await ocrEngine.EnsureAvailableAsync(cancellation.Token);
}
preparation.Stop();
var results = new List<FixtureResult>();

foreach (var fixture in selected)
{
    var fixturePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, fixture.File));
    if (!File.Exists(fixturePath)) throw new FileNotFoundException($"Benchmark fixture '{fixture.Id}' is missing.", fixturePath);
    var previous = baseline?.Results.FirstOrDefault(result => result.Id == fixture.Id);
    if (previous is not null && previous.Sha256 != fixture.Sha256)
        throw new InvalidDataException($"Fixture '{fixture.Id}' differs from the baseline input; its timings cannot be compared.");
    if (fixture.Sha256 is { Length: > 0 })
    {
        await using var fixtureStream = File.OpenRead(fixturePath);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fixtureStream, cancellation.Token));
        if (!hash.Equals(fixture.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Benchmark fixture '{fixture.Id}' no longer matches its manifest SHA-256.");
    }
    Console.WriteLine($"{fixture.Id}: warming up ({(fixture.RequiresOcr ? "OCR" : "native extraction")})...");
    var warmup = await extractor.ExtractAsync(new ExtractionRequest(fixturePath), cancellation.Token);
    var problems = QualityProblems(fixture, warmup);
    var samples = new List<Measurement>();
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        var gcBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        await using var memory = new WorkingSetSampler();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew();
        var result = await extractor.ExtractAsync(new ExtractionRequest(fixturePath), cancellation.Token);
        timer.Stop();
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        await memory.StopAsync();
        var gcCollections = Enumerable.Range(0, 3).Select(generation => GC.CollectionCount(generation) - gcBefore[generation]).ToArray();
        problems.AddRange(QualityProblems(fixture, result));
        samples.Add(new Measurement(timer.Elapsed.TotalMilliseconds, allocatedBytes, memory.PeakBytes,
            gcCollections, CountSections(result.Root), CountCharacters(result.Root), OcrConfidence(result.Root)));
    }

    var elapsed = Median(samples.Select(sample => sample.ElapsedMilliseconds));
    var row = new FixtureResult(fixture.Id, fixture.File, fixture.RequiresOcr,
        new FileInfo(fixturePath).Length, elapsed, (long)Median(samples.Select(sample => (double)sample.AllocatedBytes)),
        samples.Max(sample => sample.PeakWorkingSetBytes),
        previous is { MedianMilliseconds: > 0, Problems.Length: 0 } && problems.Count == 0
            ? elapsed / previous.MedianMilliseconds : null,
        problems.Distinct(StringComparer.Ordinal).ToArray(), samples, fixture.Sha256!);
    results.Add(row);
    var comparison = row.BaselineTimeRatio is { } ratio ? $", baseline time ×{ratio:F2}" : string.Empty;
    Console.WriteLine($"{fixture.Id}: {row.MedianMilliseconds:F1} ms median, {row.MedianAllocatedBytes / 1048576d:F1} MiB allocated, {row.PeakWorkingSetBytes / 1048576d:F1} MiB sampled peak process working set, quality {(row.Problems.Length == 0 ? "PASS" : "FAIL")}{comparison}");
    foreach (var problem in row.Problems) Console.WriteLine($"  {problem}");
}

var report = new BenchmarkReport(DateTimeOffset.UtcNow, RuntimeInformation.FrameworkDescription,
    RuntimeInformation.OSDescription, Environment.ProcessorCount, settings.Profile.ToString(), settings.ThreadLimit,
    includeOcr, iterations, preparation.Elapsed.TotalMilliseconds,
    PpOcrV6Engine.DetectorRevision, PpOcrV6Engine.RecognizerRevision, results, machineIdentity, cpuIdentity, buildIdentity);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, jsonContext.BenchmarkReport), cancellation.Token);
Console.WriteLine($"Report: {outputPath}");
Console.WriteLine($"Completed {results.Count} fixtures; skipped {manifest.Fixtures.Length - selected.Length} OCR fixtures. Timing ratios are informational; extraction errors and missing quality anchors fail the run.");
Console.WriteLine("Working set is sampled every 25 ms and includes loaded models and earlier fixtures. Managed allocation totals include sampling overhead. Quality checks use text anchors and size minimums; they do not measure full transcription accuracy.");
if (results.Any(result => result.Problems.Length != 0)) Environment.ExitCode = 1;

static List<string> QualityProblems(Fixture fixture, ExtractionResult result)
{
    var problems = result.Errors.Select(error => $"{error.Code}: {error.Message}").ToList();
    var text = TextNormalization.ForSearch(string.Join('\n', SectionTexts(result.Root)));
    foreach (var expected in fixture.ExpectedText)
        if (!text.Contains(TextNormalization.ForSearch(expected), StringComparison.OrdinalIgnoreCase))
            problems.Add($"Missing expected text: {expected}");
    if (CountSections(result.Root) < fixture.MinimumSections)
        problems.Add($"Expected at least {fixture.MinimumSections} sections; found {CountSections(result.Root)}.");
    if (CountCharacters(result.Root) < fixture.MinimumCharacters)
        problems.Add($"Expected at least {fixture.MinimumCharacters} extracted characters; found {CountCharacters(result.Root)}.");
    if (fixture.RequiresOcr && !ContainsOcr(result.Root)) problems.Add("Expected an OCR section.");
    return problems;
}

static IEnumerable<string> SectionTexts(ExtractedNode node) =>
    node.Sections.Select(section => section.Text).Concat(node.Attachments.SelectMany(SectionTexts));
static int CountSections(ExtractedNode node) => node.Sections.Count + node.Attachments.Sum(CountSections);
static int CountCharacters(ExtractedNode node) => node.Sections.Sum(section => section.Text.Length) + node.Attachments.Sum(CountCharacters);
static bool ContainsOcr(ExtractedNode node) => node.Sections.Any(section => section.Method == ExtractionMethod.Ocr) || node.Attachments.Any(ContainsOcr);
static double? OcrConfidence(ExtractedNode node)
{
    var sections = OcrSections(node).Where(section => section.OcrConfidence.HasValue && section.Text.Length > 0).ToArray();
    var characters = sections.Sum(section => (long)section.Text.Length);
    return characters == 0 ? null : sections.Sum(section => section.OcrConfidence!.Value * section.Text.Length) / characters;
}
static IEnumerable<ExtractedSection> OcrSections(ExtractedNode node) =>
    node.Sections.Where(section => section.Method == ExtractionMethod.Ocr).Concat(node.Attachments.SelectMany(OcrSections));
static double Median(IEnumerable<double> values)
{
    var sorted = values.Order().ToArray();
    return sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
}

sealed record FixtureManifest(Fixture[] Fixtures);
sealed record Fixture(string Id, string File, bool RequiresOcr, string[] ExpectedText, int MinimumSections = 1,
    string? Sha256 = null, int MinimumCharacters = 0);
sealed record Measurement(double ElapsedMilliseconds, long AllocatedBytes, long PeakWorkingSetBytes,
    int[] GcCollections, int Sections, int Characters, double? OcrConfidence = null);
sealed record FixtureResult(string Id, string File, bool RequiresOcr, long SourceBytes, double MedianMilliseconds,
    long MedianAllocatedBytes, long PeakWorkingSetBytes, double? BaselineTimeRatio,
    string[] Problems, List<Measurement> Samples, string Sha256);
sealed record BenchmarkReport(DateTimeOffset CreatedUtc, string Runtime, string OperatingSystem, int LogicalProcessors,
    string CpuProfile, int ThreadLimit, bool IncludesOcr, int Iterations, double OcrPreparationMilliseconds,
    string DetectorRevision, string RecognizerRevision, List<FixtureResult> Results,
    string MachineIdentity, string CpuIdentity, string BuildIdentity);

[JsonSerializable(typeof(FixtureManifest))]
[JsonSerializable(typeof(BenchmarkReport))]
partial class BenchmarkJsonContext : JsonSerializerContext { }

sealed class DisabledOcrEngine : IOcrEngine
{
    public bool IsAvailable => false;
    public string? UnavailableReason => "This fixture required OCR; mark it requiresOcr and run with --ocr.";
    public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
        throw new ContextMoleException("benchmark_ocr_disabled", UnavailableReason!);
}

sealed class WorkingSetSampler : IAsyncDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Timer _timer;
    private long _peakBytes;
    private int _stopped;

    public WorkingSetSampler()
    {
        Sample();
        _timer = new Timer(_ => Sample(), null, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25));
    }
    public long PeakBytes => Volatile.Read(ref _peakBytes);
    private void Sample()
    {
        lock (_process)
        {
            _process.Refresh();
            _peakBytes = Math.Max(_peakBytes, _process.WorkingSet64);
        }
    }
    public async ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        await _timer.DisposeAsync();
        Sample();
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _process.Dispose();
    }
}
