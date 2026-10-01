using System.Text.Json;
using ContextMole.Benchmarks;
using ContextMole.Broker.Protocol;
using ContextMole.Core;

namespace ContextMole.Tests;

public sealed class VidorePdfBenchmarkTests
{
    private static readonly PdfPageKey First = new("first", 1);
    private static readonly PdfPageKey Second = new("first", 2);
    private static readonly PdfPageKey Third = new("second", 1);
    private static readonly IReadOnlyDictionary<int, PdfPageKey> Pages = new Dictionary<int, PdfPageKey>
        { [10] = First, [11] = Second, [12] = Third };

    [Fact]
    public void PageDeduplicationPrecedesCutoffAndNdcgUsesLinearGrades()
    {
        var query = Query(new VidoreQrel(10, 1, []), new(11, 2, []));
        PdfRetrievalAnchor[] anchors = [Anchor(First, 3), Anchor(First, 2), Anchor(Second, 1)];
        var metric = PdfBenchmarkMetrics.Evaluate(query, Pages, anchors, 2);
        Assert.Equal(2, metric.RetrievedPages);
        Assert.Equal(2, metric.RetrievedRelevantPages);
        Assert.Equal(1, metric.Recall);
        var expected = (1 + 2 / Math.Log2(3)) / (2 + 1 / Math.Log2(3));
        Assert.Equal(expected, metric.Ndcg, 10);
        Assert.Equal(1, metric.ReciprocalRank);
    }

    [Fact]
    public void RecallAndIdealRankingRetainAllRelevantPagesOutsideCutoff()
    {
        var metric = PdfBenchmarkMetrics.Evaluate(Query(new VidoreQrel(10, 1, []), new(11, 2, [])), Pages,
            [Anchor(Third, 3), Anchor(First, 2), Anchor(Second, 1)], 2);
        Assert.Equal(0.5, metric.Recall);
        Assert.Equal(0.5, metric.ReciprocalRank);
        Assert.Equal((1 / Math.Log2(3)) / (2 + 1 / Math.Log2(3)), metric.Ndcg, 10);
    }

    [Fact]
    public void EvidenceCoverageUnionsOverlappingRegionsAndDuplicateAnnotations()
    {
        var query = Query(new VidoreQrel(10, 1, [[0, 0, 1, 1]]), new(10, 2, [[0, 0, 1, 1]]));
        var metric = PdfBenchmarkMetrics.Evaluate(query, Pages,
            [Anchor(First, 2, new(0, 0, 0.6, 1)), Anchor(First, 1, new(0.2, 0, 0.6, 1))], 1);
        Assert.Equal(1, metric.RelevantPages);
        Assert.Equal(1, metric.GoldRegions);
        Assert.Equal(0.8, metric.EvidenceAreaCoverage!.Value, 10);
        Assert.Equal(1, metric.EvidenceRegionRecall);
    }

    [Fact]
    public void UnretrievedGoldPagesRemainInGeometryDenominator()
    {
        var query = Query(new VidoreQrel(10, 1, [[0, 0, 1, 1]]), new(11, 2, [[0, 0, 1, 1]]));
        var metric = PdfBenchmarkMetrics.Evaluate(query, Pages, [Anchor(First, 1, new(0, 0, 1, 1))], 1);
        Assert.Equal(0.5, metric.EvidenceAreaCoverage);
        Assert.Equal(0.5, metric.EvidenceRegionRecall);
        Assert.Equal(1, metric.CoveredRegions);
    }

    [Fact]
    public void GoldRectangleUnionDoesNotDoubleCountOverlappingAnnotations()
    {
        var query = Query(new VidoreQrel(10, 1, [[0, 0, 0.75, 1], [0.25, 0, 1, 1]]));
        var metric = PdfBenchmarkMetrics.Evaluate(query, Pages, [Anchor(First, 1, new(0.25, 0, 0.5, 1))], 1);
        Assert.Equal(0.5, metric.EvidenceAreaCoverage!.Value, 10);
        Assert.Equal(1, metric.EvidenceRegionRecall);
    }

    [Fact]
    public void MissingGeometryIsZeroWithGoldAnnotationsAndNullWithoutThem()
    {
        var missing = PdfBenchmarkMetrics.Evaluate(Query(new VidoreQrel(10, 1, [[0, 0, 1, 1]])), Pages, [Anchor(First, 1)], 1);
        Assert.Equal(0, missing.EvidenceAreaCoverage);
        Assert.Equal(0, missing.EvidenceRegionRecall);
        var unannotated = PdfBenchmarkMetrics.Evaluate(Query(new VidoreQrel(10, 1, [])), Pages, [Anchor(First, 1)], 1);
        Assert.Null(unannotated.EvidenceAreaCoverage);
        Assert.Null(unannotated.EvidenceRegionRecall);
    }

