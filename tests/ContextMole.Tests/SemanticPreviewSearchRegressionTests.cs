using System.Text.Json;
using ContextMole.Broker.Protocol;
using ContextMole.Core;

namespace ContextMole.Tests;

public sealed partial class HybridSearchTests
{
    [Fact]
    public async Task OptionalSemanticAnchorsChangeOnlyLiteralPreviewWindows()
    {
        var text = string.Concat(Enumerable.Repeat("An ordinary observation is recorded. ", 35)) +
            "The ZXTR category accounts for 9% of jobs. " +
            string.Concat(Enumerable.Repeat("Supporting details remain available. ", 20));
        var first = Candidate(Guid.NewGuid(), text) with { Location = new SourceLocation(LocationKind.Page, Page: 3) };
        var second = Candidate(Guid.NewGuid(), "A brief unrelated passage.");
        var candidates = new[] { first, second };
        var store = new SearchStoreFake([], new VectorSnapshot(27, Policy,
            [VectorEntry(first, 1), VectorEntry(second, 0.8f)]), candidates);
        var request = new SearchRequest(Guid.NewGuid(), SearchMode.Semantic, "What is the percentage for ZXTR?");
        var baseline = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)))
            .SearchAsync(request, TestContext.Current.CancellationToken);
        var anchored = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)), anchorSemanticPreviews: true)
            .SearchAsync(request, TestContext.Current.CancellationToken);
        var oldPreview = baseline.Results[0].Previews[0];
        var preview = anchored.Results[0].Previews[0];
        Assert.Equal(0, oldPreview.ExcerptStart);
        Assert.DoesNotContain("9%", oldPreview.Excerpt, StringComparison.Ordinal);
        Assert.Contains("ZXTR category accounts for 9%", preview.Excerpt, StringComparison.Ordinal);
        Assert.Equal(text.Substring(preview.ExcerptStart, preview.ExcerptLength), preview.Excerpt);
        Assert.InRange(preview.ExcerptLength, 1, 800);
        Assert.Empty(preview.MatchSpans);
        Assert.Empty(preview.MatchedClauseIds);
        Assert.Empty(preview.MatchedFields);
        Assert.Equal([first.PassageId], preview.EvidencePassageIds);
        var normalized = anchored with
        {
            Results = NormalizeGroups(anchored.Results, baseline.Results),
            RankedGroups = NormalizeGroups(anchored.RankedGroups, baseline.RankedGroups)
        };
        Assert.Equal(JsonSerializer.Serialize(baseline, BrokerJson.Options), JsonSerializer.Serialize(normalized, BrokerJson.Options));
        Assert.Equal(JsonSerializer.Serialize(baseline.RankedGroups, BrokerJson.Options),
            JsonSerializer.Serialize(normalized.RankedGroups, BrokerJson.Options));
        Assert.Equal(baseline.SemanticPolicyKey, normalized.SemanticPolicyKey);
        for (var index = 0; index < baseline.RankedGroups.Count; index++)
        {
            Assert.Equal(baseline.RankedGroups[index].CompactEvidencePassageIds, normalized.RankedGroups[index].CompactEvidencePassageIds);
            Assert.Equal(baseline.RankedGroups[index].CompactClauseEvidence, normalized.RankedGroups[index].CompactClauseEvidence);
        }

        static SearchResultGroup[] NormalizeGroups(IReadOnlyList<SearchResultGroup> actual, IReadOnlyList<SearchResultGroup> expected) =>
            actual.Select((group, index) => group with
            {
                Previews = group.Previews.Select((item, previewIndex) => item with
                {
                    Excerpt = expected[index].Previews[previewIndex].Excerpt,
                    ExcerptStart = expected[index].Previews[previewIndex].ExcerptStart,
                    ExcerptLength = expected[index].Previews[previewIndex].ExcerptLength,
                    Truncated = expected[index].Previews[previewIndex].Truncated
                }).ToArray()
            }).ToArray();
    }

    [Theory]
    [InlineData("What percentage of current jobs?", "current jobs")]
    [InlineData("What percentage for ZXTR?", "no literal acronym here")]
    public async Task OptionalSemanticAnchorsPreserveCompleteResponseOnFallback(string query, string tail)
    {
        var candidate = Candidate(Guid.NewGuid(), new string('x', 1100) + " " + tail);
        var store = new SearchStoreFake([], new VectorSnapshot(28, Policy, [VectorEntry(candidate, 1)]), [candidate]);
        var request = new SearchRequest(Guid.NewGuid(), SearchMode.Semantic, query);
        var baseline = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)))
            .SearchAsync(request, TestContext.Current.CancellationToken);
        var anchored = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)), anchorSemanticPreviews: true)
            .SearchAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(JsonSerializer.Serialize(baseline, BrokerJson.Options), JsonSerializer.Serialize(anchored, BrokerJson.Options));
    }

    [Fact]
    public async Task ExplicitBodyEvidenceTakesPriorityOverOptionalSemanticAnchors()
    {
        var candidate = Candidate(Guid.NewGuid(), "required literal evidence " + new string('x', 1400) + " ZXTR 9%");
        var store = new SearchStoreFake([candidate], new VectorSnapshot(29, Policy, [VectorEntry(candidate, 1)]), [candidate]);
        var request = new SearchRequest(Guid.NewGuid(), SearchMode.Hybrid, "What is the percentage for ZXTR?",
            [new SearchClause("required", "required", Fields: [SearchField.Body])]);
        var baseline = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)))
            .SearchAsync(request, TestContext.Current.CancellationToken);
        var anchored = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)), anchorSemanticPreviews: true)
            .SearchAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(JsonSerializer.Serialize(baseline, BrokerJson.Options), JsonSerializer.Serialize(anchored, BrokerJson.Options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalSemanticAnchorWindowsRespectUtf16AndKeepTheAnchorWhenTrimmingSentences(bool splitEnd)
    {
        // The centered start lands on the low surrogate of the first emoji.
        var prefix = new string('x', 899) + "😀" + new string('x', 398) + " ";
        var text = splitEnd
            ? new string('x', 1200) + " ZXTR has a 9% share. " + new string('y', 378) + "😀" + new string('z', 800)
            : prefix + "ZXTR has a 9% share.\n" + new string('y', 800) + "😀";
        var candidate = Candidate(Guid.NewGuid(), text);
        var store = new SearchStoreFake([], new VectorSnapshot(30, Policy, [VectorEntry(candidate, 1)]), [candidate]);
        var response = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)), anchorSemanticPreviews: true)
            .SearchAsync(new SearchRequest(Guid.NewGuid(), SearchMode.Semantic, "What share is ZXTR?"),
                TestContext.Current.CancellationToken);
        var preview = Assert.Single(Assert.Single(response.Results).Previews);
        Assert.Contains("ZXTR has a 9% share", preview.Excerpt, StringComparison.Ordinal);
        Assert.Equal(text.Substring(preview.ExcerptStart, preview.ExcerptLength), preview.Excerpt);
        Assert.False(char.IsLowSurrogate(preview.Excerpt[0]));
        Assert.False(char.IsHighSurrogate(preview.Excerpt[^1]));
        Assert.InRange(preview.ExcerptLength, 1, 800);
        if (splitEnd) Assert.Equal(799, preview.ExcerptLength);
        else Assert.Equal(899, preview.ExcerptStart);
        Assert.Empty(preview.MatchSpans);
    }

    [Fact]
    public async Task OptionalSemanticAnchorsCannotBypassExclusionsOrMetadataRequirements()
    {
        var accepted = Candidate(Guid.NewGuid(), new string('x', 1200) + " ZXTR has a 9% share.") with { Title = "Approved" };
        var excluded = Candidate(Guid.NewGuid(), "Restricted " + accepted.DisplayText) with { Title = "Approved" };
        var missingMetadata = Candidate(Guid.NewGuid(), accepted.DisplayText) with { Title = "Draft" };
        var candidates = new[] { excluded, missingMetadata, accepted };
        var store = new SearchStoreFake([], new VectorSnapshot(31, Policy,
            candidates.Select((candidate, index) => VectorEntry(candidate, 1f - index * 0.1f)).ToArray()), candidates);
        var request = new SearchRequest(Guid.NewGuid(), SearchMode.Semantic, "What percentage applies to ZXTR?",
            [new("approved", "approved", SearchClauseOccur.Must, Fields: [SearchField.Title]),
             new("exclude", "restricted", SearchClauseOccur.MustNot, Fields: [SearchField.Body])]);
        var baseline = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)))
            .SearchAsync(request, TestContext.Current.CancellationToken);
        var anchored = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)), anchorSemanticPreviews: true)
            .SearchAsync(request, TestContext.Current.CancellationToken);
        var preview = Assert.Single(Assert.Single(anchored.Results).Previews);
        var original = Assert.Single(Assert.Single(baseline.Results).Previews);
        Assert.Equal(accepted.PassageId, preview.PassageId);
        Assert.Equal(baseline.Branches, anchored.Branches);
        Assert.Equal(1, anchored.Branches.Semantic.MatchedCandidates);
        Assert.Contains("ZXTR has a 9% share", preview.Excerpt, StringComparison.Ordinal);
        Assert.Equal(["approved"], preview.MatchedClauseIds);
        Assert.Equal([SearchField.Title], preview.MatchedFields);
        Assert.Equal(original.FieldMatches, preview.FieldMatches);
        Assert.Empty(preview.MatchSpans);
        Assert.Equal(original.SemanticRank, preview.SemanticRank);
        Assert.Equal(original.FusedScore, preview.FusedScore);
    }

}
