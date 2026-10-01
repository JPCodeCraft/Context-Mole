#:property TargetFramework=net10.0
#:property PublishAot=false
#:property NuGetLockFilePath=../artifacts/SearchQualityBenchmark.packages.lock.json
#:project ../src/Infrastructure/ContextMole.Infrastructure.csproj
#:project ../src/Storage/ContextMole.Storage.csproj
#:project ../src/Search/ContextMole.Search.csproj
#:project ../src/Indexing/ContextMole.Indexing.csproj
#:project ../src/Broker.Protocol/ContextMole.Broker.Protocol.csproj

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextMole.Broker.Protocol;
using ContextMole.Core;
using ContextMole.Indexing;
using ContextMole.Infrastructure;
using ContextMole.Search;
using ContextMole.Storage;
using Microsoft.Data.Sqlite;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("dotnet run --file tools/SearchQualityBenchmark.cs -- [--semantic] [--model Granite97M|Granite311M] [--output artifacts/search-quality.json]");
    Console.WriteLine("Indexes the labeled synthetic benchmarks/search/corpus.json in an owned temporary SQLite database, then runs the actual HybridSearchService. No downloads or changes to the application index/settings. Default: lexical passage/section retrieval. --semantic requires installed model assets and adds semantic and hybrid retrieval, comparing an explicit metadata-first/raw baseline with the production semantic preparation functions. Three warm search timings per query, Recall@5, MRR@5, nDCG@5, compact/full serialized response bytes, token/truncation diagnostics and literal preview/read consistency are reported. This small corpus is a regression workload, not a model quality or latency guarantee.");
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
var semantic = args.Contains("--semantic", StringComparer.Ordinal);
var model = Enum.Parse<EmbeddingModelChoice>(Option("--model") ?? "Granite97M");
var outputPath = Option("--output");
var bytes = await File.ReadAllBytesAsync("benchmarks/search/corpus.json");
var corpus = JsonSerializer.Deserialize<Corpus>(bytes, BrokerJson.Options) ?? throw new InvalidDataException("Missing corpus.");
if (corpus.Version != 1 || corpus.Documents.Length == 0 || corpus.Queries.Length == 0)
    throw new InvalidDataException("Unsupported or empty corpus.");
var profiles = semantic ? new[] { "raw", "body_context", "prepared" } : new[] { "lexical" };
var reports = new List<object>();
foreach (var profile in profiles) reports.Add(await RunAsync(profile));
var report = new
{
    corpus_sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), corpus_version = corpus.Version,
    measured_utc = DateTimeOffset.UtcNow, runtime = Environment.Version.ToString(),
    platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    cpu_architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    semantic_enabled = semantic, selected_model = semantic ? model.ToString() : null,
    candidate_limit = 1000, group_limit = 5, measured_iterations = 3,
    note = "Synthetic structural regression workload. Model loading, indexing, warm-up and evidence-read verification are excluded from search latency; query embedding and vector retrieval are included. Semantic results use no lexical clauses; hybrid retains them. Raw baseline is defined here, not a replay of a historical build.",
    profiles = reports
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(BrokerJson.Options) { WriteIndented = true });
Console.WriteLine(json);
if (outputPath is not null)
{
    var absoluteOutput = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(absoluteOutput)!);
    await File.WriteAllTextAsync(absoluteOutput, json + "\n", new UTF8Encoding(false));
}

