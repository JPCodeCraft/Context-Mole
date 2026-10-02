using ContextMole.Core;

namespace ContextMole.Search;

public static class StructuredSearchQuery
{
    private static readonly SearchField[] AllFields = Enum.GetValues<SearchField>();

    public static string BuildFtsQuery(IReadOnlyList<SearchClause> clauses, int minimumShouldMatch)
    {
        var must = clauses.Where(clause => clause.Occur == SearchClauseOccur.Must)
            .Select(BuildClauseExpression).ToArray();
        var should = clauses.Where(clause => clause.Occur == SearchClauseOccur.Should)
            .Select(BuildClauseExpression).ToArray();
        var mustNot = clauses.Where(clause => clause.Occur == SearchClauseOccur.MustNot)
            .Select(BuildClauseExpression).ToArray();

        var parts = new List<string>();
        parts.AddRange(must.Select(expression => $"({expression})"));
        if (should.Length > 0 && (must.Length == 0 || minimumShouldMatch > 0))
            parts.Add($"({string.Join(" OR ", should.Select(expression => $"({expression})"))})");
        var query = string.Join(" AND ", parts);
        if (query.Length == 0) return string.Empty;
        foreach (var expression in mustNot)
            query = $"({query}) NOT ({expression})";
        return query;
    }

    public static string BuildOptionalShouldBoostQuery(IReadOnlyList<SearchClause> clauses, int minimumShouldMatch)
    {
        if (minimumShouldMatch > 0 || clauses.All(clause => clause.Occur != SearchClauseOccur.Must))
            return string.Empty;
        var must = clauses.Where(clause => clause.Occur == SearchClauseOccur.Must)
            .Select(BuildClauseExpression).ToArray();
        var should = clauses.Where(clause => clause.Occur == SearchClauseOccur.Should)
            .Select(BuildClauseExpression).ToArray();
        var mustNot = clauses.Where(clause => clause.Occur == SearchClauseOccur.MustNot)
            .Select(BuildClauseExpression).ToArray();
        if (should.Length == 0) return string.Empty;
        var query = $"({string.Join(" AND ", must.Select(expression => $"({expression})"))}) AND " +
                    $"({string.Join(" OR ", should.Select(expression => $"({expression})"))})";
        foreach (var expression in mustNot)
            query = $"({query}) NOT ({expression})";
        return query;
    }

    public static ClauseEvaluation Evaluate(SearchCandidate candidate, IReadOnlyList<SearchClause> clauses,
        int minimumShouldMatch) => Prepare(clauses).Evaluate(candidate, minimumShouldMatch);

    // A prepared query belongs to one request. Keep only small match masks, not token streams,
    // so shared section text is tokenized once even when hundreds of its vectors are retrieved.
    internal static PreparedQuery Prepare(IReadOnlyList<SearchClause> clauses) => new(clauses);

    public static IReadOnlyList<string> Tokens(string? value) => LexicalText.Tokens(value);

    public static string Normalize(string? value) => LexicalText.Normalize(value);

    public static string? FieldValue(SearchCandidate candidate, SearchField field) => field switch
    {
        SearchField.Body => candidate.SectionText ?? candidate.BodySearchText ?? candidate.DisplayText,
        SearchField.Title => candidate.Title,
        SearchField.Heading => candidate.Heading,
        SearchField.Filename => candidate.FileName,
        SearchField.Path => candidate.SourcePath,
        SearchField.ContentName => candidate.ContentName,
        SearchField.Sheet => candidate.Location.Sheet,
        SearchField.EmailSubject => candidate.EmailSubject,
        _ => null
    };

    public static IReadOnlyList<SearchMatchSpan> FindBodyMatches(string text, IReadOnlyList<SearchClause> clauses) =>
        Prepare(clauses).FindBodyMatches(text);

    public static IReadOnlyList<SearchFieldMatch> FindFieldMatches(SearchCandidate candidate,
        IReadOnlyList<SearchClause> clauses) => Prepare(clauses).FindFieldMatches(candidate);

