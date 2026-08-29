using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;

using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Search;

namespace ContextMole.Tests;

public sealed class DocumentExtractionTests
{
    [Fact]
    public void RegistryIncludesImportantWebAndContainerFormatsCaseInsensitively()
    {
        var extractor = new DocumentExtractionRegistry(new UnexpectedOcrEngine());

        Assert.Equal(SupportedContent.Extensions.Order(), extractor.Extensions.Order());
        foreach (var fileName in new[] { "page.HTML", "page.HTM", "page.MHT", "page.MHTML", "mail.EML", "archive.ZIP" })
            Assert.True(SupportedContent.IsSupported(fileName), fileName);
    }

    [Theory]
    [InlineData(".html")]
    [InlineData(".htm")]
    [InlineData(".png")]
    public async Task HtmlVariantsAndDisguisedHtmlAreExtractedAsInertText(string extension)
    {
        using var workspace = new TemporaryDirectory();
        var path = workspace.File("page" + extension);
        await File.WriteAllTextAsync(path,
            "<!doctype html><html><head><style>hidden style</style></head><body>Visible HTML evidence." +
            "<script>hidden script</script></body></html>", TestContext.Current.CancellationToken);

        var result = await ExtractAsync(path, TestContext.Current.CancellationToken);
        var text = SectionText(result.Root);

        Assert.Empty(result.Errors);
        Assert.Contains("Visible HTML evidence", text);
        Assert.DoesNotContain("hidden style", text);
        Assert.DoesNotContain("hidden script", text);
        Assert.Equal(ExtractionMethod.Html, Assert.Single(result.Root.Sections).Method);
    }

    [Theory]
    [InlineData(".mht", false)]
    [InlineData(".mhtml", true)]
    public async Task MhtmlVariantsDecodeTheirHtmlBodyAndIgnoreExecutableOrStyleParts(
        string extension,
        bool useBase64)
    {
        using var workspace = new TemporaryDirectory();
        var path = workspace.File("saved-page" + extension);
        await File.WriteAllTextAsync(path, BuildMhtml(useBase64), Encoding.ASCII,
            TestContext.Current.CancellationToken);

        var result = await ExtractAsync(path, TestContext.Current.CancellationToken);
        var text = SectionText(result.Root);

        Assert.Empty(result.Errors);
        Assert.Contains("MHTML searchable evidence — café", text);
        Assert.DoesNotContain("archive script content", text);
        Assert.DoesNotContain("stylesheet content", text);
        Assert.Equal(ExtractionMethod.Html, Assert.Single(result.Root.Sections).Method);
    }

    [Fact]
    public async Task EmlExtractsSupportedAttachmentsAndIsolatesUnsupportedOnes()
    {
        using var workspace = new TemporaryDirectory();
        var path = workspace.File("message.eml");
        await File.WriteAllTextAsync(path, BuildEmail(), Encoding.ASCII, TestContext.Current.CancellationToken);

        var result = await ExtractAsync(path, TestContext.Current.CancellationToken);
        var supported = Assert.Single(result.Root.Attachments, item => item.Name == "notes.txt");
        var unsupported = Assert.Single(result.Root.Attachments, item => item.Name == "drawing.emf");

        Assert.Empty(result.Errors);
        Assert.Contains("Parent message evidence", SectionText(result.Root));
        Assert.Contains("Supported attachment evidence", SectionText(supported));
        Assert.Equal("indexed", supported.Status);
        Assert.Equal("unsupported_format", unsupported.Status);
    }

    [Fact]
    public async Task LegacyWindows1252TextIsDecodedWithoutTreatingEncodingDetectionAsFailure()
    {
        using var workspace = new TemporaryDirectory();
        var path = workspace.File("legacy.txt");
        await File.WriteAllBytesAsync(path,
            [0x52, 0xE9, 0x73, 0x75, 0x6D, 0xE9, 0x20, 0x65, 0x76, 0x69, 0x64, 0x65, 0x6E, 0x63, 0x65],
            TestContext.Current.CancellationToken);

        var result = await ExtractAsync(path, TestContext.Current.CancellationToken);

        Assert.Empty(result.Errors);
        Assert.Contains("Résumé evidence", SectionText(result.Root));
    }

    [Fact]
    public async Task ZipKeepsHealthyEntriesSearchableWhenAnotherEntryIsMalformed()
    {
        using var workspace = new TemporaryDirectory();
        var path = workspace.File("documents.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            await WriteZipEntryAsync(archive, "safe/evidence.txt", Encoding.UTF8.GetBytes("Healthy ZIP evidence."),
                TestContext.Current.CancellationToken);
            await WriteZipEntryAsync(archive, "broken.png", [0x89, 0x50, 0x4e, 0x47, 0x00, 0x01],
                TestContext.Current.CancellationToken);
        }

        var result = await ExtractAsync(path, TestContext.Current.CancellationToken);
        var healthy = Assert.Single(result.Root.Attachments, item => item.Name == "safe/evidence.txt");
        var malformed = Assert.Single(result.Root.Attachments, item => item.Name == "broken.png");

        Assert.Contains("Healthy ZIP evidence", SectionText(healthy));
        Assert.Equal("indexed", result.Root.Status);
        Assert.Equal("malformed_document", malformed.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal("malformed_document", error.Code);
        Assert.Equal("broken.png", error.ItemName);
    }

    private static Task<ExtractionResult> ExtractAsync(string path, CancellationToken cancellationToken) =>
        new DocumentExtractionRegistry(new UnexpectedOcrEngine())
            .ExtractAsync(new ExtractionRequest(path), cancellationToken);

    private static string SectionText(ExtractedNode node) =>
        string.Join('\n', node.Sections.Select(section => section.Text));