async Task<object> RunAsync(string profile)
{
    using var paths = new BenchmarkPaths();
    var cpuSettings = new FixedCpuSettings();
    using var cpu = new GlobalCpuBudget(cpuSettings);
    await using IEmbeddingGenerator embeddings = semantic
        ? new GraniteEmbeddingGenerator(paths, cpuSettings, new FixedModelSettings(model), cpu)
        : new NoEmbeddings();
    if (semantic)
    {
        await embeddings.ReloadAsync();
        if (!embeddings.IsAvailable)
            throw new InvalidOperationException($"--semantic needs installed model assets: {embeddings.UnavailableReason}");
    }
    using var writer = new DatabaseWriterService(paths);
    var store = new SqliteSearchStore(paths);
    await writer.StartAsync(CancellationToken.None);
    await writer.Ready.WaitAsync(TimeSpan.FromSeconds(30));
    try
    {
        var project = await writer.CreateProjectAsync(new CreateProjectRequest("Search quality corpus", [paths.SourceDirectory]));
        var folder = (await store.ListProjectsAsync()).Single().Folders.Single().Id;
        var contentKeys = new Dictionary<Guid, string>();
        var sectionKeys = new Dictionary<Guid, string>();
        var semanticTokens = new List<int>();
        var embeddedCount = 0;
        var passageCount = 0;
        foreach (var document in corpus.Documents)
        {
            var path = Path.Combine(paths.SourceDirectory, document.Id + ".txt");
            var sourceText = string.Join("\n\n", document.Sections.SelectMany(section => section.Passages));
            await File.WriteAllTextAsync(path, sourceText, new UTF8Encoding(false));
            var file = new FileInfo(path);
            var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            await writer.ObserveFileAsync(new FileObservation(project, folder, path, file.Length, modified));
            var job = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(5)) ?? throw new InvalidOperationException("Missing index job.");
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var revision = await writer.BeginRevisionAsync(job, hash, file.Length, modified);
            if (!revision.ShouldExtract || revision.RevisionId is null) throw new InvalidOperationException("Missing revision.");
            var contentId = Id(document.Id);
            contentKeys.Add(contentId, document.Id);
            var passages = new List<PassageDraft>();
            var sections = new List<SectionDraft>();
            var representations = new List<(int Index, string Text)>();
            var ordinal = 0;
            for (var sectionOrdinal = 0; sectionOrdinal < document.Sections.Length; sectionOrdinal++)
            {
                var section = document.Sections[sectionOrdinal];
                var sectionId = Id(document.Id + "/" + section.Id);
                sectionKeys.Add(sectionId, document.Id + "/" + section.Id);
                var display = section.Passages.Select((text, index) => index == 0 && section.PrefixRepeat > 0
                    ? string.Concat(Enumerable.Repeat("Routine incident observation without the distinctive terms. ", section.PrefixRepeat)) + text
                    : text).ToArray();
                var sectionText = string.Join("\n\n", display);
                var location = new SourceLocation(LocationKind.Page, Page: sectionOrdinal + 1, StructurePath: section.Id);
                sections.Add(new SectionDraft(sectionId, contentId, sectionOrdinal, sectionText, section.Heading,
                    [section.Heading], "synthetic", location));
                var offset = 0;
                foreach (var original in display)
                {
                    foreach (var chunk in Chunks(original, document.Title, section.Heading, Path.GetFileName(path)))
                    {
                        var text = chunk.Text;
                        var passageId = Id($"{document.Id}/{section.Id}/{ordinal}");
                        passages.Add(new PassageDraft(passageId, contentId, ordinal++, text, TextNormalization.ForSearch(text),
                            location, ExtractionMethod.NativeText, null, null, LexicalText.Canonicalize(text),
                            Title: document.Title, Heading: section.Heading, FileName: Path.GetFileName(path),
                            SourcePath: path, ContentName: Path.GetFileName(path))
                            { SectionId = sectionId, SectionOffset = offset + chunk.Start });
                        if (!semantic) continue;
                        var representation = profile == "raw"
                            ? $"Title: {document.Title}\nFile: {Path.GetFileName(path)}\nHeading: {section.Heading}\n{text}"
                            : SemanticTextPreparation.Compose(profile == "prepared" ? SemanticTextPreparation.CleanBody(text, section.Boilerplate) : text,
                                [("Title", document.Title), ("Heading", section.Heading)], embeddings.CountTokens);
                        // Production boilerplate has lexical evidence but no semantic vector.
                        if (profile == "prepared" && (section.Boilerplate || SemanticTextPreparation.CleanBody(text).Length == 0))
                        {
                            passages[^1] = passages[^1] with { SemanticEligible = false };
                            continue;
                        }
                        passages[^1] = passages[^1] with { SearchText = representation };
                        representations.Add((passages.Count - 1, representation));
                        var tokenCount = embeddings.CountTokens(representation); // Includes the production BOS token.
                        if (tokenCount > 512) throw new InvalidDataException($"Unrepresented evidence: {document.Id}/{section.Id} has {tokenCount} tokens.");
                        semanticTokens.Add(tokenCount);
                    }
                    offset += original.Length + 2;
                }
            }
            if (semantic && representations.Count > 0)
            {
                var batch = await embeddings.EmbedPassagesAsync(representations.Select(value => value.Text).ToArray(), CancellationToken.None);
                if (batch.Vectors.Count != representations.Count) throw new InvalidOperationException("Missing vectors.");
                for (var index = 0; index < representations.Count; index++)
                    passages[representations[index].Index] = passages[representations[index].Index] with { Embedding = batch.Vectors[index] };
                embeddedCount += representations.Count;
            }
            passageCount += passages.Count;
            if (!await writer.CommitRevisionAsync(new IndexCommitRequest(job.JobId, project, job.DocumentId,
                    revision.RevisionId.Value, job.ExpectedObservationEpoch, hash, file.Length, modified,
                    [new ContentNodeDraft(contentId, null, 0, Path.GetFileName(path), "text/plain", "root", 0)],
                    passages, semantic ? embeddings.Policy : null, []) { Sections = sections }))
                throw new InvalidOperationException("Corpus commit rejected.");
        }
        var service = new HybridSearchService(store, embeddings, new FlatVectorIndexFactory(),
            new VectorIndexCache(128L * 1024 * 1024), cpu);
        var modes = semantic ? new[] { SearchMode.Keyword, SearchMode.Semantic, SearchMode.Hybrid } : new[] { SearchMode.Keyword };
        var rows = new List<QueryRow>();
        foreach (var mode in modes)
        foreach (var query in corpus.Queries)
        {
            var request = new SearchRequest(project, mode, mode == SearchMode.Keyword ? null : query.SemanticQuery,
                mode == SearchMode.Semantic ? null : query.Clauses,
                ResultOptions: new SearchResultOptions(GroupLimit: 5, PreviewsPerGroup: 2, MaxGroupsPerDocument: 3),
                Scope: query.Scope, CandidateLimit: 1000);
            await service.SearchAsync(request); // Warm the query and vector snapshot outside the timing samples.
            var samples = new List<double>();
            SearchResponse? response = null;
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var watch = Stopwatch.StartNew();
                response = await service.SearchAsync(request);
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            var ranked = response!.Results.Select(group => query.Scope == SearchScope.Section
                ? sectionKeys[group.SectionId!.Value] : contentKeys[group.ContentId]).ToArray();
            var relevant = query.Relevant.ToHashSet(StringComparer.Ordinal);
            var metrics = Metrics(ranked, relevant, 5);
            if (mode != SearchMode.Keyword && !response.Branches.Semantic.Completed)
                throw new InvalidDataException($"The requested semantic benchmark branch did not complete: {query.Id}: " +
                    string.Join("; ", response.Warnings.Select(warning => warning.Message)));
            var anchors = 0;
            foreach (var preview in response.Results.SelectMany(group => group.Previews))
            {
                var read = (await store.ReadPassagesAsync(project, [preview.PassageId], 0, 0, response.SearchGeneration)).Single();
                if (preview.Excerpt != read.Text.Substring(preview.ExcerptStart, preview.ExcerptLength) ||
                    preview.Location != read.Location)
                    throw new InvalidDataException($"Preview/read provenance mismatch: {query.Id}/{preview.PassageId}");
                anchors++;
            }
            if (mode == SearchMode.Keyword && metrics.Recall < 1)
                throw new InvalidDataException($"Lexical labeled evidence was missed: {query.Id}, returned {string.Join(",", ranked)}");
            rows.Add(new QueryRow(query.Id, mode.ToString().ToLowerInvariant(), query.Scope.ToString().ToLowerInvariant(),
                ranked, query.Relevant, metrics.Recall, metrics.Mrr, metrics.Ndcg, samples.Order().ElementAt(1),
                JsonSerializer.SerializeToUtf8Bytes(SearchWireResponse.FromDomain(response, SearchDetail.Compact), BrokerJson.Options).Length,
                JsonSerializer.SerializeToUtf8Bytes(SearchWireResponse.FromDomain(response, SearchDetail.Full), BrokerJson.Options).Length,
                anchors, response.CandidateLimitReached, response.Branches));
        }
        return new
        {
            profile, preprocessing_version = IndexPreparation.Version,
            embedding_policy = embeddings.Policy?.Key, documents = corpus.Documents.Length,
            sections = sectionKeys.Count, passages = passageCount, embedded_passages = embeddedCount,
            mean_semantic_tokens = semanticTokens.Count == 0 ? (double?)null : semanticTokens.Average(),
            representations_over_512_tokens = semanticTokens.Count(value => value > 512),
            summaries = rows.GroupBy(row => row.Mode).Select(group => new
            {
                mode = group.Key, queries = group.Count(), recall_at_5 = group.Average(row => row.RecallAt5),
                mrr_at_5 = group.Average(row => row.MrrAt5), ndcg_at_5 = group.Average(row => row.NdcgAt5),
                median_search_ms = Percentile(group.Select(row => row.MedianSearchMs), 0.5),
                p95_search_ms = Percentile(group.Select(row => row.MedianSearchMs), 0.95),
                mean_compact_response_bytes = group.Average(row => row.CompactResponseBytes),
                mean_full_response_bytes = group.Average(row => row.FullResponseBytes),
                verified_anchors = group.Sum(row => row.VerifiedAnchors)
            }).ToArray(), query_results = rows
        };

        IEnumerable<(string Text, int Start)> Chunks(string text, string title, string heading, string fileName)
        {
            if (!semantic) { yield return (text, 0); yield break; }
            // A shared chunk partition keeps raw/prepared comparisons controlled. Both complete
            // representations must fit the production 512-token limit (including BOS); no truncation.
            var start = 0;
            while (start < text.Length)
            {
                var low = 1;
                var high = text.Length - start;
                while (low < high)
                {
                    var probe = low + (high - low + 1) / 2;
                    if (Fits(text.Substring(start, probe))) low = probe;
                    else high = probe - 1;
                }
                var length = low;
                if (start + length < text.Length)
                {
                    if (length > 1 && char.IsHighSurrogate(text[start + length - 1])) length--;
                    var boundary = length;
                    while (boundary > 1 && !char.IsWhiteSpace(text[start + boundary - 1])) boundary--;
                    if (boundary > length / 2) length = boundary;
                }
                var value = text.Substring(start, length);
                if (!Fits(value)) throw new InvalidDataException("A corpus token cannot fit the embedding input.");
                yield return (value, start);
                start += length;
            }
            bool Fits(string body) => embeddings.CountTokens($"Title: {title}\nFile: {fileName}\nHeading: {heading}\n{body}") <= 512 &&
                embeddings.CountTokens(SemanticTextPreparation.Compose(SemanticTextPreparation.CleanBody(body),
                    [("Title", title), ("Heading", heading)], embeddings.CountTokens)) <= 512;
        }
    }
    finally
    {
        await writer.StopAsync(CancellationToken.None);
        SqliteConnection.ClearAllPools();
    }
}

