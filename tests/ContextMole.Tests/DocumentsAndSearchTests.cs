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
        var entries = new[]
        {
            Entry(first, ".txt", isAttachment: false, 0.9f),
            Entry(second, ".pdf", isAttachment: true, 0.8f),
            Entry(third, ".md", isAttachment: false, 0.7f)
        };
        var index = new FlatVectorIndex(new VectorSnapshot(12, TestPolicy, entries));

        var all = index.Search(Vector(1), 10);
        Assert.Equal(new[] { first, second, third }, all.Select(match => match.PassageId));
        Assert.Equal(new[] { 1, 2, 3 }, all.Select(match => match.Rank));

        var filtered = index.Search(Vector(1), 10,
            new SearchFilters(Extensions: ["PDF"], AttachmentScope: AttachmentScope.AttachmentsOnly));
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

    private static VectorEntry Entry(Guid passageId, string extension, bool isAttachment, float similarity) =>
        new(passageId, Guid.NewGuid(), Guid.NewGuid(), Path.Combine(Path.GetTempPath(), "vectors", passageId.ToString()),
            extension, DateTimeOffset.UnixEpoch, isAttachment, Vector(similarity));

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
    public async Task ReciprocalRankFusionPromotesAResultFoundByBothBranches()
    {
        var overlapId = Guid.Parse("00000000-0000-0000-0000-000000000011");
        var semanticOnlyId = Guid.Parse("00000000-0000-0000-0000-000000000012");
        var overlap = Candidate(overlapId, "Keyword and semantic evidence");
        var semanticOnly = Candidate(semanticOnlyId, "Semantic evidence");
        var snapshot = new VectorSnapshot(7, Policy,
        [
            VectorEntry(overlap, 0.8f),
            VectorEntry(semanticOnly, 1f)
        ]);
        var store = new SearchStoreFake([overlap], snapshot, [overlap, semanticOnly]);
        var embeddings = new EmbeddingGeneratorFake(Policy, Policy, Vector(1));
        var search = CreateSearch(store, embeddings);

        var result = await search.SearchAsync(new SearchRequest(Guid.NewGuid(), "evidence"),
            TestContext.Current.CancellationToken);

        Assert.Equal("hybrid", result.ActualMode);
        Assert.Empty(result.Warnings);
        Assert.Equal(new[] { overlapId, semanticOnlyId }, result.Results.Select(item => item.PassageId));
        Assert.Equal(1, result.Results[0].KeywordRank);
        Assert.Equal(2, result.Results[0].SemanticRank);
        Assert.Null(result.Results[1].KeywordRank);
        Assert.Equal(1, result.Results[1].SemanticRank);
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

        var result = await search.SearchAsync(new SearchRequest(Guid.NewGuid(), "fallback"),
            TestContext.Current.CancellationToken);

        Assert.Equal("keyword", result.ActualMode);
        Assert.Equal(keyword.PassageId, Assert.Single(result.Results).PassageId);
        Assert.Contains(result.Warnings, warning => warning.Contains("re-embedding", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(changesDuringQuery ? 1 : 0, embeddings.QueryCalls);
    }

    [Fact]
    public async Task SearchWarnsWhenNoSemanticEmbeddingsAreAvailable()
    {
        var keyword = Candidate(Guid.NewGuid(), "Keyword-only evidence");
        var snapshot = new VectorSnapshot(10, null, []);
        var store = new SearchStoreFake([keyword], snapshot, [keyword]);
        var embeddings = new EmbeddingGeneratorFake(Policy, Policy, Vector(1));
        var search = CreateSearch(store, embeddings);

        var result = await search.SearchAsync(new SearchRequest(Guid.NewGuid(), "evidence"),
            TestContext.Current.CancellationToken);

        Assert.Equal("keyword", result.ActualMode);
        Assert.Contains(result.Warnings,
            warning => warning.Contains("No semantic embeddings", StringComparison.OrdinalIgnoreCase));
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
        IReadOnlyList<SearchCandidate> candidates) : ISearchStore
    {
        private readonly Dictionary<Guid, SearchCandidate> _candidates =
            candidates.ToDictionary(candidate => candidate.PassageId);

        public Task<KeywordSearchPage> KeywordSearchAsync(Guid projectId, string ftsQuery, int count,
            SearchFilters? filters, CancellationToken cancellationToken = default) =>
            Task.FromResult(new KeywordSearchPage(snapshot.SearchGeneration, keywordCandidates.Take(count).ToArray()));

        public Task<VectorSnapshotMetadata> LoadVectorSnapshotMetadataAsync(Guid projectId,
            CancellationToken cancellationToken = default) => Task.FromResult(new VectorSnapshotMetadata(
            snapshot.SearchGeneration, snapshot.Policy, snapshot.Entries.Count, snapshot.RequiresStreaming, snapshot.Warning));

        public Task<VectorSnapshot> LoadVectorSnapshotAsync(Guid projectId,
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);

        public async IAsyncEnumerable<VectorEntry> StreamVectorEntriesAsync(Guid projectId, long expectedGeneration,
            SearchFilters? filters, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var entry in snapshot.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
            }

            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<SearchCandidate>> LoadCandidatesAsync(Guid projectId,
            IReadOnlyCollection<Guid> passageIds, long expectedGeneration,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SearchCandidate>>(
            passageIds.Select(id => _candidates[id]).ToArray());

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
        float[] queryVector) : IEmbeddingGenerator
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public EmbeddingPolicy? Policy { get; } = currentPolicy;
        public int QueryCalls { get; private set; }
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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