    internal sealed class PreparedQuery
    {
        private readonly PreparedClause[] _clauses;
        private readonly PreparedClause[] _bodyClauses;
        // Section hydration shares the same string instance. Reference keys avoid repeatedly
        // hashing a potentially very large section and do not conflate different metadata.
        private readonly Dictionary<string, bool[]> _matches = new(ReferenceEqualityComparer.Instance);

        public PreparedQuery(IReadOnlyList<SearchClause> clauses)
        {
            _clauses = clauses.Select(clause => new PreparedClause(clause, Tokens(clause.Text),
                clause.Fields is { Count: > 0 } ? clause.Fields.Distinct().ToArray() : AllFields)).ToArray();
            _bodyClauses = _clauses.Where(clause => clause.Clause.Occur != SearchClauseOccur.MustNot &&
                clause.Fields.Contains(SearchField.Body)).ToArray();
        }

        public ClauseEvaluation Evaluate(SearchCandidate candidate, int minimumShouldMatch)
        {
            if (_clauses.Length == 0) return new ClauseEvaluation(true, [], []);
            var matchedIds = new List<string>();
            var matchedFields = new HashSet<SearchField>();
            var shouldMatches = 0;
            for (var index = 0; index < _clauses.Length; index++)
            {
                var clause = _clauses[index];
                var matched = false;
                foreach (var field in clause.Fields)
                {
                    var text = FieldValue(candidate, field);
                    if (string.IsNullOrEmpty(text) || !Matches(text)[index]) continue;
                    matched = true;
                    if (clause.Clause.Occur != SearchClauseOccur.MustNot) matchedFields.Add(field);
                }
                if (clause.Clause.Occur == SearchClauseOccur.Must && !matched ||
                    clause.Clause.Occur == SearchClauseOccur.MustNot && matched)
                    return new ClauseEvaluation(false, [], []);
                if (!matched || clause.Clause.Occur == SearchClauseOccur.MustNot) continue;
                matchedIds.Add(clause.Clause.Id);
                if (clause.Clause.Occur == SearchClauseOccur.Should) shouldMatches++;
            }
            return shouldMatches < minimumShouldMatch
                ? new ClauseEvaluation(false, [], [])
                : new ClauseEvaluation(true, matchedIds, matchedFields.Order().ToArray());
        }

        public IReadOnlyList<SearchMatchSpan> FindBodyMatches(string text)
        {
            // Semantic-only and metadata-only queries have no body spans. In particular, do
            // not tokenize every section and passage merely to return an empty collection.
            if (_bodyClauses.Length == 0 || text.Length == 0) return [];
            var tokens = LexicalText.TokenizeWithOffsets(text);
            var spans = new List<SearchMatchSpan>();
            foreach (var clause in _bodyClauses)
                AddSpans(tokens, clause, spans);
            return spans;
        }

        public IReadOnlyList<SearchFieldMatch> FindFieldMatches(SearchCandidate candidate)
        {
            var result = new List<SearchFieldMatch>();
            var tokensByField = new Dictionary<SearchField, IReadOnlyList<LexicalToken>>();
            foreach (var clause in _clauses)
            {
                if (clause.Clause.Occur == SearchClauseOccur.MustNot) continue;
                foreach (var field in clause.Fields)
                {
                    // Body matching is handled separately with literal passage offsets.
                    if (field == SearchField.Body) continue;
                    var text = FieldValue(candidate, field);
                    if (string.IsNullOrEmpty(text)) continue;
                    if (!tokensByField.TryGetValue(field, out var tokens))
                        tokensByField[field] = tokens = LexicalText.TokenizeWithOffsets(text);
                    var span = FirstSpan(tokens, clause);
                    if (span is null) continue;
                    var start = Math.Max(0, span.Start - 60);
                    var end = Math.Min(text.Length, Math.Max(start + 160, span.Start + span.Length));
                    result.Add(new SearchFieldMatch(clause.Clause.Id, field, text[start..end], span.Start,
                        span.Length) { ExcerptStart = start });
                }
            }
            return result;
        }

