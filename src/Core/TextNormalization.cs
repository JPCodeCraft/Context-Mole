using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ContextMole.Core;

public static partial class TextNormalization
{
    public static string ForDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

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

        return NewLineWhitespace().Replace(builder.ToString().Replace("\r\n", "\n").Replace('\r', '\n'), "\n").Trim();
    }

    public static string ForSearch(string? value, bool dehyphenateLineBreaks = false)
    {
        var text = ForDisplay(value).Normalize(NormalizationForm.FormKC);
        if (dehyphenateLineBreaks)
        {
            text = LineBreakHyphen().Replace(text, "$1$2");
        }

        return AllWhitespace().Replace(text, " ").Trim();
    }

    public static string NameKey(string value) => ForSearch(value).ToUpperInvariant();

    public static string QuoteFtsTerms(string query)
    {
        var terms = WordTokens().Matches(ForSearch(query))
            .Select(match => match.Value.Replace("\"", "\"\"", StringComparison.Ordinal))
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

    [GeneratedRegex(@"([\p{L}\p{M}])-\s*\n\s*([\p{Ll}])")]
    private static partial Regex LineBreakHyphen();

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}_]+")]
    private static partial Regex WordTokens();
}