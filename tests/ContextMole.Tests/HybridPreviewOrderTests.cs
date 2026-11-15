using ContextMole.Core;

namespace ContextMole.Tests;

public sealed partial class HybridSearchTests
{
    [Fact]
    public async Task HybridPreviewsFollowTheFusedScoreWhileKeywordPreviewsFollowClauseCoverage()
    {
        var token = TestContext.Current.CancellationToken;
        var documentId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        SearchCandidate Passage(string text, int ordinal) => Candidate(Guid.NewGuid(), text) with
        {
            DocumentId = documentId, ContentId = contentId, Ordinal = ordinal, BodySearchText = text,
            Location = new SourceLocation(LocationKind.Page, Page: ordinal + 1)
        };
        // One file: a long passage that repeats many query words but says little, and the passage that
        // the vectors rank first. Every query word is an optional clause, as a caller passing a natural
        // question as terms would send.
        var wordy = Passage("alpha beta gamma", 0);
        var middle = Passage("alpha beta", 1);
        var relevant = Passage("alpha", 2);
        var candidates = new[] { wordy, middle, relevant };
        var store = new SearchStoreFake(candidates, new VectorSnapshot(31, Policy,
            [VectorEntry(relevant, 0.9f), VectorEntry(middle, 0.5f), VectorEntry(wordy, 0.1f)]), candidates);
        SearchClause[] clauses = [new("alpha", "alpha"), new("beta", "beta"), new("gamma", "gamma")];
        var options = new SearchResultOptions(PreviewsPerGroup: 3);
        var search = CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)));

        var hybrid = Assert.Single((await search.SearchAsync(new SearchRequest(Guid.NewGuid(), SearchMode.Hybrid,
            "the passage that answers the question", clauses, BranchWeights: new SearchBranchWeights(1, 2),
            ResultOptions: options), token)).Results);
        Assert.Equal([relevant.PassageId, middle.PassageId, wordy.PassageId],
            hybrid.Previews.Select(preview => preview.PassageId));
        Assert.Equal(relevant.PassageId, hybrid.SemanticAnchorPassageId);
        Assert.True(hybrid.Previews[0].FusedScore >= hybrid.Previews[1].FusedScore);
        Assert.True(hybrid.Previews[1].FusedScore >= hybrid.Previews[2].FusedScore);

        var keyword = Assert.Single((await search.SearchAsync(new SearchRequest(Guid.NewGuid(), SearchMode.Keyword,
            Clauses: clauses, ResultOptions: options), token)).Results);
        Assert.Equal([wordy.PassageId, middle.PassageId, relevant.PassageId],
            keyword.Previews.Select(preview => preview.PassageId));
    }
}
