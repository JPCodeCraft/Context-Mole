#:property TargetFramework=net10.0
#:property PublishAot=false
#:property NuGetLockFilePath=../artifacts/OlmOcrBenchmark.packages.lock.json
#:project ../tools/BenchmarkSupport/ContextMole.BenchmarkSupport.csproj
#:project ../src/Documents/ContextMole.Documents.csproj
#:project ../src/Infrastructure/ContextMole.Infrastructure.csproj

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ContextMole.Benchmarks;
using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Infrastructure;
using UglyToad.PdfPig;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("dotnet run --file tools/OlmOcrBenchmark.cs -- --manifest <cache>/olmocr/manifest.json [--mode native|ocr] [--assets <installed-assets>] [--threads 4] [--timeout-minutes 60] [--no-auto-baseline] [--output artifacts/olmocr.json]");
    Console.WriteLine("Evaluates pinned original JSONL checks against production PDF extraction, retaining every selected check. Native mode disables OCR without rendering pages; ocr mode uses the production native/OCR reconciliation with verified cached models only. Reports category coverage and conservative scores; math, footnotes and bold/italic checks remain explicitly unsupported. Text fuzzy matching and TSV table graph are documented adaptations, not an official olmOCR score. No downloads or application data/settings changes. Exit 0 only for a complete all-pass run; exit 1 for failures/incomplete coverage.");
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
var known = new HashSet<string>(["--manifest", "--mode", "--assets", "--threads", "--timeout-minutes", "--no-auto-baseline", "--output"], StringComparer.Ordinal);
foreach (var option in args.Where(argument => argument.StartsWith("--", StringComparison.Ordinal)))
    if (!known.Contains(option)) throw new ArgumentException($"Unknown option: {option}.");
var manifestPath = Path.GetFullPath(Option("--manifest") ?? throw new ArgumentException("--manifest is required. Run the pinned downloader first."));
var outputPath = Path.GetFullPath(Option("--output") ?? "artifacts/olmocr.json");
var mode = Option("--mode") ?? "native";
if (mode is not ("native" or "ocr")) throw new ArgumentException("--mode must be native or ocr.");
var threads = int.Parse(Option("--threads") ?? Math.Min(4, Environment.ProcessorCount).ToString(), System.Globalization.CultureInfo.InvariantCulture);
if (threads < 1 || threads > Environment.ProcessorCount) throw new ArgumentException("--threads must be between 1 and the logical processor count.");
var timeout = double.Parse(Option("--timeout-minutes") ?? "60", System.Globalization.CultureInfo.InvariantCulture);
if (!double.IsFinite(timeout) || timeout <= 0) throw new ArgumentException("--timeout-minutes must be positive.");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(timeout));
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
var token = cancellation.Token;
var manifestBytes = await File.ReadAllBytesAsync(manifestPath, token);
var manifest = JsonSerializer.Deserialize<OlmOcrManifest>(manifestBytes, json) ?? throw new InvalidDataException("Empty manifest.");
manifest.Validate();
var checksPath = OlmOcrManifest.ResolveCachePath(manifestPath, manifest.ChecksFile);
var checksBytes = await File.ReadAllBytesAsync(checksPath, token);
if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(checksBytes)), manifest.ChecksSha256, StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("Upstream checks JSONL hash mismatch.");
var addBaseline = !args.Contains("--no-auto-baseline", StringComparer.Ordinal);
var checks = OlmOcrBenchmarkEvaluation.ReadChecks(System.Text.Encoding.UTF8.GetString(checksBytes), manifest.Documents, addBaseline);
using var paths = new OlmOcrPaths(Option("--assets"));
var sourcePaths = new Dictionary<string, string>(StringComparer.Ordinal);
for (var index = 0; index < manifest.Documents.Length; index++)
{
    var document = manifest.Documents[index];
    var source = OlmOcrManifest.ResolveCachePath(manifestPath, document.File);
    var copy = Path.Combine(paths.SourceDirectory, $"{index:D6}.pdf");
    File.Copy(source, copy, overwrite: false);
    await using var stream = File.OpenRead(copy);
    if (!string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)), document.Sha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"Copied original PDF hash mismatch: {document.Id}.");
    sourcePaths.Add(document.Id, copy);
}
var settings = new OlmOcrCpuSettings(threads);
using var budget = new GlobalCpuBudget(settings);
using var ocr = mode == "ocr" ? new PpOcrV6Engine(paths, settings, budget) : null;
if (ocr is not null) await ocr.PrepareCachedAssetsAsync(token);
var extractor = new DocumentExtractionRegistry(ocr is null ? new OlmOcrNativeOnly() : ocr);
var outputs = new List<OlmOcrDocumentOutput>();
var timing = new List<object>();
foreach (var document in manifest.Documents)
{
    token.ThrowIfCancellationRequested();
    Console.WriteLine($"Extracting {document.Id} ({document.Category}, {mode})...");
    var watch = Stopwatch.StartNew();
    try
    {
        using var pdf = PdfDocument.Open(sourcePaths[document.Id]);
        var pageCount = pdf.NumberOfPages;
        var extraction = await extractor.ExtractAsync(new ExtractionRequest(sourcePaths[document.Id]), token);
        outputs.Add(OlmOcrBenchmarkEvaluation.Project(document.Id, pageCount, extraction));
        timing.Add(new { document.Id, pageCount, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
            nativeSections = extraction.Root.Sections.Count(section => section.Method == ExtractionMethod.NativeText),
            ocrSections = extraction.Root.Sections.Count(section => section.Method == ExtractionMethod.Ocr), extraction.Errors });
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
    catch (Exception exception)
    {
        outputs.Add(new OlmOcrDocumentOutput(document.Id, 0, [], [exception.Message]));
        timing.Add(new { document.Id, pageCount = 0, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, error = exception.Message });
    }
}
var evaluation = OlmOcrBenchmarkEvaluation.Evaluate(checks, outputs, token);
var report = new
{
    version = 1, benchmark = "Context Mole olmOCR consumer-output adaptation", officialScore = false,
    manifest.Dataset, manifest.Revision, evaluatorRevision = OlmOcrManifest.EvaluatorRevision, datasetLicense = "ODC-BY-1.0",
    upstreamCodeLicense = "Apache-2.0", manifest.Selection, mode, autoBaseline = addBaseline, threads,
    manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), manifest.ChecksSha256,
    buildIdentity = typeof(DocumentExtractionRegistry).Assembly.ManifestModule.ModuleVersionId,
    detectorRevision = mode == "ocr" ? PpOcrV6Engine.DetectorRevision : null,
    recognizerRevision = mode == "ocr" ? PpOcrV6Engine.RecognizerRevision : null,
    adaptations = new[]
    {
        "One extraction per PDF; no repeated-run majority vote or bootstrap confidence intervals.",
        "Canonical root PDF sections concatenated in production order per physical page; annotated boilerplate retained and embedded attachments excluded.",
        "Native mode returns empty OCR results without rendering; ocr mode uses production native/OCR selection and reconciliation, not forced OCR on every page.",
        "Exact symmetric partial text matching at zero differences; bounded Levenshtein approximate text matching replaces RapidFuzz partial Indel similarity.",
        "Tables projected only from explicit table sections as rectangular TSV grids; merged-cell spans and explicit HTML headers are unavailable.",
        "Heading format uses explicit metadata; math rendering, footnotes, bold and italic checks are unsupported and remain in denominators.",
        "Conservative category macro includes unsupported/error checks as not passed; supported-only rates always appear with coverage. Not the official olmOCR score."
    },
    evaluation, documents = timing, pageOutputs = outputs
};
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, json), token);
Console.WriteLine($"{evaluation.Verdict}: {evaluation.Passed}/{evaluation.Total} passed; coverage {evaluation.Coverage:P1}; {evaluation.Unsupported} unsupported, {evaluation.Errors} errors. Report: {outputPath}");
Environment.ExitCode = evaluation.Verdict == "passed" ? 0 : 1;

