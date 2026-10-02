#:property TargetFramework=net10.0
#:property PublishAot=false
#:property NuGetLockFilePath=../artifacts/VidoreSnapshotReplay.packages.lock.json
#:project ../tools/BenchmarkSupport/ContextMole.BenchmarkSupport.csproj
#:project ../src/Infrastructure/ContextMole.Infrastructure.csproj
#:project ../src/Storage/ContextMole.Storage.csproj
#:project ../src/Search/ContextMole.Search.csproj
#:project ../src/Broker.Protocol/ContextMole.Broker.Protocol.csproj

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextMole.Benchmarks;
using ContextMole.Broker.Protocol;
using ContextMole.Core;
using ContextMole.Infrastructure;
using ContextMole.Search;
using ContextMole.Storage;
using Microsoft.Data.Sqlite;

if (args.Contains("--help"))
{
    Console.WriteLine("Read-only ViDoRe production-service replay. Required: --manifest --snapshot --model --assets --verify-report --verify-manifest --output. Optional: --validate-only (identity checks without inference), --verification-only (reproduce saved development rows without extra evaluation). Every saved development row must reproduce exactly before evaluation begins.");
    return;
}
string Option(string name)
{
    var index = Array.IndexOf(args, name);
    if (index < 0 || index + 1 == args.Length) throw new ArgumentException(name + " required");
    return args[index + 1];
}
var manifestPath = Path.GetFullPath(Option("--manifest"));
var snapshotPath = Path.GetFullPath(Option("--snapshot"));
var outputPath = Path.GetFullPath(Option("--output"));
var verificationReportPath = Path.GetFullPath(Option("--verify-report"));
var verificationManifestPath = Path.GetFullPath(Option("--verify-manifest"));
var verificationOnly = args.Contains("--verification-only");
var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
var manifest = JsonSerializer.Deserialize<VidoreManifest>(manifestBytes, BrokerJson.Options)!;
var verifyBytes = await File.ReadAllBytesAsync(verificationManifestPath);
var verifyManifest = JsonSerializer.Deserialize<VidoreManifest>(verifyBytes, BrokerJson.Options)!;
manifest.Validate(); verifyManifest.Validate();
if (manifest.Dataset != verifyManifest.Dataset || manifest.Revision != verifyManifest.Revision ||
    manifest.Documents.Length != verifyManifest.Documents.Length || manifest.Pages.Length != verifyManifest.Pages.Length ||
    manifest.Documents.Any(doc => !verifyManifest.Documents.Any(other => doc.Id == other.Id && doc.Sha256 == other.Sha256 && doc.PageCount == other.PageCount && doc.License == other.License)) ||
    !manifest.Pages.OrderBy(page => page.CorpusId).SequenceEqual(verifyManifest.Pages.OrderBy(page => page.CorpusId)))
    throw new InvalidDataException("Replay/verification manifests must retain the identical complete PDF corpus.");
using var provenanceJson = JsonDocument.Parse(await File.ReadAllBytesAsync(snapshotPath + ".provenance.json"));
var provenance = provenanceJson.RootElement;
if (!provenance.GetProperty("owned_public_benchmark_index").GetBoolean()) throw new InvalidDataException("Snapshot must be an owned public benchmark index.");
var snapshotHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(snapshotPath)));
if (snapshotHash != provenance.GetProperty("snapshot_sha256").GetString()) throw new InvalidDataException("Snapshot SHA256 mismatch.");
using var verificationJson = JsonDocument.Parse(await File.ReadAllBytesAsync(verificationReportPath));
var verification = verificationJson.RootElement;
if (verification.GetProperty("manifest_sha256").GetString() != Convert.ToHexStringLower(SHA256.HashData(verifyBytes)) ||
    provenance.GetProperty("manifest_sha256").GetString() != verification.GetProperty("manifest_sha256").GetString())
    throw new InvalidDataException("Verification report manifest hash differs.");
var verificationRows = verification.GetProperty("queries").EnumerateArray().Where(row => row.GetProperty("preview_policy").GetString() == "rank")
    .ToDictionary(row => row.GetProperty("id").GetString()!, row => row.Clone(), StringComparer.Ordinal);
