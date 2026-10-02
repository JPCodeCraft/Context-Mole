using System.Text;
using ContextMole.Core;

namespace ContextMole.Search;

// Experimental excerpt positioning only. These literal hints are never lexical
// clauses, match evidence, retrieval constraints, or ranking features.
internal sealed class SemanticPreviewAnchors
{
    private readonly HashSet<string> _terms;

    internal SemanticPreviewAnchors(string query)
    {
        var tokens = LexicalText.TokenizeWithOffsets(query);
        // Do not mistake an all-uppercase natural-language question for a list
        // of acronyms. A single acronym/identifier is still a useful query.
        var allowAcronyms = tokens.Count == 1 || query.EnumerateRunes().Any(Rune.IsLower);
        _terms = tokens.Where(token => IsSalient(query.AsSpan(token.Start, token.Length), allowAcronyms))
            .Select(token => token.Value).ToHashSet(StringComparer.Ordinal);
    }

    internal Window? FindWindow(string text, int length)
    {
        if (_terms.Count == 0 || text.Length <= length) return null;
        var matches = LexicalText.TokenizeWithOffsets(text)
            .Where(token => token.Length <= length && _terms.Contains(token.Value)).ToArray();
        if (matches.Length == 0) return null;

        // Prefix first, then centered windows in source order. Distinct anchors
        // matter; repeated acronyms must not outvote complementary context.
        var offsets = matches.Select(token => Math.Clamp(token.Start - length / 2, 0, text.Length - length))
            .Prepend(0).Distinct();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var left = 0;
        var right = 0;
        var bestCount = 0;
        var bestStart = 0;
        foreach (var start in offsets)
        {
            while (right < matches.Length && matches[right].Start + matches[right].Length <= start + length)
            {
                var value = matches[right++].Value;
                counts[value] = counts.GetValueOrDefault(value) + 1;
            }
            while (left < right && matches[left].Start < start)
            {
                var value = matches[left++].Value;
                if (--counts[value] == 0) counts.Remove(value);
            }
            if (counts.Count <= bestCount) continue;
            bestCount = counts.Count;
            bestStart = start;
        }
        if (bestCount == 0) return null;
        return new Window(bestStart, matches.Where(token => token.Start >= bestStart &&
            token.Start + token.Length <= bestStart + length).ToArray());
    }

    private static bool IsSalient(ReadOnlySpan<char> text, bool allowAcronyms)
    {
        var letters = 0;
        var digits = 0;
        var allUpper = true;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune))
            {
                letters++;
                allUpper &= Rune.IsUpper(rune);
            }
            else if (Rune.IsDigit(rune)) digits++;
        }
        // Exclude short ambiguous abbreviations, bare dates/numbers and ordinary
        // question words, without maintaining a language-specific stopword list.
        return letters >= 2 && digits > 0 || allowAcronyms && letters >= 3 && allUpper;
    }

    internal sealed record Window(int Start, IReadOnlyList<LexicalToken> Anchors);
}
