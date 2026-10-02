#:property TargetFramework=net10.0
#:property PublishAot=false
#:property NuGetLockFilePath=../artifacts/MixedFormatSearchBenchmark.packages.lock.json
#:project ../tools/BenchmarkSupport/ContextMole.BenchmarkSupport.csproj
#:project ../src/Documents/ContextMole.Documents.csproj
#:project ../src/Infrastructure/ContextMole.Infrastructure.csproj
#:project ../src/Storage/ContextMole.Storage.csproj
#:project ../src/Search/ContextMole.Search.csproj
#:project ../src/Indexing/ContextMole.Indexing.csproj
#:project ../src/Broker.Protocol/ContextMole.Broker.Protocol.csproj

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ContextMole.Benchmarks;
using ContextMole.Broker.Protocol;
using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Indexing;
using ContextMole.Infrastructure;
using ContextMole.Search;
using ContextMole.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Contains("--help"))
{
    Console.WriteLine("dotnet run --file tools/MixedFormatSearchBenchmark.cs -- [--manifest benchmarks/mixed-formats/manifest.json] [--semantic] [--ocr] [--assets <installed-assets>] [--iterations 3] [--output <report.json>]");
    Console.WriteLine("Verifies pinned author-written gold facts, copies only public-safe fixtures to an isolated directory, uses actual extraction→IndexingCoordinator→SQLite→HybridSearchService→read evidence. No downloads, fake vectors, LLM answers, or app-data/settings changes. Default native-only keyword; --semantic requires real pinned 97M assets. --ocr requires verified cached PP-OCR. Compares existing 1 versus 2 versus 3 previews per group; measures fact/source/location coverage, Recall@1/5/10, MRR@10, excerpt/read answer coverage, literal citations, payload and warm latency. Synthetic structural regression, not a generalization guarantee. Semantic negative-query abstention is intentionally not asserted.");
    return;
}
string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    if (index < 0) return null;
    if (index + 1 == args.Length || args[index + 1].StartsWith("--")) throw new ArgumentException(name + " needs a value.");
    return args[index + 1];
}
var known = new HashSet<string>(["--manifest", "--semantic", "--ocr", "--assets", "--iterations", "--output"]);
foreach (var argument in args.Where(value => value.StartsWith("--")))
    if (!known.Contains(argument)) throw new ArgumentException("Unknown option: " + argument);
