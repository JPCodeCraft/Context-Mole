#:property TargetFramework=net10.0
#:property PublishAot=false
#:property NuGetLockFilePath=../artifacts/VidorePdfBenchmark.packages.lock.json
#:project ../tools/BenchmarkSupport/ContextMole.BenchmarkSupport.csproj
#:project ../src/Documents/ContextMole.Documents.csproj
#:project ../src/Infrastructure/ContextMole.Infrastructure.csproj
#:project ../src/Storage/ContextMole.Storage.csproj
#:project ../src/Search/ContextMole.Search.csproj
#:project ../src/Indexing/ContextMole.Indexing.csproj
#:project ../src/Broker.Protocol/ContextMole.Broker.Protocol.csproj

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
using UglyToad.PdfPig;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("dotnet run --file tools/VidorePdfBenchmark.cs -- --manifest <cache>/vidore/manifest.json [--mode semantic|keyword|hybrid] [--model Granite97M] [--ocr] [--assets <installed-assets>] [--include-evidence] [--quality-only] [--stable-document-ids] [--preview-diversity rank|diverse|compare] [--comparison-manifest <devmanifest>] [--compare-natural-hybrid] [--k 5,10] [--iterations 3] [--timeout-minutes 60] [--index-snapshot-out <new-db>] [--output artifacts/vidore.json]");
    Console.WriteLine("Uses verified original PDFs, the actual extraction registry, IndexingCoordinator, isolated SQLite store, installed embeddings and HybridSearchService. No dataset/model download, application index/settings changes or answer generation. Default semantic mode needs installed Granite97M assets. --ocr enables only verified cached PP-OCR assets; otherwise OCR is unavailable and extraction warnings remain in the report. Ranks exposed production previews globally by fused score, then deduplicates physical pages before graded nDCG/Recall/MRR and geometric source-region coverage. Reports output/candidate caps, extraction failures, page coverage, literal citation checks, warm latency and compact/full output size. Keyword/hybrid use an explicit simple optional-token lexical baseline. This consumer-output adaptation is not the official ViDoRe visual retriever protocol or an LLM answer-quality score.");
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
var knownOptions = new HashSet<string>(["--manifest", "--mode", "--model", "--ocr", "--assets", "--include-evidence", "--quality-only", "--stable-document-ids", "--preview-diversity", "--comparison-manifest", "--compare-natural-hybrid", "--k", "--iterations", "--timeout-minutes", "--index-snapshot-out", "--output"], StringComparer.Ordinal);
foreach (var argument in args.Where(argument => argument.StartsWith("--", StringComparison.Ordinal)))
    if (!knownOptions.Contains(argument)) throw new ArgumentException($"Unknown option: {argument}.");
var manifestPath = Path.GetFullPath(Option("--manifest") ?? throw new ArgumentException("--manifest is required. Run the pinned dataset downloader first."));
var mode = (Option("--mode") ?? "semantic") switch
{
    "semantic" => SearchMode.Semantic, "keyword" => SearchMode.Keyword, "hybrid" => SearchMode.Hybrid,
    _ => throw new ArgumentException("--mode must be semantic, keyword, or hybrid.")
};
var model = Enum.Parse<EmbeddingModelChoice>(Option("--model") ?? "Granite97M");
if (!Enum.IsDefined(model)) throw new ArgumentException("Unknown embedding model.");
var useOcr = args.Contains("--ocr", StringComparer.Ordinal);
var includeEvidence = args.Contains("--include-evidence", StringComparer.Ordinal);
var qualityOnly = args.Contains("--quality-only", StringComparer.Ordinal);
var stableDocumentIds = args.Contains("--stable-document-ids", StringComparer.Ordinal);
IReadOnlyList<BenchmarkDocumentIdentity>? documentIdentityMap = null;
var compareNaturalHybrid = args.Contains("--compare-natural-hybrid", StringComparer.Ordinal);
if (compareNaturalHybrid && mode != SearchMode.Semantic)
    throw new ArgumentException("--compare-natural-hybrid requires primary --mode semantic.");
