using System.Text.RegularExpressions;

using ContextMole.Core;

namespace ContextMole.Indexing;

/// <summary>
/// Table header recognition and header-aware embedding input. Canonical display and lexical
/// table text are never changed: only the semantic text pairs each value with its column name,
/// because "Kerala 2400 7.2 0.5" says nothing about what the numbers measure.
/// </summary>
public static partial class TableSemantics
{
    /// <param name="Display">Header names joined for the "Table headers" context line.</param>
    /// <param name="Cells">Header names by TSV position; empty for unnamed columns.</param>
    /// <param name="Columns">Header names by spreadsheet column letter, when cells carry references.</param>
    public sealed record Header(string Display, IReadOnlyList<string> Cells, IReadOnlyDictionary<string, string>? Columns);

    /// <summary>
    /// Reads the first TSV row as column names. A row that is mostly numbers is data, not a
    /// header, and naming columns after it would mislabel every value in the table.
    /// </summary>
    /// <param name="cellReferences">Cells are prefixed with their spreadsheet reference ("A1: value").</param>
    public static Header? ParseHeader(string text, bool cellReferences)
    {
        var firstRow = text.Split('\n').FirstOrDefault(row => row.Trim().Length > 0);
        if (firstRow is null) return null;
        var cells = new List<string>();
        var columns = cellReferences ? new Dictionary<string, string>(StringComparer.Ordinal) : null;
        foreach (var raw in firstRow.Split('\t'))
        {
            var (column, value) = cellReferences ? SplitReference(raw) : (null, raw);
            value = TextNormalization.ForSearch(value);
            cells.Add(value);
            if (column is not null && value.Length > 0) columns![column] = value;
        }
        var named = cells.Where(cell => cell.Length > 0).ToArray();
        if (named.Length == 0 || named.Count(IsNumericValue) * 2 >= named.Length) return null;
        var display = string.Join(" | ", named);
        return new Header(display.Length <= 500 ? display : display[..500], cells, columns);
    }

    /// <summary>
    /// Rewrites TSV rows as "Column: value" pairs. Rows whose cells cannot be matched to the
    /// header by position or by spreadsheet column keep their plain values. The header row
    /// itself is left out unless it is the only row.
    /// </summary>
    public static string Linearize(string body, Header header, bool cellReferences)
    {
        var output = new List<string>();
        string? headerRowText = null;
        foreach (var row in body.Split('\n'))
        {
            if (row.Trim().Length == 0) continue;
            var cells = row.Split('\t');
            var positional = !cellReferences && cells.Length == header.Cells.Count;
            var values = new List<string>(cells.Length);
            var headerRow = true;
            for (var index = 0; index < cells.Length; index++)
            {
                var (column, value) = cellReferences ? SplitReference(cells[index]) : (null, cells[index]);
                value = TextNormalization.ForSearch(value);
                if (value.Length == 0) continue;
                var name = column is not null ? header.Columns?.GetValueOrDefault(column)
                    : positional ? header.Cells[index] : null;
                var isName = string.Equals(name, value, StringComparison.OrdinalIgnoreCase);
                headerRow &= isName;
                values.Add(string.IsNullOrEmpty(name) || isName ? value : $"{name}: {value}");
            }
            if (values.Count == 0) continue;
            var text = string.Join("; ", values) + ".";
            // The header row itself is already the "Table headers" context line.
            if (headerRow) headerRowText ??= text;
            else output.Add(text);
        }
        // A chunk or spreadsheet row that holds only the header still embeds the column names.
        if (output.Count == 0 && headerRowText is not null) output.Add(headerRowText);
        return string.Join('\n', output);
    }

    private static (string? Column, string Value) SplitReference(string cell)
    {
        var match = CellReference().Match(cell);
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : (null, cell);
    }

    private static bool IsNumericValue(string value)
    {
        var digits = 0;
        foreach (var character in value)
        {
            if (char.IsLetter(character)) return false;
            if (char.IsDigit(character)) digits++;
        }
        // Years name columns in financial and statistical tables.
        return digits > 0 && !Year().IsMatch(value);
    }

    // Spreadsheet rows use "A1: value"; delimited text uses "A: value".
    [GeneratedRegex(@"^([A-Z]{1,3})\d*: (.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CellReference();

    [GeneratedRegex(@"^(?:1[89]|2[01])\d\d$", RegexOptions.CultureInvariant)]
    private static partial Regex Year();
}