static Guid Id(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
static double Percentile(IEnumerable<double> source, double fraction)
{
    var sorted = source.Order().ToArray();
    return sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1)];
}
static (double Recall, double Mrr, double Ndcg) Metrics(IReadOnlyList<string> ranked, HashSet<string> relevant, int limit)
{
    var returned = ranked.Take(limit).ToArray();
    var positions = returned.Select((key, index) => (key, index)).Where(value => relevant.Contains(value.key)).ToArray();
    var dcg = positions.Sum(value => 1 / Math.Log2(value.index + 2));
    var ideal = Enumerable.Range(0, Math.Min(relevant.Count, limit)).Sum(index => 1 / Math.Log2(index + 2));
    return (returned.Intersect(relevant).Count() / (double)relevant.Count,
        positions.Length == 0 ? 0 : 1d / (positions[0].index + 1), ideal == 0 ? 0 : dcg / ideal);
}

sealed record Corpus(int Version, CorpusDocument[] Documents, CorpusQuery[] Queries);
sealed record CorpusDocument(string Id, string Title, CorpusSection[] Sections);
sealed record CorpusSection(string Id, string Heading, string[] Passages, int PrefixRepeat = 0, bool Boilerplate = false);
sealed record CorpusQuery(string Id, SearchScope Scope, string SemanticQuery, SearchClause[] Clauses, string[] Relevant);
sealed record QueryRow(string QueryId, string Mode, string Scope, string[] Returned, string[] Relevant,
    double RecallAt5, double MrrAt5, double NdcgAt5, double MedianSearchMs, int CompactResponseBytes,
    int FullResponseBytes, int VerifiedAnchors, bool CandidateLimitReached, SearchBranchDiagnosticsMap Branches);

