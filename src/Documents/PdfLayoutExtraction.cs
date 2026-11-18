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
        bool IsTable = false, string? Warning = null, double BoldFraction = 0);

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
                    var (size, bold) = LetterStyle(block.TextLines.SelectMany(line => line.Words));
                    var bounds = block.BoundingBox;
                    var inferred = bounds.Height <= 0 && size > 0;
                    if (inferred) bounds = new PdfRectangle(bounds.Left, bounds.Bottom - size * 0.2,
                        bounds.Right, bounds.Top + size * 0.8);
                    return new PdfTextBlock(block.Text, PdfRegion(bounds, page), size,
                        Warning: inferred ? "native_region_inferred: missing font geometry approximated from text baseline." : null,
                        BoldFraction: bold);
                }).Concat(tables).ToArray();
            return JoinLineMarkers(OrderPdfBlocks(blocks));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ArithmeticException)
        {
            // Keep all native evidence if malformed geometry defeats layout analysis.
            return [new PdfTextBlock(ContentOrderTextExtractor.GetText(page, addDoubleNewline: true), null,
                Warning: "layout_fallback: text geometry could not be ordered reliably.")];
        }
    }

    /// <summary>Median point size and the share of bold letters, for heading recognition.</summary>
    private static (double Size, double BoldFraction) LetterStyle(IEnumerable<Word> words)
    {
        var letters = words.SelectMany(word => word.Letters).Where(letter => !string.IsNullOrWhiteSpace(letter.Value)).ToArray();
        return (Median(letters.Select(letter => letter.PointSize)),
            letters.Length == 0 ? 0 : letters.Count(IsBoldLetter) / (double)letters.Length);
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
        var result = PdfGridTables(page, words, used).ToList();
        var rows = new List<List<Word>>();
        foreach (var word in words.Where(word => !used.Contains(word))
                     .OrderByDescending(word => word.BoundingBox.Top).ThenBy(word => word.BoundingBox.Left))
        {
            var row = rows.LastOrDefault();
            if (row is null || Math.Abs(row[0].BoundingBox.Top - word.BoundingBox.Top) >
                Math.Max(2, Math.Max(row[0].BoundingBox.Height, word.BoundingBox.Height) * 0.6))
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
                // Inter-cell gutters can be smaller than two glyph heights in compact
                // numeric tables. Normal word spaces remain below this threshold.
                if (previous is null || gap > Math.Max(6, word.BoundingBox.Height * 0.85))
                { previous = []; cells.Add(previous); }
                previous.Add(word);
            }
            return cells;
        }).ToArray();
        var previousTableEnd = 0;
        for (var start = 0; start < candidates.Length; start++)
        {
            var first = candidates[start];
            if (first.Count < 2 || first.Count > 12) continue;
            var end = start + 1;
            var tolerance = Math.Max(4, (double)page.Width * 0.008);
            while (end < candidates.Length && candidates[end].Count == first.Count &&
                   candidates[end].Select((cell, index) => CellsAlign(cell, first[index], tolerance,
                       rowLabel: index == 0)).All(value => value) &&
                   rows[end - 1][0].BoundingBox.Top - rows[end][0].BoundingBox.Top <= Math.Max(40, rows[end][0].BoundingBox.Height * 3.5)) end++;
            if (end - start < 3) continue;
            var tableRows = candidates.Skip(start).Take(end - start).ToArray();
            if (first.Count == 2 && tableRows.All(row => ListItemMarker().IsMatch(string.Join(' ', row[0].Select(word => word.Text)))))
            {
                // Numbered steps ("1." beside "Calculate the trip distance") align like a two-column
                // table but are prose: keep them as one text block that merges with its lead-in.
                var items = tableRows.SelectMany(row => row.SelectMany(cell => cell)).ToArray();
                var (size, bold) = LetterStyle(items);
                result.Add(new PdfTextBlock(string.Join('\n', tableRows.Select(row =>
                        string.Join(' ', row.SelectMany(cell => cell).Select(word => word.Text)))),
                    UnionRegions(items.Select(word => PdfRegion(word.BoundingBox, page))), size, BoldFraction: bold));
                foreach (var word in items) used.Add(word);
                previousTableEnd = end;
                start = end - 1;
                continue;
            }
            var numericRows = Enumerable.Range(0, first.Count).Max(column => tableRows.Count(row =>
            {
                var text = string.Join(' ', row[column].Select(word => word.Text));
                return text.Any(char.IsDigit) && text.Count(char.IsDigit) >= text.Count(char.IsLetter);
            }));
            var all = tableRows.SelectMany(row => row.SelectMany(cell => cell)).ToArray();
            var left = all.Min(word => word.BoundingBox.Left);
            var right = all.Max(word => word.BoundingBox.Right);
            var top = all.Max(word => word.BoundingBox.Top);
            var bottom = all.Min(word => word.BoundingBox.Bottom);
            var ruled = HasPdfTableRules(page, left, bottom, right, top, first.Count);
            // Aligned prose (especially bibliographies) can have scattered numeric fragments
            // in alternating columns. Require a consistently numeric column, or visible rules.
            if (numericRows < Math.Max(2, (int)Math.Ceiling(tableRows.Length * 0.6)) && !ruled) continue;
            var (header, headerWords) = PdfTableHeader(page, rows, candidates, start, previousTableEnd, tableRows, left, right);
            all = all.Concat(headerWords).ToArray();
            top = all.Max(word => word.BoundingBox.Top);
            foreach (var word in all) used.Add(word);
            var body = tableRows.Select(row => string.Join('\t', row.Select(cell => string.Join(' ', cell.Select(word => word.Text)))));
            result.Add(new PdfTextBlock(string.Join('\n', header is null ? body : body.Prepend(header)),
                PdfRegion(new PdfRectangle(left, bottom, right, top), page), IsTable: true,
                Warning: ruled
                    ? "table_inferred: grid rules support aligned cells; merged cells and column semantics are unverified."
                    : "table_inferred: cells inferred from aligned text; merged cells and column semantics are unverified."));
            previousTableEnd = end;
            start = end - 1;
        }
        return result;

        static bool CellsAlign(List<Word> first, List<Word> second, double tolerance, bool rowLabel)
        {
            // Nested row labels commonly change indentation by one em even though all data
            // columns are unchanged. Keep their small indentation shifts inside the table;
            // this allowance must not loosen alignment for its numeric/data columns.
            if (rowLabel && first.Any(word => word.Text.Any(char.IsLetter)) &&
                second.Any(word => word.Text.Any(char.IsLetter)))
                tolerance = Math.Max(tolerance, Median(first.Concat(second).SelectMany(word => word.Letters)
                    .Select(letter => letter.PointSize)) * 1.25);
            var firstLeft = first[0].BoundingBox.Left;
            var secondLeft = second[0].BoundingBox.Left;
            var firstRight = first[^1].BoundingBox.Right;
            var secondRight = second[^1].BoundingBox.Right;
            return Math.Abs(firstLeft - secondLeft) <= tolerance ||
                   Math.Abs(firstRight - secondRight) <= tolerance ||
                   Math.Abs((firstLeft + firstRight - secondLeft - secondRight) / 2) <= tolerance;
        }
    }

    /// <summary>
    /// Column headings above aligned data rows. They rarely share the data's cell count:
    /// labels wrap ("Read &amp;" over "Write"), sit between lines, or span several columns
    /// ("Literacy Status"). Rows directly above the table are taken while every word lies over
    /// the table, no cell reads like data or a sentence, and no caption ("Table 6: ...") is
    /// reached. The band must name at least half of the columns on its own, otherwise a short
    /// line of prose above a table would be mistaken for its header.
    /// </summary>
    private static (string? Header, IReadOnlyList<Word> Words) PdfTableHeader(Page page, List<List<Word>> rows,
        List<List<Word>>[] candidates, int start, int floor, List<List<Word>>[] tableRows, double left, double right)
    {
        var columns = tableRows[0].Count;
        var extents = Enumerable.Range(0, columns).Select(column => (
            Left: tableRows.Min(row => row[column][0].BoundingBox.Left),
            Right: tableRows.Max(row => row[column][^1].BoundingBox.Right))).ToArray();
        var tolerance = Math.Max(6, (double)page.Width * 0.02);
        var fragments = Enumerable.Range(0, columns).Select(_ => new List<string>()).ToArray();
        var named = new HashSet<int>();
        var words = new List<Word>();
        var below = rows[start];
        for (var index = start - 1; index >= floor && index >= start - 5; index--)
        {
            var row = rows[index];
            var height = Math.Max(row[0].BoundingBox.Height, below[0].BoundingBox.Height);
            // Label lines are set close together; a wider gap separates a title from them.
            var gap = row[0].BoundingBox.Bottom - below[0].BoundingBox.Top;
            if (gap > (index == start - 1 ? Math.Max(24, height * 3) : height * 1.2) ||
                row.Any(word => word.BoundingBox.Left < left - tolerance || word.BoundingBox.Right > right + tolerance))
                break;
            var cells = candidates[index].Select(cell => (Text: string.Join(' ', cell.Select(word => word.Text)),
                Left: cell[0].BoundingBox.Left, Right: cell[^1].BoundingBox.Right)).ToArray();
            if (cells.Any(cell => TableCaption().IsMatch(cell.Text) || cell.Text.Split(' ').Length > 6 || LooksNumeric(cell.Text)))
                break;
            var placed = new List<(int Column, string Text)>();
            foreach (var cell in cells)
            {
                var spanned = Enumerable.Range(0, columns).Where(column =>
                {
                    var overlap = Math.Min(cell.Right, extents[column].Right) - Math.Max(cell.Left, extents[column].Left);
                    var center = (extents[column].Left + extents[column].Right) / 2;
                    return overlap >= Math.Min(cell.Right - cell.Left, extents[column].Right - extents[column].Left) * 0.5 ||
                           center >= cell.Left && center <= cell.Right;
                }).ToArray();
                if (spanned.Length == 0)
                    spanned = [Enumerable.Range(0, columns).MinBy(column => Math.Abs(
                        (extents[column].Left + extents[column].Right - cell.Left - cell.Right) / 2))];
                if (spanned.Length == 1) named.Add(spanned[0]);
                placed.AddRange(spanned.Select(column => (column, cell.Text)));
            }
            // Fragments are collected bottom-up; headings read top-down.
            foreach (var (column, text) in placed) fragments[column].Insert(0, text);
            words.AddRange(row);
            below = row;
        }
        if (words.Count == 0 || named.Count < Math.Max(2, (columns + 1) / 2)) return (null, []);
        return (string.Join('\t', fragments.Select(parts => string.Join(' ', parts))), words);

        // Labels such as "9-12 std." or "2019" name columns; a value has at most a unit letter.
        static bool LooksNumeric(string text) => text.Any(char.IsDigit) && text.Count(char.IsLetter) < 2 &&
            !HeaderYear().IsMatch(text.Trim());
    }

    [GeneratedRegex(@"^(?:FY\s?)?(?:1[89]|2[01])\d\d[A-Za-z]?$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderYear();

    // "1.", "a)", "(iv)", "B." or a bullet glyph at the start of a list item.
    [GeneratedRegex(@"^(?:\(?(?:\d{1,3}|[A-Za-z]|[ivxlcIVXLC]{1,6})[.)]|[•◦▪‣⁃●○■□►▸–*])$", RegexOptions.CultureInvariant)]
    private static partial Regex ListItemMarker();

    // "Table 6 : ...", "Table 2-1.", "TABLE IV", "Tabela 3 –": the caption that names a table.
    [GeneratedRegex(@"^(?:table|tab\.|tabela|tabla|tableau|tabelle|quadro|exhibit)\s*(?:[A-Z]?\d+[A-Za-z]?(?:[.\-\u2013]\d+)*|[IVXLC]+)(?:\s|[.:\-\u2013]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TableCaption();

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
        var nonWhitespace = normalized.Count(character => !char.IsWhiteSpace(character));
        var alphaNumeric = normalized.Count(char.IsLetterOrDigit);
        var symbolNoisePenalty = nonWhitespace >= 3 && alphaNumeric / (double)nonWhitespace < 0.45
            ? (0.55 - alphaNumeric / (double)nonWhitespace) * 1.5
            : 0;
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var longWords = words.Count(word => word.Length > 30);
        var garbledWords = words.Count(IsGarbledOcrToken);
        return Math.Clamp(printable / (double)normalized.Length - replacements / (double)normalized.Length * 8 -
            longWords / (double)Math.Max(1, words.Length) * 0.5 - symbolNoisePenalty -
            garbledWords / (double)Math.Max(1, words.Length) * 1.4, 0, 1);
    }

    private static bool IsGarbledOcrToken(string token)
    {
        if (token.Length < 4 || token.Contains("://", StringComparison.Ordinal) || token.Contains('@') ||
            token.StartsWith('/') || token.Contains(":\\", StringComparison.Ordinal))
            return false;
        const string outerTrim = "[\"'«“‘$#.,;:!?]\"'»”’%";
        var core = token.AsSpan().Trim(outerTrim);
        while (core.Length >= 2 && core[0] == '(' && core[^1] == ')')
            core = core[1..^1].Trim(outerTrim);
        while (core.Length > 0 && core[0] == '(' && CountChar(core, '(') > CountChar(core, ')'))
            core = core[1..].TrimStart(outerTrim);
        while (core.Length > 0 && core[^1] == ')' && CountChar(core, ')') > CountChar(core, '('))
            core = core[..^1].TrimEnd(outerTrim);
        if (core.Length < 4 || !core.ToString().Any(char.IsLetterOrDigit)) return false;
        if (core.IndexOf("()", StringComparison.Ordinal) is >= 0 and var emptyParen && emptyParen + 2 < core.Length &&
            core[emptyParen + 2] != '.')
            return true;

        var hasLetters = false;
        var hasIntruder = false;
        var parenBalance = 0;
        var unbalancedParen = false;
        var punctRuns = 0;
        var prevPunct = false;
        var prevPunctChar = '\0';
        var jitterFlips = 0;
        var prevCase = 0; // 1 = upper, 2 = lower
        var runLen = 0;
        for (var i = 0; i < core.Length; i++)
        {
            var ch = core[i];
            if (char.IsLetter(ch)) hasLetters = true;
            if (ch == '(') parenBalance++;
            else if (ch == ')')
            {
                if (parenBalance == 0) unbalancedParen = true;
                else parenBalance--;
            }
            if (i > 0 && i < core.Length - 1)
            {
                if (ch is '~' or '«' or '»' or '*' or '\\' or '|' or ';' or '!' or '?' or '"' or '£')
                    hasIntruder = true;
                else if (ch is '<' or '>' && (char.IsLetter(core[i - 1]) && char.IsLetter(core[i + 1]) ||
                         !char.IsLetterOrDigit(core[i - 1]) || !char.IsLetterOrDigit(core[i + 1])))
                    hasIntruder = true;
            }
            if (ch is not ('(' or ')') && (char.IsPunctuation(ch) || char.IsSymbol(ch)))
            {
                var isEllipsis = ch == '.' && prevPunctChar == '.';
                var isDoublePlus = ch == '+' && prevPunctChar == '+';
                var isPossessiveAbbrev = i == core.Length - 2 && prevPunctChar == '.' &&
                    ch is '\'' or '’' && core[^1] is 's' or 'S';
                if (prevPunct && ch is not '-' && !isEllipsis && !isDoublePlus && !isPossessiveAbbrev)
                    punctRuns++;
                prevPunct = true;
                prevPunctChar = ch;
            }
            else if (ch is not ('(' or ')'))
            {
                prevPunct = false;
                prevPunctChar = '\0';
            }
            var curCase = char.IsUpper(ch) ? 1 : char.IsLower(ch) ? 2 : 0;
            if (curCase != 0)
            {
                if (prevCase != 0 && curCase != prevCase)
                {
                    if (!(runLen == 1 && prevCase == 1 && curCase == 2) && !(i == 1 && prevCase == 2 && curCase == 1))
                        jitterFlips++;
                    runLen = 1;
                }
                else
                {
                    runLen++;
                }
                prevCase = curCase;
            }
        }
        if (parenBalance != 0) unbalancedParen = true;
        var zeroQueuePrefix = core.Length >= 5 && core[0] == '0' && core[1] == 'Q' &&
            char.IsLetterOrDigit(core[2]) && core.Slice(2).ToString().Any(char.IsDigit);
        return hasIntruder || unbalancedParen || punctRuns >= 1 || zeroQueuePrefix || hasLetters && jitterFlips >= 3;

        static int CountChar(ReadOnlySpan<char> span, char target)
        {
            var count = 0;
            foreach (var c in span)
                if (c == target) count++;
            return count;
        }
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

        var totalNativeWeight = native.Sum(block => Math.Max(1, block.Text.Length));
        var pageNativeQuality = native.Sum(block => TextQuality(block.Text) * Math.Max(1, block.Text.Length)) /
            Math.Max(1, totalNativeWeight);

        var result = native.ToList();
        var replacedNative = new HashSet<PdfTextBlock>();
        var nativeKeys = result.Where(block => TextQuality(block.Text) >= 0.7)
            .SelectMany(block => block.Text.Split('\n'))
            .Select(EvidenceKey).Where(key => key.Length > 0).ToArray();
        foreach (var line in recognized)
        {
            var lineQuality = TextQuality(line.Text);
            if (lineQuality < 0.7 || line.Confidence is < 50) continue;
            var key = EvidenceKey(line.Text);
            if (key.Length == 0 || nativeKeys.Any(nativeKey => nativeKey.Contains(key, StringComparison.Ordinal) ||
                    key.Contains(nativeKey, StringComparison.Ordinal) && nativeKey.Length >= key.Length * 0.8)) continue;
            var colliding = native.Where(block => block.Region is { } region && line.Region is { } lineRegion &&
                RegionIntersection(region, lineRegion) >
                Math.Min(region.Width * region.Height, lineRegion.Width * lineRegion.Height) * 0.5).ToArray();
            if (colliding.Length > 0)
            {
                var collidingWeight = colliding.Sum(block => Math.Max(1, block.Text.Length));
                var collidingQuality = colliding.Sum(block => TextQuality(block.Text) * Math.Max(1, block.Text.Length)) /
                    Math.Max(1, collidingWeight);
                var replaceSandwichRegion = colliding.All(block => TextQuality(block.Text) < 0.7) ||
                    collidingQuality < 0.72 && line.Confidence is >= 75 && lineQuality > collidingQuality + 0.15;
                // Valid native text is preferred in the same region. Add OCR only for missing or corrupted regions.
                if (!replaceSandwichRegion) continue;
                var removedAny = false;
                foreach (var block in colliding)
                {
                    if (lineQuality > TextQuality(block.Text) + 0.1 || collidingQuality < 0.72)
                    {
                        removedAny |= result.Remove(block) || replacedNative.Contains(block);
                        replacedNative.Add(block);
                    }
                }
                result.Add(!removedAny ? line : line with
                { Warning = string.Join(' ', new[] { line.Warning, "native_text_replaced: illegible native glyph mapping replaced by OCR in this region." }.Where(value => value is not null)) });
                continue;
            }
            result.Add(line);
        }
        if (pageNativeQuality < 0.72 && ocr.Confidence is >= 75 && TextQuality(ocr.Text) > pageNativeQuality + 0.15)
            result.RemoveAll(block => block.Method == ExtractionMethod.NativeText && TextQuality(block.Text) < 0.5);
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
            if (selected.Length < 3 || Enumerable.Range(0, first.Length).Max(column => selected.Count(row =>
                {
                    var text = row.OrderBy(line => line.Region.X).ElementAt(column).Text;
                    return text.Any(char.IsDigit) && text.Count(char.IsDigit) >= text.Count(char.IsLetter);
                })) < Math.Max(2, (int)Math.Ceiling(selected.Length * 0.6))) continue;
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

    private static IReadOnlyList<PdfTextBlock> GroupOcrParagraphs(IReadOnlyList<PdfTextBlock> blocks)
    {
        var result = new List<PdfTextBlock>();
        foreach (var block in blocks)
        {
            var previous = result.LastOrDefault();
            if (previous is not { Method: ExtractionMethod.Ocr, IsTable: false, Region: { } region } ||
                block is not { Method: ExtractionMethod.Ocr, IsTable: false, Region: { } next } ||
                previous.Warning is not null || block.Warning is not null)
            { result.Add(block); continue; }

            var lineCount = previous.Text.Count(character => character == '\n') + 1;
            var previousHeight = region.Height / lineCount;
            var gap = next.Y - (region.Y + region.Height);
            // Work only inside the same column, with nearby baselines and similar text size.
            // Tables are kept atomic; large paragraph gaps and indents start a new block.
            if (Math.Abs(region.X - next.X) > 0.015 || next.Height < previousHeight * 0.65 ||
                next.Height > previousHeight * 1.5 || gap < -next.Height * 0.2 ||
                gap > Math.Max(0.006, next.Height * 1.25) ||
                Math.Min(region.X + region.Width, next.X + next.Width) - Math.Max(region.X, next.X) <
                Math.Min(region.Width, next.Width) * 0.8)
            { result.Add(block); continue; }

            var confidence = previous.Confidence is { } first && block.Confidence is { } second
                ? (first * previous.Text.Length + second * block.Text.Length) /
                  Math.Max(1, previous.Text.Length + block.Text.Length)
                : previous.Confidence ?? block.Confidence;
            result[^1] = previous with { Text = previous.Text + "\n" + block.Text,
                Region = UnionRegions([region, next]), Confidence = confidence };
        }
        return result;
    }

    private static IReadOnlyList<ExtractedSection> ImageOcrSections(OcrResult ocr, SourceLocation location) =>
        GroupImageOcrBlocks(GroupOcrParagraphs(GroupImageOcrLines(OcrBlocks(ocr)))).Select((block, index) => new ExtractedSection(block.Text,
            location with { Region = block.Region, StructurePath = block.IsTable ? $"table[{index + 1}]" : $"block[{index + 1}]",
                LayoutWarning = block.Warning }, ExtractionMethod.Ocr, block.Confidence,
            SectionKey: $"image:{location.ImageFrame ?? location.Page ?? 1}")).ToArray();

    // OCR can detect individual words of a short label as separate boxes. Rejoin only
    // close, similarly sized boxes on the same baseline before vertical context grouping.
    private static IReadOnlyList<PdfTextBlock> GroupImageOcrLines(IReadOnlyList<PdfTextBlock> blocks)
    {
        var result = new List<PdfTextBlock>();
        foreach (var block in blocks)
        {
            var previous = result.LastOrDefault();
            if (previous is not { IsTable: false, Region: { } region } || block is not { IsTable: false, Region: { } next } ||
                previous.Warning is not null || block.Warning is not null || previous.Text.Contains('\n') || block.Text.Contains('\n'))
            { result.Add(block); continue; }
            var gap = next.X - (region.X + region.Width);
            if (previous.Text.Length + block.Text.Length > 160 ||
                Math.Abs(region.Y + region.Height / 2 - next.Y - next.Height / 2) > Math.Max(region.Height, next.Height) * .4 ||
                next.Height < region.Height * .65 || next.Height > region.Height * 1.5 ||
                gap < -Math.Min(region.Width, next.Width) * .05 || gap > Math.Min(region.Width, next.Width) * .3)
            { result.Add(block); continue; }
            var confidence = previous.Confidence is { } first && block.Confidence is { } second
                ? (first * previous.Text.Length + second * block.Text.Length) / Math.Max(1, previous.Text.Length + block.Text.Length)
                : previous.Confidence ?? block.Confidence;
            result[^1] = previous with { Text = previous.Text + " " + block.Text, Region = UnionRegions([region, next]), Confidence = confidence };
        }
        return result;
    }

    // Standalone images often use short, spaced label/value blocks rather than prose paragraphs.
    // Keep nearby blocks in one bounded same-column context so a label search can return its value.
    // This does not change PDF grouping or merge across inferred tables/layout warnings.
    private static IReadOnlyList<PdfTextBlock> GroupImageOcrBlocks(IReadOnlyList<PdfTextBlock> blocks)
    {
        var result = new List<PdfTextBlock>();
        foreach (var block in blocks)
        {
            var previous = result.LastOrDefault();
            if (previous is not { IsTable: false, Region: { } region } || block is not { IsTable: false, Region: { } next } ||
                previous.Warning is not null || block.Warning is not null ||
                IsProseParagraph(previous.Text) || IsProseParagraph(block.Text))
            { result.Add(block); continue; }
            var lines = previous.Text.Count(value => value == '\n') + 1;
            var nextLines = block.Text.Count(value => value == '\n') + 1;
            var previousHeight = region.Height / lines;
            var nextHeight = next.Height / nextLines;
            var gap = next.Y - (region.Y + region.Height);
            var words = (previous.Text + " " + block.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (lines + nextLines > 4 || words > 64 || Math.Abs(region.X - next.X) > 0.015 ||
                nextHeight < previousHeight * 0.5 || nextHeight > previousHeight * 2 || gap < -nextHeight * 0.2 ||
                gap > Math.Max(previousHeight, nextHeight) * 2.5 ||
                Math.Min(region.X + region.Width, next.X + next.Width) - Math.Max(region.X, next.X) < Math.Min(region.Width, next.Width) * 0.8)
            { result.Add(block); continue; }
            var confidence = previous.Confidence is { } first && block.Confidence is { } second
                ? (first * previous.Text.Length + second * block.Text.Length) / Math.Max(1, previous.Text.Length + block.Text.Length)
                : previous.Confidence ?? block.Confidence;
            result[^1] = previous with { Text = previous.Text + "\n" + block.Text, Region = UnionRegions([region, next]), Confidence = confidence };
        }
        return result;

        static bool IsProseParagraph(string text)
        {
            var trimmed = text.AsSpan().TrimEnd();
            return trimmed.Length > 0 && trimmed[^1] is '.' or '!' or '?' or '。' or '！' or '？' &&
                text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 4;
        }
    }

    private static string EvidenceKey(string text) => string.Concat(TextNormalization.ForSearch(text).Normalize()
        .Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
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