if (verificationRows.Count != verifyManifest.Queries.Length || !verificationRows.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(verifyManifest.Queries.Select(query => query.Id))) throw new InvalidDataException("Every saved development row must be reproduced before evaluation.");
var allQueries = verifyManifest.Queries.Concat(verificationOnly ? [] : manifest.Queries).DistinctBy(query => query.Id).ToArray();
if (!verificationOnly && manifest.Queries.Any(query => verificationRows.ContainsKey(query.Id))) throw new InvalidDataException("Verification and evaluation selections must be disjoint.");
var model = Enum.Parse<EmbeddingModelChoice>(Option("--model"));
var savedOptions = JsonSerializer.Deserialize<SearchResultOptions>(verification.GetProperty("result_options"), BrokerJson.Options);
if (verification.GetProperty("selected_model").GetString() != model.ToString() ||
    verification.GetProperty("mode").GetString() != "semantic" || verification.GetProperty("ocr_enabled").GetBoolean() ||
    verification.GetProperty("candidate_limit").GetInt32() != 10000 || verification.GetProperty("primary_preview_policy").GetString() != "rank" ||
    savedOptions != new SearchResultOptions(GroupLimit: 50, PreviewsPerGroup: 10, MaxGroupsPerDocument: 50,
        SemanticSimilarityThreshold: -1, StrictSemanticThreshold: false))
    throw new InvalidDataException("Saved development report has a different model, mode or consumer output budget.");
using var paths = new ReplayPaths(snapshotPath, Option("--assets"));
var cpuSettings = new ReplayCpuSettings();
using var cpu = new GlobalCpuBudget(cpuSettings);
var store = new SqliteSearchStore(paths);
var project = provenance.GetProperty("project_id").GetGuid();
var sourceDocuments = new Dictionary<string, VidoreDocument>(StringComparer.OrdinalIgnoreCase);
foreach (var row in provenance.GetProperty("source_documents").EnumerateArray())
{
    var document = manifest.Documents.Single(doc => doc.Id == row.GetProperty("original_document_id").GetString());
    if (document.Sha256 != row.GetProperty("source_sha256").GetString()) throw new InvalidDataException("Snapshot source hash differs.");
    var original = VidoreManifest.ResolvePdfPath(manifestPath, document.File);
    if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(original))) != document.Sha256)
        throw new InvalidDataException("Original PDF hash differs from snapshot provenance.");
    sourceDocuments.Add(Path.GetFullPath(row.GetProperty("source_path").GetString()!), document);
}
if (sourceDocuments.Count != manifest.Documents.Length) throw new InvalidDataException("Snapshot source coverage is incomplete.");
var corpusPages = manifest.Pages.ToDictionary(page => page.CorpusId, page => new PdfPageKey(page.DocumentId, page.PageNumber));
var physicalPages = corpusPages.Values.ToHashSet();
var corpusIds = manifest.Pages.ToDictionary(page => new PdfPageKey(page.DocumentId, page.PageNumber), page => page.CorpusId);
var indexedPageKeys = new HashSet<PdfPageKey>();
using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshotPath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
{
    await connection.OpenAsync();
    using var generation = connection.CreateCommand();
    generation.CommandText = "SELECT search_generation FROM projects WHERE id=$project;";
    generation.Parameters.AddWithValue("$project", project.ToString());
    if ((long)generation.ExecuteScalar()! != provenance.GetProperty("search_generation").GetInt64()) throw new InvalidDataException("Snapshot generation differs.");
    using var revisions = connection.CreateCommand();
    revisions.CommandText = "SELECT d.path,r.sha256,r.preparation_version,r.embedding_policy_json FROM documents d JOIN document_revisions r ON r.id=d.active_revision_id WHERE d.project_id=$project;";
    revisions.Parameters.AddWithValue("$project", project.ToString());
    using (var revisionReader = revisions.ExecuteReader())
    {
        var count = 0;
        while (revisionReader.Read())
        {
            var document = sourceDocuments[Path.GetFullPath(revisionReader.GetString(0))];
            using var policy = JsonDocument.Parse(revisionReader.GetString(3));
            if (revisionReader.GetString(1) != document.Sha256 ||
                revisionReader.GetString(2) != provenance.GetProperty("embedding_policy").GetProperty("preparation_version").GetString() ||
                policy.RootElement.GetProperty("Key").GetString() != provenance.GetProperty("embedding_policy").GetProperty("key").GetString())
                throw new InvalidDataException("Active revision source/preparation/model differs from snapshot provenance.");
            count++;
        }
        if (count != manifest.Documents.Length) throw new InvalidDataException("Snapshot active revision coverage is incomplete.");
    }
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT d.path,p.page FROM passages p JOIN documents d ON d.active_revision_id=p.revision_id JOIN content_nodes c ON c.id=p.content_id WHERE c.parent_id IS NULL AND p.page IS NOT NULL;";
    using var reader = command.ExecuteReader();
    while (reader.Read()) indexedPageKeys.Add(new PdfPageKey(sourceDocuments[Path.GetFullPath(reader.GetString(0))].Id, reader.GetInt32(1)));
}
if (args.Contains("--validate-only"))
{
    Console.WriteLine($"Validated immutable SHA256, generation, active policies and {sourceDocuments.Count} original PDF hashes; {verificationRows.Count} development rows await exact replay verification. No inference performed.");
    return;
}
await using var embeddings = new GraniteEmbeddingGenerator(paths, cpuSettings, new ReplayModelSettings(model), cpu);
await embeddings.ReloadAsync();
if (!embeddings.IsAvailable || embeddings.Policy!.Key != provenance.GetProperty("embedding_policy").GetProperty("key").GetString() ||
    embeddings.Policy.Key != verification.GetProperty("embedding_policy").GetProperty("key").GetString())
    throw new InvalidDataException("Model/preparation policy must match the immutable snapshot and saved development report.");
