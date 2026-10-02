using ContextMole.Core;

using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace ContextMole.Documents;

public sealed partial class DocumentExtractionRegistry
{
    /// <summary>
    /// Use connected, orthogonal ruling lines before guessing columns from text alignment.
    /// Real grids retain empty cells, right/center alignment and wrapped cell text. Only
    /// full-width row rules and substantial column dividers are accepted; decorative boxes
    /// without repeated rows and columns are not tables.
    /// </summary>
    private static IReadOnlyList<PdfTextBlock> PdfGridTables(Page page, Word[] words, HashSet<Word> used)
    {
        const double tolerance = 2;
        var rules = PdfRulingLines(page).ToArray();
        var horizontal = MergeRules(rules.Where(line => line.Height <= tolerance && line.Width >= 4), true)
            .Where(line => line.Width >= 24).ToArray();
        var vertical = MergeRules(rules.Where(line => line.Width <= tolerance && line.Height >= 4), false)
            .Where(line => line.Height >= 12).ToArray();
        if (horizontal.Length < 3 || vertical.Length < 3 ||
            (long)horizontal.Length * vertical.Length > 1_000_000) return [];

        var parents = Enumerable.Range(0, horizontal.Length + vertical.Length).ToArray();
        for (var h = 0; h < horizontal.Length; h++)
            for (var v = 0; v < vertical.Length; v++)
                if (vertical[v].Left >= horizontal[h].Left - tolerance &&
                    vertical[v].Right <= horizontal[h].Right + tolerance &&
                    horizontal[h].Bottom >= vertical[v].Bottom - tolerance &&
                    horizontal[h].Top <= vertical[v].Top + tolerance)
                    parents[Root(h)] = Root(horizontal.Length + v);

        var result = new List<PdfTextBlock>();
        foreach (var component in Enumerable.Range(0, parents.Length).GroupBy(Root))
        {
            var hs = component.Where(index => index < horizontal.Length).Select(index => horizontal[index]).ToArray();
            var vs = component.Where(index => index >= horizontal.Length).Select(index => vertical[index - horizontal.Length]).ToArray();
            if (hs.Length < 3 || vs.Length < 3) continue;
            var left = hs.Min(line => line.Left);
            var right = hs.Max(line => line.Right);
            var bottom = vs.Min(line => line.Bottom);
            var top = vs.Max(line => line.Top);
            // Interior dividers often stop at a merged heading. A divider running through
            // most of the body still defines a column; requiring full height silently merges
            // several numeric columns into one. Short decorative/header-only rules do not.
            var xs = Coordinates(vs.Where(line => line.Height >= (top - bottom) * 0.6)
                .Select(line => (line.Left + line.Right) / 2));
            var ys = Coordinates(hs.Where(line => line.Left <= left + tolerance && line.Right >= right - tolerance)
                .Select(line => (line.Bottom + line.Top) / 2));
            if (xs.Length < 3 || ys.Length < 3 || xs.Length > 65 || ys.Length > 501 ||
                Math.Abs(xs[0] - left) > tolerance || Math.Abs(xs[^1] - right) > tolerance ||
                Math.Abs(ys[0] - bottom) > tolerance || Math.Abs(ys[^1] - top) > tolerance) continue;

            var cells = new List<Word>[ys.Length - 1, xs.Length - 1];
            for (var row = 0; row < cells.GetLength(0); row++)
                for (var column = 0; column < cells.GetLength(1); column++) cells[row, column] = [];
            var selected = words.Where(word => !used.Contains(word) &&
                word.BoundingBox.Left >= left - tolerance && word.BoundingBox.Right <= right + tolerance &&
                word.BoundingBox.Bottom >= bottom - tolerance && word.BoundingBox.Top <= top + tolerance).ToArray();
            foreach (var word in selected)
            {
                var x = (word.BoundingBox.Left + word.BoundingBox.Right) / 2;
                var y = (word.BoundingBox.Bottom + word.BoundingBox.Top) / 2;
                var column = Math.Clamp(Array.FindLastIndex(xs, edge => edge <= x), 0, xs.Length - 2);
                var row = ys.Length - 2 - Math.Clamp(Array.FindLastIndex(ys, edge => edge <= y), 0, ys.Length - 2);
                cells[row, column].Add(word);
            }
            // Avoid classifying a framed paragraph crossing many grid cells as a table.
            if (Enumerable.Range(0, cells.GetLength(0)).Count(row =>
                    Enumerable.Range(0, cells.GetLength(1)).Count(column => cells[row, column].Count > 0) >= 2) < 2) continue;
            var rows = Enumerable.Range(0, cells.GetLength(0)).Select(row => string.Join('\t',
                Enumerable.Range(0, cells.GetLength(1)).Select(column => PdfCellText(cells[row, column]))));
            foreach (var word in selected) used.Add(word);
            result.Add(new PdfTextBlock(string.Join('\n', rows),
                PdfRegion(new PdfRectangle(left, bottom, right, top), page), IsTable: true,
                Warning: "table_inferred: grid rules define rows and columns; merged cells and column semantics are unverified."));
        }
        return result;

        int Root(int item)
        {
            while (parents[item] != item)
            {
                parents[item] = parents[parents[item]];
                item = parents[item];
            }
            return item;
        }

        static double[] Coordinates(IEnumerable<double> values)
        {
            var result = new List<double>();
            foreach (var value in values.Order())
                if (result.Count == 0 || value - result[^1] > tolerance) result.Add(value);
            return result.ToArray();
        }
    }