    private static async Task WriteZipEntryAsync(ZipArchive archive, string name, byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = archive.CreateEntry(name).Open();
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static string BuildMhtml(bool useBase64)
    {
        const string html = "<!doctype html><html><body><h1>MHTML searchable evidence — café.</h1>" +
                            "<script>archive script content</script></body></html>";
        var encodedHtml = useBase64
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes(html))
            : html.Replace("—", "=E2=80=94", StringComparison.Ordinal)
                .Replace("é", "=C3=A9", StringComparison.Ordinal);
        var transferEncoding = useBase64 ? "base64" : "quoted-printable";

        return ("MIME-Version: 1.0\n" +
                "Subject: Saved web page\n" +
                "Content-Type: multipart/related; boundary=\"mhtml-fixture\"; type=\"text/html\"; start=\"<root-part>\"\n\n" +
                "--mhtml-fixture\n" +
                "Content-Type: text/css; charset=utf-8\n" +
                "Content-ID: <style-part>\n\n" +
                "stylesheet content\n" +
                "--mhtml-fixture\n" +
                "Content-Type: text/html; charset=utf-8\n" +
                $"Content-Transfer-Encoding: {transferEncoding}\n" +
                "Content-ID: <root-part>\n\n" +
                encodedHtml + "\n" +
                "--mhtml-fixture--\n")
            .Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private static string BuildEmail() =>
        ("From: sender@example.test\n" +
         "To: reader@example.test\n" +
         "Subject: Attachment isolation\n" +
         "MIME-Version: 1.0\n" +
         "Content-Type: multipart/mixed; boundary=fixture\n\n" +
         "--fixture\nContent-Type: text/plain; charset=utf-8\n\nParent message evidence.\n" +
         Attachment("text/plain", "notes.txt", Encoding.UTF8.GetBytes("Supported attachment evidence.")) +
         Attachment("image/x-emf", "drawing.emf", [1, 2, 3, 4]) +
         "--fixture--\n")
        .Replace("\n", "\r\n", StringComparison.Ordinal);

    private static string Attachment(string contentType, string name, byte[] bytes) =>
        "--fixture\n" +
        $"Content-Type: {contentType}; name=\"{name}\"\n" +
        $"Content-Disposition: attachment; filename=\"{name}\"\n" +
        "Content-Transfer-Encoding: base64\n\n" +
        Convert.ToBase64String(bytes) + "\n";
}

public sealed class FlatVectorIndexTests
{
    [Fact]
    public void SearchRanksBySimilarityAndHonorsExtensionAndAttachmentFilters()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var third = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var focusedContent = Guid.NewGuid();
        var entries = new[]
        {
            Entry(first, ".txt", isAttachment: false, 0.9f),
            Entry(second, ".msg", isAttachment: true, 0.8f, focusedContent, ".pdf"),
            Entry(third, ".md", isAttachment: false, 0.7f)
        };
        var index = new FlatVectorIndex(new VectorSnapshot(12, TestPolicy, entries));

        var all = index.Search(Vector(1), 10);
        Assert.Equal(new[] { first, second, third }, all.Select(match => match.PassageId));
        Assert.Equal(new[] { 1, 2, 3 }, all.Select(match => match.Rank));

        var filtered = index.Search(Vector(1), 10,
            new SearchFilters(ContentIds: [focusedContent], RootExtensions: ["MSG"], ContentExtensions: ["PDF"],
                AttachmentScope: AttachmentScope.AttachmentsOnly));
        Assert.Equal(second, Assert.Single(filtered).PassageId);
    }

    [Fact]
    public void SearchRejectsQueriesThatDoNotMatchTheStoredVectorDimensions()
    {
        var index = new FlatVectorIndex(new VectorSnapshot(1, TestPolicy,
            [Entry(Guid.NewGuid(), ".txt", isAttachment: false, 1)]));

        var exception = Assert.Throws<ArgumentException>(() => index.Search(new float[383], 1));
        Assert.Equal("query", exception.ParamName);
    }

    private static VectorEntry Entry(Guid passageId, string extension, bool isAttachment, float similarity,
        Guid? contentId = null, string? contentExtension = null) =>
        new(passageId, Guid.NewGuid(), contentId ?? Guid.NewGuid(),
            Path.Combine(Path.GetTempPath(), "vectors", passageId.ToString()), extension, DateTimeOffset.UnixEpoch,
            isAttachment, Vector(similarity), contentExtension);

    private static float[] Vector(float firstValue)
    {
        var vector = new float[384];
        vector[0] = firstValue;
        return vector;
    }

    private static readonly EmbeddingPolicy TestPolicy =
        new("test-model", "1", "model-sha", "tokenizer-sha", "fp32", 384, 384, "cls", "l2");
}

public sealed class HybridSearchTests
{
    [Fact]
    public async Task KeywordSearchMixesClauseTypesAndReportsMatchedFields()
    {
        var accepted = Candidate(Guid.NewGuid(), "The final release plan is ready") with
        {
            Heading = "Budget forecast", BodySearchText = "The final release plan is ready"
        };
        var excluded = Candidate(Guid.NewGuid(), "The draft release plan is ready") with
        {
            Heading = "Budget forecast", BodySearchText = "The draft release plan is ready"
        };
        var store = new SearchStoreFake([accepted, excluded], new VectorSnapshot(4, null, []), [accepted, excluded]);
        var embeddings = new EmbeddingGeneratorFake(Policy, Policy, Vector(1));
        var result = await CreateSearch(store, embeddings).SearchAsync(new SearchRequest(Guid.NewGuid(),
            SearchMode.Keyword, Clauses:
            [
                new SearchClause("required", "release plan", SearchClauseOccur.Must, SearchMatchKind.Phrase,
                    [SearchField.Body]),
                new SearchClause("boost", "bud", SearchClauseOccur.Should, SearchMatchKind.Prefix,
                    [SearchField.Heading]),
                new SearchClause("exclude", "draft", SearchClauseOccur.MustNot, SearchMatchKind.Term,
                    [SearchField.Body])
            ]), TestContext.Current.CancellationToken);

        var preview = Assert.Single(Assert.Single(result.Results).Previews);
        Assert.Equal(accepted.PassageId, preview.PassageId);
        Assert.Equal(["required", "boost"], preview.MatchedClauseIds);
        Assert.Equal([SearchField.Body, SearchField.Heading], preview.MatchedFields);
        Assert.Equal(0, embeddings.QueryCalls);
    }