sealed class OlmOcrPaths : IAppPaths, IDisposable
{
    private readonly string _parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ContextMole-olmocr-benchmark"));
    private readonly string _root;
    public OlmOcrPaths(string? assets)
    {
        _root = Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        var data = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable);
        AssetsDirectory = assets is not null ? Path.GetFullPath(assets) : Path.Combine(string.IsNullOrWhiteSpace(data)
            ? ContextMoleLocalData.GetDefaultDataDirectory() : Path.GetFullPath(data), "assets");
        Directory.CreateDirectory(SourceDirectory);
    }
    public string DataDirectory => Path.Combine(_root, "data");
    public string DatabasePath => Path.Combine(DataDirectory, "index.db");
    public string AssetsDirectory { get; }
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string TempDirectory => Path.Combine(DataDirectory, "temp");
    public string SourceDirectory => Path.Combine(_root, "sources");
    public void Dispose()
    {
        var root = Path.GetFullPath(_root);
        var relative = Path.GetRelativePath(_parent, root);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Benchmark cleanup escaped its owned temporary directory.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
sealed class OlmOcrCpuSettings(int threads) : ICpuUsageSettings
{
    public CpuUsageProfile Profile => CpuUsageProfile.Normal;
    public int LogicalProcessorCount => Environment.ProcessorCount;
    public int ThreadLimit => threads;
    public int MaximumThreadLimit => threads;
    public event EventHandler? Changed { add { } remove { } }
    public void SetProfile(CpuUsageProfile profile) => throw new NotSupportedException();
}
sealed class OlmOcrNativeOnly : IOcrEngine
{
    public bool IsAvailable => false;
    public string UnavailableReason => "OCR disabled for native-only extraction.";
    public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) => Task.FromResult(new OcrResult("", null));
    public Task<OcrResult> RecognizeAsync(Func<CancellationToken, Task<OcrRequest>> prepareRequest, CancellationToken cancellationToken) => Task.FromResult(new OcrResult("", null));
}
