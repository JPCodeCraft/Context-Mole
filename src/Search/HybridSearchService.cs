using MCPIndexSearch.Core;

namespace MCPIndexSearch.Search;

public sealed class HybridSearchService(
    ISearchStore store,
    IEmbeddingGenerator embeddingGenerator,
    IVectorIndexFactory vectorFactory,
    VectorIndexCache cache,
    IGlobalCpuBudget cpuBudget)
{
    private const double RrfK = 60;
    private readonly ISearchStore _store = store;
    private readonly IEmbeddingGenerator _embeddingGenerator = embeddingGenerator;
    private readonly IVectorIndexFactory _vectorFactory = vectorFactory;
    private readonly VectorIndexCache _cache = cache;
    private readonly IGlobalCpuBudget _cpuBudget = cpuBudget;
    private readonly SemaphoreSlim _embeddingReloadGate = new(1, 1);
    private readonly SemaphoreSlim _vectorLoadGate = new(1, 1);

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Limit is < 1 or > 50)
            throw new McpIndexException("invalid_request", "limit must be between 1 and 50.");
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new McpIndexException("invalid_request", "query must not be empty.");

        var candidateK = Math.Clamp(Math.Max(100, request.Limit * 5), 100, 500);
        var fts = TextNormalization.QuoteFtsTerms(request.Query);
        if (fts.Length == 0)
            throw new McpIndexException("invalid_request", "query must contain at least one letter, number, or underscore.");

        using var worker = await _cpuBudget.AcquireWorkerAsync(cancellationToken).ConfigureAwait(false);
        using var activeWorker = worker.Activate();
        KeywordSearchPage keywordPage = new(0, []);
        VectorSnapshotMetadata vectorMetadata = new(0, null, 0);
        var warnings = new List<string>();
        Exception? keywordFailure = null;
        try
        {
            keywordPage = await _store.KeywordSearchAsync(request.ProjectId, fts, candidateK, request.Filters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            keywordFailure = exception;
            warnings.Add($"Keyword search is unavailable: {exception.Message}");
        }

        try
        {
            vectorMetadata = await _store.LoadVectorSnapshotMetadataAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add($"Semantic index is unavailable: {exception.Message}");
        }

        if (keywordFailure is not null && vectorMetadata.SearchGeneration == 0)
            throw keywordFailure;

        for (var attempt = 0; keywordPage.SearchGeneration != 0 && vectorMetadata.SearchGeneration != 0 &&
             keywordPage.SearchGeneration != vectorMetadata.SearchGeneration && attempt < 2; attempt++)
        {
            keywordPage = await _store.KeywordSearchAsync(request.ProjectId, fts, candidateK, request.Filters, cancellationToken).ConfigureAwait(false);
            vectorMetadata = await _store.LoadVectorSnapshotMetadataAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        }
        if (keywordPage.SearchGeneration != 0 && vectorMetadata.SearchGeneration != 0 && keywordPage.SearchGeneration != vectorMetadata.SearchGeneration)
            throw new McpIndexException("index_changed", "The project index changed during search. Retry the request.", true);

        var keyword = keywordPage.Candidates.Select((candidate, index) => candidate with { KeywordRank = index + 1 }).ToArray();
        var semanticMatches = Array.Empty<VectorMatch>();
        if (vectorMetadata.Warning is not null) warnings.Add(vectorMetadata.Warning);
        if (vectorMetadata.EntryCount > 0)
            await EnsureEmbeddingAvailableAsync(cancellationToken).ConfigureAwait(false);
        var semanticEnabled = _embeddingGenerator.IsAvailable && vectorMetadata.Policy is not null &&
            vectorMetadata.EntryCount > 0 && vectorMetadata.IsComplete;
        if (!_embeddingGenerator.IsAvailable)
            warnings.Add(_embeddingGenerator.UnavailableReason ?? "Granite model assets are unavailable; using keyword search.");
        else if (vectorMetadata.EntryCount == 0)
            warnings.Add("No semantic embeddings are currently available for this project; using keyword search.");
        else if (vectorMetadata.Policy is null && vectorMetadata.EntryCount > 0)
            warnings.Add("The project contains incompatible embedding policy generations; using keyword search until re-embedding completes.");
        else if (vectorMetadata.Policy is not null && !string.Equals(vectorMetadata.Policy.Key, _embeddingGenerator.Policy?.Key, StringComparison.Ordinal))
        {
            semanticEnabled = false;
            warnings.Add("The active embedding policy differs from the local model; using keyword search until re-embedding completes.");
        }

        if (semanticEnabled)
        {
            try
            {
                var queryEmbedding = await _embeddingGenerator.EmbedQueryAsync(request.Query, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(queryEmbedding.Policy.Key, vectorMetadata.Policy!.Key, StringComparison.Ordinal))
                {
                    warnings.Add("The embedding model changed during this search; using keyword results while re-embedding completes.");
                }
                else if (vectorMetadata.RequiresStreaming)
                {
                    semanticMatches = (await FlatVectorIndex.SearchStreamingAsync(
                        _store.StreamVectorEntriesAsync(request.ProjectId, vectorMetadata.SearchGeneration, request.Filters, cancellationToken),
                        queryEmbedding.Vector, candidateK, cancellationToken).ConfigureAwait(false)).ToArray();
                }
                else
                {
                    var vectorIndex = await GetVectorIndexAsync(request.ProjectId, vectorMetadata, cancellationToken)
                        .ConfigureAwait(false);
                    semanticMatches = vectorIndex.Search(queryEmbedding.Vector, candidateK, request.Filters).ToArray();
                }
            }
            catch (McpIndexException exception) when (exception.Code == "index_changed")
            {
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add($"Semantic search is unavailable: {exception.Message}");
            }
        }

        SearchCandidate[] semanticCandidates = [];
        if (semanticMatches.Length > 0)
        {
            var matchesByPassage = semanticMatches.ToDictionary(match => match.PassageId);
            semanticCandidates = (await _store.LoadCandidatesAsync(request.ProjectId, matchesByPassage.Keys.ToArray(),
                    vectorMetadata.SearchGeneration, cancellationToken)
                .ConfigureAwait(false)).Select(candidate =>
                {
                    var match = matchesByPassage[candidate.PassageId];
                    return candidate with { SemanticRank = match.Rank, SemanticScore = match.Score };
                }).ToArray();
        }

        var fused = new Dictionary<Guid, Fused>();
        foreach (var candidate in keyword)
            Add(candidate, candidate.KeywordRank!.Value, true);
        foreach (var candidate in semanticCandidates)
            Add(candidate, candidate.SemanticRank!.Value, false);

        var results = fused.Values.OrderByDescending(item => item.Score)
            .ThenBy(item => Math.Min(item.Candidate.KeywordRank ?? int.MaxValue, item.Candidate.SemanticRank ?? int.MaxValue))
            .ThenBy(item => item.Candidate.PassageId)
            .Take(request.Limit)
            .Select(item => ToResult(item.Candidate, item.Score)).ToArray();

        var actualMode = semanticMatches.Length > 0 && keyword.Length > 0 ? "hybrid"
            : semanticMatches.Length > 0 ? "semantic" : "keyword";
        return new SearchResponse(actualMode, warnings.Distinct(StringComparer.Ordinal).ToArray(),
            keywordPage.SearchGeneration != 0 ? keywordPage.SearchGeneration : vectorMetadata.SearchGeneration, results);

        void Add(SearchCandidate candidate, int rank, bool keywordBranch)
        {
            if (!fused.TryGetValue(candidate.PassageId, out var current))
                current = new Fused(candidate, 0);
            var merged = current.Candidate with
            {
                KeywordRank = keywordBranch ? candidate.KeywordRank : current.Candidate.KeywordRank,
                KeywordScore = keywordBranch ? candidate.KeywordScore : current.Candidate.KeywordScore,
                SemanticRank = keywordBranch ? current.Candidate.SemanticRank : candidate.SemanticRank,
                SemanticScore = keywordBranch ? current.Candidate.SemanticScore : candidate.SemanticScore
            };
            fused[candidate.PassageId] = new Fused(merged, current.Score + 1d / (RrfK + rank));
        }
    }

    private static SearchResultItem ToResult(SearchCandidate candidate, double score)
    {
        var truncated = candidate.DisplayText.Length > 800;
        var excerpt = truncated ? candidate.DisplayText[..800] : candidate.DisplayText;
        return new SearchResultItem(candidate.PassageId, candidate.DocumentId, candidate.ContentId, excerpt, truncated,
            candidate.SourcePath, candidate.FileName, candidate.FileType, candidate.ModifiedUtc, candidate.Location,
            candidate.AttachmentChain, candidate.ExtractionMethod, candidate.OcrConfidence, score, candidate.KeywordScore,
            candidate.SemanticScore, candidate.KeywordRank, candidate.SemanticRank);
    }

    private async Task EnsureEmbeddingAvailableAsync(CancellationToken cancellationToken)
    {
        await _embeddingReloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _embeddingGenerator.ReloadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _embeddingReloadGate.Release();
        }
    }

    private async Task<IVectorIndex> GetVectorIndexAsync(
        Guid projectId,
        VectorSnapshotMetadata metadata,
        CancellationToken cancellationToken)
    {
        var policyKey = metadata.Policy!.Key;
        if (_cache.TryGet(projectId, metadata.SearchGeneration, policyKey, out var cached))
            return cached;

        await _vectorLoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGet(projectId, metadata.SearchGeneration, policyKey, out cached))
                return cached;

            var snapshot = await _store.LoadVectorSnapshotAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (snapshot.SearchGeneration != metadata.SearchGeneration ||
                !string.Equals(snapshot.Policy?.Key, policyKey, StringComparison.Ordinal))
                throw new McpIndexException("index_changed", "The project index changed while loading semantic vectors.", true);
            if (snapshot.Warning is not null)
                throw new McpIndexException("semantic_index_invalid", snapshot.Warning);
            return _cache.GetOrCreate(projectId, snapshot, _vectorFactory);
        }
        finally
        {
            _vectorLoadGate.Release();
        }
    }

    private sealed record Fused(SearchCandidate Candidate, double Score);
}

