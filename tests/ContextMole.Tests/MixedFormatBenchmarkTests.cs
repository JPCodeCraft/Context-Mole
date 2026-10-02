using ContextMole.Benchmarks;
using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Indexing;
using ContextMole.Infrastructure;
using ContextMole.Search;
using ContextMole.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextMole.Tests;

public sealed class MixedFormatFixtureTests
{
    internal static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "benchmarks", "mixed-formats", "manifest.json");
    [Fact]
    public async Task FrozenNativeCorpusPreservesIndependentGoldFactsAndLocations()
    {
        var token = TestContext.Current.CancellationToken;
        var manifest = await MixedFormatManifest.LoadAsync(ManifestPath, token);
        await MixedFormatManifest.VerifyFixturesAsync(ManifestPath, manifest.Fixtures, token);
        var extractor = new DocumentExtractionRegistry(new NoMixedOcr());
        foreach (var fixture in manifest.Fixtures.Where(value => !value.RequiresOcr))
        {
            var result = await extractor.ExtractAsync(new ExtractionRequest(MixedFormatManifest.ResolvePath(ManifestPath, fixture)), token);
            Assert.Empty(result.Errors);
            if (fixture.Id == "duplicate_eml") Assert.Equal(2, result.Root.Attachments.Count(value => value.Name == "status.txt"));
            foreach (var fact in manifest.Facts.Where(value => value.Root == fixture.Id))
            {
                var matching = MixedFormatEvidence.Sections(result.Root).Where(value => value.Chain.SequenceEqual(fact.Chain) &&
                    MixedFormatEvidence.MatchesLocation(value.Section.Location, fact.Location)).ToArray();
                Assert.NotEmpty(matching);
                Assert.True(MixedFormatEvidence.AnchorCoverage(string.Join('\n', matching.Select(value => value.Section.Text)), fact) == 1,
                    $"{fact.Id}: gold fact missing at independently specified source location.");
                if (fact.Heading is not null) Assert.Contains(matching, value => value.Section.Heading == fact.Heading);
            }
        }
    }
    [Fact]
    public void GoldLocationMatchingCannotBeSatisfiedByAnotherSheetOrEmailParent()
    {
        var fact = new MixedFormatFact("f", "mail", ["annex.xlsx"], ["73"], new SourceLocation(LocationKind.Sheet, Sheet: "Stock", CellRange: "A2:C2"));
        Assert.True(MixedFormatEvidence.MatchesSource("mail", ["annex.xlsx"], fact.Location, fact));
        Assert.False(MixedFormatEvidence.MatchesSource("mail", [], fact.Location, fact));
        Assert.False(MixedFormatEvidence.MatchesSource("mail", ["annex.xlsx"], fact.Location with { Sheet = "Other" }, fact));
        Assert.False(MixedFormatEvidence.MatchesSource("mail", ["annex.xlsx"], fact.Location with { CellRange = "A3:C3" }, fact));
        var paragraph = new SourceLocation(LocationKind.Structure, StructurePath: "document/paragraph[2]");
        Assert.True(MixedFormatEvidence.MatchesLocation(paragraph with { StructurePath = "document/paragraph[1]..document/paragraph[3]" }, paragraph));
        Assert.False(MixedFormatEvidence.MatchesLocation(paragraph with { StructurePath = "document/paragraph[3]..document/paragraph[5]" }, paragraph));
        Assert.False(MixedFormatEvidence.MatchesLocation(paragraph with { StructurePath = "document/table[1]..document/table[3]" }, paragraph));

    }
    [Theory]
    [InlineData("48", "Maré sensor 148", 0)]
    [InlineData("48", "Maré sensor 48", 1)]
    [InlineData("48", "Maré sensor -48", 0)]
    [InlineData("48", "Maré sensor 48.5", 0)]
    [InlineData("1250", "B2: 91250", 0)]
    [InlineData("AX-204", "cabinet AX-2049", 0)]
    [InlineData("Inês", "owner Inêss", 0)]
    [InlineData("Inês", "owner: Inês Gonçalves", 1)]
    public void LiteralGoldAnchorsRejectNumericAndIdentifierPrefixCorruption(string anchor, string text, double expected)
    {
        var fact = new MixedFormatFact("f", "root", [], [anchor], new SourceLocation(LocationKind.Document));
        Assert.Equal(expected, MixedFormatEvidence.AnchorCoverage(text, fact));
    }
    [Fact]
    public void PublicDefaultReturnsTwoBoundedAnchorPreviews()
    {
        Assert.Equal(2, new SearchResultOptions().PreviewsPerGroup);
        Assert.Equal(2, new McpSearchResultOptions().ToDomain().PreviewsPerGroup);
    }
    private sealed class NoMixedOcr : IOcrEngine
    {
        public bool IsAvailable => false; public string UnavailableReason => "Native fixture unexpectedly requested OCR.";
        public Task EnsureAvailableAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken token) => throw new InvalidOperationException(UnavailableReason);
    }
}

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class MixedFormatPipelineTests
{
    [Fact]
    public async Task ActualIngestionKeywordSearchAndReadPreserveMixedSourcesAndAttachmentScope()
    {
        var token = TestContext.Current.CancellationToken;
        var manifest = await MixedFormatManifest.LoadAsync(MixedFormatFixtureTests.ManifestPath, token);
        await using var database = await StorageTestDatabase.CreateAsync(token);
        var fixtures = manifest.Fixtures.Where(value => !value.RequiresOcr).ToArray();
        var roots = new Dictionary<string, string>();
        foreach (var fixture in fixtures)
        {
            var path = Path.Combine(database.Paths.SourceDirectory, Path.GetFileName(fixture.File));
            File.Copy(MixedFormatManifest.ResolvePath(MixedFormatFixtureTests.ManifestPath, fixture), path);
            roots.Add(Path.GetFullPath(path), fixture.Id);
        }
        var (project, _) = await database.CreateProjectAsync("Mixed-format end-to-end regression", token);
        await using var embeddings = new StorageUnavailableEmbeddings();
        using var cpu = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new DocumentExtractionRegistry(new StorageNoOcr()), embeddings, new IndexingActivityTracker(),
            new EmbeddingPolicyRefreshTracker(), cpu, NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
            while (true)
            {
                var summary = (await database.Store.ListProjectsAsync(timeout.Token)).Single(value => value.Id == project);
                if (summary.DocumentCount == fixtures.Length && summary.PendingCount == 0) break;
                await Task.Delay(100, timeout.Token);
            }
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
        var facts = manifest.Facts.Where(value => fixtures.Any(fixture => fixture.Id == value.Root)).ToDictionary(value => value.Id);
        var search = new HybridSearchService(database.Store, embeddings, new FlatVectorIndexFactory(), new VectorIndexCache(32 * 1024 * 1024), cpu);
        var distinctDuplicateIds = new Dictionary<string, HashSet<Guid>>();
        // Fixed predeclared cases include both development and holdout. No gold is inferred from extraction.
        foreach (var query in manifest.Queries.Where(value => value.Relevant.All(facts.ContainsKey)))
        {
            var response = await search.SearchAsync(new SearchRequest(project, SearchMode.Keyword,
                Clauses: query.Terms.Select((term, index) => new SearchClause("t" + index, term, SearchClauseOccur.Must)).ToArray(),
                Filters: new SearchFilters(AttachmentScope: query.Scope switch { "root_only" => AttachmentScope.RootOnly, "attachments_only" => AttachmentScope.AttachmentsOnly, _ => AttachmentScope.Any }),
                ResultOptions: new SearchResultOptions(GroupLimit: 20, PreviewsPerGroup: 3, MaxGroupsPerDocument: 10)), token);
            if (query.Negative) { Assert.Empty(response.Results); continue; }
            var previewIds = response.Results.SelectMany(value => value.EvidencePassageIds.Concat(value.Previews.Select(preview => preview.PassageId))).Distinct().ToArray();
            var reads = new List<PassageInfo>();
            foreach (var batch in previewIds.Chunk(50)) reads.AddRange(await database.Store.ReadPassagesAsync(project, batch, 0, 0, response.SearchGeneration, token));
            foreach (var preview in response.Results.SelectMany(value => value.Previews))
            {
                var read = Assert.Single(reads, value => value.PassageId == preview.PassageId);
                Assert.Equal(preview.Excerpt, read.Text.Substring(preview.ExcerptStart, preview.ExcerptLength));
                Assert.Equal(preview.Location, read.Location); Assert.Equal(preview.AttachmentChain, read.AttachmentChain);
                Assert.Equal(preview.SourcePath, read.SourcePath);
            }
            foreach (var id in query.Relevant)
            {
                var fact = facts[id];
                var matching = reads.Where(value => MixedFormatEvidence.MatchesSource(roots[Path.GetFullPath(value.SourcePath)], value.AttachmentChain, value.Location, fact)).ToArray();
                Assert.True(matching.Length > 0, $"{query.Id}: correct gold source/location not retrieved for {id}.");
                if (id is "duplicate_first" or "duplicate_second")
                    distinctDuplicateIds[id] = matching.Where(value => MixedFormatEvidence.AnchorCoverage(value.Text, fact) == 1).Select(value => value.ContentId).ToHashSet();
                Assert.True(MixedFormatEvidence.AnchorCoverage(string.Join('\n', matching.Select(value => value.Text)), fact) == 1,
                    $"{query.Id}: full readable evidence lacks the independently authored answer {id}.");
            }
        }
        Assert.Single(distinctDuplicateIds["duplicate_first"]);
        Assert.Single(distinctDuplicateIds["duplicate_second"]);
        Assert.Empty(distinctDuplicateIds["duplicate_first"].Intersect(distinctDuplicateIds["duplicate_second"]));
    }
}
