using System.Text.Json.Serialization;
using ContextMole.Core;

namespace ContextMole.Benchmarks;

public sealed record VidoreManifest(
    int Version, string Dataset, string Revision, string Selection,
    [property: JsonPropertyName("coordinates_frame")] string CoordinatesFrame,
    VidoreDocument[] Documents, VidorePage[] Pages, VidoreQuery[] Queries)
{
    public const string SupportedDataset = "vidore/vidore_v3_hr";
    public const string SupportedRevision = "0cdf0979f2c5a0fd3e335e6373b9da48a9fe3bc3";
    [JsonPropertyName("unusable_bounding_box_count")]
    public int UnusableBoundingBoxCount { get; init; }
    [JsonPropertyName("annotation_warnings")]
    public string[] AnnotationWarnings { get; init; } = [];

    public void Validate()
    {
        if (Version != 1 || CoordinatesFrame != "imageNormalizedTopLeft" ||
            Dataset != SupportedDataset || Revision != SupportedRevision || string.IsNullOrWhiteSpace(Selection) ||
            UnusableBoundingBoxCount < 0 || AnnotationWarnings is null ||
            Documents is null || Pages is null || Queries is null ||
            Documents.Length == 0 || Pages.Length == 0 || Queries.Length == 0)
            throw new InvalidDataException("Unsupported, empty, or unpinned ViDoRe manifest.");
        if (Documents.Select(document => document.Id).Distinct(StringComparer.Ordinal).Count() != Documents.Length ||
            Pages.Select(page => page.CorpusId).Distinct().Count() != Pages.Length ||
            Pages.Select(page => new PdfPageKey(page.DocumentId, page.PageNumber)).Distinct().Count() != Pages.Length ||
            Queries.Select(query => query.Id).Distinct(StringComparer.Ordinal).Count() != Queries.Length)
            throw new InvalidDataException("Duplicate document, page, corpus, or query identifiers.");
        var documents = Documents.ToDictionary(document => document.Id, StringComparer.Ordinal);
        foreach (var document in Documents)
        {
            if (string.IsNullOrWhiteSpace(document.Id) || document.Id != Path.GetFileName(document.Id) ||
                document.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                document.PageCount < 1 || string.IsNullOrWhiteSpace(document.License) ||
                document.Sha256 is null || document.Sha256.Length != 64 || !document.Sha256.All(Uri.IsHexDigit) ||
                string.IsNullOrWhiteSpace(document.File) || Path.IsPathRooted(document.File))
                throw new InvalidDataException($"Invalid document metadata: {document.Id}.");
        }
        foreach (var page in Pages)
            if (!documents.TryGetValue(page.DocumentId, out var document) ||
                page.PageNumber < 1 || page.PageNumber > document.PageCount)
                throw new InvalidDataException($"Invalid corpus page: {page.CorpusId}.");
        foreach (var document in Documents)
            if (Pages.Count(page => page.DocumentId == document.Id) != document.PageCount)
                throw new InvalidDataException($"The corpus must retain every page of {document.Id}.");
        var corpusIds = Pages.Select(page => page.CorpusId).ToHashSet();
        foreach (var query in Queries)
        {
            if (string.IsNullOrWhiteSpace(query.Id) || string.IsNullOrWhiteSpace(query.Text) ||
                string.IsNullOrWhiteSpace(query.Language) || query.Qrels is null || query.Qrels.Length == 0)
                throw new InvalidDataException("Empty query or relevance judgments.");
            foreach (var qrel in query.Qrels)
                if (!corpusIds.Contains(qrel.CorpusId) || qrel.Score is < 1 or > 2 ||
                    qrel.BoundingBoxes is null || qrel.BoundingBoxes.Any(box => !PdfBenchmarkMetrics.IsValidBox(box)))
                    throw new InvalidDataException($"Invalid or out-of-selection relevance judgment: {query.Id}/{qrel.CorpusId}.");
        }
    }

    public static string ResolvePdfPath(string manifestPath, string relativeFile)
    {
        if (Path.IsPathRooted(relativeFile)) throw new InvalidDataException("PDF paths must be cache-relative.");
        var root = Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativeFile));
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new InvalidDataException("A PDF path escaped the benchmark cache.");
        return fullPath;
    }
}

public sealed record VidoreDocument(string Id, string File, string Sha256, string License,
    [property: JsonPropertyName("page_count")] int PageCount);