var manifestPath = Path.GetFullPath(Option("--manifest") ?? "benchmarks/mixed-formats/manifest.json");
var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
var manifest = await MixedFormatManifest.LoadAsync(manifestPath);
await MixedFormatManifest.VerifyFixturesAsync(manifestPath, manifest.Fixtures);
var semantic = args.Contains("--semantic");
var useOcr = args.Contains("--ocr");
var iterations = int.Parse(Option("--iterations") ?? "3");
if (iterations is < 1 or > 20) throw new ArgumentException("--iterations must be 1–20.");
using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(30));
var token = cancel.Token;
using var paths = new MixedPaths(Option("--assets"));
var fixtures = manifest.Fixtures.Where(value => useOcr || !value.RequiresOcr).ToArray();
var roots = new Dictionary<string, MixedFormatFixture>(StringComparer.Ordinal);
foreach (var fixture in fixtures)
{
    var destination = Path.Combine(paths.SourceDirectory, Path.GetFileName(fixture.File));
    File.Copy(MixedFormatManifest.ResolvePath(manifestPath, fixture), destination);
    if (!roots.TryAdd(Path.GetFullPath(destination), fixture)) throw new InvalidDataException("Duplicate fixture basename.");
}
var cpuSettings = new MixedCpuSettings();
using var cpu = new GlobalCpuBudget(cpuSettings);
await using IEmbeddingGenerator embeddings = semantic ? new GraniteEmbeddingGenerator(paths, cpuSettings, new MixedModelSettings(), cpu) : new MixedNoEmbeddings();
if (semantic)
{
    await embeddings.ReloadAsync(token);
    if (!embeddings.IsAvailable) throw new InvalidOperationException("Real 97M assets required: " + embeddings.UnavailableReason);
}
using var ocr = useOcr ? new PpOcrV6Engine(paths, cpuSettings, cpu) : null;
if (ocr is not null) await ocr.PrepareCachedAssetsAsync(token);
IOcrEngine ocrEngine = ocr is not null ? ocr : new MixedNoOcr();
var extractor = new DocumentExtractionRegistry(ocrEngine);
using var writer = new DatabaseWriterService(paths);
var store = new SqliteSearchStore(paths);
await writer.StartAsync(token);
await writer.Ready.WaitAsync(TimeSpan.FromSeconds(30), token);
try
{
    var project = await writer.CreateProjectAsync(new CreateProjectRequest("Mixed-format fixture benchmark", [paths.SourceDirectory]), token);
    var folder = (await store.ListProjectsAsync(token)).Single(value => value.Id == project).Folders.Single().Id;
    foreach (var path in roots.Keys)
    {
        var file = new FileInfo(path);
        await writer.ObserveFileAsync(new FileObservation(project, folder, path, file.Length,
            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)), token);
    }
    await using (var identityConnection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
    {
        await identityConnection.OpenAsync(token);
        await VidoreBenchmarkIdentity.ApplyAsync(identityConnection, project, "authored-mixed-formats", "v1",
            roots.Select(pair => new BenchmarkDocumentIdentityInput(pair.Key, pair.Value.Id, pair.Value.Sha256)).ToArray(), token);
    }
    using var coordinator = new IndexingCoordinator(writer, store, paths, extractor, embeddings,
        new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(), cpu, NullLogger<IndexingCoordinator>.Instance);
    var watch = Stopwatch.StartNew();
    await coordinator.StartAsync(token);
    try
    {
        var stable = 0;
        while (stable < 2)
        {
            await Task.Delay(200, token);
            var summary = (await store.ListProjectsAsync(token)).Single(value => value.Id == project);
            stable = summary.DocumentCount == fixtures.Length && summary.PendingCount == 0 ? stable + 1 : 0;
        }
    }
    finally { await coordinator.StopAsync(CancellationToken.None); }
    watch.Stop();
    Console.Error.WriteLine($"Indexed {fixtures.Length} roots in {watch.Elapsed.TotalSeconds:F2}s.");
    var inventory = await store.ListDocumentsAsync(new DocumentListRequest(project, Limit: 100), token);
    var errors = await store.ListProjectErrorsAsync(project, 100, token);
    var ids = new List<Guid>();
    await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Mode=ReadOnly"))
    {
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT p.id FROM passages p JOIN documents d ON d.active_revision_id=p.revision_id WHERE d.project_id=$project ORDER BY d.path,p.content_id,p.ordinal";
        command.Parameters.AddWithValue("$project", project.ToString());
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) ids.Add(Guid.Parse(reader.GetString(0)));
    }
    var passages = new List<PassageInfo>();
    foreach (var batch in ids.Chunk(50)) passages.AddRange(await store.ReadPassagesAsync(project, batch, 0, 0, token));
    var activeRoots = fixtures.Select(value => value.Id).ToHashSet();
    var facts = manifest.Facts.Where(value => activeRoots.Contains(value.Root)).ToDictionary(value => value.Id);
    var factRows = facts.Values.Select(fact =>
    {
        var matching = passages.Where(passage => MixedFormatEvidence.MatchesSource(roots[Path.GetFullPath(passage.SourcePath)].Id,
            passage.AttachmentChain, passage.Location, fact)).ToArray();
        return new { fact.Id, fact.Root, fact.Chain, fact.Location, fact.Notes,
            anchor_coverage = MixedFormatEvidence.AnchorCoverage(string.Join('\n', matching.Select(value => value.Text)), fact),
            content_ids = matching.GroupBy(value => value.ContentId).Where(group => MixedFormatEvidence.AnchorCoverage(string.Join('\n', group.Select(value => value.Text)), fact) == 1).Select(group => group.Key).ToArray(),
            methods = matching.Select(value => value.ExtractionMethod.ToString()).Distinct().ToArray() };
    }).ToArray();
    var duplicateFacts = factRows.Where(value => value.Id is "duplicate_first" or "duplicate_second").ToArray();
    if (duplicateFacts.Length == 2 && (duplicateFacts.Any(value => value.content_ids.Length != 1) ||
        duplicateFacts[0].content_ids[0] == duplicateFacts[1].content_ids[0]))
        throw new InvalidDataException("Duplicate-name email attachments lost independent content identities.");
    var factContentIds = factRows.ToDictionary(value => value.Id, value => value.content_ids.ToHashSet());
    var service = new HybridSearchService(store, embeddings, new FlatVectorIndexFactory(), new VectorIndexCache(128L * 1024 * 1024), cpu);
    var rows = new List<MixedQueryResult>();
    var modes = semantic ? new[] { SearchMode.Keyword, SearchMode.Semantic, SearchMode.Hybrid } : new[] { SearchMode.Keyword };
    foreach (var previewCount in new[] { 1, 2, 3 })
    foreach (var mode in modes)
    foreach (var query in manifest.Queries.Where(value => value.Relevant.All(facts.ContainsKey)))
    {
        var request = new SearchRequest(project, mode, mode == SearchMode.Keyword ? null : query.Text,
            mode == SearchMode.Semantic ? null : query.Terms.Select((term, index) => new SearchClause("t" + index, term, SearchClauseOccur.Must)).ToArray(),
            Filters: new SearchFilters(AttachmentScope: query.Scope switch { "root_only" => AttachmentScope.RootOnly, "attachments_only" => AttachmentScope.AttachmentsOnly, _ => AttachmentScope.Any }),
            ResultOptions: new SearchResultOptions(GroupLimit: 10, PreviewsPerGroup: previewCount, MaxGroupsPerDocument: 2), CandidateLimit: 1000);
        await service.SearchAsync(request, token);
        var times = new List<double>();
        SearchResponse? response = null;
        for (var index = 0; index < iterations; index++)
        {
            var clock = Stopwatch.StartNew(); response = await service.SearchAsync(request, token); times.Add(clock.Elapsed.TotalMilliseconds);
        }
        if (mode != SearchMode.Keyword && !response!.Branches.Semantic.Completed)
            throw new InvalidDataException("Requested real semantic branch did not complete: " + query.Id);
        var compactResponse = SearchWireResponse.FromDomain(response!, SearchDetail.Compact);
        var compactIds = compactResponse.Results.SelectMany(value => value.EvidencePassageIds.Concat(value.Previews.Select(preview => preview.PassageId))).ToHashSet();
        var previews = response!.Results.SelectMany(value => value.Previews).ToArray();
        var readIds = response.Results.SelectMany(value => value.EvidencePassageIds.Concat(value.Previews.Select(preview => preview.PassageId))).Distinct().ToArray();
        var reads = new List<PassageInfo>();
        foreach (var batch in readIds.Chunk(50)) reads.AddRange(await store.ReadPassagesAsync(project, batch, 0, 0, response.SearchGeneration, token));
        var literalValid = 0;
        foreach (var preview in previews)
        {
            var read = reads.Single(value => value.PassageId == preview.PassageId);
            if (preview.Excerpt != read.Text.Substring(preview.ExcerptStart, preview.ExcerptLength) || preview.Location != read.Location ||
                preview.SourcePath != read.SourcePath || !preview.AttachmentChain.SequenceEqual(read.AttachmentChain))
                throw new InvalidDataException("Invalid literal citation/source: " + query.Id);
            literalValid++;
        }
        var sourceRanks = query.Relevant.Select(id => response.Results.Select((group, index) => (group, rank: index + 1))
            .Where(value => factContentIds[id].Contains(value.group.ContentId)).Select(value => value.rank).DefaultIfEmpty(0).First()).ToArray();
        var ranks = query.Relevant.Select(id => compactResponse.Results.Select((group, index) => (group, rank: index + 1))
            .Where(value => {
                var available = value.group.EvidencePassageIds.Concat(value.group.Previews.Select(preview => preview.PassageId)).ToHashSet();
                var fact = facts[id];
                var text = string.Join('\n', reads.Where(read => available.Contains(read.PassageId) &&
                    MixedFormatEvidence.MatchesSource(roots[Path.GetFullPath(read.SourcePath)].Id, read.AttachmentChain, read.Location, fact)).Select(read => read.Text));
                return MixedFormatEvidence.AnchorCoverage(text, fact) == 1;
            }).Select(value => value.rank).DefaultIfEmpty(0).First()).ToArray();
        double Recall(int k) => ranks.Length == 0 ? 0 : ranks.Count(rank => rank > 0 && rank <= k) / (double)ranks.Length;
        double Coverage(bool previewOnly, bool full = false) => query.Relevant.Length == 0 ? 0 : query.Relevant.Average(id =>
        {
            var fact = facts[id];
            var text = previewOnly
                ? string.Join('\n', previews.Where(value => MixedFormatEvidence.MatchesSource(roots[Path.GetFullPath(value.SourcePath)].Id, value.AttachmentChain, value.Location, fact)).Select(value => value.Excerpt))
                : string.Join('\n', reads.Where(value => (full || compactIds.Contains(value.PassageId)) && MixedFormatEvidence.MatchesSource(roots[Path.GetFullPath(value.SourcePath)].Id, value.AttachmentChain, value.Location, fact)).Select(value => value.Text));
            return MixedFormatEvidence.AnchorCoverage(text, fact);
        });
        rows.Add(new MixedQueryResult(query.Id, query.Split, query.Language, mode.ToString().ToLowerInvariant(), previewCount,
            query.Negative, query.Relevant, ranks, sourceRanks, sourceRanks.Length == 0 ? 0 : sourceRanks.Count(rank => rank > 0 && rank <= 5) / (double)sourceRanks.Length, Recall(1), Recall(5), Recall(10), ranks.Where(value => value > 0).Select(value => 1d / value).DefaultIfEmpty(0).Max(),
            Coverage(true), Coverage(false), Coverage(false, true), response.Results.Count, previews.Count(value => value.Truncated), literalValid,
            JsonSerializer.SerializeToUtf8Bytes(SearchWireResponse.FromDomain(response, SearchDetail.Compact), BrokerJson.Options).Length,
            JsonSerializer.SerializeToUtf8Bytes(SearchWireResponse.FromDomain(response, SearchDetail.Full), BrokerJson.Options).Length,
            times.Order().ElementAt(times.Count / 2), response.CandidateLimitReached,
            response.Results.Select(group => new MixedReturnedSource(roots[Path.GetFullPath(group.SourcePath)].Id, group.ContentName, group.AttachmentChain.ToArray())).ToArray()));
    }
    var report = new {
        protocol = "authored-mixed-format-search-v1", identity_protocol = VidoreBenchmarkIdentity.Protocol, measured_utc = DateTimeOffset.UtcNow,
        corpus_sha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), runtime = Environment.Version.ToString(),
        platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription, semantic_enabled = semantic, ocr_enabled = useOcr,
        embedding_policy = embeddings.Policy?.Key, preprocessing_version = IndexPreparation.Version,
        result_limits = new { group_limit = 10, max_groups_per_document = 2, compared_previews_per_group = new[] { 1, 2, 3 }, excerpt_maximum_characters = 800, candidate_limit = 1000 },
        fixtures = fixtures.Length, skipped_ocr_fixtures = manifest.Fixtures.Count(value => value.RequiresOcr && !useOcr),
        facts = facts.Count, passages = passages.Count, indexing_seconds = watch.Elapsed.TotalSeconds,
        extraction_fact_coverage = factRows.Average(value => value.anchor_coverage),
        complete_extracted_facts = factRows.Count(value => value.anchor_coverage == 1), fact_results = factRows,
        inventory = inventory.Documents.Select(value => new { root = roots[Path.GetFullPath(value.SourcePath)].Id, value.Status, value.ExtractedPassageCount, value.ErrorCount, value.ErrorSummary }),
        errors,
        passage_audit = passages.Select(value => new { root = roots[Path.GetFullPath(value.SourcePath)].Id, value.ContentId, value.PassageId, value.AttachmentChain, value.Location, value.Text, value.ExtractionMethod, value.OcrConfidence }),
        summaries = rows.GroupBy(row => new { row.Mode, row.PreviewsPerGroup, row.Split }).Select(group => new {
            group.Key, positive_queries = group.Count(value => !value.Negative), negative_queries = group.Count(value => value.Negative),
            source_recall_at_5 = Mean(group, value => value.SourceRecallAt5), full_read_answer_coverage = Mean(group, value => value.FullReadCoverage),
            recall_at_1 = Mean(group, value => value.RecallAt1), recall_at_5 = Mean(group, value => value.RecallAt5), recall_at_10 = Mean(group, value => value.RecallAt10),
            mrr_at_10 = Mean(group, value => value.MrrAt10), excerpt_answer_coverage = Mean(group, value => value.ExcerptCoverage), read_answer_coverage = Mean(group, value => value.ReadCoverage),
            keyword_negative_success = group.Key.Mode == "keyword" ? (double?)group.Where(value => value.Negative).Select(value => value.ReturnedGroups == 0 ? 1d : 0d).DefaultIfEmpty(1).Average() : null,
            verified_literal_citations = group.Sum(value => value.ValidCitations), median_query_median_ms = Percentile(group.Select(value => value.MedianMs), .5), p95_query_median_ms = Percentile(group.Select(value => value.MedianMs), .95),
            mean_compact_bytes = group.Average(value => value.CompactBytes), mean_full_bytes = group.Average(value => value.FullBytes) }),
        query_results = rows,
        limitations = new[] { "Frozen synthetic public-safe regression corpus, not evidence of unseen real-world generalization.", "Primary recall ranks require compact-response exposed evidence with correct attachment/sheet/cell/structure and all literal gold anchors; source_recall is the looser content-group diagnostic. Read coverage uses compact exposed IDs; full_read uses full-detail IDs.", "No generated answers or formula evaluation. Cached formula values are authored file content. Semantic negatives report returned sources without asserting abstention.", "Warm timings exclude indexing, loading, and evidence reads; include real query embeddings. Keyword clauses are explicit MUST anchor terms, not a natural-language keyword parser." }
    };
    var json = JsonSerializer.Serialize(report, MixedFormatManifest.JsonOptions);
    Console.WriteLine(json);
    if (Option("--output") is { } output)
    {
        var absolute = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, json + "\n", token);
    }
}
finally { await writer.StopAsync(CancellationToken.None); SqliteConnection.ClearAllPools(); }
static double Mean(IEnumerable<MixedQueryResult> rows, Func<MixedQueryResult, double> select) => rows.Where(value => !value.Negative).Select(select).DefaultIfEmpty(0).Average();
static double Percentile(IEnumerable<double> values, double q) { var sorted = values.Order().ToArray(); return sorted[(int)Math.Clamp(Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)]; }
sealed record MixedReturnedSource(string Root, string Name, string[] Chain);
sealed record MixedQueryResult(string Id, string Split, string Language, string Mode, int PreviewsPerGroup, bool Negative,
    string[] Relevant, int[] Ranks, int[] SourceRanks, double SourceRecallAt5, double RecallAt1, double RecallAt5, double RecallAt10, double MrrAt10,
    double ExcerptCoverage, double ReadCoverage, double FullReadCoverage, int ReturnedGroups, int TruncatedPreviews, int ValidCitations,
    int CompactBytes, int FullBytes, double MedianMs, bool CandidateLimitReached, MixedReturnedSource[] ReturnedSources);
