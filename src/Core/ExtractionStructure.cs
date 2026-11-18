namespace ContextMole.Core;

/// <summary>
/// A text region in the displayed page or image. Coordinates are fractions of its width
/// and height (0..1), with the origin at the top left, independent of OCR rendering DPI.
/// </summary>
public sealed record SourceRegion(double X, double Y, double Width, double Height);

/// <summary>Recognized text and its own recognition confidence and location.</summary>
public sealed record OcrTextLine(string Text, double? Confidence, SourceRegion Region);

/// <summary>Geometry-based ordering shared by native PDF and OCR text.</summary>
public static class ExtractionLayout
{
    public static IReadOnlyList<OcrTextLine> OrderLines(IReadOnlyList<OcrTextLine> lines)
    {
        if (lines.Count < 2) return lines.ToArray();
        var result = new List<OcrTextLine>(lines.Count);
        Add(lines.ToArray(), 0);
        return result;

        void Add(OcrTextLine[] items, int depth)
        {
            if (items.Length < 2 || depth > 24) { result.AddRange(items.OrderBy(line => line.Region.Y).ThenBy(line => line.Region.X)); return; }
            var heights = items.Select(line => line.Region.Height).Where(height => height > 0).Order().ToArray();
            var medianHeight = heights.Length == 0 ? 0.01 : heights[heights.Length / 2];
            var horizontal = Gap(items, vertical: false);
            var vertical = Gap(items, vertical: true);
            var canSplitColumns = vertical.Size > 0.025 ||
                vertical.Size > 0.010 && IsColumnBand(items, vertical.At, 5, 0.18, 0.010);
            var hasLargeHorizontal = horizontal.Size > Math.Max(0.025, medianHeight * 1.5);
            if (hasLargeHorizontal)
            {
                var above = items.Where(line => line.Region.Y < horizontal.At).ToArray();
                var below = items.Where(line => line.Region.Y >= horizontal.At).ToArray();
                var minGutter = vertical.Size > 0.025 ? 0.025 : 0.010;
                // Keep whole columns together when an aligned paragraph gap cuts across multi-column bands on both sides,
                // while still peeling off single-column headings, banners, and footers first.
                if (canSplitColumns && IsColumnBand(items, vertical.At, 2, minGutter: minGutter) &&
                    IsColumnBand(above, Gap(above, vertical: true).At, 2, minGutter: minGutter) &&
                    IsColumnBand(below, Gap(below, vertical: true).At, 2, minGutter: minGutter) &&
                    Split(items, vertical.At, vertical: true, depth))
                    return;
                if (Split(items, horizontal.At, vertical: false, depth)) return;
            }
            if (canSplitColumns && Split(items, vertical.At, vertical: true, depth)) return;
            if (items.Length >= 6 && UnblockColumnGutter(items, Math.Max(0.002, medianHeight * 0.25)) is { } unblockAt &&
                Split(items, unblockAt, vertical: false, depth)) return;

            // Row groups avoid the non-transitive pairwise 'same line' comparison previously used.
            var remaining = items.OrderBy(line => line.Region.Y).ThenBy(line => line.Region.X).ToList();
            while (remaining.Count > 0)
            {
                var first = remaining[0];
                var tolerance = Math.Max(0.003, first.Region.Height * 0.5);
                var row = remaining.Where(line => line.Region.Y - first.Region.Y <= tolerance)
                    .OrderBy(line => line.Region.X).ToArray();
                result.AddRange(row);
                foreach (var line in row) remaining.Remove(line);
            }
        }

        bool Split(OcrTextLine[] items, double at, bool vertical, int depth)
        {
            var first = items.Where(line => (vertical ? line.Region.X : line.Region.Y) < at).ToArray();
            var second = items.Where(line => (vertical ? line.Region.X : line.Region.Y) >= at).ToArray();
            if (first.Length == 0 || second.Length == 0) return false;
            Add(first, depth + 1);
            Add(second, depth + 1);
            return true;
        }

        static bool IsColumnBand(OcrTextLine[] subset, double verticalAt, int minPerColumn, double minMaxWidth = 0,
            double minGutter = 0.025)
        {
            if (subset.Length < minPerColumn * 2) return false;
            var gap = Gap(subset, vertical: true);
            if (gap.Size <= minGutter) return false;
            var cut = verticalAt > 0 ? verticalAt : gap.At;
            var left = subset.Where(line => line.Region.X < cut).ToArray();
            var right = subset.Where(line => line.Region.X >= cut).ToArray();
            if (left.Length < minPerColumn || right.Length < minPerColumn) return false;
            if (minMaxWidth > 0 && (left.Max(line => line.Region.Width) < minMaxWidth ||
                    right.Max(line => line.Region.Width) < minMaxWidth))
                return false;
            var leftTop = left.Min(line => line.Region.Y);
            var leftBottom = left.Max(line => line.Region.Y + line.Region.Height);
            var rightTop = right.Min(line => line.Region.Y);
            var rightBottom = right.Max(line => line.Region.Y + line.Region.Height);
            var minSpan = Math.Min(leftBottom - leftTop, rightBottom - rightTop);
            var overlap = Math.Min(leftBottom, rightBottom) - Math.Max(leftTop, rightTop);
            return minSpan > 0 && overlap >= minSpan * 0.4;
        }

        static bool HasBlockedColumns(OcrTextLine[] subset) =>
            IsColumnBand(subset, 0, 3, 0.12) || IsColumnBand(subset, 0, 5, 0.18, 0.010);

        static double? UnblockColumnGutter(OcrTextLine[] items, double minGap)
        {
            var gaps = AllGaps(items, vertical: false, minGap);
            if (gaps.Length == 0) return null;
            foreach (var candidate in gaps.OrderByDescending(gap => gap.Size))
            {
                var above = items.Where(line => line.Region.Y < candidate.At).ToArray();
                var below = items.Where(line => line.Region.Y >= candidate.At).ToArray();
                if (HasBlockedColumns(above) || HasBlockedColumns(below))
                    return candidate.At;
            }
            // When both a full-width running header and a full-width footer/caption sit tightly
            // against a multi-column body, peel the top header first so the next pass peels the footer.
            var topCuts = gaps.Take(8).ToArray();
            var bottomCuts = gaps.Skip(Math.Max(0, gaps.Length - 8)).ToArray();
            foreach (var top in topCuts)
            {
                foreach (var bottom in bottomCuts)
                {
                    if (bottom.At <= top.At) continue;
                    var middle = items.Where(line => line.Region.Y >= top.At && line.Region.Y < bottom.At).ToArray();
                    if (HasBlockedColumns(middle)) return top.At;
                }
            }
            return null;
        }

        static (double Size, double At)[] AllGaps(OcrTextLine[] items, bool vertical, double minSize)
        {
            var spans = items.Select(line => (Start: vertical ? line.Region.X : line.Region.Y,
                    End: vertical ? line.Region.X + line.Region.Width : line.Region.Y + line.Region.Height))
                .OrderBy(span => span.Start).ToArray();
            var end = spans[0].End;
            var gaps = new List<(double Size, double At)>();
            foreach (var span in spans.Skip(1))
            {
                if (span.Start - end >= minSize) gaps.Add((span.Start - end, (span.Start + end) / 2));
                end = Math.Max(end, span.End);
            }
            return gaps.ToArray();
        }

        static (double Size, double At) Gap(OcrTextLine[] items, bool vertical)
        {
            var spans = items.Select(line => (Start: vertical ? line.Region.X : line.Region.Y,
                    End: vertical ? line.Region.X + line.Region.Width : line.Region.Y + line.Region.Height))
                .OrderBy(span => span.Start).ToArray();
            var end = spans[0].End;
            var best = (Size: 0d, At: 0d);
            foreach (var span in spans.Skip(1))
            {
                if (span.Start - end > best.Size) best = (span.Start - end, (span.Start + end) / 2);
                end = Math.Max(end, span.End);
            }
            return best;
        }
    }
}