var previewPolicies = (Option("--preview-diversity") ?? "rank") switch
{
    "rank" => new[] { "rank" }, "diverse" => new[] { "diverse" }, "compare" => new[] { "rank", "diverse" },
    _ => throw new ArgumentException("--preview-diversity must be rank, diverse, or compare.")
};
var ks = (Option("--k") ?? "5,10").Split(',').Select(int.Parse).Distinct().Order().ToArray();
if (ks.Length == 0 || ks.Any(k => k is < 1 or > 500)) throw new ArgumentException("--k requires cutoffs between 1 and 500.");
var iterations = int.Parse(Option("--iterations") ?? "3");
if (iterations is < 1 or > 20) throw new ArgumentException("--iterations must be between 1 and 20.");
var timeoutMinutes = double.Parse(Option("--timeout-minutes") ?? "60", System.Globalization.CultureInfo.InvariantCulture);
if (!double.IsFinite(timeoutMinutes) || timeoutMinutes <= 0) throw new ArgumentException("--timeout-minutes must be positive.");
var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
var manifest = JsonSerializer.Deserialize<VidoreManifest>(manifestBytes, BrokerJson.Options)
    ?? throw new InvalidDataException("Missing manifest.");
manifest.Validate();
HashSet<string>? comparisonQueryIds = null;
string? comparisonManifestHash = null;
if (Option("--comparison-manifest") is { } comparisonManifestPath)
{
    var comparisonBytes = await File.ReadAllBytesAsync(Path.GetFullPath(comparisonManifestPath));
    var comparison = JsonSerializer.Deserialize<VidoreManifest>(comparisonBytes, BrokerJson.Options)
        ?? throw new InvalidDataException("Missing comparison manifest.");
    comparison.Validate();
    if (comparison.Documents.Length != manifest.Documents.Length || comparison.Pages.Length != manifest.Pages.Length ||
        comparison.Documents.Any(document => !manifest.Documents.Any(original => original.Id == document.Id && original.Sha256 == document.Sha256 && original.PageCount == document.PageCount)) ||
        comparison.Pages.Any(page => !manifest.Pages.Any(original => original.CorpusId == page.CorpusId && original.DocumentId == page.DocumentId && original.PageNumber == page.PageNumber)) ||
        comparison.Queries.Any(query => !manifest.Queries.Any(original => original.Id == query.Id && original.Text == query.Text && original.Language == query.Language)))
        throw new InvalidDataException("Comparison queries must be a fixed subset of the identical complete PDF corpus.");
    comparisonQueryIds = comparison.Queries.Select(query => query.Id).ToHashSet(StringComparer.Ordinal);
    comparisonManifestHash = Convert.ToHexStringLower(SHA256.HashData(comparisonBytes));
}
foreach (var warning in manifest.AnnotationWarnings) Console.Error.WriteLine("Dataset annotation warning: " + warning);
var corpusPages = manifest.Pages.ToDictionary(page => page.CorpusId, page => new PdfPageKey(page.DocumentId, page.PageNumber));
var physicalPages = corpusPages.Values.ToHashSet();
var corpusIds = manifest.Pages.ToDictionary(page => new PdfPageKey(page.DocumentId, page.PageNumber), page => page.CorpusId);
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
var token = cancellation.Token;
string? progressPath = null;
if (Option("--output") is { } requestedOutput)
{
    progressPath = Path.GetFullPath(requestedOutput) + ".progress.jsonl";
    Directory.CreateDirectory(Path.GetDirectoryName(progressPath)!);
    await File.WriteAllTextAsync(progressPath, "", new UTF8Encoding(false), token);
}
using var paths = new VidorePaths(Option("--assets"));
var sourceDocuments = new Dictionary<string, VidoreDocument>(StringComparer.OrdinalIgnoreCase);
foreach (var document in manifest.Documents)
{
    var original = VidoreManifest.ResolvePdfPath(manifestPath, document.File);
    await using (var stream = File.OpenRead(original))
        if (!string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)), document.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"PDF hash mismatch: {document.Id}.");
    using (var pdf = PdfDocument.Open(original))
        if (pdf.NumberOfPages != document.PageCount) throw new InvalidDataException($"PDF page-count mismatch: {document.Id}.");
    var target = Path.Combine(paths.SourceDirectory, document.Id + ".pdf");
    File.Copy(original, target, overwrite: false);
    // The copied bytes, not an independently reopened source file, become indexed evidence.
    await using (var stream = File.OpenRead(target))
        if (!string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)), document.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Copied PDF hash mismatch: {document.Id}.");
    sourceDocuments.Add(Path.GetFullPath(target), document);
}
var cpuSettings = new VidoreCpuSettings();
using var cpu = new GlobalCpuBudget(cpuSettings);
await using IEmbeddingGenerator embeddings = mode == SearchMode.Keyword
    ? new VidoreNoEmbeddings()
    : new GraniteEmbeddingGenerator(paths, cpuSettings, new VidoreModelSettings(model), cpu);