    [Fact]
    public async Task OptionalShouldClauseBoostsButDoesNotBecomeAFilter()
    {
        var plain = Candidate(Guid.NewGuid(), "anchor evidence") with { BodySearchText = "anchor evidence" };
        var boosted = Candidate(Guid.NewGuid(), "anchor evidence") with
        {
            BodySearchText = "anchor evidence", Heading = "budget"
        };
        var store = new SearchStoreFake([plain, boosted], new VectorSnapshot(5, null, []), [plain, boosted]);
        var result = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1))).SearchAsync(
            new SearchRequest(Guid.NewGuid(), SearchMode.Keyword, Clauses:
            [
                new SearchClause("anchor", "anchor", SearchClauseOccur.Must, Fields: [SearchField.Body]),
                new SearchClause("budget", "budget", SearchClauseOccur.Should, Fields: [SearchField.Heading])
            ]), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Results.Count);
        Assert.Equal(boosted.PassageId, result.Results[0].Previews[0].PassageId);
        Assert.Contains("budget", result.Results[0].Previews[0].MatchedClauseIds);
        Assert.DoesNotContain("budget", result.Results[1].Previews[0].MatchedClauseIds);
        Assert.Equal(2, store.KeywordQueries.Count);
        Assert.Contains("budget", store.KeywordQueries[0], StringComparison.Ordinal);
        Assert.DoesNotContain("budget", store.KeywordQueries[1], StringComparison.Ordinal);
        Assert.Equal(new SearchBranchCandidateDepths(2, 2, 0), result.InspectedCandidateDepths);
        Assert.Equal(2, result.CandidateMatchCount);
    }

    [Fact]
    public async Task HybridCandidateCountsSeparateBranchDepthsAndDeduplicateMatches()
    {
        var first = Candidate(Guid.NewGuid(), "anchor alpha") with { BodySearchText = "anchor alpha" };
        var second = Candidate(Guid.NewGuid(), "anchor beta") with { BodySearchText = "anchor beta" };
        var candidates = new[] { first, second };
        var entries = candidates.Select((candidate, index) => VectorEntry(candidate, 0.9f - index * 0.1f)).ToArray();
        var store = new SearchStoreFake(candidates, new VectorSnapshot(5, Policy, entries), candidates);

        var result = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1))).SearchAsync(
            new SearchRequest(Guid.NewGuid(), SearchMode.Hybrid, "related concept",
                [new SearchClause("anchor", "anchor", SearchClauseOccur.Must, Fields: [SearchField.Body])]),
            TestContext.Current.CancellationToken);

        Assert.Equal(new SearchBranchCandidateDepths(2, 0, 2), result.InspectedCandidateDepths);
        Assert.Equal(2, result.CandidateMatchCount);
    }

    [Fact]
    public async Task OptionalShouldSignalsPromoteDeepMatchesWithoutAutomaticallyBeatingTheStrongestMustMatch()
    {
        var decoys = Enumerable.Range(0, 200).Select(index => Candidate(Guid.NewGuid(), $"anchor {index}") with
        {
            BodySearchText = "anchor"
        }).ToArray();
        var boosted = Candidate(Guid.NewGuid(), "anchor evidence") with
        {
            BodySearchText = "anchor", Heading = "budget"
        };
        var all = decoys.Append(boosted).ToArray();
        var store = new SearchStoreFake(all, new VectorSnapshot(5, null, []), all,
            keywordResolver: query => query.Contains("budget", StringComparison.Ordinal)
                ? [boosted]
                : all);

        var result = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1))).SearchAsync(
            new SearchRequest(Guid.NewGuid(), SearchMode.Keyword, Clauses:
            [
                new SearchClause("anchor", "anchor", SearchClauseOccur.Must, Fields: [SearchField.Body]),
                new SearchClause("budget", "budget", SearchClauseOccur.Should, Fields: [SearchField.Heading])
            ]), TestContext.Current.CancellationToken);

        var previews = result.Results.SelectMany(group => group.Previews).ToArray();
        Assert.NotEqual(boosted.PassageId, previews[0].PassageId);
        var promoted = Assert.Single(previews, preview => preview.PassageId == boosted.PassageId);
        Assert.Contains("budget", promoted.MatchedClauseIds);
    }

    [Fact]
    public async Task ProgressiveKeywordPagingGetsPastMoreThanFiveHundredRejectedDecoys()
    {
        var decoys = Enumerable.Range(0, 700).Select(index => Candidate(Guid.NewGuid(),
            index < 350 ? "anchor red blue forbidden" : "anchor red") with
        {
            BodySearchText = index < 350 ? "anchor red blue forbidden" : "anchor red"
        });
        var valid = Enumerable.Range(0, 10).Select(_ => Candidate(Guid.NewGuid(), "anchor red blue") with
        {
            BodySearchText = "anchor red blue"
        }).ToArray();
        var ordered = decoys.Concat(valid).ToArray();
        var store = new SearchStoreFake(ordered, new VectorSnapshot(13, null, []), ordered);
        var result = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1))).SearchAsync(
            new SearchRequest(Guid.NewGuid(), SearchMode.Keyword, Clauses:
            [
                new SearchClause("anchor", "anchor", SearchClauseOccur.Must, Fields: [SearchField.Body]),
                new SearchClause("red", "red", SearchClauseOccur.Should, Fields: [SearchField.Body]),
                new SearchClause("blue", "blue", SearchClauseOccur.Should, Fields: [SearchField.Body]),
                new SearchClause("forbidden", "forbidden", SearchClauseOccur.MustNot, Fields: [SearchField.Body])
            ], MinimumShouldMatch: 2), TestContext.Current.CancellationToken);

        Assert.Equal(10, result.ReturnedGroupCount);
        Assert.All(result.Results.SelectMany(group => group.Previews),
            preview => Assert.Contains("blue", preview.Excerpt, StringComparison.Ordinal));
        Assert.True(store.KeywordOffsets.Max() > 500);
        Assert.Contains("NOT", store.KeywordQueries[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgressivePagingPreventsOneLargeContentNodeFromStarvingGroups()
    {
        var crowdedDocument = Guid.NewGuid();
        var crowdedContent = Guid.NewGuid();
        var crowded = Enumerable.Range(0, 600).Select(index => Candidate(Guid.NewGuid(), "anchor") with
        {
            DocumentId = crowdedDocument, ContentId = crowdedContent, Ordinal = index, BodySearchText = "anchor"
        });
        var diverse = Enumerable.Range(0, 9).Select(_ => Candidate(Guid.NewGuid(), "anchor") with
        {
            BodySearchText = "anchor"
        });
        var ordered = crowded.Concat(diverse).ToArray();
        var store = new SearchStoreFake(ordered, new VectorSnapshot(14, null, []), ordered);
        var result = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1))).SearchAsync(
            new SearchRequest(Guid.NewGuid(), SearchMode.Keyword,
                Clauses: [new SearchClause("anchor", "anchor", Fields: [SearchField.Body])]),
            TestContext.Current.CancellationToken);

        Assert.Equal(10, result.ReturnedGroupCount);
        Assert.Equal(10, result.Results.Select(group => group.ContentId).Distinct().Count());
        Assert.True(store.KeywordOffsets.Max() > 500);
    }

    [Fact]
    public async Task ProgressiveSemanticDepthPreventsOneLargeContentNodeFromStarvingGroups()
    {
        var crowdedDocument = Guid.NewGuid();
        var crowdedContent = Guid.NewGuid();
        var crowded = Enumerable.Range(0, 600).Select(index => Candidate(Guid.NewGuid(), "crowded") with
        {
            DocumentId = crowdedDocument, ContentId = crowdedContent, Ordinal = index
        }).ToArray();
        var diverse = Enumerable.Range(0, 9).Select(index => Candidate(Guid.NewGuid(), $"diverse {index}"))
            .ToArray();
        var candidates = crowded.Concat(diverse).ToArray();
        var entries = crowded.Select((candidate, index) => VectorEntry(candidate, 1f - index * 0.0001f))
            .Concat(diverse.Select((candidate, index) => VectorEntry(candidate, 0.8f - index * 0.01f)))
            .ToArray();
        var store = new SearchStoreFake([], new VectorSnapshot(20, Policy, entries), candidates);

        var result = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1))).SearchAsync(
            new SearchRequest(Guid.NewGuid(), SearchMode.Semantic, "concept"),
            TestContext.Current.CancellationToken);

        Assert.Equal(10, result.ReturnedGroupCount);
        Assert.Equal(10, result.Results.Select(group => group.ContentId).Distinct().Count());
        Assert.True(result.InspectedCandidateDepths.Semantic > 500);
    }

    [Fact]
    public async Task ConsolidationNeverCrossesStructuralOrProvenanceBoundaries()
    {
        var documentId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var source = Candidate(Guid.NewGuid(), "anchor") with
        {
            DocumentId = documentId, ContentId = contentId, BodySearchText = "anchor"
        };
        var candidates = new[]
        {
            source with { PassageId = Guid.NewGuid(), Ordinal = 0, Location = new SourceLocation(LocationKind.Page, Page: 1), Heading = "A" },
            source with { PassageId = Guid.NewGuid(), Ordinal = 1, Location = new SourceLocation(LocationKind.Page, Page: 2), Heading = "A" },
            source with { PassageId = Guid.NewGuid(), Ordinal = 2, Location = new SourceLocation(LocationKind.Slide, Slide: 1), Heading = "A" },
            source with { PassageId = Guid.NewGuid(), Ordinal = 3, Location = new SourceLocation(LocationKind.Sheet, Sheet: "One"), Heading = "A" },
            source with { PassageId = Guid.NewGuid(), Ordinal = 4, Location = new SourceLocation(LocationKind.Sheet, Sheet: "Two"), Heading = "A" },
            source with { PassageId = Guid.NewGuid(), Ordinal = 5, Location = new SourceLocation(LocationKind.EmailPart, EmailPart: "headers"), Heading = "A" },
            source with { PassageId = Guid.NewGuid(), Ordinal = 6, Location = new SourceLocation(LocationKind.EmailPart, EmailPart: "body"), Heading = "A" },
            source with { PassageId = Guid.NewGuid(), Ordinal = 7, Location = new SourceLocation(LocationKind.EmailPart, EmailPart: "body"), Heading = "B" },
            source with { PassageId = Guid.NewGuid(), Ordinal = 8, Location = new SourceLocation(LocationKind.EmailPart, EmailPart: "body"), Heading = "B", ExtractionMethod = ExtractionMethod.Email }
        };
        var store = new SearchStoreFake(candidates, new VectorSnapshot(15, null, []), candidates);
        var result = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1))).SearchAsync(
            new SearchRequest(Guid.NewGuid(), SearchMode.Keyword,
                Clauses: [new SearchClause("anchor", "anchor", Fields: [SearchField.Body])],
                ResultOptions: new SearchResultOptions(PreviewsPerGroup: 10)),
            TestContext.Current.CancellationToken);

        var group = Assert.Single(result.Results);
        Assert.Equal(candidates.Length, group.Previews.Count);
        Assert.All(group.Previews, preview => Assert.Single(preview.ConsolidatedPassageIds));
    }

    [Fact]
    public async Task BranchSpecificNoOpOptionsAreRejected()
    {
        var store = new SearchStoreFake([], new VectorSnapshot(16, null, []), []);
        var search = CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)));
        var project = Guid.NewGuid();
        var requests = new SearchRequest[]
        {
            new(project, SearchMode.Keyword, Clauses: [new SearchClause("term", "term")],
                ResultOptions: new SearchResultOptions(SemanticConfidenceThreshold: 0.5)),
            new(project, SearchMode.Hybrid, "concept", FieldWeights: new SearchFieldWeights()),
            new(project, SearchMode.Hybrid, "concept", [new SearchClause("term", "term")],
                BranchWeights: new SearchBranchWeights(1, 0)),
            new(project, SearchMode.Keyword, Clauses: [new SearchClause("term", "term", SearchClauseOccur.Must)],
                MinimumShouldMatch: 0),
            new(project, SearchMode.Keyword, Clauses: [new SearchClause("term", "term")],
                MinimumShouldMatch: 0),
            new(project, SearchMode.Hybrid, Clauses: [new SearchClause("term", "term")],
                BranchWeights: new SearchBranchWeights(2, 1))
        };

        foreach (var request in requests)
        {
            var exception = await Assert.ThrowsAsync<ContextMoleException>(() => search.SearchAsync(request,
                TestContext.Current.CancellationToken));
            Assert.Equal("invalid_request", exception.Code);
        }
        Assert.Empty(store.KeywordQueries);
    }

    [Fact]
    public async Task HybridSemanticSearchAllowsOnlyShouldClausesWithZeroMinimum()
    {
        var lexical = Candidate(Guid.NewGuid(), "budget") with { Heading = "budget" };
        var conceptual = Candidate(Guid.NewGuid(), "conceptual match without lexical term");
        var snapshot = new VectorSnapshot(18, Policy,
        [
            VectorEntry(conceptual, 1f),
            VectorEntry(lexical, 0.5f)
        ]);
        var store = new SearchStoreFake([lexical], snapshot, [lexical, conceptual]);
        var result = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1))).SearchAsync(
            new SearchRequest(Guid.NewGuid(), SearchMode.Hybrid, "financial planning",
                [new SearchClause("budget", "budget", SearchClauseOccur.Should, Fields: [SearchField.Heading])],
                MinimumShouldMatch: 0), TestContext.Current.CancellationToken);

        Assert.Equal("hybrid", result.ActualMode);
        Assert.Contains(result.Results.SelectMany(group => group.Previews),
            preview => preview.PassageId == conceptual.PassageId);
    }

    [Fact]
    public async Task HybridSearchUsesCompatibleVectorsWhenSemanticCoverageIsPartial()
    {
        var keywordOnly = Candidate(Guid.NewGuid(), "exact keyword evidence") with
        {
            BodySearchText = "exact keyword evidence"
        };
        var semantic = Candidate(Guid.NewGuid(), "conceptual evidence from a compatible vector");
        var snapshot = new VectorSnapshot(24, Policy, [VectorEntry(semantic, 1f)]);
        var metadata = new VectorSnapshotMetadata(snapshot.SearchGeneration, Policy, 1,
            Warning: "Semantic search covers 1 of 2 indexed documents; 1 is excluded. Background embedding repair is queued.",
            IsComplete: false, TotalDocumentCount: 2, CompatibleDocumentCount: 1,
            RepairQueuedDocumentCount: 1, TotalPassageCount: 2);
        var store = new SearchStoreFake([keywordOnly], snapshot, [keywordOnly, semantic],
            vectorMetadata: metadata);

        var result = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1))).SearchAsync(
            new SearchRequest(Guid.NewGuid(), SearchMode.Hybrid, "conceptual evidence",
                [new SearchClause("exact", "exact", SearchClauseOccur.Should)], MinimumShouldMatch: 0),
            TestContext.Current.CancellationToken);

        Assert.Equal("hybrid", result.ActualMode);
        Assert.Contains(result.Warnings, warning => warning.Code == "semantic_partial_coverage" &&
                                                   warning.Message.Contains("1 of 2", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "semantic_unavailable");
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "fallback_keyword");
        var passageIds = result.Results.SelectMany(group => group.Previews).Select(preview => preview.PassageId);
        Assert.Contains(keywordOnly.PassageId, passageIds);
        Assert.Contains(semantic.PassageId, passageIds);
    }

    [Fact]
    public void SemanticOnlyNegativeClausesDoNotProduceAnInvalidFtsQuery()
    {
        var clauses = new[]
        {
            new SearchClause("exclude", "draft", SearchClauseOccur.MustNot, Fields: [SearchField.Body])
        };
        Assert.Equal(string.Empty, StructuredSearchQuery.BuildFtsQuery(clauses, 0));
    }

    [Theory]
    [InlineData(SearchMode.Semantic)]
    [InlineData(SearchMode.Hybrid)]
    public async Task EmbeddingReloadFailuresBecomeStructuredUnavailableResponses(SearchMode mode)
    {
        var keyword = Candidate(Guid.NewGuid(), "evidence") with { BodySearchText = "evidence" };
        var snapshot = new VectorSnapshot(17, Policy, [VectorEntry(keyword, 1)]);
        var store = new SearchStoreFake([keyword], snapshot, [keyword]);
        var embeddings = new EmbeddingGeneratorFake(Policy, Policy, Vector(1), new IOException("reload failed"));
        var request = mode == SearchMode.Semantic
            ? new SearchRequest(Guid.NewGuid(), mode, "concept")
            : new SearchRequest(Guid.NewGuid(), mode, "concept", [new SearchClause("evidence", "evidence")]);

        var result = await CreateSearch(store, embeddings).SearchAsync(request,
            TestContext.Current.CancellationToken);
        Assert.Contains(result.Warnings, warning => warning.Code == "semantic_unavailable" &&
                                                   warning.Message.Contains("reload failed", StringComparison.Ordinal));
        if (mode == SearchMode.Semantic)
        {
            Assert.Equal("unavailable", result.ActualMode);
            Assert.Empty(result.Results);
        }
        else
        {
            Assert.Equal("keyword", result.ActualMode);
            Assert.Single(result.Results);
            Assert.Contains(result.Warnings, warning => warning.Code == "fallback_keyword");
        }
    }

    [Theory]
    [InlineData(SearchMode.Semantic)]
    [InlineData(SearchMode.Hybrid)]
    public async Task StreamingSemanticFailuresBecomeStructuredUnavailableResponses(SearchMode mode)
    {
        var keyword = Candidate(Guid.NewGuid(), "evidence") with { BodySearchText = "evidence" };
        var snapshot = new VectorSnapshot(19, Policy, [VectorEntry(keyword, 1)], RequiresStreaming: true);
        var store = new SearchStoreFake([keyword], snapshot, [keyword],
            streamingException: new IOException("stream failed"));
        var request = mode == SearchMode.Semantic
            ? new SearchRequest(Guid.NewGuid(), mode, "concept")
            : new SearchRequest(Guid.NewGuid(), mode, "concept", [new SearchClause("evidence", "evidence")]);

        var result = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1))).SearchAsync(
            request, TestContext.Current.CancellationToken);
        Assert.Contains(result.Warnings, warning => warning.Code == "semantic_unavailable" &&
                                                   warning.Message.Contains("stream failed", StringComparison.Ordinal));
        Assert.Equal(mode == SearchMode.Semantic ? "unavailable" : "keyword", result.ActualMode);
        if (mode == SearchMode.Hybrid) Assert.Single(result.Results);
        else Assert.Empty(result.Results);
    }

    [Theory]
    [InlineData(SearchMode.Semantic)]
    [InlineData(SearchMode.Hybrid)]
    public async Task SemanticHydrationFailuresBecomeStructuredUnavailableResponses(SearchMode mode)
    {
        var keyword = Candidate(Guid.NewGuid(), "evidence") with { BodySearchText = "evidence" };
        var snapshot = new VectorSnapshot(19, Policy, [VectorEntry(keyword, 1)]);
        var store = new SearchStoreFake([keyword], snapshot, [keyword],
            candidateLoadException: new IOException("hydrate failed"));
        var request = mode == SearchMode.Semantic
            ? new SearchRequest(Guid.NewGuid(), mode, "concept")
            : new SearchRequest(Guid.NewGuid(), mode, "concept", [new SearchClause("evidence", "evidence")]);

        var result = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1))).SearchAsync(
            request, TestContext.Current.CancellationToken);

        Assert.Contains(result.Warnings, warning => warning.Code == "semantic_unavailable" &&
                                                   warning.Message.Contains("hydrate failed", StringComparison.Ordinal));
        Assert.Equal(mode == SearchMode.Semantic ? "unavailable" : "keyword", result.ActualMode);
        if (mode == SearchMode.Hybrid)
        {
            Assert.Single(result.Results);
            Assert.Contains(result.Warnings, warning => warning.Code == "fallback_keyword");
        }
        else Assert.Empty(result.Results);
    }

    [Fact]
    public async Task SemanticThresholdLabelsByDefaultAndFiltersOnlyWhenStrict()
    {
        var high = Candidate(Guid.NewGuid(), "high confidence");
        var low = Candidate(Guid.NewGuid(), "borderline lead");
        var snapshot = new VectorSnapshot(6, Policy,
        [
            VectorEntry(high, 0.8f),
            VectorEntry(low, 0.1f)
        ]);
        var store = new SearchStoreFake([], snapshot, [high, low]);
        var search = CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)));

        var permissive = await search.SearchAsync(new SearchRequest(Guid.NewGuid(), SearchMode.Semantic, "concept"),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, permissive.Results.Count);
        Assert.False(permissive.Results[0].Previews[0].LowConfidence);
        Assert.True(permissive.Results[1].Previews[0].LowConfidence);

        var strict = await search.SearchAsync(new SearchRequest(Guid.NewGuid(), SearchMode.Semantic, "concept",
            ResultOptions: new SearchResultOptions(StrictSemanticThreshold: true)),
            TestContext.Current.CancellationToken);
        Assert.Single(strict.Results);
        Assert.Equal(high.PassageId, strict.Results[0].Previews[0].PassageId);
    }

    [Fact]
    public async Task SemanticModeNeverSilentlyFallsBackWhenEmbeddingsAreUnavailable()
    {
        var keyword = Candidate(Guid.NewGuid(), "keyword evidence");
        var store = new SearchStoreFake([keyword], new VectorSnapshot(11, null, []), [keyword]);
        var result = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1))).SearchAsync(
            new SearchRequest(Guid.NewGuid(), SearchMode.Semantic, "concept"),
            TestContext.Current.CancellationToken);

        Assert.Equal("unavailable", result.ActualMode);
        Assert.Empty(result.Results);
        Assert.Contains(result.Warnings, warning => warning.Code == "semantic_unavailable");
        Assert.Empty(store.KeywordQueries);
    }

    [Fact]
    public async Task GroupDiversityReportsSuppressedContentForTheSameRootDocument()
    {
        var documentId = Guid.NewGuid();
        var candidates = Enumerable.Range(0, 3).Select(index => Candidate(Guid.NewGuid(), "anchor") with
        {
            DocumentId = documentId,
            ContentId = Guid.NewGuid(),
            BodySearchText = "anchor",
            KeywordScore = 3 - index
        }).ToArray();
        var store = new SearchStoreFake(candidates, new VectorSnapshot(12, null, []), candidates);
        var result = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1))).SearchAsync(
            new SearchRequest(Guid.NewGuid(), SearchMode.Keyword, Clauses: [new SearchClause("anchor", "anchor")],
                ResultOptions: new SearchResultOptions(MaxGroupsPerDocument: 2)),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ReturnedGroupCount);
        Assert.Equal(1, result.SuppressedGroupCount);
        Assert.Equal(1, Assert.Single(result.SuppressedSources).SuppressedContentGroups);
    }

    [Fact]
    public async Task ReciprocalRankFusionPromotesAResultFoundByBothBranches()
    {
        var overlapId = Guid.Parse("00000000-0000-0000-0000-000000000011");
        var semanticOnlyId = Guid.Parse("00000000-0000-0000-0000-000000000012");
        var overlap = Candidate(overlapId, "Keyword and semantic evidence");
        var semanticOnly = Candidate(semanticOnlyId, "Semantic evidence");
        var snapshot = new VectorSnapshot(7, Policy,
        [
            VectorEntry(overlap, 0.1f),
            VectorEntry(semanticOnly, 1f)
        ]);
        var store = new SearchStoreFake([overlap], snapshot, [overlap, semanticOnly]);
        var embeddings = new EmbeddingGeneratorFake(Policy, Policy, Vector(1));
        var search = CreateSearch(store, embeddings);

        var result = await search.SearchAsync(new SearchRequest(Guid.NewGuid(), SearchMode.Hybrid, "evidence",
                [new SearchClause("evidence", "evidence")]),
            TestContext.Current.CancellationToken);

        Assert.Equal("hybrid", result.ActualMode);
        Assert.Empty(result.Warnings);
        var previews = result.Results.SelectMany(group => group.Previews).ToArray();
        Assert.Equal(new[] { overlapId, semanticOnlyId }, previews.Select(item => item.PassageId));
        Assert.Equal(1, previews[0].KeywordRank);
        Assert.Equal(2, previews[0].SemanticRank);
        Assert.True(previews[0].LowConfidence);
        Assert.Null(previews[1].KeywordRank);
        Assert.Equal(1, previews[1].SemanticRank);
        Assert.False(previews[1].LowConfidence);
        Assert.Equal(1, embeddings.QueryCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchFallsBackToKeywordsWhenTheStoredAndQueryPoliciesDoNotMatch(bool changesDuringQuery)
    {
        var keyword = Candidate(Guid.NewGuid(), "Keyword fallback evidence");
        var otherPolicy = Policy with { Revision = "2" };
        var snapshot = new VectorSnapshot(9, Policy, [VectorEntry(keyword, 1)]);
        var store = new SearchStoreFake([keyword], snapshot, [keyword]);
        var embeddings = changesDuringQuery
            ? new EmbeddingGeneratorFake(Policy, otherPolicy, Vector(1))
            : new EmbeddingGeneratorFake(otherPolicy, otherPolicy, Vector(1));
        var search = CreateSearch(store, embeddings);

        var result = await search.SearchAsync(new SearchRequest(Guid.NewGuid(), SearchMode.Hybrid, "fallback",
                [new SearchClause("fallback", "fallback")]),
            TestContext.Current.CancellationToken);

        Assert.Equal("keyword", result.ActualMode);
        Assert.Equal(keyword.PassageId, Assert.Single(Assert.Single(result.Results).Previews).PassageId);
        Assert.Contains(result.Warnings, warning =>
            warning.Message.Contains("re-embedding", StringComparison.OrdinalIgnoreCase) ||
            warning.Code == "semantic_model_changed");
        Assert.Contains(result.Warnings, warning => warning.Code == "semantic_unavailable");
        Assert.Equal(changesDuringQuery ? 1 : 0, embeddings.QueryCalls);
    }

    [Fact]
    public async Task SemanticModeReportsUnavailableWhenTheModelChangesDuringTheQuery()
    {
        var candidate = Candidate(Guid.NewGuid(), "semantic evidence");
        var changedPolicy = Policy with { Revision = "2" };
        var snapshot = new VectorSnapshot(9, Policy, [VectorEntry(candidate, 1)]);
        var store = new SearchStoreFake([], snapshot, [candidate]);
        var embeddings = new EmbeddingGeneratorFake(Policy, changedPolicy, Vector(1));

        var result = await CreateSearch(store, embeddings).SearchAsync(
            new SearchRequest(Guid.NewGuid(), SearchMode.Semantic, "evidence"),
            TestContext.Current.CancellationToken);

        Assert.Equal("unavailable", result.ActualMode);
        Assert.Empty(result.Results);
        Assert.Contains(result.Warnings, warning => warning.Code == "semantic_model_changed");
        Assert.Contains(result.Warnings, warning => warning.Code == "semantic_unavailable");
    }

    [Theory]
    [InlineData(SearchMode.Keyword)]
    [InlineData(SearchMode.Semantic)]
    [InlineData(SearchMode.Hybrid)]
    public async Task MissingProjectsAreNeverMaskedAsBranchAvailabilityWarnings(SearchMode mode)
    {
        var missing = new ContextMoleException("project_not_found", "The project does not exist.");
        var store = new SearchStoreFake([], new VectorSnapshot(1, Policy, []), [], branchException: missing);
        var request = mode switch
        {
            SearchMode.Keyword => new SearchRequest(Guid.NewGuid(), mode,
                Clauses: [new SearchClause("evidence", "evidence")]),
            SearchMode.Semantic => new SearchRequest(Guid.NewGuid(), mode, "evidence"),
            _ => new SearchRequest(Guid.NewGuid(), mode, "evidence",
                [new SearchClause("evidence", "evidence")])
        };

        var exception = await Assert.ThrowsAsync<ContextMoleException>(() =>
            CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1))).SearchAsync(
                request, TestContext.Current.CancellationToken));

        Assert.Equal("project_not_found", exception.Code);
    }

    [Fact]
    public async Task LargeSemanticCandidateSetsAreHydratedInSqliteSafeBatches()
    {
        var documentId = Guid.NewGuid();
        var candidates = Enumerable.Range(0, 700).Select(index =>
            Candidate(Guid.NewGuid(), $"semantic evidence {index}") with
            {
                DocumentId = documentId,
                ContentId = Guid.NewGuid()
            }).ToArray();
        var snapshot = new VectorSnapshot(23, Policy,
            candidates.Select((candidate, index) => VectorEntry(candidate, 1f - index / 1000f)).ToArray());
        var store = new SearchStoreFake([], snapshot, candidates, maximumCandidateBatch: 500);

        var result = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1))).SearchAsync(
            new SearchRequest(Guid.NewGuid(), SearchMode.Semantic, "evidence",
                ResultOptions: new SearchResultOptions(GroupLimit: 50, MaxGroupsPerDocument: 1)),
            TestContext.Current.CancellationToken);

        Assert.Single(result.Results);
        Assert.True(store.CandidateBatchSizes.Count >= 2);
        Assert.All(store.CandidateBatchSizes, size => Assert.InRange(size, 1, 500));
    }

    [Fact]
    public async Task SearchWarnsWhenNoSemanticEmbeddingsAreAvailable()
    {
        var keyword = Candidate(Guid.NewGuid(), "Keyword-only evidence");
        var snapshot = new VectorSnapshot(10, null, []);
        var store = new SearchStoreFake([keyword], snapshot, [keyword]);
        var embeddings = new EmbeddingGeneratorFake(Policy, Policy, Vector(1));
        var search = CreateSearch(store, embeddings);

        var result = await search.SearchAsync(new SearchRequest(Guid.NewGuid(), SearchMode.Hybrid, "evidence",
                [new SearchClause("evidence", "evidence")]),
            TestContext.Current.CancellationToken);

        Assert.Equal("keyword", result.ActualMode);
        Assert.Contains(result.Warnings,
            warning => warning.Message.Contains("No semantic embeddings", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, embeddings.QueryCalls);
    }

    private static HybridSearchService CreateSearch(ISearchStore store, IEmbeddingGenerator embeddings) =>
        new(store, embeddings, new FlatVectorIndexFactory(), new VectorIndexCache(), new ImmediateCpuBudget());

    private static SearchCandidate Candidate(Guid passageId, string text) =>
        new(passageId, Guid.NewGuid(), Guid.NewGuid(), text, Path.Combine(Path.GetTempPath(), passageId + ".txt"),
            passageId + ".txt", ".txt", DateTimeOffset.UnixEpoch, new SourceLocation(LocationKind.Document), [],
            ExtractionMethod.NativeText, null, KeywordScore: -1);

    private static VectorEntry VectorEntry(SearchCandidate candidate, float similarity) =>
        new(candidate.PassageId, candidate.DocumentId, candidate.ContentId, candidate.SourcePath, candidate.FileType,
            candidate.ModifiedUtc, candidate.AttachmentChain.Count > 0, Vector(similarity));

    private static float[] Vector(float firstValue)
    {
        var vector = new float[384];
        vector[0] = firstValue;
        return vector;
    }

    private static readonly EmbeddingPolicy Policy =
        new("test-model", "1", "model-sha", "tokenizer-sha", "fp32", 384, 384, "cls", "l2");

    private sealed class SearchStoreFake(
        IReadOnlyList<SearchCandidate> keywordCandidates,
        VectorSnapshot snapshot,
        IReadOnlyList<SearchCandidate> candidates,
        Func<string, IReadOnlyList<SearchCandidate>>? keywordResolver = null,
        Exception? streamingException = null,
        Exception? branchException = null,
        int? maximumCandidateBatch = null,
        Exception? candidateLoadException = null,
        VectorSnapshotMetadata? vectorMetadata = null) : ISearchStore
    {
        private readonly Dictionary<Guid, SearchCandidate> _candidates =
            candidates.ToDictionary(candidate => candidate.PassageId);

        public List<string> KeywordQueries { get; } = [];
        public List<int> KeywordOffsets { get; } = [];
        public List<int> CandidateBatchSizes { get; } = [];

        public Task<KeywordSearchPage> KeywordSearchAsync(Guid projectId, string ftsQuery, int count,
            SearchFilters? filters, CancellationToken cancellationToken = default)
        {
            if (branchException is not null) return Task.FromException<KeywordSearchPage>(branchException);
            KeywordQueries.Add(ftsQuery);
            var resolved = keywordResolver?.Invoke(ftsQuery) ?? keywordCandidates;
            return Task.FromResult(new KeywordSearchPage(snapshot.SearchGeneration, resolved.Take(count).ToArray()));
        }

        public Task<KeywordSearchPage> KeywordSearchAsync(Guid projectId, string ftsQuery, int count, int offset,
            SearchFilters? filters, SearchFieldWeights fieldWeights,
            CancellationToken cancellationToken = default)
        {
            if (branchException is not null) return Task.FromException<KeywordSearchPage>(branchException);
            KeywordQueries.Add(ftsQuery);
            KeywordOffsets.Add(offset);
            var resolved = keywordResolver?.Invoke(ftsQuery) ?? keywordCandidates;
            return Task.FromResult(new KeywordSearchPage(snapshot.SearchGeneration,
                resolved.Skip(offset).Take(count).ToArray()));
        }

        public Task<VectorSnapshotMetadata> LoadVectorSnapshotMetadataAsync(Guid projectId,
            CancellationToken cancellationToken = default) => branchException is not null
            ? Task.FromException<VectorSnapshotMetadata>(branchException)
            : Task.FromResult(vectorMetadata ?? new VectorSnapshotMetadata(snapshot.SearchGeneration, snapshot.Policy,
                snapshot.Entries.Count, snapshot.RequiresStreaming, snapshot.Warning));

        public Task<VectorSnapshotMetadata> LoadVectorSnapshotMetadataAsync(Guid projectId,
            EmbeddingPolicy targetPolicy, CancellationToken cancellationToken = default) =>
            LoadVectorSnapshotMetadataAsync(projectId, cancellationToken);

        public Task<VectorSnapshot> LoadVectorSnapshotAsync(Guid projectId,
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);

        public Task<VectorSnapshot> LoadVectorSnapshotAsync(Guid projectId, EmbeddingPolicy targetPolicy,
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);

        public async IAsyncEnumerable<VectorEntry> StreamVectorEntriesAsync(Guid projectId, long expectedGeneration,
            SearchFilters? filters, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (streamingException is not null) throw streamingException;
            foreach (var entry in snapshot.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
            }

            await Task.CompletedTask;
        }

        public IAsyncEnumerable<VectorEntry> StreamVectorEntriesAsync(Guid projectId, long expectedGeneration,
            EmbeddingPolicy targetPolicy, SearchFilters? filters, CancellationToken cancellationToken = default) =>
            StreamVectorEntriesAsync(projectId, expectedGeneration, filters, cancellationToken);

        public Task<IReadOnlyList<SearchCandidate>> LoadCandidatesAsync(Guid projectId,
            IReadOnlyCollection<Guid> passageIds, long expectedGeneration,
            CancellationToken cancellationToken = default)
        {
            CandidateBatchSizes.Add(passageIds.Count);
            if (candidateLoadException is not null)
                return Task.FromException<IReadOnlyList<SearchCandidate>>(candidateLoadException);
            if (maximumCandidateBatch is { } maximum && passageIds.Count > maximum)
                return Task.FromException<IReadOnlyList<SearchCandidate>>(
                    new InvalidOperationException($"Candidate batch exceeded {maximum}."));
            return Task.FromResult<IReadOnlyList<SearchCandidate>>(
                passageIds.Select(id => _candidates[id]).ToArray());
        }

        public Task<bool> IsInitializedAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectFileTypeCount>> ListProjectFileTypeCountsAsync(Guid projectId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DocumentListResponse> ListDocumentsAsync(DocumentListRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectErrorInfo>> ListProjectErrorsAsync(Guid projectId, int limit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PassageInfo>> ReadPassagesAsync(Guid projectId,
            IReadOnlyCollection<Guid> passageIds, int contextBefore, int contextAfter,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DocumentInfo?> GetDocumentInfoAsync(Guid projectId, Guid documentId, Guid? contentId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AttachmentPage> ListAttachmentsAsync(Guid projectId, Guid documentId, string? cursor, int limit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResolvedLocalFile?> ResolveLocalFileAsync(Guid projectId, Guid documentId, Guid? contentId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IndexedContentMaterialization?> GetContentMaterializationAsync(Guid projectId, Guid contentId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmbeddingGeneratorFake(
        EmbeddingPolicy currentPolicy,
        EmbeddingPolicy queryPolicy,
        float[] queryVector,
        Exception? reloadException = null) : IEmbeddingGenerator
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public EmbeddingPolicy? Policy { get; } = currentPolicy;
        public int QueryCalls { get; private set; }
        public Task ReloadAsync(CancellationToken cancellationToken = default) => reloadException is null
            ? Task.CompletedTask
            : Task.FromException(reloadException);
        public int CountTokens(string text) => text.Length;
        public Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken)
        {
            QueryCalls++;
            return Task.FromResult(new QueryEmbedding(queryVector, queryPolicy));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ImmediateCpuBudget : IGlobalCpuBudget
    {
        public int MaximumWorkerCount => 1;
        public ValueTask<ICpuWorkerLease> AcquireWorkerAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ICpuWorkerLease>(new WorkerLease());
        }
        public ValueTask<ICpuFullCapacityLease> AcquireFullCapacityAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ICpuFullCapacityLease>(new FullCapacityLease());
        }
    }

    private sealed class WorkerLease : ICpuWorkerLease
    {
        public IDisposable Activate() => NoopDisposable.Instance;
        public void Dispose() { }
    }

    private sealed class FullCapacityLease : ICpuFullCapacityLease
    {
        public int ThreadCount => 1;
        public void Dispose() { }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}

file sealed class UnexpectedOcrEngine : IOcrEngine
{
    public bool IsAvailable => false;
    public string UnavailableReason => "OCR must not be reached by these fixtures.";
    public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException(UnavailableReason));
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
        Task.FromException<OcrResult>(new InvalidOperationException(UnavailableReason));
}

file sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "ContextMole.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string File(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
