using ContextMole.Core;

namespace ContextMole.Search;

public sealed class HybridSearchService(
    ISearchStore store,
    IEmbeddingGenerator embeddingGenerator,
    IVectorIndexFactory vectorFactory,
    VectorIndexCache cache,
    IGlobalCpuBudget cpuBudget,
    bool diversifyPreviews = false,
    bool anchorSemanticPreviews = false)
{
    private const double RrfK = 60;
    private const double OptionalShouldBranchWeight = 0.6;
    private readonly ISearchStore _store = store;
    private readonly IEmbeddingGenerator _embeddingGenerator = embeddingGenerator;
    private readonly IVectorIndexFactory _vectorFactory = vectorFactory;
    private readonly VectorIndexCache _cache = cache;
    private readonly IGlobalCpuBudget _cpuBudget = cpuBudget;
    private readonly bool _diversifyPreviews = diversifyPreviews;
    private readonly bool _anchorSemanticPreviews = anchorSemanticPreviews;
    private readonly SemaphoreSlim _embeddingReloadGate = new(1, 1);
    private readonly SemaphoreSlim _vectorLoadGate = new(1, 1);

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var clauses = request.Clauses?.ToArray() ?? [];
        var options = request.ResultOptions ?? new SearchResultOptions();
        var fieldWeights = request.FieldWeights ?? new SearchFieldWeights();
        var branchWeights = request.BranchWeights ?? new SearchBranchWeights();
        var minimumShouldMatch = ValidateRequest(request, clauses, options, fieldWeights, branchWeights);
        var preparedQuery = StructuredSearchQuery.Prepare(clauses);
        var keywordQuery = StructuredSearchQuery.BuildFtsQuery(clauses, minimumShouldMatch);
        var optionalKeywordQuery = StructuredSearchQuery.BuildOptionalShouldBoostQuery(clauses, minimumShouldMatch);
        var hasKeywordBranch = (request.Mode is SearchMode.Keyword or SearchMode.Hybrid) && keywordQuery.Length > 0 &&
                               branchWeights.Keyword > 0;
        var hasSemanticBranch = (request.Mode is SearchMode.Semantic or SearchMode.Hybrid) &&
                                !string.IsNullOrWhiteSpace(request.SemanticQuery) && branchWeights.Semantic > 0;
        if (!hasKeywordBranch && !hasSemanticBranch)
            throw new ContextMoleException("invalid_request",
                "The selected mode, query inputs, and branch weights leave no applicable search branch.");

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
                await EnsureEmbeddingAvailableAsync(cancellationToken).ConfigureAwait(false);
                var activePolicy = _embeddingGenerator.Policy;
                if (_embeddingGenerator.IsAvailable && activePolicy is not null)
                {
                    vectorMetadata = await _store.LoadVectorSnapshotMetadataAsync(request.ProjectId, activePolicy,
                        cancellationToken).ConfigureAwait(false);
                    if (vectorMetadata.Warning is not null)
                    {
                        var warningCode = vectorMetadata.HasPartialCoverage && vectorMetadata.EntryCount > 0
                            ? "semantic_partial_coverage"
                            : "semantic_index_incomplete";
                        warnings.Add(new SearchWarning(warningCode, vectorMetadata.Warning));
                    }
                }

                var semanticEnabled = _embeddingGenerator.IsAvailable && activePolicy is not null &&
                                      vectorMetadata.Policy is not null && vectorMetadata.EntryCount > 0;
                var unavailableReason = !_embeddingGenerator.IsAvailable
                    ? _embeddingGenerator.UnavailableReason ?? "Granite model assets are unavailable."
                    : activePolicy is null
                        ? "The active embedding policy is unavailable."
                        : vectorMetadata.EntryCount == 0
                            ? "No semantic embeddings compatible with the active model are currently available for this project."
                            : vectorMetadata.Policy is null ||
                              !string.Equals(vectorMetadata.Policy.Key, activePolicy.Key, StringComparison.Ordinal)
                                ? "The active embedding policy differs from the indexed vectors while re-embedding completes."
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
                        else
                            _cache.Invalidate(request.ProjectId);
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

        KeywordBranchSnapshot keywordSnapshot = new(0, [], [], false, false);
        if (hasKeywordBranch)
        {
            try
            {
                keywordSnapshot = await _store.LoadKeywordBranchesAsync(request.ProjectId, keywordQuery,
                    optionalKeywordQuery, request.CandidateLimit, request.Filters, fieldWeights, request.Scope,
                    cancellationToken).ConfigureAwait(false);
                keywordCompleted = true;
            }
            catch (ContextMoleException) { throw; }
            catch (Exception exception) when (exception is not OperationCanceledException && request.Mode != SearchMode.Keyword)
            {
                warnings.Add(new SearchWarning("keyword_unavailable", $"Keyword search is unavailable: {exception.Message}"));
            }
        }

        var keyword = RerankKeywordCandidates(keywordSnapshot.MainCandidates,
            keywordSnapshot.OptionalCandidates, clauses, minimumShouldMatch, preparedQuery);
        SearchCandidate[] semanticCandidates = [];
        var semanticDepth = 0;
        var semanticMatchedCount = 0;
        var semanticLimitReached = false;
        if (semanticCompleted)
        {
            try
            {
                var matches = vectorMetadata.RequiresStreaming
                    ? await FlatVectorIndex.SearchStreamingAsync(_store.StreamVectorEntriesAsync(request.ProjectId,
                        vectorMetadata.SearchGeneration, vectorMetadata.Policy!, request.Filters, cancellationToken),
                        queryEmbedding!.Vector, request.CandidateLimit + 1, cancellationToken).ConfigureAwait(false)
                    : vectorIndex!.Search(queryEmbedding!.Vector, request.CandidateLimit + 1, request.Filters);
                semanticLimitReached = matches.Count > request.CandidateLimit;
                var inspected = matches.Take(request.CandidateLimit).ToArray();
                semanticDepth = inspected.Length;
                var matchesByPassage = inspected.ToDictionary(match => match.PassageId);
                var hydrated = await _store.LoadCandidatesAsync(request.ProjectId, matchesByPassage.Keys.ToArray(),
                    vectorMetadata.SearchGeneration, request.Scope, cancellationToken).ConfigureAwait(false);
                var acceptedSemantic = hydrated.Select(candidate => candidate with
                    {
                        SemanticPassageId = candidate.PassageId,
                        SemanticRank = matchesByPassage[candidate.PassageId].Rank,
                        SemanticScore = matchesByPassage[candidate.PassageId].Score
                    }).Where(candidate => preparedQuery.Evaluate(candidate, minimumShouldMatch).IsMatch)
                    .Where(candidate => !options.StrictSemanticThreshold || candidate.SemanticScore >= options.SemanticSimilarityThreshold).ToArray();
                semanticMatchedCount = acceptedSemantic.Length;
                semanticCandidates = acceptedSemantic.GroupBy(CandidateKey).Select(group => group.OrderByDescending(candidate => candidate.SemanticScore)
                        .ThenBy(candidate => candidate.PassageId).First())
                    .OrderByDescending(candidate => candidate.SemanticScore).ThenBy(CandidateKey)
                    .Select((candidate, rank) => candidate with { SemanticRank = rank + 1 }).ToArray();
            }
            catch (ContextMoleException exception) when (!IsSemanticAvailabilityFailure(exception)) { throw; }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                semanticCompleted = false;
                warnings.Add(new SearchWarning("semantic_unavailable", $"Semantic search is unavailable: {exception.Message}"));
                if (hasKeywordBranch)
                    warnings.Add(new SearchWarning("fallback_keyword", "The requested hybrid search returned keyword results only."));
            }
        }
        if (keywordSnapshot.SearchGeneration != 0 && vectorMetadata.SearchGeneration != 0 &&
            keywordSnapshot.SearchGeneration != vectorMetadata.SearchGeneration)
            throw new ContextMoleException("index_changed", "The project index changed during search. Retry the request.", true);
        var generation = keywordSnapshot.SearchGeneration != 0 ? keywordSnapshot.SearchGeneration : vectorMetadata.SearchGeneration;
        var currentGeneration = await _store.GetSearchGenerationAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (generation == 0) generation = currentGeneration;
        else if (currentGeneration != generation)
            throw new ContextMoleException("index_changed", "The project index changed before search completed. Retry the request.", true);
        var selection = BuildSelection(keyword, semanticCandidates, keywordCompleted, semanticCompleted,
            branchWeights, clauses, minimumShouldMatch, options, request.Scope, preparedQuery, _diversifyPreviews,
            _anchorSemanticPreviews && semanticCompleted ? new SemanticPreviewAnchors(request.SemanticQuery!) : null);
        var capped = keywordSnapshot.MainLimitReached || keywordSnapshot.OptionalLimitReached || semanticLimitReached;
        return new SearchResponse(request.Mode, keywordCompleted && semanticCompleted ? "hybrid" : semanticCompleted ? "semantic" : keywordCompleted ? "keyword" : "unavailable",
            warnings.DistinctBy(warning => (warning.Code, warning.Message)).ToArray(), generation, selection.RankedCount,
            new SearchBranchCandidateDepths(keywordSnapshot.MainCandidates.Count, keywordSnapshot.OptionalCandidates.Count, semanticDepth),
            capped, selection.Returned.Count, selection.AllGroups.Count - selection.Returned.Count,
            selection.SuppressedSources, selection.Returned)
        {
            RankedGroups = selection.AllGroups,
            HasMore = selection.AllGroups.Count > selection.Returned.Count,
            MatchScope = request.Scope,
            CandidateLimit = request.CandidateLimit,
            StopReason = capped ? "candidate_budget" : !keywordCompleted && !semanticCompleted ? "unavailable" :
                hasKeywordBranch && !keywordCompleted || hasSemanticBranch && !semanticCompleted ? "branch_unavailable" : "exhausted",
            Branches = new SearchBranchDiagnosticsMap(
                new(hasKeywordBranch, keywordCompleted, keywordSnapshot.MainCandidates.Count,
                    keywordSnapshot.MainCandidates.Count(candidate => preparedQuery.Evaluate(candidate, minimumShouldMatch).IsMatch),
                    keywordSnapshot.MainLimitReached, keywordCompleted && !keywordSnapshot.MainLimitReached),
                new(hasKeywordBranch && optionalKeywordQuery.Length > 0, keywordCompleted && optionalKeywordQuery.Length > 0,
                    keywordSnapshot.OptionalCandidates.Count,
                    keywordSnapshot.OptionalCandidates.Count(candidate => preparedQuery.Evaluate(candidate, minimumShouldMatch).IsMatch),
                    keywordSnapshot.OptionalLimitReached, keywordCompleted && optionalKeywordQuery.Length > 0 && !keywordSnapshot.OptionalLimitReached),
                new(hasSemanticBranch, semanticCompleted, semanticDepth, semanticMatchedCount,
                    semanticLimitReached, semanticCompleted && !semanticLimitReached)),
            SemanticPolicyKey = semanticCompleted ? queryEmbedding!.Policy.Key : null
        };
    }

    private static Guid CandidateKey(SearchCandidate candidate) => candidate.SectionText is not null ? candidate.SectionId ?? candidate.PassageId : candidate.PassageId;

    private static SearchCandidate[] RerankKeywordCandidates(IReadOnlyList<SearchCandidate> mainCandidates,
        IReadOnlyList<SearchCandidate> optionalCandidates, IReadOnlyList<SearchClause> clauses,
        int minimumShouldMatch, StructuredSearchQuery.PreparedQuery preparedQuery)
    {
        var shouldIds = clauses.Where(clause => clause.Occur == SearchClauseOccur.Should)
            .Select(clause => clause.Id).ToHashSet(StringComparer.Ordinal);
        var mainRanks = mainCandidates.Select((candidate, index) => (PassageId: CandidateKey(candidate), Rank: index + 1))
            .ToDictionary(item => item.PassageId, item => item.Rank);
        var optionalRanks = optionalCandidates.Select((candidate, index) => (PassageId: CandidateKey(candidate), Rank: index + 1))
            .ToDictionary(item => item.PassageId, item => item.Rank);
        var candidates = mainCandidates.Concat(optionalCandidates).DistinctBy(CandidateKey);
        return candidates.Select(candidate =>
        {
            var evaluation = preparedQuery.Evaluate(candidate, minimumShouldMatch);
            var baseRank = mainRanks.TryGetValue(CandidateKey(candidate), out var mainRank)
                ? mainRank
                : mainCandidates.Count + optionalRanks[CandidateKey(candidate)];
            if (!evaluation.IsMatch)
                return (Candidate: candidate, Score: double.MinValue, Include: false, BaseRank: baseRank);
            var optionalMatches = evaluation.MatchedClauseIds.Count(shouldIds.Contains);
            var optionalBoost = shouldIds.Count == 0 || optionalMatches == 0 ||
                                !optionalRanks.TryGetValue(CandidateKey(candidate), out var optionalRank)
                ? 0
                : OptionalShouldBranchWeight * optionalMatches / shouldIds.Count / (RrfK + optionalRank);
            return (Candidate: candidate, Score: 1d / (RrfK + baseRank) + optionalBoost, Include: true, BaseRank: baseRank);
        }).Where(item => item.Include).OrderByDescending(item => item.Score).ThenBy(item => item.BaseRank)
          .ThenBy(item => item.Candidate.PassageId).Select((item, rank) => item.Candidate with { KeywordRank = rank + 1 })
          .ToArray();
    }

    private static Selection BuildSelection(IReadOnlyList<SearchCandidate> keyword,
        IReadOnlyList<SearchCandidate> semantic, bool keywordCompleted, bool semanticCompleted,
        SearchBranchWeights branchWeights, IReadOnlyList<SearchClause> clauses, int minimumShouldMatch,
        SearchResultOptions options, SearchScope scope, StructuredSearchQuery.PreparedQuery preparedQuery,
        bool diversifyPreviews, SemanticPreviewAnchors? semanticAnchors)
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
        var groups = ranked.GroupBy(item => scope == SearchScope.Section ? CandidateKey(item.Candidate) : item.Candidate.ContentId)
            .Select(group => BuildGroup(group.ToArray(), clauses, minimumShouldMatch, options, preparedQuery,
                diversifyPreviews, semanticAnchors))
            .OrderByDescending(group => group.Score).ThenBy(group => group.DocumentId).ThenBy(group => group.ContentId)
            .ThenBy(group => group.SectionId)
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
            if (!fused.TryGetValue(CandidateKey(candidate), out var current))
                current = new Fused(candidate, 0);
            var merged = current.Candidate with
            {
                KeywordRank = keywordBranch ? candidate.KeywordRank : current.Candidate.KeywordRank,
                KeywordScore = keywordBranch ? candidate.KeywordScore : current.Candidate.KeywordScore,
                SemanticRank = keywordBranch ? current.Candidate.SemanticRank : candidate.SemanticRank,
                SemanticScore = keywordBranch ? current.Candidate.SemanticScore : candidate.SemanticScore,
                SemanticPassageId = keywordBranch ? current.Candidate.SemanticPassageId : candidate.SemanticPassageId
            };
            fused[CandidateKey(candidate)] = new Fused(merged, current.Score + weight / (RrfK + rank));
        }
    }

    private static int ValidateRequest(SearchRequest request, IReadOnlyList<SearchClause> clauses,
        SearchResultOptions options, SearchFieldWeights fieldWeights, SearchBranchWeights branchWeights)
    {
        if (!Enum.IsDefined(request.Scope) || !Enum.IsDefined(request.Detail))
            throw new ContextMoleException("invalid_request", "match_scope and detail have invalid values.");
        if (request.CandidateLimit is < 1 or > 10000)
            throw new ContextMoleException("invalid_request", "candidate_limit must be between 1 and 10000.");
        if (request.Cursor is not null)
            throw new ContextMoleException("invalid_request", "Search continuations must be handled by the broker session.");
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
                                      options.SemanticSimilarityThreshold != 0.25;
        if (request.ResultOptions is not null && customSemanticThreshold &&
            (!semanticInput || branchWeights.Semantic == 0))
            throw new ContextMoleException("invalid_request",
                "Semantic similarity settings require an enabled semantic branch.");

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
        if (!double.IsFinite(options.SemanticSimilarityThreshold) || options.SemanticSimilarityThreshold is < -1 or > 1)
            throw new ContextMoleException("invalid_request", "semantic_similarity_threshold must be between -1 and 1.");
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
        int minimumShouldMatch, SearchResultOptions options, StructuredSearchQuery.PreparedQuery preparedQuery,
        bool diversifyPreviews, SemanticPreviewAnchors? semanticAnchors)
    {
        var best = matches.OrderByDescending(match => match.Score).ThenBy(match => match.Candidate.PassageId).First();
        var scopeCandidate = best.Candidate;
        var evidence = matches.SelectMany(match => match.Candidate.SectionText is not null && match.Candidate.SectionPassages is { Count: > 0 }
                ? match.Candidate.SectionPassages.Select(passage => new Fused(passage with
                {
                    SectionText = null,
                    SemanticRank = passage.PassageId == (match.Candidate.SemanticPassageId ?? match.Candidate.PassageId) ? match.Candidate.SemanticRank : null,
                    SemanticScore = passage.PassageId == (match.Candidate.SemanticPassageId ?? match.Candidate.PassageId) ? match.Candidate.SemanticScore : null,
                    KeywordRank = match.Candidate.KeywordRank,
                    KeywordScore = match.Candidate.KeywordScore
                }, match.Score))
                : [match])
            .DistinctBy(item => item.Candidate.PassageId).ToArray();
        var sectionSpans = scopeCandidate.SectionText is not null
            ? preparedQuery.FindBodyMatches(scopeCandidate.SectionText) : [];
        var evaluated = evidence.Select(item => new EvaluatedEvidence(item,
            preparedQuery.FindBodyMatches(item.Candidate.DisplayText)
                .Concat(sectionSpans.Where(span => span.Start < item.Candidate.SectionOffset + item.Candidate.DisplayText.Length &&
                    span.Start + span.Length > item.Candidate.SectionOffset)
                    .Select(span => new SearchMatchSpan(span.ClauseId, Math.Max(0, span.Start - item.Candidate.SectionOffset),
                        Math.Min(item.Candidate.DisplayText.Length, span.Start + span.Length - item.Candidate.SectionOffset) -
                        Math.Max(0, span.Start - item.Candidate.SectionOffset))))
                .Distinct().ToArray(),
            preparedQuery.FindFieldMatches(item.Candidate))).ToArray();
        var matchingEvidence = evaluated.Where(item => item.Spans.Length > 0 || item.Fields.Count > 0 ||
            item.Item.Candidate.SemanticRank is not null).ToArray();
        var rankedEvidence = matchingEvidence.OrderByDescending(item => item.Spans.Select(span => span.ClauseId)
                .Concat(item.Fields.Select(field => field.ClauseId)).Distinct().Count())
            .ThenByDescending(item => item.Item.Score).ThenByDescending(item => item.Item.Candidate.SemanticScore)
            .ThenBy(item => item.Item.Candidate.Ordinal)
            .ThenBy(item => item.Item.Candidate.PassageId).ToArray();
        var previewEvidence = diversifyPreviews
            ? SelectDiversePreviews(rankedEvidence, options.PreviewsPerGroup)
            : rankedEvidence.Take(options.PreviewsPerGroup);
        var previews = previewEvidence.Select(item => BuildPreview(item.Item, clauses, options, item.Spans, item.Fields,
            semanticAnchors)).ToArray();
        var scopeEvaluation = preparedQuery.Evaluate(scopeCandidate, minimumShouldMatch);
        var matchedClauseIds = scopeEvaluation.MatchedClauseIds.Concat(evaluated.SelectMany(item =>
            item.Spans.Select(span => span.ClauseId).Concat(item.Fields.Select(field => field.ClauseId)))).Distinct().ToArray();
        var clauseEvidence = matchedClauseIds.Select(id => new SearchClauseEvidence(id,
            evaluated.Where(item => item.Spans.Any(span => span.ClauseId == id) || item.Fields.Any(field => field.ClauseId == id))
                .Select(item => item.Item.Candidate.PassageId).ToArray(),
            evaluated.SelectMany(item => item.Fields.Where(field => field.ClauseId == id).Select(field => field.Field)
                .Concat(item.Spans.Any(span => span.ClauseId == id) ? [SearchField.Body] : [])).Distinct().Order().ToArray())).ToArray();
        var preferred = evaluated.OrderByDescending(item => previews.Any(preview => preview.PassageId == item.Item.Candidate.PassageId))
            .ThenBy(item => item.Item.Candidate.Ordinal).ThenBy(item => item.Item.Candidate.PassageId).ToArray();
        var compactEvidence = matchedClauseIds.Select(id =>
        {
            var metadata = preferred.FirstOrDefault(item => item.Fields.Any(field => field.ClauseId == id));
            if (metadata is not null)
                return new SearchClauseEvidence(id, [metadata.Item.Candidate.PassageId], metadata.Fields
                    .Where(field => field.ClauseId == id).Select(field => field.Field).Distinct().Order().ToArray());
            if (scopeCandidate.SectionText is not null)
            {
                var occurrence = sectionSpans.Where(span => span.ClauseId == id).OrderBy(span => span.Start).FirstOrDefault();
                if (occurrence is not null)
                {
                    var complete = preferred.FirstOrDefault(item => item.Item.Candidate.SectionOffset <= occurrence.Start &&
                        item.Item.Candidate.SectionOffset + item.Item.Candidate.DisplayText.Length >= occurrence.Start + occurrence.Length);
                    if (complete is not null)
                        return new SearchClauseEvidence(id, [complete.Item.Candidate.PassageId], [SearchField.Body]);
                    // A phrase that crosses a chunk boundary needs every member intersecting this
                    // complete occurrence. Selecting an arbitrary member would give incomplete proof.
                    return new SearchClauseEvidence(id, preferred.Where(item =>
                            item.Item.Candidate.SectionOffset < occurrence.Start + occurrence.Length &&
                            item.Item.Candidate.SectionOffset + item.Item.Candidate.DisplayText.Length > occurrence.Start)
                        .OrderBy(item => item.Item.Candidate.Ordinal).Select(item => item.Item.Candidate.PassageId).ToArray(), [SearchField.Body]);
                }
            }
            var body = preferred.FirstOrDefault(item => item.Spans.Any(span => span.ClauseId == id));
            return new SearchClauseEvidence(id, body is null ? [] : [body.Item.Candidate.PassageId],
                body is null ? [] : [SearchField.Body]);
        }).ToArray();
        var semanticMatch = matches.Where(item => item.Candidate.SemanticRank is not null)
            .OrderByDescending(item => item.Candidate.SemanticScore).ThenBy(item => item.Candidate.SemanticRank)
            .ThenBy(item => item.Candidate.PassageId).FirstOrDefault();
        var semanticAnchor = semanticMatch is null ? (Guid?)null :
            semanticMatch.Candidate.SemanticPassageId ?? semanticMatch.Candidate.PassageId;
        var contentName = scopeCandidate.ContentName ?? scopeCandidate.AttachmentChain.LastOrDefault() ?? scopeCandidate.FileName;
        return new SearchResultGroup(scopeCandidate.DocumentId, scopeCandidate.ContentId, scopeCandidate.SourcePath,
            scopeCandidate.FileName, scopeCandidate.FileType, contentName, scopeCandidate.ContentMimeType,
            scopeCandidate.ContentExtension, scopeCandidate.AttachmentChain, best.Score, matchingEvidence.Length,
            Math.Max(0, matchingEvidence.Length - previews.Length), previews)
        {
            Title = !string.IsNullOrWhiteSpace(scopeCandidate.Title) ? scopeCandidate.Title :
                !string.IsNullOrWhiteSpace(scopeCandidate.Heading) ? scopeCandidate.Heading : contentName,
            SectionId = scopeCandidate.SectionText is not null ? scopeCandidate.SectionId : null,
            MatchedClauseIds = matchedClauseIds,
            EvidencePassageIds = clauseEvidence.SelectMany(item => item.PassageIds)
                .Concat(matches.Where(item => item.Candidate.SemanticRank is not null)
                    .Select(item => item.Candidate.SemanticPassageId ?? item.Candidate.PassageId)).Distinct().ToArray(),
            ClauseEvidence = clauseEvidence,
            SemanticSimilarity = semanticMatch?.Candidate.SemanticScore,
            SemanticAnchorPassageId = semanticAnchor,
            BelowSimilarityThreshold = semanticMatch?.Candidate.SemanticScore is { } similarity
                ? similarity < options.SemanticSimilarityThreshold : null,
            CompactClauseEvidence = compactEvidence,
            CompactEvidencePassageIds = compactEvidence.SelectMany(item => item.PassageIds)
                .Concat(semanticAnchor is { } anchor ? [anchor] : []).Distinct().ToArray()
        };
    }

    private static IEnumerable<EvaluatedEvidence> SelectDiversePreviews(
        IReadOnlyList<EvaluatedEvidence> ranked, int limit)
    {
        if (limit == 1 || ranked.Count <= limit) return ranked.Take(limit);
        // Diversify only a bounded high-ranked pool, rather than pulling weak evidence from
        // arbitrarily deep in the result set. Ranking scores and all citation anchors stay intact.
        var pool = ranked.Take(limit * 3).ToArray();
        var selected = new List<EvaluatedEvidence>(limit);
        foreach (var tier in pool.GroupBy(ClauseCount))
        {
            var seen = new HashSet<(Guid ContentId, int Page, string Clauses)>();
            var deferred = new List<EvaluatedEvidence>();
            foreach (var item in tier)
            {
                var candidate = item.Item.Candidate;
                // Different positive-clause sets are complementary evidence even on one page.
                var clauses = string.Join('\n', item.Spans.Select(span => span.ClauseId)
                    .Concat(item.Fields.Select(field => field.ClauseId)).Distinct().Order(StringComparer.Ordinal));
                if (candidate.Location.Page is > 0 and var page &&
                    !seen.Add((candidate.ContentId, page, clauses)))
                {
                    deferred.Add(item);
                    continue;
                }
                selected.Add(item);
                if (selected.Count == limit) return selected;
            }
            foreach (var item in deferred)
            {
                selected.Add(item);
                if (selected.Count == limit) return selected;
            }
        }
        return selected;

        static int ClauseCount(EvaluatedEvidence item) => item.Spans.Select(span => span.ClauseId)
            .Concat(item.Fields.Select(field => field.ClauseId)).Distinct().Count();
    }

    private static SearchResultItem BuildPreview(Fused item, IReadOnlyList<SearchClause> clauses,
        SearchResultOptions options, IReadOnlyList<SearchMatchSpan> spans, IReadOnlyList<SearchFieldMatch> fields,
        SemanticPreviewAnchors? semanticAnchors)
    {
        var candidate = item.Candidate;
        var text = candidate.DisplayText;
        var length = Math.Min(800, text.Length);
        var start = 0;
        SemanticPreviewAnchors.Window? semanticWindow = null;
        if (spans.Count > 0 && text.Length > length)
        {
            start = spans.Select(span => Math.Clamp(span.Start - length / 2, 0, text.Length - length))
                .Distinct().OrderByDescending(offset => spans.Where(span => span.Start >= offset && span.Start + span.Length <= offset + length)
                    .Select(span => span.ClauseId).Distinct().Count())
                .ThenByDescending(offset => spans.Count(span => span.Start >= offset && span.Start + span.Length <= offset + length))
                .ThenBy(offset => offset).First();
        }
        else if (candidate.SemanticRank is not null && semanticAnchors is not null)
        {
            semanticWindow = semanticAnchors.FindWindow(text, length);
            if (semanticWindow is not null) start = semanticWindow.Start;
        }
        if (start > 0 && char.IsLowSurrogate(text[start])) start--;
        length = Math.Min(800, text.Length - start);
        if (length > 0 && start + length < text.Length && char.IsHighSurrogate(text[start + length - 1])) length--;
        (start, length) = PreserveSentenceBoundaries(text, start, length, spans, semanticWindow?.Anchors);
        // Excerpts are literal slices of exactly this anchor passage. Offsets are UTF-16 positions in read_passages.text.
        var visibleSpans = spans.Where(span => span.Start >= start && span.Start + span.Length <= start + length).ToArray();
        return new SearchResultItem(candidate.PassageId, candidate.DocumentId, candidate.ContentId,
            text.Substring(start, length), start > 0 || start + length < text.Length, candidate.SourcePath,
            candidate.FileName, candidate.FileType, candidate.ModifiedUtc, candidate.Location, candidate.AttachmentChain,
            candidate.ExtractionMethod, candidate.OcrConfidence, item.Score, candidate.KeywordScore, candidate.SemanticScore,
            candidate.KeywordRank, candidate.SemanticRank,
            candidate.SemanticScore is { } score ? score < options.SemanticSimilarityThreshold : null,
            visibleSpans.Select(span => span.ClauseId).Concat(fields.Select(field => field.ClauseId)).Distinct().ToArray(),
            fields.Select(field => field.Field).Concat(visibleSpans.Length > 0 ? [SearchField.Body] : []).Distinct().Order().ToArray(),
            [candidate.PassageId])
        {
            ExcerptStart = start,
            ExcerptLength = length,
            SectionId = candidate.SectionId,
            MatchSpans = visibleSpans,
            FieldMatches = fields
        };
    }

    private static (int Start, int Length) PreserveSentenceBoundaries(string text, int start, int length,
        IReadOnlyList<SearchMatchSpan> spans, IReadOnlyList<LexicalToken>? semanticAnchors)
    {
        var end = start + length;
        var covered = spans.Where(span => span.Start >= start && span.Start + span.Length <= end).ToArray();
        var firstEvidence = covered.Length == 0 ? end : covered.Min(span => span.Start);
        var lastEvidence = covered.Length == 0 ? start : covered.Max(span => span.Start + span.Length);
        if (semanticAnchors is { Count: > 0 })
        {
            firstEvidence = Math.Min(firstEvidence, semanticAnchors.Min(token => token.Start));
            lastEvidence = Math.Max(lastEvidence, semanticAnchors.Max(token => token.Start + token.Length));
        }
        // Trim at most 80 characters inward. Never remove positive evidence that the chosen
        // centered window already contains, expand beyond 800, or fabricate sentence text.
        if (start > 0)
            for (var boundary = start; boundary <= Math.Min(Math.Min(start + 80, firstEvidence), end - 1); boundary++)
                if (IsSentenceStart(boundary)) { start = boundary; break; }
        if (end < text.Length)
            for (var boundary = end; boundary >= Math.Max(Math.Max(end - 80, lastEvidence), start + 1); boundary--)
                if (IsSentenceEnd(boundary)) { end = boundary; break; }
        return (start, end - start);

        bool IsSentenceStart(int offset)
        {
            if (offset == 0) return true;
            if (offset >= text.Length || char.IsWhiteSpace(text[offset]) || char.IsLowSurrogate(text[offset])) return false;
            var previous = offset - 1;
            if (!char.IsWhiteSpace(text[previous])) return false;
            while (previous >= 0 && char.IsWhiteSpace(text[previous]))
            {
                if (text[previous] is '\n' or '\r') return true;
                previous--;
            }
            return previous < 0 || text[previous] is '.' or '!' or '?' or '。' or '！' or '？';
        }
        bool IsSentenceEnd(int offset) => offset == text.Length ||
            offset > 0 && (text[offset - 1] is '.' or '!' or '?' or '。' or '！' or '？' or '\n') &&
            char.IsWhiteSpace(text[offset]);
    }

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

            var snapshot = await _store.LoadVectorSnapshotAsync(projectId, metadata.Policy!, cancellationToken)
                .ConfigureAwait(false);
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

    private sealed record EvaluatedEvidence(Fused Item, SearchMatchSpan[] Spans,
        IReadOnlyList<SearchFieldMatch> Fields);

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
    public const long DefaultByteBudget = 512L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid ProjectId, long Generation, string Policy), Entry> _entries = [];
    private readonly long _byteBudget;
    private long _bytes;

    public VectorIndexCache(long byteBudget = DefaultByteBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);
        _byteBudget = byteBudget;
    }

    public long ByteBudget => _byteBudget;

    public long CurrentBytes
    {
        get { lock (_gate) return _bytes; }
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public static long CalculateAdaptiveBudget(long totalPhysicalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalPhysicalBytes);
        return Math.Min(DefaultByteBudget, Math.Max(1, totalPhysicalBytes / 20));
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _bytes = 0;
        }
    }

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

    public void Invalidate(Guid projectId)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(key => key.ProjectId == projectId).ToArray())
            {
                _bytes -= _entries[key].Bytes;
                _entries.Remove(key);
            }
        }
    }

    public IVectorIndex GetOrCreate(Guid projectId, VectorSnapshot snapshot, IVectorIndexFactory factory)
    {
        var policy = snapshot.Policy?.Key ?? string.Empty;
        var key = (projectId, snapshot.SearchGeneration, policy);
        var bytes = EstimateBytes(snapshot, _byteBudget);
        if (bytes > _byteBudget)
        {
            Invalidate(projectId);
            return factory.Create(snapshot);
        }

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

            while (_bytes > _byteBudget - bytes && _entries.Count > 0)
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

    private static long EstimateBytes(VectorSnapshot snapshot, long budget)
    {
        long bytes = 0;
        foreach (var entry in snapshot.Entries)
        {
            var entryBytes = 512L + entry.Vector.LongLength * sizeof(float) +
                             2L * (entry.SourcePath.Length + entry.Extension.Length);
            if (entryBytes > budget - bytes) return budget == long.MaxValue ? long.MaxValue : budget + 1;
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
