using ContextMole.Core;

namespace ContextMole.Broker;

/// <summary>Retains bounded result records, never document bodies or embedding vectors.</summary>
internal sealed class SearchSessionCache(TimeProvider timeProvider, long byteBudget = 64L * 1024 * 1024)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Session> _sessions = [];
    private long _bytes;

    public SearchResponse Start(SearchRequest request, SearchResponse response)
    {
        var groups = response.RankedGroups.Count > 0 ? response.RankedGroups : response.Results;
        var pages = Paginate(groups, request.ResultOptions ?? new SearchResultOptions());
        if (pages.Count <= 1) return response with { HasMore = false, NextCursor = null };
        var size = EstimateBytes(response, pages);
        if (size > byteBudget)
            throw new ContextMoleException("search_session_too_large", "The search result session exceeds its memory limit. Reduce candidate_limit or previews_per_group.");
        lock (_gate)
        {
            RemoveExpired();
            while (_bytes + size > byteBudget && _sessions.Count > 0)
                Remove(_sessions.MinBy(item => item.Value.LastAccessUtc).Key);
            var id = Guid.NewGuid();
            var now = timeProvider.GetUtcNow();
            var session = new Session(request.ProjectId, request.Detail, response with { RankedGroups = [] }, pages,
                response.SearchGeneration, response.SemanticPolicyKey, now + Lifetime, now, size);
            _sessions.Add(id, session);
            _bytes += size;
            return Page(id, session, 0);
        }
    }

    public Continuation Get(Guid projectId, string cursor)
    {
        if (cursor.Length > 96 || !TryParse(cursor, out var id, out var page))
            throw new ContextMoleException("invalid_cursor", "The search cursor is invalid.");
        lock (_gate)
        {
            RemoveExpired();
            if (!_sessions.TryGetValue(id, out var session))
                throw new ContextMoleException("cursor_expired", "The search session expired or was evicted. Run the search again.", true);
            if (session.ProjectId != projectId)
                throw new ContextMoleException("invalid_cursor", "The search cursor belongs to a different project.");
            if (page < 1 || page >= session.Pages.Count)
                throw new ContextMoleException("invalid_cursor", "The search cursor page is invalid.");
            session.LastAccessUtc = timeProvider.GetUtcNow();
            return new Continuation(Page(id, session, page), session.Detail, session.Generation, session.PolicyKey);
        }
    }

    internal static IReadOnlyList<IReadOnlyList<SearchResultGroup>> Paginate(IReadOnlyList<SearchResultGroup> groups,
        SearchResultOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.GroupLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxGroupsPerDocument);
        var documents = groups.Select((group, rank) => (Group: group, Rank: rank)).GroupBy(item => item.Group.DocumentId)
            .ToDictionary(group => group.Key, group => new Queue<(SearchResultGroup Group, int Rank)>(group));
        var eligible = new PriorityQueue<Guid, int>();
        foreach (var (document, queue) in documents) eligible.Enqueue(document, queue.Peek().Rank);
        var pages = new List<IReadOnlyList<SearchResultGroup>>();
        while (eligible.Count > 0)
        {
            var page = new List<SearchResultGroup>();
            var documentCounts = new Dictionary<Guid, int>();
            var deferred = new List<Guid>();
            while (eligible.Count > 0 && page.Count < options.GroupLimit)
            {
                var document = eligible.Dequeue();
                var queue = documents[document];
                page.Add(queue.Dequeue().Group);
                var count = documentCounts.GetValueOrDefault(document) + 1;
                documentCounts[document] = count;
                if (queue.Count > 0)
                {
                    if (count >= options.MaxGroupsPerDocument) deferred.Add(document);
                    else eligible.Enqueue(document, queue.Peek().Rank);
                }
            }
            pages.Add(page);
            foreach (var document in deferred) eligible.Enqueue(document, documents[document].Peek().Rank);
        }
        return pages;
    }

    private static SearchResponse Page(Guid id, Session session, int page)
    {
        var groups = session.Pages[page];
        var hasMore = page + 1 < session.Pages.Count;
        return session.Response with
        {
            Results = groups,
            ReturnedGroupCount = groups.Count,
            HasMore = hasMore,
            NextCursor = hasMore ? $"{id:N}.{page + 1}" : null
        };
    }

    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var id in _sessions.Where(item => item.Value.ExpiresUtc <= now).Select(item => item.Key).ToArray()) Remove(id);
    }

    private void Remove(Guid id)
    {
        if (_sessions.Remove(id, out var session)) _bytes -= session.Size;
    }

    private static bool TryParse(string cursor, out Guid id, out int page)
    {
        id = default;
        page = 0;
        var parts = cursor.Split('.');
        return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out id) &&
            int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out page);
    }

    private static long EstimateBytes(SearchResponse response, IReadOnlyList<IReadOnlyList<SearchResultGroup>> pages)
    {
        // Count strings each time they are referenced, even when shared. Object/array allowances are
        // deliberately conservative; every retained variable-length field must participate in the budget.
        static long Text(string? value) => value is null ? 0 : 32L + value.Length * 2L;
        static long Strings(IReadOnlyList<string> values) => 32L + values.Count * 8L + values.Sum(Text);
        static long Guids(IReadOnlyList<Guid> values) => 32L + values.Count * 32L;
        static long Location(SourceLocation location) => 192L + Text(location.Sheet) + Text(location.CellRange) +
            Text(location.StructurePath) + Text(location.EmailPart) + Text(location.LayoutWarning);
        var size = 4096L + Text(response.ActualMode) + Text(response.StopReason) + Text(response.SemanticPolicyKey) +
            Text(response.NextCursor) + response.Warnings.Sum(warning => 96L + Text(warning.Code) + Text(warning.Message)) +
            response.SuppressedSources.Sum(source => 128L + Text(source.SourcePath) + Text(source.FileName)) +
            32L + response.Results.Count * 8L;
        foreach (var page in pages)
        {
            size += 128L + page.Count * 16L;
            foreach (var group in page)
            {
                size += 1024L + Text(group.SourcePath) + Text(group.FileName) + Text(group.ContentName) + Text(group.Title) +
                    Text(group.RootExtension) + Text(group.ContentExtension) + Text(group.ContentMimeType) +
                    Strings(group.AttachmentChain) + Strings(group.MatchedClauseIds) + Guids(group.EvidencePassageIds) +
                    Guids(group.CompactEvidencePassageIds) + 32L + group.CompactClauseEvidence.Count * 8L +
                    group.CompactClauseEvidence.Sum(evidence => 128L + Text(evidence.ClauseId) + Guids(evidence.PassageIds) +
                        32L + evidence.Fields.Count * 8L) +
                    32L + group.ClauseEvidence.Count * 8L + group.ClauseEvidence.Sum(evidence =>
                        128L + Text(evidence.ClauseId) + Guids(evidence.PassageIds) + 32L + evidence.Fields.Count * 8L) +
                    32L + group.Previews.Count * 8L;
                foreach (var preview in group.Previews)
                    size += 1024L + Text(preview.Excerpt) + Text(preview.SourcePath) + Text(preview.FileName) +
                        Text(preview.FileType) + Location(preview.Location) + Strings(preview.AttachmentChain) +
                        Strings(preview.MatchedClauseIds) + Guids(preview.EvidencePassageIds) +
                        32L + preview.MatchedFields.Count * 8L + 32L + preview.FieldMatches.Count * 8L +
                        preview.FieldMatches.Sum(field => 128L + Text(field.ClauseId) + Text(field.Text)) +
                        32L + preview.MatchSpans.Count * 8L + preview.MatchSpans.Sum(span => 64L + Text(span.ClauseId));
            }
        }
        return size;
    }

    internal sealed record Continuation(SearchResponse Response, SearchDetail Detail, long Generation, string? PolicyKey);
    private sealed class Session(Guid projectId, SearchDetail detail, SearchResponse response,
        IReadOnlyList<IReadOnlyList<SearchResultGroup>> pages, long generation, string? policyKey,
        DateTimeOffset expiresUtc, DateTimeOffset lastAccessUtc, long size)
    {
        public Guid ProjectId { get; } = projectId;
        public SearchDetail Detail { get; } = detail;
        public SearchResponse Response { get; } = response;
        public IReadOnlyList<IReadOnlyList<SearchResultGroup>> Pages { get; } = pages;
        public long Generation { get; } = generation;
        public string? PolicyKey { get; } = policyKey;
        public DateTimeOffset ExpiresUtc { get; } = expiresUtc;
        public DateTimeOffset LastAccessUtc { get; set; } = lastAccessUtc;
        public long Size { get; } = size;
    }
}