var mode = SearchMode.Semantic;
var previewPolicies = new[] { "rank" };
var compareNaturalHybrid = false;
HashSet<string>? comparisonQueryIds = null;
var includeEvidence = true;
var qualityOnly = true;
var iterations = 1;
var ks = new[] { 5, 10 };
var token = CancellationToken.None;
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
string? progressPath = outputPath + ".progress.jsonl";
await File.WriteAllTextAsync(progressPath, "");
var verifiedQueries = 0;
    var vectorCache = new VectorIndexCache(128L * 1024 * 1024);
    var services = previewPolicies.ToDictionary(policy => policy, policy => new HybridSearchService(store, embeddings,
        new FlatVectorIndexFactory(), vectorCache, cpu, diversifyPreviews: policy == "diverse"));
    var rows = new List<VidoreQueryResult>();
    var searchOptions = new SearchResultOptions(GroupLimit: 50, PreviewsPerGroup: 10, MaxGroupsPerDocument: 50,
        SemanticSimilarityThreshold: mode == SearchMode.Keyword ? 0.25 : -1, StrictSemanticThreshold: false);
    foreach (var query in allQueries)
    foreach (var previewPolicy in previewPolicies.Where(policy => policy == previewPolicies[0] || comparisonQueryIds is null || comparisonQueryIds.Contains(query.Id))
        .Concat(compareNaturalHybrid && (comparisonQueryIds is null || comparisonQueryIds.Contains(query.Id)) ? new[] { "natural_hybrid_msm0" } : Array.Empty<string>()))
    {
        token.ThrowIfCancellationRequested();
        var naturalHybrid = previewPolicy == "natural_hybrid_msm0";
        var queryMode = naturalHybrid ? SearchMode.Hybrid : mode;
        var service = services[naturalHybrid ? previewPolicies[0] : previewPolicy];
        var clauses = queryMode == SearchMode.Semantic ? null : LexicalClauses(query.Text);
        var request = new SearchRequest(project, queryMode, queryMode == SearchMode.Keyword ? null : query.Text, clauses,
            MinimumShouldMatch: naturalHybrid ? 0 : null,
            Filters: new SearchFilters(AttachmentScope: AttachmentScope.RootOnly), ResultOptions: searchOptions,
            Scope: SearchScope.Passage, CandidateLimit: 10000, Detail: SearchDetail.Compact);
        if (!qualityOnly) _ = await service.SearchAsync(request, token); // Exclude model/vector warm-up from warm query latency.
        var timings = new double[qualityOnly ? 1 : iterations];
        SearchResponse response = null!;
        for (var iteration = 0; iteration < timings.Length; iteration++)
        {
            var clock = Stopwatch.StartNew();
            response = await service.SearchAsync(request, token);
            clock.Stop();
            timings[iteration] = clock.Elapsed.TotalMilliseconds;
        }
        if (mode != SearchMode.Keyword && response.Warnings.Any(warning => warning.Code.StartsWith("semantic_", StringComparison.Ordinal)))
            throw new InvalidOperationException($"Query {query.Id} did not complete semantic search: {string.Join(';', response.Warnings.Select(warning => warning.Message))}");
        var previews = response.Results.SelectMany(group => group.Previews).ToArray();
        var reads = new Dictionary<Guid, PassageInfo>();
        foreach (var batch in previews.Select(preview => preview.PassageId).Distinct().Chunk(50))
            foreach (var read in await store.ReadPassagesAsync(project, batch, 0, 0, response.SearchGeneration, token))
                reads[read.PassageId] = read;
        var anchors = new List<PdfRetrievalAnchor>();
        var invalidCitations = new List<string>();
        var validCitations = 0;
        foreach (var preview in previews)
        {
            if (preview.AttachmentChain.Count > 0 ||
                !sourceDocuments.TryGetValue(Path.GetFullPath(preview.SourcePath), out var document) ||
                preview.Location is not { Kind: LocationKind.Page, Page: not null })
            { invalidCitations.Add($"{preview.PassageId}: unknown PDF/page location"); continue; }
            var page = new PdfPageKey(document.Id, preview.Location.Page.Value);
            if (!physicalPages.Contains(page))
            { invalidCitations.Add($"{preview.PassageId}: page outside corpus"); continue; }
            var literal = PdfBenchmarkMetrics.IsLiteralCitation(preview, reads.GetValueOrDefault(preview.PassageId), page);
            if (literal) validCitations++;
            else invalidCitations.Add($"{preview.PassageId}: identity/location/literal excerpt mismatch");
            anchors.Add(new PdfRetrievalAnchor(page, preview.PassageId, preview.FusedScore, preview.SemanticRank,
                preview.KeywordRank, literal ? preview.Location.Region : null));
        }
        var metrics = ks.Select(k => PdfBenchmarkMetrics.Evaluate(query, corpusPages, anchors, k)).ToArray();
        var ranked = PdfBenchmarkMetrics.RankPages(anchors).Take(ks.Max()).Select(anchor => new
        {
            corpus_id = corpusIds[anchor.Page], document_id = anchor.Page.DocumentId, page_number = anchor.Page.PageNumber,
            anchor.Score, anchor.SemanticRank, anchor.KeywordRank,
            region = anchor.Region, relevant_grade = query.Qrels.Where(qrel => qrel.CorpusId == corpusIds[anchor.Page])
                .Select(qrel => qrel.Score).DefaultIfEmpty(0).Max()
        }).ToArray();
        var row = new VidoreQueryResult(query.Id, query.Language, query.Text, metrics, Median(timings), timings,
            JsonSerializer.SerializeToUtf8Bytes(SearchWireResponse.FromDomain(response, SearchDetail.Compact), BrokerJson.Options).Length,
            JsonSerializer.SerializeToUtf8Bytes(SearchWireResponse.FromDomain(response, SearchDetail.Full), BrokerJson.Options).Length,
            previews.Length, anchors.Select(anchor => anchor.Page).Distinct().Count(), validCitations,
            invalidCitations.ToArray(), response.CandidateLimitReached, response.Branches,
            response.Warnings, response.SuppressedGroupCount, response.Results.Count(group => group.Previews.Count == 10),
            query.Qrels.Select(qrel => corpusPages[qrel.CorpusId]).Distinct().Count(indexedPageKeys.Contains), ranked);
        row.PreviewPolicy = previewPolicy;
        row.QueryMode = queryMode.ToString().ToLowerInvariant();
        row.MinimumShouldMatch = naturalHybrid ? 0 : null;
        row.QueryCompleteTokens = mode == SearchMode.Keyword ? null : embeddings.CountTokens(query.Text);
        row.QueryMaximumTokens = mode == SearchMode.Keyword ? null : GraniteEmbeddingInputEncoding.QueryMaximumTokens;
        rows.Add(row);
        if (includeEvidence)
            row.ExposedEvidence = previews.Select(preview =>
            {
                var document = sourceDocuments.GetValueOrDefault(Path.GetFullPath(preview.SourcePath));
                var page = preview.Location.Page is { } number && document is not null ? new PdfPageKey(document.Id, number) : null;
                var read = reads.GetValueOrDefault(preview.PassageId);
                return new
                {
                    preview.PassageId, indexed_document_id = preview.DocumentId, preview.ContentId,
                    original_document_id = document?.Id, page_number = preview.Location.Page,
                    literal_citation_valid = page is not null && PdfBenchmarkMetrics.IsLiteralCitation(preview, read, page),
                    preview.Excerpt, preview.ExcerptStart, preview.ExcerptLength,
                    preview.Location, preview.ExtractionMethod, preview.FusedScore, preview.SemanticRank, preview.KeywordRank,
                    preview.SemanticSimilarity, preview.KeywordScore, preview.Truncated
                };
            }).ToArray();
        if (verificationRows.TryGetValue(query.Id, out var expected))
        {
            VerifyReplay(expected, row);
            verifiedQueries++;
            if (verifiedQueries % 25 == 0 || verifiedQueries == verificationRows.Count)
                Console.Error.WriteLine($"Exactly reproduced {verifiedQueries}/{verificationRows.Count} saved development queries.");
            rows.RemoveAt(rows.Count - 1);
            continue;
        }
        if (progressPath is not null)
            await File.AppendAllTextAsync(progressPath, JsonSerializer.Serialize(row, BrokerJson.Options) + "\n", new UTF8Encoding(false), token);
        Console.Error.WriteLine($"Query {query.Id} ({query.Language}, {previewPolicy}): {row.ExposedPages} unique exposed pages, {row.ValidCitations}/{row.ExposedPreviews} literal citations.");
    }

