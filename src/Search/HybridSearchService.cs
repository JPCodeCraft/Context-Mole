using ContextMole.Core;

namespace ContextMole.Search;

public sealed class HybridSearchService(
    ISearchStore store,
    IEmbeddingGenerator embeddingGenerator,
    IVectorIndexFactory vectorFactory,
    VectorIndexCache cache,
    IGlobalCpuBudget cpuBudget)
{
    private const double RrfK = 60;
    private const double OptionalShouldBranchWeight = 0.6;
    private const int CandidateHydrationBatchSize = 500;
    private readonly ISearchStore _store = store;
    private readonly IEmbeddingGenerator _embeddingGenerator = embeddingGenerator;
    private readonly IVectorIndexFactory _vectorFactory = vectorFactory;
    private readonly VectorIndexCache _cache = cache;
    private readonly IGlobalCpuBudget _cpuBudget = cpuBudget;
    private readonly SemaphoreSlim _embeddingReloadGate = new(1, 1);
    private readonly SemaphoreSlim _vectorLoadGate = new(1, 1);

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var clauses = request.Clauses?.ToArray() ?? [];
        var options = request.ResultOptions ?? new SearchResultOptions();
        var fieldWeights = request.FieldWeights ?? new SearchFieldWeights();
        var branchWeights = request.BranchWeights ?? new SearchBranchWeights();
        var minimumShouldMatch = ValidateRequest(request, clauses, options, fieldWeights, branchWeights);
        var keywordQuery = StructuredSearchQuery.BuildFtsQuery(clauses, minimumShouldMatch);
        var optionalKeywordQuery = StructuredSearchQuery.BuildOptionalShouldBoostQuery(clauses, minimumShouldMatch);
        var hasKeywordBranch = (request.Mode is SearchMode.Keyword or SearchMode.Hybrid) && keywordQuery.Length > 0 &&
                               branchWeights.Keyword > 0;
        var hasSemanticBranch = (request.Mode is SearchMode.Semantic or SearchMode.Hybrid) &&
                                !string.IsNullOrWhiteSpace(request.SemanticQuery) && branchWeights.Semantic > 0;
        if (!hasKeywordBranch && !hasSemanticBranch)
            throw new ContextMoleException("invalid_request",
                "The selected mode, query inputs, and branch weights leave no applicable search branch.");
        var pageSize = Math.Clamp(Math.Max(128,
            options.GroupLimit * Math.Max(options.PreviewsPerGroup, 1) * 8), 128, 1000);

        using var worker = await _cpuBudget.AcquireWorkerAsync(cancellationToken).ConfigureAwait(false);
        using var activeWorker = worker.Activate();
        VectorSnapshotMetadata vectorMetadata = new(0, null, 0);
        var warnings = new List<SearchWarning>();
        var keywordCompleted = false;
        var semanticCompleted = false;
        QueryEmbedding? queryEmbedding = null;
        IVectorIndex? vectorIndex = null;
        if (hasSemanticBranch)
        {
            try
            {
                vectorMetadata = await _store.LoadVectorSnapshotMetadataAsync(request.ProjectId, cancellationToken)
                    .ConfigureAwait(false);
                if (vectorMetadata.Warning is not null)
                    warnings.Add(new SearchWarning("semantic_index_incomplete", vectorMetadata.Warning));
                if (vectorMetadata.EntryCount > 0)
                    await EnsureEmbeddingAvailableAsync(cancellationToken).ConfigureAwait(false);

                var semanticEnabled = _embeddingGenerator.IsAvailable && vectorMetadata.Policy is not null &&
                                      vectorMetadata.EntryCount > 0 && vectorMetadata.IsComplete;
                var unavailableReason = !_embeddingGenerator.IsAvailable
                    ? _embeddingGenerator.UnavailableReason ?? "Granite model assets are unavailable."
                    : vectorMetadata.EntryCount == 0
                        ? "No semantic embeddings are currently available for this project."
                        : vectorMetadata.Policy is null
                            ? "The project contains incompatible embedding policy generations."
                            : !vectorMetadata.IsComplete
                                ? "Semantic embeddings are incomplete while background indexing continues."
                                : !string.Equals(vectorMetadata.Policy.Key, _embeddingGenerator.Policy?.Key,
                                    StringComparison.Ordinal)
                                    ? "The active embedding policy differs from the local model while re-embedding completes."
                                    : null;
                if (!semanticEnabled || unavailableReason is not null)
                    warnings.Add(new SearchWarning("semantic_unavailable",
                        unavailableReason ?? "Semantic search is unavailable."));
                else
                {
                    queryEmbedding = await _embeddingGenerator.EmbedQueryAsync(request.SemanticQuery!, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.Equals(queryEmbedding.Policy.Key, vectorMetadata.Policy!.Key, StringComparison.Ordinal))
                    {
                        warnings.Add(new SearchWarning("semantic_model_changed",
                            "The embedding model changed during this search; semantic results were not used."));
                        warnings.Add(new SearchWarning("semantic_unavailable",
                            "Semantic search became unavailable because the embedding model changed during this search."));
                    }
                    else
                    {
                        if (!vectorMetadata.RequiresStreaming)
                            vectorIndex = await GetVectorIndexAsync(request.ProjectId, vectorMetadata,
                                cancellationToken).ConfigureAwait(false);
                        semanticCompleted = true;
                    }
                }
            }
            catch (ContextMoleException exception) when (!IsSemanticAvailabilityFailure(exception))
            {
                throw;
            }
            catch (ContextMoleException exception)
            {
                warnings.Add(new SearchWarning("semantic_unavailable",
                    $"Semantic search is unavailable: {exception.Message}"));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add(new SearchWarning("semantic_unavailable",
                    $"Semantic search is unavailable: {exception.Message}"));
            }
        }

        if (hasSemanticBranch && !semanticCompleted && hasKeywordBranch)
            warnings.Add(new SearchWarning("fallback_keyword",
                "The requested hybrid search returned keyword results only."));

        var keywordPool = new List<SearchCandidate>();
        var keywordIds = new HashSet<Guid>();
        var optionalKeywordPool = new List<SearchCandidate>();
        var optionalKeywordIds = new HashSet<Guid>();
        var keywordOffset = 0;
        var optionalKeywordOffset = 0;
        var keywordGeneration = 0L;
        var mainKeywordExhausted = !hasKeywordBranch;
        var optionalKeywordExhausted = !hasKeywordBranch || optionalKeywordQuery.Length == 0;
        var semanticExhausted = !semanticCompleted;
        var semanticTarget = pageSize;
        var semanticMatches = Array.Empty<VectorMatch>();
        var branchCapped = false;
        Selection selection = Selection.Empty;

        while (true)
        {
            var progressed = false;
            if (!optionalKeywordExhausted)
            {
                try
                {
                    var page = await _store.KeywordSearchAsync(request.ProjectId, optionalKeywordQuery, pageSize,
                        optionalKeywordOffset, request.Filters, fieldWeights, cancellationToken).ConfigureAwait(false);
                    if (keywordGeneration == 0) keywordGeneration = page.SearchGeneration;
                    else if (page.SearchGeneration != keywordGeneration)
                        throw new ContextMoleException("index_changed",
                            "The project index changed during keyword paging. Retry the request.", true);
                    foreach (var candidate in page.Candidates)
                        if (optionalKeywordIds.Add(candidate.PassageId)) optionalKeywordPool.Add(candidate);
                    optionalKeywordOffset += page.Candidates.Count;
                    optionalKeywordExhausted = page.Candidates.Count < pageSize;
                    progressed |= page.Candidates.Count > 0;
                }
                catch (ContextMoleException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    optionalKeywordExhausted = true;
                    warnings.Add(new SearchWarning("keyword_optional_unavailable",
                        $"Optional keyword boosting is unavailable: {exception.Message}"));
                }
            }

            if (!mainKeywordExhausted)
            {
                try
                {
                    var page = await _store.KeywordSearchAsync(request.ProjectId, keywordQuery, pageSize,
                        keywordOffset, request.Filters, fieldWeights, cancellationToken).ConfigureAwait(false);
                    keywordCompleted = true;
                    if (keywordGeneration == 0) keywordGeneration = page.SearchGeneration;
                    else if (page.SearchGeneration != keywordGeneration)
                        throw new ContextMoleException("index_changed",
                            "The project index changed during keyword paging. Retry the request.", true);
                    foreach (var candidate in page.Candidates)
                        if (keywordIds.Add(candidate.PassageId)) keywordPool.Add(candidate);
                    keywordOffset += page.Candidates.Count;
                    mainKeywordExhausted = page.Candidates.Count < pageSize;
                    progressed |= page.Candidates.Count > 0;
                }
                catch (ContextMoleException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (request.Mode == SearchMode.Keyword) throw;
                    mainKeywordExhausted = true;
                    warnings.Add(new SearchWarning("keyword_unavailable",
                        $"Keyword search is unavailable: {exception.Message}"));
                }
            }

            var keywordExhausted = mainKeywordExhausted && optionalKeywordExhausted;

            if (semanticCompleted && !semanticExhausted)
            {
                try
                {
                    var maximum = (int)Math.Min(int.MaxValue, vectorMetadata.EntryCount);
                    var target = Math.Min(semanticTarget, maximum);
                    semanticMatches = vectorMetadata.RequiresStreaming
                        ? (await FlatVectorIndex.SearchStreamingAsync(
                            _store.StreamVectorEntriesAsync(request.ProjectId, vectorMetadata.SearchGeneration,
                                request.Filters, cancellationToken), queryEmbedding!.Vector, target,
                            cancellationToken).ConfigureAwait(false)).ToArray()
                        : vectorIndex!.Search(queryEmbedding!.Vector, target, request.Filters).ToArray();
                    semanticExhausted = target >= maximum || semanticMatches.Length < target;
                    progressed |= semanticMatches.Length > 0;
                }
                catch (ContextMoleException exception) when (!IsSemanticAvailabilityFailure(exception))
                {
                    throw;
                }
                catch (ContextMoleException exception)
                {
                    semanticMatches = [];
                    semanticCompleted = false;
                    semanticExhausted = true;
                    warnings.Add(new SearchWarning("semantic_unavailable",
                        $"Semantic search is unavailable: {exception.Message}"));
                    if (hasKeywordBranch)
                        warnings.Add(new SearchWarning("fallback_keyword",
                            "The requested hybrid search returned keyword results only."));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    semanticMatches = [];
                    semanticCompleted = false;
                    semanticExhausted = true;
                    warnings.Add(new SearchWarning("semantic_unavailable",
                        $"Semantic search is unavailable: {exception.Message}"));
                    if (hasKeywordBranch)
                        warnings.Add(new SearchWarning("fallback_keyword",
                            "The requested hybrid search returned keyword results only."));
                }
            }

            if (keywordGeneration != 0 && vectorMetadata.SearchGeneration != 0 &&
                keywordGeneration != vectorMetadata.SearchGeneration)
                throw new ContextMoleException("index_changed",
                    "The project index changed during search. Retry the request.", true);

            var keyword = RerankKeywordCandidates(keywordPool, optionalKeywordPool, clauses, minimumShouldMatch);
            SearchCandidate[] semanticCandidates = [];
            if (semanticMatches.Length > 0)
            {
                try
                {
                    var matchesByPassage = semanticMatches.ToDictionary(match => match.PassageId);
                    var hydrated = new List<SearchCandidate>(matchesByPassage.Count);
                    foreach (var batch in matchesByPassage.Keys.Chunk(CandidateHydrationBatchSize))
                    {
                        hydrated.AddRange(await _store.LoadCandidatesAsync(request.ProjectId, batch,
                            vectorMetadata.SearchGeneration, cancellationToken).ConfigureAwait(false));
                    }
                    semanticCandidates = hydrated.Select(candidate =>
                        {
                            var match = matchesByPassage[candidate.PassageId];
                            return candidate with { SemanticRank = match.Rank, SemanticScore = match.Score };
                        }).Where(candidate => StructuredSearchQuery.Evaluate(candidate, clauses, minimumShouldMatch).IsMatch)
                        .Where(candidate => !options.StrictSemanticThreshold ||
                                            candidate.SemanticScore >= options.SemanticConfidenceThreshold).ToArray();
                }
                catch (ContextMoleException exception) when (!IsSemanticAvailabilityFailure(exception))
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    semanticMatches = [];
                    semanticCompleted = false;
                    semanticExhausted = true;
                    warnings.Add(new SearchWarning("semantic_unavailable",
                        $"Semantic search is unavailable: {exception.Message}"));
                    if (hasKeywordBranch)
                        warnings.Add(new SearchWarning("fallback_keyword",
                            "The requested hybrid search returned keyword results only."));
                }
            }

            selection = BuildSelection(keyword, semanticCandidates, keywordCompleted, semanticCompleted,
                branchWeights, clauses, minimumShouldMatch, options);
            if (selection.Returned.Count >= options.GroupLimit || keywordExhausted && semanticExhausted) break;

            if (semanticCompleted && !semanticExhausted)
            {
                var maximum = (int)Math.Min(int.MaxValue, vectorMetadata.EntryCount);
                var next = (int)Math.Min(maximum, Math.Max((long)semanticTarget + pageSize,
                    (long)semanticTarget * 2));
                if (next == semanticTarget)
                {
                    semanticExhausted = true;
                    branchCapped = vectorMetadata.EntryCount > int.MaxValue;
                }
                else semanticTarget = next;
            }
            if (!progressed && keywordExhausted && semanticExhausted) break;
        }

        var actualMode = keywordCompleted && semanticCompleted ? "hybrid"
            : semanticCompleted ? "semantic"
            : keywordCompleted ? "keyword"
            : "unavailable";
        var generation = keywordGeneration != 0 ? keywordGeneration : vectorMetadata.SearchGeneration;
        var candidateLimitReached = branchCapped ||
                                    selection.Returned.Count >= options.GroupLimit &&
                                    (!mainKeywordExhausted || !optionalKeywordExhausted || !semanticExhausted);
        return new SearchResponse(request.Mode, actualMode,
            warnings.DistinctBy(warning => (warning.Code, warning.Message)).ToArray(), generation,
            selection.RankedCount,
            new SearchBranchCandidateDepths(keywordOffset, optionalKeywordOffset, semanticMatches.Length),
            candidateLimitReached, selection.Returned.Count,
            selection.AllGroups.Count - selection.Returned.Count, selection.SuppressedSources, selection.Returned);
    }

    private static SearchCandidate[] RerankKeywordCandidates(IReadOnlyList<SearchCandidate> mainCandidates,
        IReadOnlyList<SearchCandidate> optionalCandidates, IReadOnlyList<SearchClause> clauses,
        int minimumShouldMatch)
    {
        var shouldIds = clauses.Where(clause => clause.Occur == SearchClauseOccur.Should)
            .Select(clause => clause.Id).ToHashSet(StringComparer.Ordinal);
        var mainRanks = mainCandidates.Select((candidate, index) => (candidate.PassageId, Rank: index + 1))
            .ToDictionary(item => item.PassageId, item => item.Rank);
        var optionalRanks = optionalCandidates.Select((candidate, index) => (candidate.PassageId, Rank: index + 1))
            .ToDictionary(item => item.PassageId, item => item.Rank);
        var candidates = mainCandidates.Concat(optionalCandidates).DistinctBy(candidate => candidate.PassageId);
        return candidates.Select(candidate =>
        {
            var evaluation = StructuredSearchQuery.Evaluate(candidate, clauses, minimumShouldMatch);
            var baseRank = mainRanks.TryGetValue(candidate.PassageId, out var mainRank)
                ? mainRank
                : mainCandidates.Count + optionalRanks[candidate.PassageId];
            if (!evaluation.IsMatch)
                return (Candidate: candidate, Score: double.MinValue, Include: false, BaseRank: baseRank);
            var optionalMatches = evaluation.MatchedClauseIds.Count(shouldIds.Contains);
            var optionalBoost = shouldIds.Count == 0 || optionalMatches == 0 ||
                                !optionalRanks.TryGetValue(candidate.PassageId, out var optionalRank)
                ? 0
                : OptionalShouldBranchWeight * optionalMatches / shouldIds.Count / (RrfK + optionalRank);
            return (Candidate: candidate with
            {
                KeywordScore = (candidate.KeywordScore ?? 0) + optionalMatches * 0.001
            }, Score: 1d / (RrfK + baseRank) + optionalBoost, Include: true, BaseRank: baseRank);
        }).Where(item => item.Include).OrderByDescending(item => item.Score).ThenBy(item => item.BaseRank)
          .ThenBy(item => item.Candidate.PassageId).Select((item, rank) => item.Candidate with { KeywordRank = rank + 1 })
          .ToArray();
    }

    private static Selection BuildSelection(IReadOnlyList<SearchCandidate> keyword,
        IReadOnlyList<SearchCandidate> semantic, bool keywordCompleted, bool semanticCompleted,
        SearchBranchWeights branchWeights, IReadOnlyList<SearchClause> clauses, int minimumShouldMatch,
        SearchResultOptions options)
    {
        var keywordWeight = keywordCompleted ? branchWeights.Keyword : 0;
        var semanticWeight = semanticCompleted ? branchWeights.Semantic : 0;
        var totalWeight = keywordWeight + semanticWeight;
        if (totalWeight > 0)
        {
            keywordWeight /= totalWeight;
            semanticWeight /= totalWeight;
        }

        var fused = new Dictionary<Guid, Fused>();
        foreach (var candidate in keyword)
            Add(candidate, candidate.KeywordRank!.Value, true, keywordWeight);
        foreach (var candidate in semantic)
            Add(candidate, candidate.SemanticRank!.Value, false, semanticWeight);

        var ranked = fused.Values.OrderByDescending(item => item.Score)
            .ThenBy(item => Math.Min(item.Candidate.KeywordRank ?? int.MaxValue,
                item.Candidate.SemanticRank ?? int.MaxValue))
            .ThenBy(item => item.Candidate.PassageId).ToArray();
        var groups = ranked.GroupBy(item => item.Candidate.ContentId)
            .Select(group => BuildGroup(group.ToArray(), clauses, minimumShouldMatch, options))
            .OrderByDescending(group => group.Score).ThenBy(group => group.DocumentId).ThenBy(group => group.ContentId)
            .ToArray();
        var returned = new List<SearchResultGroup>();
        var returnedPerDocument = new Dictionary<Guid, int>();
        foreach (var group in groups)
        {
            var documentCount = returnedPerDocument.GetValueOrDefault(group.DocumentId);
            if (returned.Count >= options.GroupLimit || documentCount >= options.MaxGroupsPerDocument) continue;
            returned.Add(group);
            returnedPerDocument[group.DocumentId] = documentCount + 1;
        }
        var suppressed = groups.GroupBy(group => group.DocumentId).Select(documentGroups =>
        {
            var first = documentGroups.First();
            var matched = documentGroups.Count();
            var returnedCount = returned.Count(group => group.DocumentId == first.DocumentId);
            return new SearchSuppressedSource(first.DocumentId, first.SourcePath, first.FileName, matched,
                returnedCount, matched - returnedCount);
        }).Where(summary => summary.SuppressedContentGroups > 0)
          .OrderByDescending(summary => summary.SuppressedContentGroups)
          .ThenBy(summary => summary.SourcePath, StringComparer.Ordinal).ToArray();
        return new Selection(ranked.Length, groups, returned, suppressed);

        void Add(SearchCandidate candidate, int rank, bool keywordBranch, double weight)
        {
            if (weight <= 0) return;
            if (!fused.TryGetValue(candidate.PassageId, out var current))
                current = new Fused(candidate, 0);
            var merged = current.Candidate with
            {
                KeywordRank = keywordBranch ? candidate.KeywordRank : current.Candidate.KeywordRank,
                KeywordScore = keywordBranch ? candidate.KeywordScore : current.Candidate.KeywordScore,
                SemanticRank = keywordBranch ? current.Candidate.SemanticRank : candidate.SemanticRank,
                SemanticScore = keywordBranch ? current.Candidate.SemanticScore : candidate.SemanticScore
            };
            fused[candidate.PassageId] = new Fused(merged, current.Score + weight / (RrfK + rank));
        }
    }

    private static int ValidateRequest(SearchRequest request, IReadOnlyList<SearchClause> clauses,
        SearchResultOptions options, SearchFieldWeights fieldWeights, SearchBranchWeights branchWeights)
    {
        if (!Enum.IsDefined(request.Mode))
            throw new ContextMoleException("invalid_request", "mode must be hybrid, keyword, or semantic.");
        if (clauses.Count > 64)
            throw new ContextMoleException("invalid_request", "clauses must contain at most 64 items.");
        if (clauses.Select(clause => clause.Id).Distinct(StringComparer.Ordinal).Count() != clauses.Count)
            throw new ContextMoleException("invalid_clause", "Every clause id must be unique.");
        foreach (var clause in clauses)
        {
            if (string.IsNullOrWhiteSpace(clause.Id) || clause.Id.Length > 64 ||
                clause.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.')))
                throw new ContextMoleException("invalid_clause",
                    "Clause ids must be 1-64 ASCII letters, numbers, dots, underscores, or hyphens.");
            if (string.IsNullOrWhiteSpace(clause.Text) || clause.Text.Length > 512)
                throw new ContextMoleException("invalid_clause", $"Clause '{clause.Id}' text must contain 1-512 characters.");
            if (!Enum.IsDefined(clause.Occur) || !Enum.IsDefined(clause.Match))
                throw new ContextMoleException("invalid_clause", $"Clause '{clause.Id}' has an invalid occur or match value.");
            if (clause.Fields?.Any(field => !Enum.IsDefined(field)) == true)
                throw new ContextMoleException("invalid_clause", $"Clause '{clause.Id}' contains an invalid field.");
            var tokenCount = StructuredSearchQuery.Tokens(clause.Text).Count;
            if (tokenCount == 0 || clause.Match is SearchMatchKind.Term or SearchMatchKind.Prefix && tokenCount != 1)
                throw new ContextMoleException("invalid_clause",
                    $"Clause '{clause.Id}' must contain one token for term/prefix or one or more tokens for phrase.");
        }

        var mustCount = clauses.Count(clause => clause.Occur == SearchClauseOccur.Must);
        var shouldCount = clauses.Count(clause => clause.Occur == SearchClauseOccur.Should);
        var minimumShouldMatch = request.MinimumShouldMatch ?? (mustCount == 0 && shouldCount > 0 ? 1 : 0);
        if (request.MinimumShouldMatch is not null && shouldCount == 0)
            throw new ContextMoleException("invalid_request",
                "minimum_should_match cannot affect a query without should clauses; omit it.");
        if (minimumShouldMatch < 0 || minimumShouldMatch > shouldCount)
            throw new ContextMoleException("invalid_request", "minimum_should_match must be between zero and the number of should clauses.");
        var semanticCanSeed = (request.Mode is SearchMode.Semantic or SearchMode.Hybrid) &&
                              !string.IsNullOrWhiteSpace(request.SemanticQuery) && branchWeights.Semantic > 0;
        if (mustCount == 0 && shouldCount > 0 && minimumShouldMatch == 0 && !semanticCanSeed)
            throw new ContextMoleException("invalid_request",
                "minimum_should_match must be at least 1 when should clauses are the only positive lexical input.");
        var hasPositiveClause = mustCount + shouldCount > 0;
        var hasSemanticQuery = !string.IsNullOrWhiteSpace(request.SemanticQuery);
        var keywordInput = (request.Mode is SearchMode.Keyword or SearchMode.Hybrid) && hasPositiveClause;
        var semanticInput = (request.Mode is SearchMode.Semantic or SearchMode.Hybrid) && hasSemanticQuery;
        if (request.SemanticQuery is { Length: > 4096 })
            throw new ContextMoleException("invalid_request", "semantic_query must not exceed 4096 characters.");
        if (request.Mode == SearchMode.Keyword && !hasPositiveClause)
            throw new ContextMoleException("invalid_request", "keyword mode requires at least one must or should clause.");
        if (request.Mode == SearchMode.Keyword && hasSemanticQuery)
            throw new ContextMoleException("invalid_request", "semantic_query cannot affect keyword mode; omit it or choose hybrid/semantic.");
        if (request.Mode == SearchMode.Semantic && !hasSemanticQuery)
            throw new ContextMoleException("invalid_request", "semantic mode requires semantic_query.");
        if (request.Mode == SearchMode.Hybrid && !hasPositiveClause && !hasSemanticQuery)
            throw new ContextMoleException("invalid_request", "hybrid mode requires semantic_query or a must/should clause.");
        if (request.Mode == SearchMode.Semantic && request.FieldWeights is not null)
            throw new ContextMoleException("invalid_request", "field_weights cannot affect semantic mode; omit them.");
        if (request.Mode != SearchMode.Hybrid && request.BranchWeights is not null)
            throw new ContextMoleException("invalid_request", "branch_weights are only valid in hybrid mode.");
        if (request.Mode == SearchMode.Hybrid && request.BranchWeights is not null &&
            (!hasPositiveClause || !hasSemanticQuery))
            throw new ContextMoleException("invalid_request",
                "branch_weights require both keyword clauses and semantic_query; otherwise one weight cannot affect retrieval.");
        if (request.Mode == SearchMode.Hybrid && hasSemanticQuery && branchWeights.Semantic == 0)
            throw new ContextMoleException("invalid_request",
                "semantic_query cannot affect hybrid search when branch_weights.semantic is zero.");
        if (request.FieldWeights is not null && (!keywordInput || branchWeights.Keyword == 0))
            throw new ContextMoleException("invalid_request",
                "field_weights require an enabled keyword branch with must/should clauses.");
        var customSemanticThreshold = options.StrictSemanticThreshold ||
                                      options.SemanticConfidenceThreshold != 0.25;
        if (request.ResultOptions is not null && customSemanticThreshold &&
            (!semanticInput || branchWeights.Semantic == 0))
            throw new ContextMoleException("invalid_request",
                "Semantic confidence settings require an enabled semantic branch.");

        ValidateWeights(fieldWeights);
        ValidateWeight(branchWeights.Keyword, "branch_weights.keyword");
        ValidateWeight(branchWeights.Semantic, "branch_weights.semantic");
        if (request.Mode == SearchMode.Hybrid && branchWeights.Keyword == 0 && branchWeights.Semantic == 0)
            throw new ContextMoleException("invalid_request", "At least one hybrid branch weight must be greater than zero.");
        if (keywordInput && branchWeights.Keyword > 0 &&
            fieldWeights is { Body: 0, Title: 0, Heading: 0, Filename: 0,
                Path: 0, ContentName: 0, Sheet: 0, EmailSubject: 0 })
            throw new ContextMoleException("invalid_request", "At least one lexical field weight must be greater than zero.");
        if (options.GroupLimit is < 1 or > 50 || options.PreviewsPerGroup is < 1 or > 10 ||
            options.MaxGroupsPerDocument is < 1 or > 50)
            throw new ContextMoleException("invalid_request",
                "result_options require group_limit 1-50, previews_per_group 1-10, and max_groups_per_document 1-50.");
        if (!double.IsFinite(options.SemanticConfidenceThreshold) || options.SemanticConfidenceThreshold is < -1 or > 1)
            throw new ContextMoleException("invalid_request", "semantic_confidence_threshold must be between -1 and 1.");
        ValidateFilters(request.Filters);
        return minimumShouldMatch;

        static void ValidateWeights(SearchFieldWeights weights)
        {
            ValidateWeight(weights.Body, "field_weights.body");
            ValidateWeight(weights.Title, "field_weights.title");
            ValidateWeight(weights.Heading, "field_weights.heading");
            ValidateWeight(weights.Filename, "field_weights.filename");
            ValidateWeight(weights.Path, "field_weights.path");
            ValidateWeight(weights.ContentName, "field_weights.content_name");
            ValidateWeight(weights.Sheet, "field_weights.sheet");
            ValidateWeight(weights.EmailSubject, "field_weights.email_subject");
        }

        static void ValidateWeight(double value, string name)
        {
            if (!double.IsFinite(value) || value is < 0 or > 10)
                throw new ContextMoleException("invalid_request", $"{name} must be a finite number from 0 to 10.");
        }
    }

    private static void ValidateFilters(SearchFilters? filters)
    {
        if (filters is null) return;
        if (!Enum.IsDefined(filters.AttachmentScope))
            throw new ContextMoleException("invalid_filter", "attachment_scope is invalid.");
        if (filters.DocumentIds is { Count: > 100 } || filters.ContentIds is { Count: > 100 })
            throw new ContextMoleException("invalid_filter", "document_ids and content_ids accept at most 100 IDs each.");
        if (filters.PathPrefixes is { Count: > 50 } ||
            filters.PathPrefixes?.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 1024) == true)
            throw new ContextMoleException("invalid_filter", "path_prefixes accepts at most 50 non-empty paths of up to 1024 characters.");
        ValidateExtensions(filters.RootExtensions, "root_extensions");
        ValidateExtensions(filters.ContentExtensions, "content_extensions");
        if (filters.ModifiedFromUtc is { } from && filters.ModifiedToUtc is { } to && from > to)
            throw new ContextMoleException("invalid_filter", "modified_from_utc must not be later than modified_to_utc.");

        static void ValidateExtensions(IReadOnlyList<string>? values, string name)
        {
            if (values is { Count: > 50 } ||
                values?.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 32) == true)
                throw new ContextMoleException("invalid_filter", $"{name} accepts at most 50 non-empty values of up to 32 characters.");
        }
    }

    private static SearchResultGroup BuildGroup(IReadOnlyList<Fused> matches, IReadOnlyList<SearchClause> clauses,
        int minimumShouldMatch, SearchResultOptions options)
    {
        var orderedByOrdinal = matches.OrderBy(match => match.Candidate.Ordinal).ThenBy(match => match.Candidate.PassageId)
            .ToArray();
        var clusters = new List<List<Fused>>();
        foreach (var match in orderedByOrdinal)
        {
            if (clusters.Count == 0 || match.Candidate.Ordinal > clusters[^1][^1].Candidate.Ordinal + 1 ||
                !CanConsolidate(clusters[^1][^1].Candidate, match.Candidate))
                clusters.Add([]);
            clusters[^1].Add(match);
        }

        var previews = clusters.Select(cluster =>
        {
            var representative = cluster.OrderByDescending(item => item.Score).ThenBy(item => item.Candidate.PassageId).First();
            var candidate = representative.Candidate;
            var combined = string.Join(" \u2026 ", cluster.OrderBy(item => item.Candidate.Ordinal)
                .Select(item => item.Candidate.DisplayText).Where(text => text.Length > 0).Distinct(StringComparer.Ordinal));
            var truncated = combined.Length > 800;
            var excerpt = truncated ? combined[..800] : combined;
            var evaluations = cluster.Select(item => StructuredSearchQuery.Evaluate(item.Candidate, clauses, minimumShouldMatch))
                .ToArray();
            return new SearchResultItem(candidate.PassageId, candidate.DocumentId, candidate.ContentId, excerpt, truncated,
                candidate.SourcePath, candidate.FileName, candidate.FileType, candidate.ModifiedUtc, candidate.Location,
                candidate.AttachmentChain, candidate.ExtractionMethod, candidate.OcrConfidence, representative.Score,
                candidate.KeywordScore, candidate.SemanticScore, candidate.KeywordRank, candidate.SemanticRank,
                candidate.SemanticScore is { } semanticScore &&
                semanticScore < options.SemanticConfidenceThreshold,
                evaluations.SelectMany(evaluation => evaluation.MatchedClauseIds).Distinct(StringComparer.Ordinal).ToArray(),
                evaluations.SelectMany(evaluation => evaluation.MatchedFields).Distinct().Order().ToArray(),
                cluster.OrderBy(item => item.Candidate.Ordinal).Select(item => item.Candidate.PassageId).ToArray());
        }).OrderByDescending(preview => preview.FusedScore).ThenBy(preview => preview.PassageId)
          .Take(options.PreviewsPerGroup).ToArray();

        var best = matches.OrderByDescending(match => match.Score).ThenBy(match => match.Candidate.PassageId).First();
        var contentName = best.Candidate.ContentName ?? best.Candidate.AttachmentChain.LastOrDefault() ?? best.Candidate.FileName;
        return new SearchResultGroup(best.Candidate.DocumentId, best.Candidate.ContentId, best.Candidate.SourcePath,
            best.Candidate.FileName, best.Candidate.FileType, contentName, best.Candidate.ContentMimeType,
            best.Candidate.ContentExtension, best.Candidate.AttachmentChain, best.Score, matches.Count,
            Math.Max(0, matches.Count - previews.Length), previews);
    }

    private static bool CanConsolidate(SearchCandidate left, SearchCandidate right) =>
        left.DocumentId == right.DocumentId && left.ContentId == right.ContentId &&
        string.Equals(left.SourcePath, right.SourcePath, StringComparison.Ordinal) &&
        left.Location == right.Location && left.ExtractionMethod == right.ExtractionMethod &&
        left.OcrConfidence == right.OcrConfidence &&
        string.Equals(left.Heading, right.Heading, StringComparison.Ordinal) &&
        left.AttachmentChain.SequenceEqual(right.AttachmentChain, StringComparer.Ordinal);

    private static bool IsSemanticAvailabilityFailure(ContextMoleException exception) =>
        exception.Code.StartsWith("semantic_", StringComparison.Ordinal) ||
        exception.Code.StartsWith("embedding_", StringComparison.Ordinal) ||
        exception.Code.StartsWith("model_", StringComparison.Ordinal) ||
        exception.Code == "asset_checksum_mismatch";

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
                throw new ContextMoleException("index_changed", "The project index changed while loading semantic vectors.", true);
            if (snapshot.Warning is not null)
                throw new ContextMoleException("semantic_index_invalid", snapshot.Warning);
            return _cache.GetOrCreate(projectId, snapshot, _vectorFactory);
        }
        finally
        {
            _vectorLoadGate.Release();
        }
    }

    private sealed record Fused(SearchCandidate Candidate, double Score);

    private sealed record Selection(
        int RankedCount,
        IReadOnlyList<SearchResultGroup> AllGroups,
        IReadOnlyList<SearchResultGroup> Returned,
        IReadOnlyList<SearchSuppressedSource> SuppressedSources)
    {
        public static Selection Empty { get; } = new(0, [], [], []);
    }
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