public sealed class VectorIndexCache
{
    private const long Budget = 512L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid ProjectId, long Generation, string Policy), Entry> _entries = [];
    private long _bytes;

    public bool TryGet(Guid projectId, long generation, string policy, out IVectorIndex index)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue((projectId, generation, policy), out var existing))
            {
                existing.LastAccessUtc = DateTime.UtcNow;
                index = existing.Index;
                return true;
            }
        }

        index = null!;
        return false;
    }

    public IVectorIndex GetOrCreate(Guid projectId, VectorSnapshot snapshot, IVectorIndexFactory factory)
    {
        var policy = snapshot.Policy?.Key ?? string.Empty;
        var key = (projectId, snapshot.SearchGeneration, policy);
        var bytes = EstimateBytes(snapshot);
        if (bytes > Budget)
            return factory.Create(snapshot);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.LastAccessUtc = DateTime.UtcNow;
                return existing.Index;
            }

            foreach (var staleKey in _entries.Keys
                         .Where(item => item.ProjectId == projectId && item != key)
                         .ToArray())
            {
                _bytes -= _entries[staleKey].Bytes;
                _entries.Remove(staleKey);
            }

            while (_bytes + bytes > Budget && _entries.Count > 0)
            {
                var oldest = _entries.MinBy(pair => pair.Value.LastAccessUtc);
                _entries.Remove(oldest.Key);
                _bytes -= oldest.Value.Bytes;
            }
            var index = factory.Create(snapshot);
            _entries[key] = new Entry(index, bytes, DateTime.UtcNow);
            _bytes += bytes;
            return index;
        }
    }

    private static long EstimateBytes(VectorSnapshot snapshot)
    {
        long bytes = 0;
        foreach (var entry in snapshot.Entries)
        {
            var entryBytes = 512L + entry.Vector.LongLength * sizeof(float) +
                             2L * (entry.SourcePath.Length + entry.Extension.Length);
            if (entryBytes > Budget - bytes) return Budget + 1;
            bytes += entryBytes;
        }

        return bytes;
    }

    private sealed class Entry(IVectorIndex index, long bytes, DateTime lastAccessUtc)
    {
        public IVectorIndex Index { get; } = index;
        public long Bytes { get; } = bytes;
        public DateTime LastAccessUtc { get; set; } = lastAccessUtc;
    }
}
