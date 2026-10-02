using System.Text;
using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Indexing;
using ContextMole.Infrastructure;
using ContextMole.Search;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace ContextMole.Tests;

public sealed class ImageOcrContextTests
{
    [Theory]
    [InlineData(.08, .25, true)]
    [InlineData(.60, .25, false)]
    [InlineData(.08, .65, false)]
    public async Task ShortImageLabelAndValueMergeOnlyWithinNearbySameColumn(double x, double y, bool expectedMerge)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ContextMole-image-context", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "context.png"); await File.WriteAllBytesAsync(path, ImageBytes(), TestContext.Current.CancellationToken);
            var result = await new DocumentExtractionRegistry(new ContextOcr(x, y)).ExtractAsync(new ExtractionRequest(path), TestContext.Current.CancellationToken);
            Assert.Empty(result.Errors);
            Assert.Equal(expectedMerge ? 1 : 2, result.Root.Sections.Count);
            if (expectedMerge)
            {
                var section = Assert.Single(result.Root.Sections); Assert.Equal("CONTROL LABEL\nMAPLE-482", section.Text);
                Assert.Equal(LocationKind.ImageFrame, section.Location.Kind); Assert.Equal(1, section.Location.ImageFrame);
                Assert.NotNull(section.Location.Region); Assert.Equal(.08, section.Location.Region.X, precision: 6);
                Assert.Equal(.10, section.Location.Region.Y, precision: 6); Assert.Equal(.21, section.Location.Region.Height, precision: 6);
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    internal static byte[] ImageBytes()
    {
        using var bitmap = new SKBitmap(800, 500); bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100); return png.ToArray();
    }
    internal sealed class ContextOcr(double x = .08, double y = .25, bool splitLabel = false) : IOcrEngine
    {
        public bool IsAvailable => true; public string? UnavailableReason => null;
        public Task EnsureAvailableAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken token) => Task.FromResult(new OcrResult("CONTROL LABEL\nMAPLE-482", 99,
            Lines: splitLabel ? [new OcrTextLine("CONTROL", 99, new SourceRegion(.08, .10, .30, .06)), new OcrTextLine("LABEL", 99, new SourceRegion(.40, .10, .15, .06)), new OcrTextLine("MAPLE-482", 99, new SourceRegion(x, y, .30, .06))] : [new OcrTextLine("CONTROL LABEL", 99, new SourceRegion(.08, .10, .60, .06)), new OcrTextLine("MAPLE-482", 99, new SourceRegion(x, y, .30, .06))]));
    }
}

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class ImageOcrSearchContextTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LabelSearchReturnsLiteralValueFromRootOrEmailImageAttachment(bool attachment, bool splitLabel)
    {
        var token = TestContext.Current.CancellationToken; await using var database = await StorageTestDatabase.CreateAsync(token);
        var path = Path.Combine(database.Paths.SourceDirectory, attachment ? "context.eml" : "context.png");
        var bytes = ImageOcrContextTests.ImageBytes();
        if (attachment)
            await File.WriteAllTextAsync(path, "From: a@example.test\r\nTo: b@example.test\r\nSubject: Control scan\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=ctx\r\n\r\n--ctx\r\nContent-Type: text/plain\r\n\r\nPlease review the attachment.\r\n--ctx\r\nContent-Type: image/png\r\nContent-Disposition: attachment; filename=card.png\r\nContent-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(bytes) + "\r\n--ctx--\r\n", Encoding.ASCII, token);
        else await File.WriteAllBytesAsync(path, bytes, token);
        var (project, _) = await database.CreateProjectAsync("OCR label and value context", token);
        await using var embeddings = new StorageUnavailableEmbeddings(); using var cpu = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths, new DocumentExtractionRegistry(new ImageOcrContextTests.ContextOcr(splitLabel: splitLabel)),
            embeddings, new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(), cpu, NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while ((await database.Store.ListProjectsAsync(timeout.Token)).Single(value => value.Id == project) is not { IndexedCount: 1, PendingCount: 0 }) await Task.Delay(50, timeout.Token);
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
        var search = new HybridSearchService(database.Store, embeddings, new FlatVectorIndexFactory(), new VectorIndexCache(), cpu);
        var result = await search.SearchAsync(new SearchRequest(project, SearchMode.Keyword, Clauses: [new SearchClause("label", "CONTROL LABEL", SearchClauseOccur.Must, SearchMatchKind.Phrase)],
            Filters: new SearchFilters(AttachmentScope: attachment ? AttachmentScope.AttachmentsOnly : AttachmentScope.RootOnly)), token);
        var group = Assert.Single(result.Results); var preview = Assert.Single(group.Previews);
        Assert.Contains("MAPLE-482", preview.Excerpt); Assert.Equal(path, preview.SourcePath);
        Assert.Equal(attachment ? new[] { "card.png" } : [], preview.AttachmentChain);
        var read = Assert.Single(await database.Store.ReadPassagesAsync(project, [preview.PassageId], 0, 0, result.SearchGeneration, token));
        Assert.Equal(preview.Excerpt, read.Text.Substring(preview.ExcerptStart, preview.ExcerptLength)); Assert.Equal(preview.Location, read.Location);
    }
}