        private bool[] Matches(string text)
        {
            if (_matches.TryGetValue(text, out var matches)) return matches;
            var tokens = Tokens(text);
            matches = _clauses.Select(clause => clause.Clause.Match switch
            {
                SearchMatchKind.Term => tokens.Contains(clause.Tokens.Single(), StringComparer.Ordinal),
                SearchMatchKind.Prefix => tokens.Any(token => token.StartsWith(clause.Tokens.Single(), StringComparison.Ordinal)),
                SearchMatchKind.Phrase => ContainsSequence(tokens, clause.Tokens),
                _ => false
            }).ToArray();
            _matches[text] = matches;
            return matches;
        }

        private static void AddSpans(IReadOnlyList<LexicalToken> tokens, PreparedClause clause,
            List<SearchMatchSpan> spans)
        {
            for (var index = 0; index < tokens.Count; index++)
                if (SpanAt(tokens, clause, index) is { } span) spans.Add(span);
        }

        private static SearchMatchSpan? FirstSpan(IReadOnlyList<LexicalToken> tokens, PreparedClause clause)
        {
            for (var index = 0; index < tokens.Count; index++)
                if (SpanAt(tokens, clause, index) is { } span) return span;
            return null;
        }

        private static SearchMatchSpan? SpanAt(IReadOnlyList<LexicalToken> tokens, PreparedClause clause, int index)
        {
            var query = clause.Tokens;
            if (query.Count == 0) return null;
            var matched = clause.Clause.Match switch
            {
                SearchMatchKind.Term => tokens[index].Value == query[0],
                SearchMatchKind.Prefix => tokens[index].Value.StartsWith(query[0], StringComparison.Ordinal),
                SearchMatchKind.Phrase => MatchesSequence(tokens, query, index),
                _ => false
            };
            if (!matched) return null;
            var last = clause.Clause.Match == SearchMatchKind.Phrase ? tokens[index + query.Count - 1] : tokens[index];
            return new SearchMatchSpan(clause.Clause.Id, tokens[index].Start,
                last.Start + last.Length - tokens[index].Start);
        }

        private static bool MatchesSequence(IReadOnlyList<LexicalToken> source, IReadOnlyList<string> query, int start)
        {
            if (start + query.Count > source.Count) return false;
            for (var offset = 0; offset < query.Count; offset++)
                if (!string.Equals(source[start + offset].Value, query[offset], StringComparison.Ordinal)) return false;
            return true;
        }

        private sealed record PreparedClause(SearchClause Clause, IReadOnlyList<string> Tokens, SearchField[] Fields);
    }

    private static string BuildClauseExpression(SearchClause clause)
    {
        var value = BuildValueExpression(clause);
        var fields = clause.Fields is { Count: > 0 } ? clause.Fields.Distinct().ToArray() : AllFields;
        return string.Join(" OR ", fields.Select(field => $"{Column(field)}:{value}"));
    }

    private static string BuildValueExpression(SearchClause clause)
    {
        var tokens = Tokens(clause.Text);
        return clause.Match switch
        {
            SearchMatchKind.Term => Quote(tokens.Single()),
            SearchMatchKind.Prefix => $"{Quote(tokens.Single())}*",
            SearchMatchKind.Phrase => Quote(string.Join(' ', tokens)),
            _ => throw new ContextMoleException("invalid_clause", $"Clause '{clause.Id}' has an invalid match type.")
        };
    }

    private static bool ContainsSequence(IReadOnlyList<string> source, IReadOnlyList<string> query)
    {
        if (query.Count == 0 || source.Count < query.Count) return false;
        for (var start = 0; start <= source.Count - query.Count; start++)
        {
            var matches = true;
            for (var offset = 0; offset < query.Count; offset++)
            {
                if (string.Equals(source[start + offset], query[offset], StringComparison.Ordinal)) continue;
                matches = false;
                break;
            }
            if (matches) return true;
        }
        return false;
    }

    private static string Column(SearchField field) => field switch
    {
        SearchField.Body => "body_text",
        SearchField.Title => "title",
        SearchField.Heading => "heading",
        SearchField.Filename => "filename",
        SearchField.Path => "path",
        SearchField.ContentName => "content_name",
        SearchField.Sheet => "sheet",
        SearchField.EmailSubject => "email_subject",
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

}

public sealed record ClauseEvaluation(
    bool IsMatch,
    IReadOnlyList<string> MatchedClauseIds,
    IReadOnlyList<SearchField> MatchedFields);
