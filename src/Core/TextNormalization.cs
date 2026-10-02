using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ContextMole.Core;

public static partial class TextNormalization
{
    public static string ForDisplay(string? value, bool preserveTableWhitespace = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // A discretionary hyphen marks a single wrapped word. Resolve its line
        // continuation before removing invisible formatting, otherwise later
        // lexical and semantic preparation can no longer distinguish this from
        // two separate words. Do not join blank paragraphs or capitalized text.
        // A TSV newline separates rows, not wrapped words. Only U+2028 is an
        // in-cell line separator in our canonical table representation.
        value = (preserveTableWhitespace ? SoftHyphenCellLineBreak() : SoftHyphenLineBreak())
            .Replace(value, "$1$2");
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value == 0x00AD)
            {
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format && rune.Value is not ('\n' or '\r' or '\t'))
            {
                continue;
            }

            builder.Append(rune.ToString());
        }

        var cleaned = builder.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
        // Tabs at either edge, or immediately before a newline, encode empty TSV cells.
        // Preserve them when canonicalizing table evidence; search normalization stays separate.
        return preserveTableWhitespace ? cleaned : NewLineWhitespace().Replace(cleaned, "\n").Trim();
    }

    public static string ForSearch(string? value, bool dehyphenateLineBreaks = false)
    {
        // Display cleanup already resolves explicit discretionary-hyphen wraps.
        // Visible line-end hyphens are joined only for semantic preparation.
        var text = ForDisplay(value);
        if (dehyphenateLineBreaks)
        {
            text = LineBreakHyphen().Replace(text, "$1$2");
        }

        return AllWhitespace().Replace(text.Normalize(NormalizationForm.FormKC), " ").Trim();
    }

    public static string NameKey(string value) => ForSearch(value).ToUpperInvariant();

    public static string QuoteFtsTerms(string query)
    {
        var terms = LexicalText.Tokens(query)
            .Select(token => token.Replace("\"", "\"\"", StringComparison.Ordinal))
            .Where(term => term.Length > 0)
            .Take(64)
            .Select(term => $"\"{term}\"")
            .ToArray();

        return string.Join(" OR ", terms);
    }

    [GeneratedRegex(@"[ \t\f\v]+\n")]
    private static partial Regex NewLineWhitespace();

    [GeneratedRegex(@"\s+")]
    private static partial Regex AllWhitespace();

    [GeneratedRegex(@"([\p{L}\p{M}])[-\u2010][ \t]*[\n\u2028][ \t]*([\p{Ll}])")]
    private static partial Regex LineBreakHyphen();

    [GeneratedRegex(@"([\p{L}\p{M}])\u00AD[ \t]*(?:\r\n|[\r\n\u2028])[ \t]*([\p{Ll}])")]
    private static partial Regex SoftHyphenLineBreak();

    [GeneratedRegex(@"([\p{L}\p{M}])\u00AD[ ]*\u2028[ ]*([\p{Ll}])")]
    private static partial Regex SoftHyphenCellLineBreak();

}