sealed class MixedPaths : IAppPaths, IDisposable
{
    private readonly string _parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ContextMole-mixed-format-benchmark"));
    private readonly string _root;
    public MixedPaths(string? assets) { _root = Path.Combine(_parent, Guid.NewGuid().ToString("N")); AssetsDirectory = Path.GetFullPath(assets ?? Path.Combine(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable) ?? ContextMoleLocalData.GetDefaultDataDirectory(), "assets")); Directory.CreateDirectory(DataDirectory); Directory.CreateDirectory(SourceDirectory); }
    public string DataDirectory => Path.Combine(_root, "data"); public string DatabasePath => Path.Combine(DataDirectory, "index.db");
    public string AssetsDirectory { get; } public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string TempDirectory => Path.Combine(DataDirectory, "temp"); public string SourceDirectory => Path.Combine(_root, "sources");
    public void Dispose() { if (!ProjectValidation.IsSameOrChild(_root, _parent) || _root == _parent) throw new InvalidOperationException("Unsafe cleanup."); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
sealed class MixedCpuSettings : ICpuUsageSettings
{
    public CpuUsageProfile Profile => CpuUsageProfile.Normal; public int LogicalProcessorCount => Environment.ProcessorCount; public int ThreadLimit => Math.Min(4, Environment.ProcessorCount); public int MaximumThreadLimit => ThreadLimit;
    public event EventHandler? Changed { add { } remove { } } public void SetProfile(CpuUsageProfile profile) => throw new NotSupportedException();
}
sealed class MixedModelSettings : IEmbeddingModelSettings
{
    public EmbeddingModelChoice Model => EmbeddingModelChoice.Granite97M; public event EventHandler? Changed { add { } remove { } }
    public void SetModel(EmbeddingModelChoice model) => throw new NotSupportedException(); public bool RefreshFromDisk() => false;
}
sealed class MixedNoEmbeddings : IEmbeddingGenerator
{
    public bool IsAvailable => false; public string UnavailableReason => "Keyword benchmark only."; public EmbeddingPolicy? Policy => null;
    public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    public Task ReloadAsync(CancellationToken token = default) => Task.CompletedTask;
    public Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken token) => throw new NotSupportedException();
    public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken token) => throw new NotSupportedException(); public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
sealed class MixedNoOcr : IOcrEngine
{
    public bool IsAvailable => false; public string UnavailableReason => "OCR deliberately excluded from native-only run.";
    public Task EnsureAvailableAsync(CancellationToken token = default) => Task.CompletedTask;
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken token) => throw new InvalidOperationException(UnavailableReason);
}