    [Fact]
    public void CitationValidationChecksLiteralOffsetsIdentityLocationAndRequestedRows()
    {
        var location = new SourceLocation(LocationKind.Page, Page: 1, Region: new(0, 0, 1, 1));
        var passage = new PassageInfo(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, "Prefix evidence suffix",
            "first.pdf", "first.pdf", ".pdf", DateTimeOffset.UnixEpoch, location, [], ExtractionMethod.NativeText, null, true);
        var preview = new SearchResultItem(passage.PassageId, passage.DocumentId, passage.ContentId, "evidence", true,
            passage.SourcePath, passage.FileName, passage.FileType, passage.ModifiedUtc, location, [],
            ExtractionMethod.NativeText, null, 1, null, 1, null, 1, false, [], [], [])
            { ExcerptStart = 7, ExcerptLength = 8 };
        Assert.True(PdfBenchmarkMetrics.IsLiteralCitation(preview, passage, First));
        Assert.False(PdfBenchmarkMetrics.IsLiteralCitation(preview, passage with { Requested = false }, First));
        Assert.False(PdfBenchmarkMetrics.IsLiteralCitation(preview, passage with { ErrorCode = "unavailable" }, First));
        Assert.False(PdfBenchmarkMetrics.IsLiteralCitation(preview, passage with { DocumentId = Guid.NewGuid() }, First));
        Assert.False(PdfBenchmarkMetrics.IsLiteralCitation(preview, passage with { AttachmentChain = ["embedded.pdf"] }, First));
        Assert.False(PdfBenchmarkMetrics.IsLiteralCitation(preview, passage with { Location = location with { Page = 2 } }, First));
        Assert.False(PdfBenchmarkMetrics.IsLiteralCitation(preview with { ExcerptStart = int.MaxValue }, passage, First));
        Assert.False(PdfBenchmarkMetrics.IsLiteralCitation(preview with { Excerpt = "different" }, passage, First));
    }

    [Fact]
    public void ManifestRoundTripKeepsPhysicalPagesAndRejectsIncompleteOrEscapedSelection()
    {
        var manifest = new VidoreManifest(1, VidoreManifest.SupportedDataset, VidoreManifest.SupportedRevision, "smoke", "imageNormalizedTopLeft",
            [new("first", "pdfs/first.pdf", new string('b', 64), "CC BY 4.0", 2)],
            [new(10, "first", 1), new(11, "first", 2)], [Query(new VidoreQrel(11, 2, [[0, 0, 1, 1]]))])
            { UnusableBoundingBoxCount = 1, AnnotationWarnings = ["A zero-area annotation was excluded from geometry."] };
        var roundTrip = JsonSerializer.Deserialize<VidoreManifest>(JsonSerializer.Serialize(manifest, BrokerJson.Options), BrokerJson.Options)!;
        roundTrip.Validate();
        Assert.Equal(2, roundTrip.Pages[1].PageNumber);
        Assert.Equal(1, roundTrip.UnusableBoundingBoxCount);
        Assert.Single(roundTrip.AnnotationWarnings);
        Assert.Throws<InvalidDataException>(() => (manifest with { Pages = [manifest.Pages[0]] }).Validate());
        Assert.Throws<InvalidDataException>(() => (manifest with { Queries = [Query(new VidoreQrel(99, 1, []))] }).Validate());
        Assert.Throws<InvalidDataException>(() => (manifest with { CoordinatesFrame = "pixels" }).Validate());
        Assert.Throws<InvalidDataException>(() => (manifest with { Revision = new string('a', 40) }).Validate());
        Assert.Throws<InvalidDataException>(() => VidoreManifest.ResolvePdfPath(Path.Combine(Path.GetTempPath(), "cache", "manifest.json"), "../escaped.pdf"));
        Assert.Throws<InvalidDataException>(() => (manifest with { Queries = [Query(new VidoreQrel(10, 1, [[0, 0, 1.1, 1]]))] }).Validate());
    }

    private static VidoreQuery Query(params VidoreQrel[] qrels) => new("0", "Question", "english", qrels);
    private static PdfRetrievalAnchor Anchor(PdfPageKey page, double score, SourceRegion? region = null) =>
        new(page, Guid.NewGuid(), score, null, null, region);
}
