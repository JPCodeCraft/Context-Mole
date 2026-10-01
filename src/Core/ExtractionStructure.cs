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
            // Separate headings and widely spaced paragraphs before looking for a column gutter.
            var horizontal = Gap(items, vertical: false);
            if (horizontal.Size > Math.Max(0.025, medianHeight * 1.5) &&
                Split(items, horizontal.At, vertical: false, depth)) return;
            var vertical = Gap(items, vertical: true);
            if (vertical.Size > 0.025 && Split(items, vertical.At, vertical: true, depth)) return;

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