if (verifiedQueries != verificationRows.Count || rows.Count != (verificationOnly ? 0 : manifest.Queries.Length)) throw new InvalidDataException("Replay coverage is incomplete.");
if (verificationOnly)
{
    if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(snapshotPath))) != snapshotHash)
        throw new InvalidDataException("Immutable snapshot changed during verification.");
    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
    {
        benchmark = "vidore_readonly_saved_development_verification", verification_only = true,
        saved_development_rows_exactly_reproduced = verifiedQueries, selected_model = model.ToString(),
        embedding_policy = embeddings.Policy, immutable_snapshot_sha256 = snapshotHash,
        snapshot_generation = provenance.GetProperty("search_generation").GetInt64(),
        verification_report_sha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(verificationReportPath))),
        note = "No additional questions evaluated; all stored ranks, scores, literal excerpts, identities, citations and metrics reproduced exactly."
    }, new JsonSerializerOptions(BrokerJson.Options) { WriteIndented = true }) + "\n");
    Console.WriteLine($"Exactly reproduced {verifiedQueries} saved development rows; no additional questions evaluated. {outputPath}");
    return;
}
var report = new
{
    benchmark = "vidore_readonly_snapshot_consumer_output_replay", schema_version = 1,
    manifest.Dataset, manifest.Revision, manifest.Selection, manifest_sha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
    selected_model = model.ToString(), embedding_policy = embeddings.Policy, mode, ocr_enabled = false,
    primary_preview_policy = "rank", candidate_limit = 10000, result_options = searchOptions,
    document_count = manifest.Documents.Length, corpus_pages = manifest.Pages.Length, query_count = rows.Count,
    query_counts_by_language = rows.GroupBy(row => row.Language).ToDictionary(group => group.Key, group => group.Count()),
    indexing_performed = false, immutable_snapshot_sha256 = snapshotHash, snapshot_project_id = project,
    snapshot_generation = provenance.GetProperty("search_generation").GetInt64(),
    saved_development_rows_exactly_reproduced = verifiedQueries,
    verification_report_sha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(verificationReportPath))),
    evaluation_scope = "Previously used HR questions; explicitly reused diagnostic evaluation, not new held-out model selection.",
    quality_only = true, measured_iterations = 0, warm_latency_median_ms = (double?)null,
    indexed_pages = indexedPageKeys.Count, citation_validity = (double)rows.Sum(row => row.ValidCitations) / rows.Sum(row => row.ExposedPreviews),
    summary = ks.Select(k => new { k, recall = rows.Average(row => row.Metrics.Single(metric => metric.K == k).Recall),
        ndcg = rows.Average(row => row.Metrics.Single(metric => metric.K == k).Ndcg),
        mrr = rows.Average(row => row.Metrics.Single(metric => metric.K == k).ReciprocalRank),
        evidence_area_coverage = NullableMean(rows.Select(row => row.Metrics.Single(metric => metric.K == k).EvidenceAreaCoverage)),
        evidence_region_recall = NullableMean(rows.Select(row => row.Metrics.Single(metric => metric.K == k).EvidenceRegionRecall)) }),
    queries = rows,
    note = "Exact production search/citation/scorer loop; immutable existing index, no extraction, migration, writes or re-embedding. Source path strings may refer to removed temporary copies; original PDF bytes are verified in the pinned external cache. Single query observations are not warmed performance measurements."
};
if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(snapshotPath))) != snapshotHash)
    throw new InvalidDataException("Immutable snapshot changed during replay.");
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(BrokerJson.Options) { WriteIndented = true }) + "\n");
Console.WriteLine($"Reproduced {verifiedQueries} development queries exactly; evaluated {rows.Count} reused queries. {outputPath}");