    private static IEnumerable<PdfRectangle> PdfRulingLines(Page page)
    {
        foreach (var subpath in page.Paths.SelectMany(path => path))
        {
            PdfPoint? start = null;
            PdfPoint? end = null;
            foreach (var command in subpath.Commands)
            {
                if (command is PdfSubpath.Move move) { start = move.Location; end = move.Location; }
                else if (command is PdfSubpath.Line line)
                {
                    end = line.To;
                    if (line.GetBoundingRectangle() is { } bounds) yield return bounds;
                }
                else if (command is PdfSubpath.Close && start is { } first && end is { } last)
                {
                    // PDF rectangle paths store three edges and a close command. The fourth
                    // edge is just as important when those rectangles form a table grid.
                    yield return new PdfRectangle(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y),
                        Math.Max(first.X, last.X), Math.Max(first.Y, last.Y));
                    end = start;
                }
                else end = null; // Curved paths cannot imply a straight closing rule.
            }
        }
    }

    private static IReadOnlyList<PdfRectangle> MergeRules(IEnumerable<PdfRectangle> rules, bool horizontal)
    {
        const double tolerance = 2;
        var groups = new List<List<PdfRectangle>>();
        foreach (var rule in rules.OrderBy(rule => horizontal ? rule.Bottom : rule.Left))
        {
            var group = groups.LastOrDefault();
            if (group is null || Math.Abs((horizontal ? group[0].Bottom : group[0].Left) -
                                         (horizontal ? rule.Bottom : rule.Left)) > tolerance)
            { group = []; groups.Add(group); }
            group.Add(rule);
        }
        var result = new List<PdfRectangle>();
        foreach (var group in groups)
        {
            var ordered = group.OrderBy(rule => horizontal ? rule.Left : rule.Bottom).ToArray();
            var merged = ordered[0];
            foreach (var rule in ordered.Skip(1))
            {
                if ((horizontal ? rule.Left - merged.Right : rule.Bottom - merged.Top) <= tolerance)
                    merged = new PdfRectangle(Math.Min(merged.Left, rule.Left), Math.Min(merged.Bottom, rule.Bottom),
                        Math.Max(merged.Right, rule.Right), Math.Max(merged.Top, rule.Top));
                else { result.Add(merged); merged = rule; }
            }
            result.Add(merged);
        }
        return result;
    }

    private static string PdfCellText(IEnumerable<Word> words)
    {
        // Glyph tops differ for capitals, lowercase, descenders and short punctuation even
        // on one printed line. Baselines retain in-line words instead of moving "sq." or
        // a dash to a spurious extra line below the rest of its cell.
        var positioned = words.Select(word => (Word: word,
            Baseline: Median(word.Letters.Select(letter => letter.StartBaseLine.Y)),
            Size: Median(word.Letters.Select(letter => letter.PointSize))));
        var rows = new List<List<(Word Word, double Baseline, double Size)>>();
        foreach (var word in positioned.OrderByDescending(word => word.Baseline).ThenBy(word => word.Word.BoundingBox.Left))
        {
            var row = rows.LastOrDefault();
            if (row is null || row[0].Baseline - word.Baseline > Math.Max(2, Math.Max(row[0].Size, word.Size) * 0.4))
            { row = []; rows.Add(row); }
            row.Add(word);
        }
        // U+2028 retains a cell's visual line break without being confused with a TSV row.
        // In particular, source hyphenation stays available to offset-aware search.
        return string.Join('\u2028', rows.Select(row => string.Join(' ',
            row.OrderBy(word => word.Word.BoundingBox.Left).Select(word => word.Word.Text))));
    }
}