public sealed record VidorePage([property: JsonPropertyName("corpus_id")] int CorpusId,
    [property: JsonPropertyName("document_id")] string DocumentId,
    [property: JsonPropertyName("page_number")] int PageNumber, int? Width = null, int? Height = null);
public sealed record VidoreQuery(string Id, string Text, string Language, VidoreQrel[] Qrels);
public sealed record VidoreQrel([property: JsonPropertyName("corpus_id")] int CorpusId, int Score,
    [property: JsonPropertyName("bounding_boxes")] double[][] BoundingBoxes);
public sealed record PdfPageKey(string DocumentId, int PageNumber);
public sealed record PdfRetrievalAnchor(PdfPageKey Page, Guid PassageId, double Score,
    int? SemanticRank, int? KeywordRank, SourceRegion? Region);
public sealed record PdfRetrievalMetrics(int K, int RetrievedPages, int RelevantPages, int RetrievedRelevantPages,
    double Recall, double Ndcg, double ReciprocalRank, double? EvidenceAreaCoverage,
    double? EvidenceRegionRecall, int GoldRegions, int CoveredRegions);

public static class PdfBenchmarkMetrics
{
    public const double RegionRecallThreshold = 0.5;

    /// <summary>The best exposed passage determines each page's rank; repeated chunks never consume k.</summary>
    public static IReadOnlyList<PdfRetrievalAnchor> RankPages(IEnumerable<PdfRetrievalAnchor> anchors) => anchors
        .OrderByDescending(anchor => anchor.Score)
        .ThenBy(anchor => anchor.SemanticRank ?? int.MaxValue)
        .ThenBy(anchor => anchor.KeywordRank ?? int.MaxValue)
        .ThenBy(anchor => anchor.Page.DocumentId, StringComparer.Ordinal)
        .ThenBy(anchor => anchor.Page.PageNumber)
        .ThenBy(anchor => anchor.PassageId)
        .DistinctBy(anchor => anchor.Page).ToArray();