static void VerifyReplay(JsonElement expected, VidoreQueryResult actual)
{
    var actualJson = JsonSerializer.SerializeToElement(actual, BrokerJson.Options);
    foreach (var name in new[] { "id", "language", "text", "metrics", "ranked_pages", "exposed_previews", "exposed_pages", "valid_citations", "invalid_citations", "exposed_evidence" })
        if (expected.GetProperty(name).GetRawText() != actualJson.GetProperty(name).GetRawText())
        {
            // Object indentation differs; compare normalized JSON, not formatting.
            var first = JsonSerializer.Serialize(expected.GetProperty(name));
            var second = JsonSerializer.Serialize(actualJson.GetProperty(name));
            if (first != second) throw new InvalidDataException($"Saved query {actual.Id} replay differs in {name}; stop before scoring evaluation queries.");
        }
}
static SearchClause[] LexicalClauses(string query)
{
    var terms = System.Text.RegularExpressions.Regex.Matches(query, @"[\p{L}\p{Nd}]+")
        .Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
    if (terms.Length == 0) throw new InvalidDataException("A query has no lexical tokens.");
    return terms.Select((term, index) => new SearchClause("token_" + index, term, SearchClauseOccur.Should,
        Fields: [SearchField.Body, SearchField.Title, SearchField.Heading])).ToArray();
}
static double Median(IEnumerable<double> values)
{
    var ordered = values.Order().ToArray();
    return ordered.Length % 2 == 0 ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2 : ordered[ordered.Length / 2];
}
static double? NullableMean(IEnumerable<double?> values)
{
    var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
    return present.Length == 0 ? null : present.Average();
}

