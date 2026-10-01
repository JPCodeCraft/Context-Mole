using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ContextMole.Core;

public sealed record LexicalToken(string Value, int Start, int Length);

/// <summary>The canonical token stream shared by FTS, clause evaluation and source snippets.</summary>
public static partial class LexicalText
{
    public static IReadOnlyList<LexicalToken> TokenizeWithOffsets(string? value, bool dehyphenate = true)
    {
        if (string.IsNullOrEmpty(value)) return [];
        var tokens = new List<LexicalToken>();
        foreach (Match match in SourceTokens().Matches(value))
        {
            // Compatibility normalization can introduce separators, e.g. ½ -> 1⁄2.
            // Split again so the canonical stream has exactly the same boundaries in SQLite.
            var parts = SourceTokens().Matches(Normalize(match.Value)).Select(part => part.Value).ToArray();
            if (parts.Length == 0) continue;
            var token = parts[0];
            if (dehyphenate && parts.Length == 1 && tokens.Count > 0)
            {
                var previous = tokens[^1];
                var previousEnd = previous.Start + previous.Length;
                var gap = value[previousEnd..match.Index];
                // Join only a hyphen at a line break followed by a lowercase letter.
                if (HyphenatedGap().IsMatch(gap) && char.IsLower(match.Value, 0) &&
                    char.IsLetter(value[previousEnd - 1]))
                {
                    tokens[^1] = new LexicalToken(previous.Value + token, previous.Start,
                        match.Index + match.Length - previous.Start);
                    continue;
                }
            }
            foreach (var part in parts) tokens.Add(new LexicalToken(part, match.Index, match.Length));
        }
        return tokens;
    }

    public static IReadOnlyList<string> Tokens(string? value) =>
        TokenizeWithOffsets(value).Select(token => token.Value).ToArray();

    public static string Canonicalize(string? value, bool dehyphenate = true) =>
        string.Join(' ', TokenizeWithOffsets(value, dehyphenate).Select(token => token.Value));

    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder();
        foreach (var rune in value.Normalize(NormalizationForm.FormKC).Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            builder.Append(Rune.ToLowerInvariant(rune));
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}_]+")]
    private static partial Regex SourceTokens();

    [GeneratedRegex(@"^-[ \t]*\r?\n\s*$")]
    private static partial Regex HyphenatedGap();
}