    public static PdfRetrievalMetrics Evaluate(VidoreQuery query, IReadOnlyDictionary<int, PdfPageKey> corpusPages,
        IEnumerable<PdfRetrievalAnchor> exposedAnchors, int k)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);
        var allAnchors = exposedAnchors.ToArray();
        var ranked = RankPages(allAnchors).Take(k).ToArray();
        // Multiple judgments/annotators for a page do not create additional relevant pages.
        var judgments = query.Qrels.GroupBy(qrel => corpusPages[qrel.CorpusId])
            .ToDictionary(group => group.Key, group => new
            {
                Grade = group.Max(qrel => qrel.Score),
                Boxes = group.SelectMany(qrel => qrel.BoundingBoxes).Distinct(DoubleArrayComparer.Instance).ToArray()
            });
        var rankedPages = ranked.Select(anchor => anchor.Page).ToHashSet();
        var grades = ranked.Select(anchor => judgments.TryGetValue(anchor.Page, out var judgment) ? judgment.Grade : 0).ToArray();
        var relevantRetrieved = grades.Count(grade => grade > 0);
        // ViDoRe's pytrec_eval/trec_eval nDCG uses the qrel value itself as gain.
        var dcg = grades.Select((grade, rank) => grade / Math.Log2(rank + 2)).Sum();
        var idcg = judgments.Values.Select(judgment => judgment.Grade).OrderDescending().Take(k)
            .Select((grade, rank) => grade / Math.Log2(rank + 2)).Sum();
        var firstRelevant = Array.FindIndex(grades, grade => grade > 0);
        double goldArea = 0, coveredArea = 0;
        var regionCount = 0;
        var coveredRegions = 0;
        foreach (var (page, judgment) in judgments)
        {
            var predicted = rankedPages.Contains(page)
                ? allAnchors.Where(anchor => anchor.Page == page && IsValidRegion(anchor.Region))
                    .Select(anchor => Box(anchor.Region!)).ToArray()
                : [];
            goldArea += UnionArea(judgment.Boxes);
            coveredArea += UnionArea(judgment.Boxes.SelectMany(gold => predicted
                .Select(region => Intersection(gold, region)).Where(box => box is not null).Select(box => box!)));
            foreach (var gold in judgment.Boxes)
            {
                regionCount++;
                var intersectedArea = UnionArea(predicted.Select(region => Intersection(gold, region))
                    .Where(box => box is not null).Select(box => box!));
                if (intersectedArea / Area(gold) >= RegionRecallThreshold) coveredRegions++;
            }
        }
        return new PdfRetrievalMetrics(k, ranked.Length, judgments.Count, relevantRetrieved,
            judgments.Count == 0 ? 0 : (double)relevantRetrieved / judgments.Count,
            idcg == 0 ? 0 : dcg / idcg, firstRelevant < 0 ? 0 : 1d / (firstRelevant + 1),
            goldArea == 0 ? null : Math.Clamp(coveredArea / goldArea, 0, 1),
            regionCount == 0 ? null : (double)coveredRegions / regionCount, regionCount, coveredRegions);
    }

    public static bool IsLiteralCitation(SearchResultItem preview, PassageInfo? read, PdfPageKey expectedPage) =>
        read is { ErrorCode: null, Requested: true } && read.PassageId == preview.PassageId &&
        read.DocumentId == preview.DocumentId && read.ContentId == preview.ContentId &&
        read.AttachmentChain.Count == 0 && preview.AttachmentChain.Count == 0 &&
        read.Location.Kind == LocationKind.Page && preview.Location.Kind == LocationKind.Page &&
        read.Location.Page == expectedPage.PageNumber && preview.Location.Page == expectedPage.PageNumber &&
        read.Location == preview.Location && read.SourcePath == preview.SourcePath &&
        preview.ExcerptStart >= 0 && preview.ExcerptLength >= 0 &&
        preview.ExcerptStart <= read.Text.Length && preview.ExcerptLength <= read.Text.Length - preview.ExcerptStart &&
        preview.ExcerptLength == preview.Excerpt.Length &&
        read.Text.AsSpan(preview.ExcerptStart, preview.ExcerptLength).SequenceEqual(preview.Excerpt.AsSpan());

    public static bool IsValidBox(double[] box) => box is not null && box.Length == 4 && box.All(double.IsFinite) &&
        box[0] >= 0 && box[1] >= 0 && box[2] <= 1 && box[3] <= 1 && box[2] > box[0] && box[3] > box[1];
    public static bool IsValidRegion(SourceRegion? region) => region is not null &&
        IsValidBox(Box(region));
    private static double[] Box(SourceRegion region) => [region.X, region.Y, region.X + region.Width, region.Y + region.Height];
    private static double Area(double[] box) => (box[2] - box[0]) * (box[3] - box[1]);
    private static double[]? Intersection(double[] left, double[] right)
    {
        double[] result = [Math.Max(left[0], right[0]), Math.Max(left[1], right[1]),
            Math.Min(left[2], right[2]), Math.Min(left[3], right[3])];
        return result[2] > result[0] && result[3] > result[1] ? result : null;
    }

    /// <summary>Rectangle union avoids double-counting overlapping gold boxes and repeated passage regions.</summary>
    private static double UnionArea(IEnumerable<double[]> input)
    {
        var boxes = input.ToArray();
        if (boxes.Length == 0) return 0;
        var boundaries = boxes.SelectMany(box => new[] { box[0], box[2] }).Distinct().Order().ToArray();
        double result = 0;
        for (var index = 1; index < boundaries.Length; index++)
        {
            var left = boundaries[index - 1];
            var right = boundaries[index];
            var intervals = boxes.Where(box => box[0] < right && box[2] > left)
                .Select(box => (Start: box[1], End: box[3])).OrderBy(interval => interval.Start).ToArray();
            double height = 0, start = 0, end = 0;
            foreach (var interval in intervals)
            {
                if (interval.Start > end) { height += end - start; start = interval.Start; end = interval.End; }
                else end = Math.Max(end, interval.End);
            }
            height += end - start;
            result += (right - left) * height;
        }
        return result;
    }

    private sealed class DoubleArrayComparer : IEqualityComparer<double[]>
    {
        public static readonly DoubleArrayComparer Instance = new();
        public bool Equals(double[]? left, double[]? right) => left is not null && right is not null && left.SequenceEqual(right);
        public int GetHashCode(double[] value)
        {
            var hash = new HashCode();
            foreach (var coordinate in value) hash.Add(coordinate);
            return hash.ToHashCode();
        }
    }
}