sealed record VidoreQueryResult(string Id, string Language, string Text, PdfRetrievalMetrics[] Metrics,
    double MedianSearchMs, double[] SearchTimingsMs, int CompactResponseBytes, int FullResponseBytes,
    int ExposedPreviews, int ExposedPages, int ValidCitations, string[] InvalidCitations,
    bool CandidateLimitReached, SearchBranchDiagnosticsMap Branches, IReadOnlyList<SearchWarning> Warnings,
    int SuppressedGroups, int GroupsAtPreviewCap, int RelevantIndexedPages, object RankedPages)
{
    public string PreviewPolicy { get; set; } = "rank";
    public string QueryMode { get; set; } = "semantic";
    public int? MinimumShouldMatch { get; set; }
    public int? QueryCompleteTokens { get; set; }
    public int? QueryMaximumTokens { get; set; }
    // Consumer-visible excerpts only, with no extra search, hidden passage expansion, or scoring changes.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public object[]? ExposedEvidence { get; set; }
}


sealed class ReplayPaths(string snapshot, string assets) : IAppPaths, IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ContextMole-snapshot-replay-" + Guid.NewGuid().ToString("N"));
    public string DataDirectory => _temp;
    public string DatabasePath => snapshot;
    public string AssetsDirectory => assets;
    public string LogsDirectory => Path.Combine(_temp, "logs");
    public string TempDirectory => Path.Combine(_temp, "temp");
    public void Dispose() { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }
}
sealed class ReplayCpuSettings : ICpuUsageSettings
{
    public CpuUsageProfile Profile => CpuUsageProfile.Normal;
    public int LogicalProcessorCount => Environment.ProcessorCount;
    public int ThreadLimit => Math.Min(4, Environment.ProcessorCount);
    public int MaximumThreadLimit => ThreadLimit;
    public event EventHandler? Changed { add { } remove { } }
    public void SetProfile(CpuUsageProfile profile) => throw new NotSupportedException();
}
sealed class ReplayModelSettings(EmbeddingModelChoice model) : IEmbeddingModelSettings
{
    public EmbeddingModelChoice Model => model;
    public event EventHandler? Changed { add { } remove { } }
    public void SetModel(EmbeddingModelChoice value) => throw new NotSupportedException();
    public bool RefreshFromDisk() => false;
}
