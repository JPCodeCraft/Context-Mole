using ContextMole.Core;
using ContextMole.Search;

namespace ContextMole.Tests;

public sealed class SearchEvaluationRegressionTests
{
    [Fact]
    public void PreparedQueryPreservesCanonicalTokensFieldsAndPhraseOffsets()
    {
        var candidate = Candidate("CAFÉ re-\nentry ＡＢＣ alpha_beta ½ notice period applies") with
        {
            Title = "Renewal BUDGET",
            Heading = "Annual forecast",
            SectionText = "CAFÉ re-\nentry ＡＢＣ alpha_beta ½ notice period applies"
        };
        SearchClause[] clauses =
        [
            new("accent", "cafe", SearchClauseOccur.Must, Fields: [SearchField.Body]),
            new("hyphen", "reentry", SearchClauseOccur.Must, Fields: [SearchField.Body]),
            new("compatibility", "ABC", SearchClauseOccur.Should, Fields: [SearchField.Body]),
            new("identifier", "alpha_beta", SearchClauseOccur.Should, Fields: [SearchField.Body]),
            new("fraction", "1 2", SearchClauseOccur.Should, SearchMatchKind.Phrase, [SearchField.Body]),
            new("phrase", "notice period", SearchClauseOccur.Must, SearchMatchKind.Phrase, [SearchField.Body]),
            new("prefix", "bud", SearchClauseOccur.Must, SearchMatchKind.Prefix, [SearchField.Title, SearchField.Title]),
            new("heading", "forecast", SearchClauseOccur.Should, Fields: [SearchField.Heading]),
            new("exclude", "obsolete", SearchClauseOccur.MustNot)
        ];
        var query = StructuredSearchQuery.Prepare(clauses);
        var result = query.Evaluate(candidate, 4);
        Assert.True(result.IsMatch);
        Assert.Equal(clauses.Take(8).Select(clause => clause.Id), result.MatchedClauseIds);
        Assert.Equal([SearchField.Body, SearchField.Title, SearchField.Heading], result.MatchedFields);
        var spans = query.FindBodyMatches(candidate.DisplayText);
        Assert.Equal("re-\nentry", SourceSpan("hyphen"));
        Assert.Equal("½", SourceSpan("fraction"));
        Assert.Equal("notice period", SourceSpan("phrase"));
        var fields = query.FindFieldMatches(candidate);
        Assert.Equal(2, fields.Count);
        Assert.Equal("BUDGET", candidate.Title.Substring(fields[0].Start, fields[0].Length));
        Assert.Equal(SearchField.Title, fields[0].Field);
        Assert.Equal(SearchField.Heading, fields[1].Field);

        string SourceSpan(string id)
        {
            var span = Assert.Single(spans, span => span.ClauseId == id);
            return candidate.DisplayText.Substring(span.Start, span.Length);
        }
    }

    [Fact]
    public void CachedSectionMatchingStillEvaluatesPassageSpecificMetadata()
    {
        var sectionText = "required evidence and supporting context";
        var first = Candidate("required evidence") with { SectionId = Guid.NewGuid(), SectionText = sectionText,
            Location = new SourceLocation(LocationKind.Sheet, Sheet: "Approved"), Title = "Current guidance" };
        var second = first with { PassageId = Guid.NewGuid(), Location = first.Location with { Sheet = "Draft" } };
        var query = StructuredSearchQuery.Prepare(
        [
            new("body", "required", SearchClauseOccur.Must, Fields: [SearchField.Body]),
            new("sheet", "draft", SearchClauseOccur.MustNot, Fields: [SearchField.Sheet])
        ]);
        Assert.True(query.Evaluate(first, 0).IsMatch);
        Assert.False(query.Evaluate(second, 0).IsMatch);
        Assert.True(query.Evaluate(first, 0).IsMatch);
        var newRequest = StructuredSearchQuery.Prepare(
            [new("different", "absent", SearchClauseOccur.Must, Fields: [SearchField.Body])]);
        Assert.False(newRequest.Evaluate(first, 0).IsMatch);
    }

    [Fact]
    public void SharedLargeSectionDoesNotRetokenizeForEveryCandidate()
    {
        var text = string.Join(' ', Enumerable.Repeat("ordinary supporting context", 10_000)) + " required needle";
        var candidate = Candidate("required needle") with { SectionId = Guid.NewGuid(), SectionText = text };
        var candidates = Enumerable.Range(0, 200).Select(index => candidate with
            { PassageId = Guid.NewGuid(), Ordinal = index }).ToArray();
        var query = StructuredSearchQuery.Prepare(
        [
            new("required", "required", SearchClauseOccur.Must, Fields: [SearchField.Body]),
            new("optional", "needle", SearchClauseOccur.Should, Fields: [SearchField.Body]),
            new("excluded", "obsolete", SearchClauseOccur.MustNot, Fields: [SearchField.Body])
        ]);
        Assert.True(query.Evaluate(candidate, 1).IsMatch);
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var member in candidates) Assert.True(query.Evaluate(member, 1).IsMatch);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Deliberately broad allocation bound, not a machine-dependent latency assertion.
        // Retokenizing this shared 270 KB section for each vector allocates several GiB.
        Assert.InRange(allocated, 0, 4 * 1024 * 1024);
    }

    [Fact]
    public void QueriesWithoutPositiveBodyClausesDoNotTokenizeBodyEvidence()
    {
        var text = string.Join(' ', Enumerable.Repeat("ordinary supporting context", 10_000));
        var candidate = Candidate(text) with { Title = "Budget" };
        var semantic = StructuredSearchQuery.Prepare([]);
        var metadata = StructuredSearchQuery.Prepare(
            [new("title", "budget", SearchClauseOccur.Must, Fields: [SearchField.Title]),
             new("exclude", "obsolete", SearchClauseOccur.MustNot, Fields: [SearchField.Body])]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Empty(semantic.FindBodyMatches(text));
        Assert.Empty(metadata.FindBodyMatches(text));
        Assert.Empty(semantic.FindFieldMatches(candidate));
        Assert.Equal(SearchField.Title, Assert.Single(metadata.FindFieldMatches(candidate)).Field);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 128 * 1024);
    }

    private static SearchCandidate Candidate(string text) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), text,
        "/documents/example.txt", "example.txt", ".txt", DateTimeOffset.UnixEpoch,
        new SourceLocation(LocationKind.Document), [], ExtractionMethod.NativeText, null);
}