if (mode != SearchMode.Keyword)
{
    await embeddings.ReloadAsync(token);
    if (!embeddings.IsAvailable) throw new InvalidOperationException($"Installed model assets are required: {embeddings.UnavailableReason}");
}
using var ocr = useOcr ? new PpOcrV6Engine(paths, cpuSettings, cpu) : null;
if (ocr is not null) await ocr.PrepareCachedAssetsAsync(token);
IOcrEngine ocrEngine = ocr is not null ? ocr : new VidoreNoOcr();
using var writer = new DatabaseWriterService(paths);
var store = new SqliteSearchStore(paths);
await writer.StartAsync(token);
await writer.Ready.WaitAsync(TimeSpan.FromSeconds(30), token);
try
{
    var project = await writer.CreateProjectAsync(new CreateProjectRequest("ViDoRe PDF benchmark", [paths.SourceDirectory]), token);
    var folder = (await store.ListProjectsAsync(token)).Single(item => item.Id == project).Folders.Single().Id;
    foreach (var (source, _) in sourceDocuments)
    {
        var file = new FileInfo(source);
        await writer.ObserveFileAsync(new FileObservation(project, folder, source, file.Length,
            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)), token);
    }
    if (stableDocumentIds)
    {
        await using var identityConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath }.ToString());
        await identityConnection.OpenAsync(token);
        documentIdentityMap = await VidoreBenchmarkIdentity.ApplyAsync(identityConnection, project, manifest.Dataset,
            manifest.Revision, sourceDocuments.Select(pair => new BenchmarkDocumentIdentityInput(pair.Key,
                pair.Value.Id, pair.Value.Sha256)).ToArray(), token);
        Console.Error.WriteLine($"Fresh fixture uses {VidoreBenchmarkIdentity.Protocol}; production-derived identities follow normally.");
    }
    using var coordinator = new IndexingCoordinator(writer, store, paths, new DocumentExtractionRegistry(ocrEngine),
        embeddings, new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(), cpu,
        NullLogger<IndexingCoordinator>.Instance);
    Console.Error.WriteLine($"Indexing {manifest.Documents.Length} PDFs / {manifest.Pages.Length} pages through the production pipeline...");
    var indexingClock = Stopwatch.StartNew();
    await coordinator.StartAsync(token);
    ProjectSummary summary;
    try
    {
        var stable = 0;
        do
        {
            await Task.Delay(250, token);
            summary = (await store.ListProjectsAsync(token)).Single(item => item.Id == project);
            stable = summary.DocumentCount == manifest.Documents.Length && summary.PendingCount == 0 ? stable + 1 : 0;
        } while (stable < 2);
    }
    finally { await coordinator.StopAsync(CancellationToken.None); }
    indexingClock.Stop();
    var inventory = await store.ListDocumentsAsync(new DocumentListRequest(project, Limit: 100), token);
    if (inventory.NextCursor is not null) throw new InvalidOperationException("Unexpected inventory pagination for the HR corpus.");
    var errors = await store.ListProjectErrorsAsync(project, 100, token);
    var inventoryRows = inventory.Documents.Select(document => new
    {
        id = sourceDocuments[Path.GetFullPath(document.SourcePath)].Id,
        document.Status, document.ExtractedPassageCount, document.ErrorCount, document.ErrorSummary,
        expected_pages = sourceDocuments[Path.GetFullPath(document.SourcePath)].PageCount
    }).ToArray();
    long embeddedPassageCount = 0;
    if (mode != SearchMode.Keyword)
    {
        var metadata = await store.LoadVectorSnapshotMetadataAsync(project, embeddings.Policy!, token);
        embeddedPassageCount = metadata.EntryCount;
        if (metadata.EntryCount == 0 || !metadata.IsComplete)
            throw new InvalidOperationException($"Semantic indexing is unavailable or incomplete: {metadata.Warning}");
    }
    var indexedPageKeys = new HashSet<PdfPageKey>();
    var extractedMethods = new Dictionary<string, int>(StringComparer.Ordinal);
    await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
    {
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT d.path, p.page, p.extraction_method FROM passages p JOIN documents d ON d.active_revision_id = p.revision_id JOIN content_nodes c ON c.id = p.content_id WHERE d.project_id = $project AND c.parent_id IS NULL;";
        command.Parameters.AddWithValue("$project", project.ToString());
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var document = sourceDocuments[Path.GetFullPath(reader.GetString(0))];
            if (!reader.IsDBNull(1))
            {
                var page = new PdfPageKey(document.Id, reader.GetInt32(1));
                if (!physicalPages.Contains(page)) throw new InvalidDataException("Indexed page has no manifest mapping.");
                indexedPageKeys.Add(page);
            }
            var method = ((ExtractionMethod)reader.GetInt32(2)).ToString();
            extractedMethods[method] = extractedMethods.GetValueOrDefault(method) + 1;
        }
    }
    object? snapshotProvenance = null;
    if (Option("--index-snapshot-out") is { } requestedSnapshot)
    {
        var snapshot = Path.GetFullPath(requestedSnapshot);
        var temporary = snapshot + ".partial";
        if (File.Exists(snapshot) || File.Exists(temporary))
            throw new InvalidOperationException("Snapshot destination already exists; choose a new explicit path.");
        Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
        using (var input = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary }.ToString()))
        {
            await input.OpenAsync(token);
            await destination.OpenAsync(token);
            input.BackupDatabase(destination);
            using var integrity = destination.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals((string?)integrity.ExecuteScalar(), "ok", StringComparison.Ordinal))
                throw new InvalidDataException("Owned SQLite snapshot failed integrity_check.");
        }
        File.Move(temporary, snapshot);
        var preparationAudit = snapshot + ".prepared-inputs.jsonl";
        var tokenLengths = new List<int>();
        var canonicalHashes = new List<string>();
        var preparationHashes = new List<string>();
        var sourceRows = new List<object>();
        long generation;
        using (var input = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            await input.OpenAsync(token);
            using (var command = input.CreateCommand())
            {
                command.CommandText = "SELECT search_generation FROM projects WHERE id=$project;";
                command.Parameters.AddWithValue("$project", project.ToString());
                generation = (long)command.ExecuteScalar()!;
            }
            using var auditWriter = new StreamWriter(preparationAudit, false, new UTF8Encoding(false));
            using var query = input.CreateCommand();
            query.CommandText = "SELECT d.path,d.sha256,d.active_revision_id,p.id,p.content_id,p.ordinal,p.page,p.structure_path,p.display_text,p.search_text,p.semantic_eligible FROM passages p JOIN documents d ON d.active_revision_id=p.revision_id JOIN content_nodes c ON c.id=p.content_id WHERE c.parent_id IS NULL ORDER BY d.file_name,p.ordinal;";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var document = sourceDocuments[Path.GetFullPath(reader.GetString(0))];
                var display = reader.GetString(8);
                var prepared = reader.GetString(9);
                var eligible = reader.GetBoolean(10);
                var displayHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(display)));
                var preparedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prepared)));
                var completeTokens = eligible ? embeddings.CountTokens(prepared) : 0;
                if (eligible) tokenLengths.Add(completeTokens);
                canonicalHashes.Add(displayHash);
                preparationHashes.Add(preparedHash);
                var row = new { document_id = document.Id, source_sha256 = reader.GetString(1), active_revision_id = reader.GetString(2),
                    passage_id = reader.GetString(3), content_id = reader.GetString(4), ordinal = reader.GetInt32(5),
                    page = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6), structure_path = reader.IsDBNull(7) ? null : reader.GetString(7),
                    display_sha256 = displayHash, prepared_sha256 = preparedHash, display_utf16_length = display.Length,
                    prepared_utf16_length = prepared.Length, semantic_eligible = eligible, complete_tokens = completeTokens };
                await auditWriter.WriteLineAsync(JsonSerializer.Serialize(row, BrokerJson.Options));
            }
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(snapshot, token)));
        snapshotProvenance = new { version = 1, owned_public_benchmark_index = true, snapshot_db = snapshot, snapshot_sha256 = hash,
            document_identity_protocol = stableDocumentIds ? VidoreBenchmarkIdentity.Protocol : "generated-v7",
            document_identity_map = documentIdentityMap,
            manifest_sha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), sqlite_integrity_check = "ok",
            project_id = project, search_generation = generation, embedding_policy = embeddings.Policy,
            indexed_root_passages = canonicalHashes.Count, embedded_passages = embeddedPassageCount,
            prepared_input_audit = preparationAudit,
            prepared_input_audit_sha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(preparationAudit, token))),
            ordered_display_hashes_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", canonicalHashes)))),
            ordered_prepared_hashes_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", preparationHashes)))),
            complete_tokens_mean = tokenLengths.Count == 0 ? (double?)null : tokenLengths.Average(),
            complete_tokens_max = tokenLengths.Count == 0 ? (int?)null : tokenLengths.Max(),
            complete_tokens_over_budget = tokenLengths.Count(length => length > GraniteEmbeddingInputEncoding.PassageMaximumTokens),
            source_documents = sourceDocuments.Select(pair => new { source_path = pair.Key, original_document_id = pair.Value.Id, source_sha256 = pair.Value.Sha256 }),
            source_paths_materialization_note = "Temporary copied source paths may be removed after completion; original PDFs remain hash-verified in the pinned external corpus." };
        await File.WriteAllTextAsync(snapshot + ".provenance.json", JsonSerializer.Serialize(snapshotProvenance, BrokerJson.Options) + "\n", token);
        Console.Error.WriteLine($"Frozen index snapshot verified: {snapshot}");
    }
    var vectorCache = new VectorIndexCache(128L * 1024 * 1024);
    var services = previewPolicies.ToDictionary(policy => policy, policy => new HybridSearchService(store, embeddings,
        new FlatVectorIndexFactory(), vectorCache, cpu, diversifyPreviews: policy == "diverse"));
    var rows = new List<VidoreQueryResult>();
    var searchOptions = new SearchResultOptions(GroupLimit: 50, PreviewsPerGroup: 10, MaxGroupsPerDocument: 50,
        SemanticSimilarityThreshold: mode == SearchMode.Keyword ? 0.25 : -1, StrictSemanticThreshold: false);
    foreach (var query in manifest.Queries)
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
        if (progressPath is not null)
            await File.AppendAllTextAsync(progressPath, JsonSerializer.Serialize(row, BrokerJson.Options) + "\n", new UTF8Encoding(false), token);
        Console.Error.WriteLine($"Query {query.Id} ({query.Language}, {previewPolicy}): {row.ExposedPages} unique exposed pages, {row.ValidCitations}/{row.ExposedPreviews} literal citations.");
    }
    var primaryRows = rows.Where(row => row.PreviewPolicy == previewPolicies[0]).ToArray();
    var report = new
    {
        benchmark = manifest.Dataset[(manifest.Dataset.IndexOf('/') + 1)..] + "_pdf_consumer_output",
        schema_version = 1, measured_utc = DateTimeOffset.UtcNow,
        manifest.Dataset, manifest.Revision, manifest.Selection,
        manifest.UnusableBoundingBoxCount, manifest.AnnotationWarnings,
        manifest_sha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
        runtime = Environment.Version.ToString(), platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        cpu_architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        selected_model = mode == SearchMode.Keyword ? null : model.ToString(), embedding_policy = embeddings.Policy,
        mode, ocr_enabled = useOcr, include_evidence = includeEvidence, primary_preview_policy = previewPolicies[0], compared_preview_policies = previewPolicies.Concat(compareNaturalHybrid ? new[] { "natural_hybrid_msm0" } : Array.Empty<string>()).ToArray(),
        comparison_manifest_sha256 = comparisonManifestHash, comparison_query_count = comparisonQueryIds?.Count,
        natural_hybrid_note = compareNaturalHybrid ? "Development-only explicit optional lexical boost with MinimumShouldMatch=0; structured API defaults unchanged. This is an alternative retrieval strategy, not the original optional-token hybrid baseline." : null, document_count = manifest.Documents.Length, corpus_pages = manifest.Pages.Length,
        index_snapshot_provenance = snapshotProvenance,
        document_identity_protocol = stableDocumentIds ? VidoreBenchmarkIdentity.Protocol : "generated-v7",
        document_identity_map = documentIdentityMap,
        query_count = manifest.Queries.Length, query_counts_by_language = manifest.Queries.GroupBy(query => query.Language).ToDictionary(group => group.Key, group => group.Count()),
        candidate_limit = 10000, result_options = searchOptions, attachment_scope = "root_only", measured_iterations = qualityOnly ? 0 : iterations,
        quality_only = qualityOnly, latency_measurement = qualityOnly ? "single unwarmed observation; not a latency benchmark" : "excluded warmup plus measured repeated searches",
        build_identity = new { extraction = typeof(DocumentExtractionRegistry).Assembly.ManifestModule.ModuleVersionId,
            search = typeof(HybridSearchService).Assembly.ManifestModule.ModuleVersionId,
            indexing = typeof(IndexingCoordinator).Assembly.ManifestModule.ModuleVersionId,
            infrastructure = typeof(GraniteEmbeddingGenerator).Assembly.ManifestModule.ModuleVersionId },
        indexing_ms = indexingClock.Elapsed.TotalMilliseconds, indexed_root_passages = extractedMethods.Values.Sum(), embedded_passages = embeddedPassageCount,
        indexed_passages_inventory_total = inventory.Documents.Sum(document => document.ExtractedPassageCount), indexed_pages = indexedPageKeys.Count,
        indexed_page_coverage = (double)indexedPageKeys.Count / manifest.Pages.Length, extraction_methods = extractedMethods,
        documents = inventoryRows, extraction_errors = errors.Select(error => new { error.Code, error.Message, error.Retryable, error.Attempt }),
        summary = ks.Select(k => new
        {
            k, recall = primaryRows.Average(row => row.Metrics.Single(metric => metric.K == k).Recall),
            ndcg = primaryRows.Average(row => row.Metrics.Single(metric => metric.K == k).Ndcg),
            mrr = primaryRows.Average(row => row.Metrics.Single(metric => metric.K == k).ReciprocalRank),
            evidence_area_coverage = NullableMean(primaryRows.Select(row => row.Metrics.Single(metric => metric.K == k).EvidenceAreaCoverage)),
            evidence_region_recall = NullableMean(primaryRows.Select(row => row.Metrics.Single(metric => metric.K == k).EvidenceRegionRecall))
        }),
        summary_by_language = primaryRows.GroupBy(row => row.Language).ToDictionary(group => group.Key, group => ks.Select(k => new
        {
            k, query_count = group.Count(), recall = group.Average(row => row.Metrics.Single(metric => metric.K == k).Recall),
            ndcg = group.Average(row => row.Metrics.Single(metric => metric.K == k).Ndcg),
            mrr = group.Average(row => row.Metrics.Single(metric => metric.K == k).ReciprocalRank),
            evidence_area_coverage = NullableMean(group.Select(row => row.Metrics.Single(metric => metric.K == k).EvidenceAreaCoverage)),
            evidence_region_recall = NullableMean(group.Select(row => row.Metrics.Single(metric => metric.K == k).EvidenceRegionRecall))
        }).ToArray()),
        citation_validity = primaryRows.Sum(row => row.ExposedPreviews) == 0 ? (double?)null : (double)primaryRows.Sum(row => row.ValidCitations) / primaryRows.Sum(row => row.ExposedPreviews),
        warm_latency_median_ms = qualityOnly ? (double?)null : Median(primaryRows.Select(row => row.MedianSearchMs)),
        warm_latency_p95_query_median_ms = qualityOnly ? (double?)null : Percentile(primaryRows.Select(row => row.MedianSearchMs), 0.95),
        summary_by_preview_policy = rows.GroupBy(row => row.PreviewPolicy).ToDictionary(group => group.Key, group => ks.Select(k => new
        {
            k, query_count = group.Count(), recall = group.Average(row => row.Metrics.Single(metric => metric.K == k).Recall),
            ndcg = group.Average(row => row.Metrics.Single(metric => metric.K == k).Ndcg),
            mrr = group.Average(row => row.Metrics.Single(metric => metric.K == k).ReciprocalRank),
            evidence_area_coverage = NullableMean(group.Select(row => row.Metrics.Single(metric => metric.K == k).EvidenceAreaCoverage)),
            evidence_region_recall = NullableMean(group.Select(row => row.Metrics.Single(metric => metric.K == k).EvidenceRegionRecall)),
            literal_citation_validity = group.Sum(row => row.ExposedPreviews) == 0 ? (double?)null : (double)group.Sum(row => row.ValidCitations) / group.Sum(row => row.ExposedPreviews)
        }).ToArray()),
        note = "Page ranking is a consumer-output adaptation: globally sort only Results.Previews by production fused score and rank tie-breaks, then deduplicate original PDF/physical page before cutoff. No raw candidates, hidden evidence expansion, benchmark markdown or answer labels enter indexing/querying. Maximum production budgets still cap each content group at 10 previews and 50 groups. Recall denominator retains every selected positive page; nDCG uses linear qrel-grade gains, matching trec_eval. Geometric coverage unions valid literal citations' inherited source-block regions on top-k pages against all gold rectangles, including unretrieved pages; it cannot establish answer completeness or excerpt-level visual precision. Region recall threshold is 50% of each gold rectangle. Native-only extraction may omit image/chart information; errors and indexed-page coverage are reported. Search timings include query embedding and retrieval; exclude indexing, warm-up, full-response and citation reads. Summary averages query metrics; p95 is across per-query medians, not pooled request latencies.",
        queries = rows
    };
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(BrokerJson.Options) { WriteIndented = true });
    Console.WriteLine(json);
    if (Option("--output") is { } output)
    {
        var absoluteOutput = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(absoluteOutput)!);
        await File.WriteAllTextAsync(absoluteOutput, json + "\n", new UTF8Encoding(false), token);
    }
}
finally
{
    await writer.StopAsync(CancellationToken.None);
    SqliteConnection.ClearAllPools();
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
static double Percentile(IEnumerable<double> values, double fraction)
{
    var ordered = values.Order().ToArray();
    return ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * fraction) - 1, 0, ordered.Length - 1)];
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

