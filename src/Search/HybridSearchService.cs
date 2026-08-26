using MCPIndexSearch.Core;

namespace MCPIndexSearch.Search;

public sealed class HybridSearchService(
    ISearchStore store,
    IEmbeddingGenerator embeddingGenerator,
    IVectorIndexFactory vectorFactory,
    VectorIndexCache cache)
{
    private const double RrfK = 60;
    private readonly ISearchStore _store = store;
    private readonly IEmbeddingGenerator _embeddingGenerator = embeddingGenerator;
    private readonly IVectorIndexFactory _vectorFactory = vectorFactory;
    private readonly VectorIndexCache _cache = cache;

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Limit is < 1 or > 50)
            throw new McpIndexException("invalid_request", "limit must be between 1 and 50.");
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new McpIndexException("invalid_request", "query must not be empty.");

        var candidateK = Math.Clamp(Math.Max(100, request.Limit * 5), 100, 500);
        var fts = TextNormalization.QuoteFtsTerms(request.Query);
        KeywordSearchPage keywordPage = new(0, []);
        VectorSnapshot vectorSnapshot = new(0, null, []);
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
            vectorSnapshot = await _store.LoadVectorSnapshotAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add($"Semantic index is unavailable: {exception.Message}");
        }

        if (keywordFailure is not null && vectorSnapshot.SearchGeneration == 0)
            throw keywordFailure;

        for (var attempt = 0; keywordPage.SearchGeneration != 0 && vectorSnapshot.SearchGeneration != 0 &&
             keywordPage.SearchGeneration != vectorSnapshot.SearchGeneration && attempt < 2; attempt++)
        {
            keywordPage = await _store.KeywordSearchAsync(request.ProjectId, fts, candidateK, request.Filters, cancellationToken).ConfigureAwait(false);
            vectorSnapshot = await _store.LoadVectorSnapshotAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        }
        if (keywordPage.SearchGeneration != 0 && vectorSnapshot.SearchGeneration != 0 && keywordPage.SearchGeneration != vectorSnapshot.SearchGeneration)
            throw new McpIndexException("index_changed", "The project index changed during search. Retry the request.", true);

        var keyword = keywordPage.Candidates.Select((candidate, index) => candidate with { KeywordRank = index + 1 }).ToArray();
        var semanticMatches = Array.Empty<VectorMatch>();
        if (vectorSnapshot.Warning is not null) warnings.Add(vectorSnapshot.Warning);
        if (!_embeddingGenerator.IsAvailable)
        {
            await _embeddingGenerator.ReloadAsync(cancellationToken).ConfigureAwait(false);
        }
        var semanticEnabled = _embeddingGenerator.IsAvailable && vectorSnapshot.Policy is not null &&
            (vectorSnapshot.Entries.Count > 0 || vectorSnapshot.RequiresStreaming);
        if (!_embeddingGenerator.IsAvailable)
            warnings.Add(_embeddingGenerator.UnavailableReason ?? "Granite model assets are unavailable; using keyword search.");
        else if (vectorSnapshot.Policy is null && vectorSnapshot.Entries.Count > 0)
            warnings.Add("The project contains incompatible embedding policy generations; using keyword search until re-embedding completes.");
        else if (vectorSnapshot.Policy is not null && !string.Equals(vectorSnapshot.Policy.Key, _embeddingGenerator.Policy?.Key, StringComparison.Ordinal))
        {
            semanticEnabled = false;
            warnings.Add("The active embedding policy differs from the local model; using keyword search until re-embedding completes.");
        }

        if (semanticEnabled)
        {
            try
            {
                var queryVector = await _embeddingGenerator.EmbedQueryAsync(request.Query, cancellationToken).ConfigureAwait(false);
                semanticMatches = vectorSnapshot.RequiresStreaming
                    ? (await FlatVectorIndex.SearchStreamingAsync(
                        _store.StreamVectorEntriesAsync(request.ProjectId, vectorSnapshot.SearchGeneration, request.Filters, cancellationToken),
                        queryVector, candidateK, cancellationToken).ConfigureAwait(false)).ToArray()
                    : _cache.GetOrCreate(request.ProjectId, vectorSnapshot, _vectorFactory)
                        .Search(queryVector, candidateK, request.Filters).ToArray();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add($"Semantic search is unavailable: {exception.Message}");
            }
        }

        var semanticCandidates = semanticMatches.Length == 0
            ? []
            : (await _store.LoadCandidatesAsync(request.ProjectId, semanticMatches.Select(match => match.PassageId).ToArray(),
                    vectorSnapshot.SearchGeneration, cancellationToken)
                .ConfigureAwait(false)).Select(candidate =>
                {
                    var match = semanticMatches.First(item => item.PassageId == candidate.PassageId);
                    return candidate with { SemanticRank = match.Rank, SemanticScore = match.Score };
                }).ToArray();

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
            keywordPage.SearchGeneration != 0 ? keywordPage.SearchGeneration : vectorSnapshot.SearchGeneration, results);

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

    private sealed record Fused(SearchCandidate Candidate, double Score);
}

public sealed class VectorIndexCache
{
    private const long Budget = 512L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid ProjectId, long Generation, string Policy), Entry> _entries = [];
    private long _bytes;

    public IVectorIndex GetOrCreate(Guid projectId, VectorSnapshot snapshot, IVectorIndexFactory factory)
    {
        var policy = snapshot.Policy?.Key ?? string.Empty;
        var key = (projectId, snapshot.SearchGeneration, policy);
        var bytes = (long)snapshot.Entries.Count * (1536 + 128);
        if (bytes > Budget)
            return factory.Create(snapshot);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.LastAccessUtc = DateTime.UtcNow;
                return existing.Index;
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

    private sealed class Entry(IVectorIndex index, long bytes, DateTime lastAccessUtc)
    {
        public IVectorIndex Index { get; } = index;
        public long Bytes { get; } = bytes;
        public DateTime LastAccessUtc { get; set; } = lastAccessUtc;
    }
}