sealed class BenchmarkPaths : IAppPaths, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ContextMole-search-quality", Guid.NewGuid().ToString("N"));
    public BenchmarkPaths()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(SourceDirectory);
    }
    public string DataDirectory => Path.Combine(_root, "data");
    public string DatabasePath => Path.Combine(DataDirectory, "index.db");
    public string AssetsDirectory
    {
        get
        {
            var data = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable);
            return Path.Combine(string.IsNullOrWhiteSpace(data) ? ContextMoleLocalData.GetDefaultDataDirectory() : Path.GetFullPath(data), "assets");
        }
    }
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string TempDirectory => Path.Combine(DataDirectory, "temp");
    public string SourceDirectory => Path.Combine(_root, "sources");
    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ContextMole-search-quality"));
        var absoluteRoot = Path.GetFullPath(_root);
        if (!ProjectValidation.IsSameOrChild(absoluteRoot, parent) || absoluteRoot == parent)
            throw new InvalidOperationException("Benchmark cleanup escaped its owned temporary directory.");
        if (Directory.Exists(absoluteRoot)) Directory.Delete(absoluteRoot, recursive: true);
    }
}
sealed class FixedCpuSettings : ICpuUsageSettings
{
    public CpuUsageProfile Profile => CpuUsageProfile.Normal;
    public int LogicalProcessorCount => Environment.ProcessorCount;
    public int ThreadLimit => Math.Min(4, Environment.ProcessorCount);
    public int MaximumThreadLimit => ThreadLimit;
    public event EventHandler? Changed { add { } remove { } }
    public void SetProfile(CpuUsageProfile profile) => throw new NotSupportedException();
}
sealed class FixedModelSettings(EmbeddingModelChoice model) : IEmbeddingModelSettings
{
    public EmbeddingModelChoice Model => model;
    public event EventHandler? Changed { add { } remove { } }
    public void SetModel(EmbeddingModelChoice value) => throw new NotSupportedException();
    public bool RefreshFromDisk() => false;
}
sealed class NoEmbeddings : IEmbeddingGenerator
{
    public bool IsAvailable => false;
    public string UnavailableReason => "Semantic benchmark disabled.";
    public EmbeddingPolicy? Policy => null;
    public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    public Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
