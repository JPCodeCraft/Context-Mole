using System.Text.RegularExpressions;

using ContextMole.Core;

using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace ContextMole.Documents;

public sealed partial class DocumentExtractionRegistry
{
    private sealed record PdfTextBlock(string Text, SourceRegion? Region, double FontSize = 0,
        ExtractionMethod Method = ExtractionMethod.NativeText, double? Confidence = null,
        bool IsTable = false, string? Warning = null);

    private static IReadOnlyList<PdfTextBlock> NativePdfBlocks(Page page)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance)
            .Where(word => !string.IsNullOrWhiteSpace(word.Text)).ToArray();
        if (words.Length == 0) return [];
        if (words.Any(word => word.TextOrientation != TextOrientation.Horizontal))
        {
            // Horizontal XY cuts reverse vertical baselines. Preserve the PDF text sequence and
            // displayed coordinates rather than inventing a reading order for rotated layouts.
            var region = UnionRegions(words.Select(word => PdfRegion(word.BoundingBox, page)));
            return [new PdfTextBlock(ContentOrderTextExtractor.GetText(page, addDoubleNewline: true), region,
                Median(page.Letters.Select(letter => letter.PointSize)),
                Warning: "rotated_layout: native content order retained; column and table layout is unverified.")];
        }
        try
        {
            var tableWords = new HashSet<Word>();
            var tables = PdfTables(page, words, tableWords);
            var blocks = RecursiveXYCut.Instance.GetBlocks(words.Where(word => !tableWords.Contains(word)))
                .Select(block =>
                {
                    var size = Median(block.TextLines.SelectMany(line => line.Words).SelectMany(word => word.Letters)
                        .Select(letter => letter.PointSize));
                    var bounds = block.BoundingBox;
                    var inferred = bounds.Height <= 0 && size > 0;
                    if (inferred) bounds = new PdfRectangle(bounds.Left, bounds.Bottom - size * 0.2,
                        bounds.Right, bounds.Top + size * 0.8);
                    return new PdfTextBlock(block.Text, PdfRegion(bounds, page), size,
                        Warning: inferred ? "native_region_inferred: missing font geometry approximated from text baseline." : null);
                }).Concat(tables).ToArray();
            return OrderPdfBlocks(blocks);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ArithmeticException)
        {
            // Keep all native evidence if malformed geometry defeats layout analysis.
            return [new PdfTextBlock(ContentOrderTextExtractor.GetText(page, addDoubleNewline: true), null,
                Warning: "layout_fallback: text geometry could not be ordered reliably.")];
        }
    }

    private static IReadOnlyList<PdfTextBlock> OrderPdfBlocks(IReadOnlyList<PdfTextBlock> blocks)
    {
        if (blocks.Count < 2 || blocks.Any(block => block.Region is null)) return blocks;
        var mapped = blocks.Select((block, index) => new OcrTextLine(index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            null, block.Region!)).ToArray();
        return ExtractionLayout.OrderLines(mapped)
            .Select(line => blocks[int.Parse(line.Text, System.Globalization.CultureInfo.InvariantCulture)]).ToArray();
    }

    private static IReadOnlyList<PdfTextBlock> PdfTables(Page page, Word[] words, HashSet<Word> used)
    {
        var rows = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(word => word.BoundingBox.Top).ThenBy(word => word.BoundingBox.Left))
        {
            var row = rows.LastOrDefault();
            if (row is null || Math.Abs(row[0].BoundingBox.Top - word.BoundingBox.Top) >
                Math.Max(2, Math.Min(row[0].BoundingBox.Height, word.BoundingBox.Height) * 0.45))
            { row = []; rows.Add(row); }
            row.Add(word);
        }
        var candidates = rows.Select(row =>
        {
            var cells = new List<List<Word>>();
            foreach (var word in row.OrderBy(word => word.BoundingBox.Left))
            {
                var previous = cells.LastOrDefault();
                var gap = previous is null ? 0 : word.BoundingBox.Left - previous[^1].BoundingBox.Right;
                if (previous is null || gap > Math.Max(12, word.BoundingBox.Height * 1.8))
                { previous = []; cells.Add(previous); }
                previous.Add(word);
            }
            return cells;
        }).ToArray();
        var result = new List<PdfTextBlock>();
        for (var start = 0; start < candidates.Length; start++)
        {
            var first = candidates[start];
            if (first.Count < 2 || first.Count > 12) continue;
            var end = start + 1;
            var tolerance = Math.Max(4, (double)page.Width * 0.015);
            while (end < candidates.Length && candidates[end].Count == first.Count &&
                   candidates[end].Select((cell, index) => Math.Abs(cell[0].BoundingBox.Left - first[index][0].BoundingBox.Left) <= tolerance).All(value => value) &&
                   rows[end - 1][0].BoundingBox.Top - rows[end][0].BoundingBox.Top <= Math.Max(40, rows[end][0].BoundingBox.Height * 3.5)) end++;
            if (end - start < 3) continue;
            var numericRows = candidates.Skip(start).Take(end - start).Count(row => row.Any(cell =>
                cell.Any(word => word.Text.Any(char.IsDigit)) &&
                string.Join(' ', cell.Select(word => word.Text)).Count(char.IsDigit) >=
                string.Join(' ', cell.Select(word => word.Text)).Count(char.IsLetter)));
            var tableRows = candidates.Skip(start).Take(end - start).ToArray();
            var all = tableRows.SelectMany(row => row.SelectMany(cell => cell)).ToArray();
            var left = all.Min(word => word.BoundingBox.Left);
            var right = all.Max(word => word.BoundingBox.Right);
            var top = all.Max(word => word.BoundingBox.Top);
            var bottom = all.Min(word => word.BoundingBox.Bottom);
            var ruled = HasPdfTableRules(page, left, bottom, right, top, first.Count);
            // Aligned prose columns are not enough evidence of a table. Require repeated numeric
            // cells or a visible grid enclosing the same rows and column boundaries.
            if (numericRows < 2 && !ruled) continue;
            foreach (var word in all) used.Add(word);
            result.Add(new PdfTextBlock(string.Join('\n', tableRows.Select(row => string.Join('\t',
                    row.Select(cell => string.Join(' ', cell.Select(word => word.Text)))))),
                PdfRegion(new PdfRectangle(left, bottom, right, top), page), IsTable: true,
                Warning: ruled
                    ? "table_inferred: grid rules support aligned cells; merged cells and column semantics are unverified."
                    : "table_inferred: cells inferred from aligned text; merged cells and column semantics are unverified."));
            start = end - 1;
        }
        return result;
    }

    private static bool HasPdfTableRules(Page page, double left, double bottom, double right, double top, int columns)
    {
        var lines = page.Paths.SelectMany(path => path).SelectMany(subpath => subpath.Commands)
            .OfType<PdfSubpath.Line>().Select(line => line.GetBoundingRectangle())
            .Where(rectangle => rectangle is not null).Select(rectangle => rectangle!.Value).ToArray();
        var horizontal = lines.Count(line => line.Height <= 2 && line.Width >= (right - left) * 0.8 &&
            line.Left <= left + 4 && line.Right >= right - 4 && line.Top >= bottom - 20 && line.Bottom <= top + 20);
        var vertical = lines.Count(line => line.Width <= 2 && line.Height >= (top - bottom) * 0.8 &&
            line.Bottom <= bottom + 4 && line.Top >= top - 4 && line.Left >= left - 20 &&
            line.Right <= right + Math.Max(20, (right - left) / columns));
        return horizontal >= 3 && vertical >= columns + 1;
    }

    private static SourceRegion PdfRegion(PdfRectangle rectangle, Page page)
    {
        var points = new[] { rectangle.TopLeft, rectangle.TopRight, rectangle.BottomLeft, rectangle.BottomRight };
        var left = points.Min(point => point.X);
        var right = points.Max(point => point.X);
        var bottom = points.Min(point => point.Y);
        var top = points.Max(point => point.Y);
        var width = Math.Max(1, (double)page.Width);
        var height = Math.Max(1, (double)page.Height);
        var x = Math.Clamp(left / width, 0, 1);
        var y = Math.Clamp(1 - top / height, 0, 1);
        return new SourceRegion(x, y, Math.Max(0, Math.Clamp(right / width, 0, 1) - x),
            Math.Max(0, Math.Clamp(1 - bottom / height, 0, 1) - y));
    }

    private static bool HasUncoveredPdfImages(Page page, IReadOnlyList<PdfTextBlock> blocks)
    {
        foreach (var image in page.GetImages())
        {
            var region = PdfRegion(image.BoundingBox, page);
            var area = region.Width * region.Height;
            if (area < 0.04) continue; // Small logos and decorative icons do not justify full-page OCR.
            var textCoverage = blocks.Where(block => block.Region is not null)
                .Sum(block => RegionIntersection(region, block.Region!));
            if (textCoverage < area * 0.12) return true;
        }
        return false;
    }

    private static double RegionIntersection(SourceRegion first, SourceRegion second) =>
        Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X)) *
        Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));

    private static double TextQuality(string text)
    {
        var normalized = TextNormalization.ForSearch(text);
        if (normalized.Length == 0) return 0;
        var printable = normalized.Count(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) ||
            char.IsPunctuation(character) || char.IsSymbol(character));
        var replacements = normalized.Count(character => character is '\uFFFD' or '\0');
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var longWords = words.Count(word => word.Length > 30);
        return Math.Clamp(printable / (double)normalized.Length - replacements / (double)normalized.Length * 8 -
            longWords / (double)Math.Max(1, words.Length) * 0.5, 0, 1);
    }

    private static IReadOnlyList<PdfTextBlock> ReconcilePdfText(IReadOnlyList<PdfTextBlock> native, OcrResult ocr)
    {
        if (string.IsNullOrWhiteSpace(ocr.Text)) return native;
        var recognized = OcrBlocks(ocr);
        if (native.Count == 0)
            return recognized;
        var nativeText = string.Join('\n', native.Select(block => block.Text));
        if (native.All(block => TextQuality(block.Text) < 0.7) && ocr.Confidence is null or >= 50 &&
            TextQuality(ocr.Text) > TextQuality(nativeText) + 0.1)
            return recognized;

        var result = native.ToList();
        var nativeKeys = native.SelectMany(block => block.Text.Split('\n'))
            .Select(EvidenceKey).Where(key => key.Length > 0).ToArray();
        foreach (var line in recognized)
        {
            if (TextQuality(line.Text) < 0.7 || line.Confidence is < 50) continue;
            var key = EvidenceKey(line.Text);
            if (key.Length == 0 || nativeKeys.Any(nativeKey => nativeKey.Contains(key, StringComparison.Ordinal) ||
                    key.Contains(nativeKey, StringComparison.Ordinal) && nativeKey.Length >= key.Length * 0.8)) continue;
            var overlapping = native.Where(block => block.Region is { } region && line.Region is { } lineRegion &&
                RegionIntersection(region, lineRegion) > lineRegion.Width * lineRegion.Height * 0.5).ToArray();
            // Valid native text is preferred in the same region. Add OCR only for missing image regions.
            if (overlapping.Any(block => TextQuality(block.Text) >= 0.7)) continue;
            var defective = native.Where(block => TextQuality(block.Text) < 0.7 &&
                TextQuality(line.Text) > TextQuality(block.Text) + 0.1 && block.Region is { } region && line.Region is { } lineRegion &&
                RegionIntersection(region, lineRegion) > region.Width * region.Height * 0.5).ToArray();
            foreach (var block in defective) result.Remove(block);
            result.Add(defective.Length == 0 ? line : line with
            { Warning = string.Join(' ', new[] { line.Warning, "native_text_replaced: illegible native glyph mapping replaced by OCR in this region." }.Where(value => value is not null)) });
        }
        return OrderPdfBlocks(result);
    }

    private static IReadOnlyList<PdfTextBlock> OcrBlocks(OcrResult ocr)
    {
        if (ocr.Lines is not { Count: > 0 })
            return string.IsNullOrWhiteSpace(ocr.Text) ? [] :
                [new PdfTextBlock(ocr.Text, null, Method: ExtractionMethod.Ocr, Confidence: ocr.Confidence,
                    Warning: "ocr_region_unknown: recognition did not provide text locations.")];
        var lines = ocr.Lines.ToArray();
        var rows = new List<List<OcrTextLine>>();
        foreach (var line in lines.OrderBy(line => line.Region.Y).ThenBy(line => line.Region.X))
        {
            var row = rows.LastOrDefault();
            if (row is null || line.Region.Y - row[0].Region.Y > Math.Max(0.004, row[0].Region.Height * 0.5))
            { row = []; rows.Add(row); }
            row.Add(line);
        }
        var used = new HashSet<OcrTextLine>();
        var blocks = new List<PdfTextBlock>();
        for (var start = 0; start < rows.Count; start++)
        {
            var first = rows[start].OrderBy(line => line.Region.X).ToArray();
            if (first.Length < 2 || first.Length > 12) continue;
            var end = start + 1;
            while (end < rows.Count && rows[end].Count == first.Length &&
                   rows[end].OrderBy(line => line.Region.X).Select((line, index) =>
                       Math.Abs(line.Region.X - first[index].Region.X) < 0.025).All(value => value) &&
                   rows[end][0].Region.Y - rows[end - 1][0].Region.Y < Math.Max(0.08, first[0].Region.Height * 3.5)) end++;
            var selected = rows.Skip(start).Take(end - start).ToArray();
            if (selected.Length < 3 || selected.Count(row => row.Any(line =>
                    line.Text.Count(char.IsDigit) > 0 && line.Text.Count(char.IsDigit) >= line.Text.Count(char.IsLetter))) < 2) continue;
            var cells = selected.SelectMany(row => row).ToArray();
            foreach (var cell in cells) used.Add(cell);
            var region = UnionRegions(cells.Select(cell => cell.Region));
            blocks.Add(new PdfTextBlock(string.Join('\n', selected.Select(row => string.Join('\t',
                    row.OrderBy(line => line.Region.X).Select(line => line.Text)))), region,
                Method: ExtractionMethod.Ocr, Confidence: ocr.Confidence, IsTable: true,
                Warning: "table_inferred: OCR cells inferred from aligned text; merged cells and column semantics are unverified."));
            start = end - 1;
        }
        blocks.AddRange(lines.Where(line => !used.Contains(line)).Select(line =>
            new PdfTextBlock(line.Text, line.Region, Method: ExtractionMethod.Ocr, Confidence: line.Confidence ?? ocr.Confidence)));
        return OrderPdfBlocks(blocks);
    }

    private static SourceRegion UnionRegions(IEnumerable<SourceRegion> regions)
    {
        var items = regions.ToArray();
        var left = items.Min(region => region.X);
        var top = items.Min(region => region.Y);
        return new SourceRegion(left, top, items.Max(region => region.X + region.Width) - left,
            items.Max(region => region.Y + region.Height) - top);
    }

    private static IReadOnlyList<ExtractedSection> ImageOcrSections(OcrResult ocr, SourceLocation location) =>
        OcrBlocks(ocr).Select((block, index) => new ExtractedSection(block.Text,
            location with { Region = block.Region, StructurePath = block.IsTable ? $"table[{index + 1}]" : $"block[{index + 1}]",
                LayoutWarning = block.Warning }, ExtractionMethod.Ocr, block.Confidence,
            SectionKey: $"image:{location.ImageFrame ?? location.Page ?? 1}")).ToArray();

    private static string EvidenceKey(string text) => string.Concat(TextNormalization.ForSearch(text).Normalize()
        .Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private static void AddPdfSections(List<ExtractedSection> sections, IReadOnlyList<PdfTextBlock> blocks,
        int pageNumber, HeadingContext heading)
    {
        // A page with one title and one long body block should use the body font as its baseline.
        var sizes = blocks.Where(block => !block.IsTable && block.FontSize > 0)
            .GroupBy(block => block.FontSize).OrderBy(group => group.Key)
            .Select(group => (Size: group.Key, Weight: group.Sum(block => (long)Math.Max(1, block.Text.Length)))).ToArray();
        var midpoint = sizes.Sum(item => item.Weight) / 2;
        long cumulative = 0;
        var typicalSize = 0d;
        foreach (var item in sizes)
        { cumulative += item.Weight; if (cumulative > midpoint) { typicalSize = item.Size; break; } }
        var ordinal = 0;
        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Text)) continue;
            var isHeading = typicalSize > 0 && block.FontSize >= typicalSize * 1.2 &&
                block.Text.Length <= 180 && !block.Text.Contains('\n') &&
                block.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 18 && !block.IsTable;
            if (isHeading) heading.Start(block.FontSize >= typicalSize * 1.6 ? 1 : 2, block.Text, "pdf-heading");
            var path = block.IsTable ? $"page[{pageNumber}]/table[{++ordinal}]" : $"page[{pageNumber}]/block[{++ordinal}]";
            sections.Add(new ExtractedSection(block.Text,
                new SourceLocation(LocationKind.Page, Page: pageNumber, StructurePath: path, Region: block.Region,
                    LayoutWarning: block.Warning), block.Method, block.Confidence, heading.Heading,
                heading.Key == "document" ? $"page:{pageNumber}" : heading.Key, heading.Path));
        }
    }

    private static void MarkRepeatedPdfMargins(List<ExtractedSection> sections)
    {
        var pageCount = sections.Select(section => section.Location.Page).Distinct().Count();
        if (pageCount < 3) return;
        var repeated = sections.Where(section => section.Location.Region is { } region &&
                (region.Y < 0.09 || region.Y + region.Height > 0.91) && section.Text.Length < 240)
            .GroupBy(section => Regex.Replace(EvidenceKey(section.Text), @"\d+", "#"))
            .Where(group => group.Key.Length > 0 && group.Select(section => section.Location.Page).Distinct().Count() >=
                Math.Max(3, (int)Math.Ceiling(pageCount * 0.6)))
            .SelectMany(group => group).ToHashSet();
        for (var index = 0; index < sections.Count; index++)
            if (repeated.Contains(sections[index])) sections[index] = sections[index] with { IsBoilerplate = true };
    }
}