sealed class VidorePaths : IAppPaths, IDisposable
{
    private readonly string _parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ContextMole-vidore-benchmark"));
    private readonly string _root;
    public VidorePaths(string? assets)
    {
        _root = Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        var data = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable);
        AssetsDirectory = assets is not null ? Path.GetFullPath(assets) : Path.Combine(string.IsNullOrWhiteSpace(data)
            ? ContextMoleLocalData.GetDefaultDataDirectory() : Path.GetFullPath(data), "assets");
        Directory.CreateDirectory(DataDirectory);
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
        var absoluteRoot = Path.GetFullPath(_root);
        if (!ProjectValidation.IsSameOrChild(absoluteRoot, _parent) || absoluteRoot == _parent)
            throw new InvalidOperationException("Benchmark cleanup escaped its owned temporary directory.");
        if (Directory.Exists(absoluteRoot)) Directory.Delete(absoluteRoot, recursive: true);
    }
}
sealed class VidoreCpuSettings : ICpuUsageSettings
{
    public CpuUsageProfile Profile => CpuUsageProfile.Normal;
    public int LogicalProcessorCount => Environment.ProcessorCount;
    public int ThreadLimit => Math.Min(4, Environment.ProcessorCount);
    public int MaximumThreadLimit => ThreadLimit;
    public event EventHandler? Changed { add { } remove { } }
    public void SetProfile(CpuUsageProfile profile) => throw new NotSupportedException();
}
sealed class VidoreModelSettings(EmbeddingModelChoice model) : IEmbeddingModelSettings
{
    public EmbeddingModelChoice Model => model;
    public event EventHandler? Changed { add { } remove { } }
    public void SetModel(EmbeddingModelChoice value) => throw new NotSupportedException();
    public bool RefreshFromDisk() => false;
}
sealed class VidoreNoEmbeddings : IEmbeddingGenerator
{
    public bool IsAvailable => false;
    public string UnavailableReason => "Semantic embeddings disabled for keyword benchmark.";
    public EmbeddingPolicy? Policy => null;
    public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    public Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
sealed class VidoreNoOcr : IOcrEngine
{
    public bool IsAvailable => false;
    public string UnavailableReason => "OCR disabled for native-only benchmark.";
    public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException(UnavailableReason);
    public Task<OcrResult> RecognizeAsync(Func<CancellationToken, Task<OcrRequest>> prepareRequest,
        CancellationToken cancellationToken) => throw new InvalidOperationException(UnavailableReason);
}
