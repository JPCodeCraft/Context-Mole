using System.Text.RegularExpressions;

using ContextMole.Core;

namespace ContextMole.Search;

public static partial class StructuredSearchQuery
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
        int minimumShouldMatch)
    {
        if (clauses.Count == 0)
            return new ClauseEvaluation(true, [], []);

        var matchedIds = new List<string>();
        var matchedFields = new HashSet<SearchField>();
        var shouldMatches = 0;

        foreach (var clause in clauses)
        {
            var fields = MatchFields(candidate, clause);
            var matched = fields.Count > 0;
            if (clause.Occur == SearchClauseOccur.Must && !matched ||
                clause.Occur == SearchClauseOccur.MustNot && matched)
                return new ClauseEvaluation(false, [], []);

            if (!matched || clause.Occur == SearchClauseOccur.MustNot) continue;
            matchedIds.Add(clause.Id);
            matchedFields.UnionWith(fields);
            if (clause.Occur == SearchClauseOccur.Should) shouldMatches++;
        }

        return shouldMatches < minimumShouldMatch
            ? new ClauseEvaluation(false, [], [])
            : new ClauseEvaluation(true, matchedIds, matchedFields.Order().ToArray());
    }

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

    public static IReadOnlyList<SearchMatchSpan> FindBodyMatches(string text, IReadOnlyList<SearchClause> clauses)
    {
        var tokens = LexicalText.TokenizeWithOffsets(text);
        var spans = new List<SearchMatchSpan>();
        foreach (var clause in clauses.Where(clause => clause.Occur != SearchClauseOccur.MustNot &&
            (clause.Fields is not { Count: > 0 } || clause.Fields.Contains(SearchField.Body))))
        {
            var query = Tokens(clause.Text);
            for (var index = 0; index < tokens.Count; index++)
            {
                var matched = clause.Match switch
                {
                    SearchMatchKind.Term => tokens[index].Value == query[0],
                    SearchMatchKind.Prefix => tokens[index].Value.StartsWith(query[0], StringComparison.Ordinal),
                    SearchMatchKind.Phrase => index + query.Count <= tokens.Count &&
                        query.Select((value, offset) => value == tokens[index + offset].Value).All(value => value),
                    _ => false
                };
                if (!matched) continue;
                var last = clause.Match == SearchMatchKind.Phrase ? tokens[index + query.Count - 1] : tokens[index];
                spans.Add(new SearchMatchSpan(clause.Id, tokens[index].Start, last.Start + last.Length - tokens[index].Start));
            }
        }
        return spans;
    }

    public static IReadOnlyList<SearchFieldMatch> FindFieldMatches(SearchCandidate candidate, IReadOnlyList<SearchClause> clauses)
    {
        var result = new List<SearchFieldMatch>();
        foreach (var clause in clauses.Where(clause => clause.Occur != SearchClauseOccur.MustNot))
        foreach (var field in MatchFields(candidate, clause).Where(field => field != SearchField.Body))
        {
            var text = FieldValue(candidate, field) ?? string.Empty;
            var bodyClause = clause with { Fields = [SearchField.Body] };
            var span = FindBodyMatches(text, [bodyClause]).FirstOrDefault();
            if (span is null) continue;
            var start = Math.Max(0, span.Start - 60);
            var end = Math.Min(text.Length, Math.Max(start + 160, span.Start + span.Length));
            result.Add(new SearchFieldMatch(clause.Id, field, text[start..end], span.Start, span.Length) { ExcerptStart = start });
        }
        return result;
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

    private static IReadOnlyList<SearchField> MatchFields(SearchCandidate candidate, SearchClause clause)
    {
        var fields = clause.Fields is { Count: > 0 } ? clause.Fields.Distinct() : AllFields;
        var matches = new List<SearchField>();
        foreach (var field in fields)
        {
            var tokens = Tokens(FieldValue(candidate, field));
            var queryTokens = Tokens(clause.Text);
            var matched = clause.Match switch
            {
                SearchMatchKind.Term => tokens.Contains(queryTokens.Single(), StringComparer.Ordinal),
                SearchMatchKind.Prefix => tokens.Any(token => token.StartsWith(queryTokens.Single(), StringComparison.Ordinal)),
                SearchMatchKind.Phrase => ContainsSequence(tokens, queryTokens),
                _ => false
            };
            if (matched) matches.Add(field);
        }
        return matches;
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

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}_]+")]
    private static partial Regex WordTokens();
}

public sealed record ClauseEvaluation(
    bool IsMatch,
    IReadOnlyList<string> MatchedClauseIds,
    IReadOnlyList<SearchField> MatchedFields);
