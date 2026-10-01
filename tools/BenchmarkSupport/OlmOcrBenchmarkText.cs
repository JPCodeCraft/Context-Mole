using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ContextMole.Benchmarks;

/// <summary>Text rules and bounded, dependency-free matching for the olmOCR benchmark adapter.</summary>
/// <remarks>
/// Independently authored from the Apache-2.0 upstream evaluator requirements at
/// https://github.com/allenai/olmocr/tree/f7cfe4c22098b154c76b6ec950d1c0a464eecf8d/olmocr/bench.
/// Approximate substring matching uses Levenshtein distance, not RapidFuzz partial Indel similarity.
/// Approximate order matches consolidate overlapping intervals; the local limits are adapter safeguards.
/// </remarks>
public static class OlmOcrBenchmarkText
{
    private const int MaximumDifferences = 256;
    private const long MaximumMatchingCells = 100_000_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex BreakTags = Pattern(@"<br/?>");
    private static readonly Regex AsteriskBold = Pattern(@"\*\*(.*?)\*\*");
    private static readonly Regex UnderscoreBold = Pattern(@"__(.*?)__");
    private static readonly Regex HtmlBold = Pattern(@"</?b>");
    private static readonly Regex HtmlItalic = Pattern(@"</?i>");
    private static readonly Regex PairedBold = Pattern(@"(\*\*|__)(.*?)\1");
    private static readonly Regex PairedItalic = Pattern(@"(\*|_)(.*?)\1");
    // Python whitespace also includes the four information separators that .NET omits from \s.
    private static readonly Regex Whitespace = Pattern(@"[\s\u001C-\u001F]+");
    private static readonly Regex ImageTags = Pattern(@"!\[.*?\]\(.*?\)");

    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = BreakTags.Replace(text, " ");
        text = AsteriskBold.Replace(text, "$1");
        text = UnderscoreBold.Replace(text, "$1");
        text = HtmlBold.Replace(text, "");
        text = HtmlItalic.Replace(text, "");
        text = PairedBold.Replace(text, "$2");
        text = PairedItalic.Replace(text, "$2");
        text = Whitespace.Replace(text, " ").Normalize(NormalizationForm.FormC);
        var result = new StringBuilder(text.Length);
        foreach (var character in text)
            result.Append(character switch
            {
                '\u2018' or '\u2019' or '\u201A' => '\'',
                '\u201C' or '\u201D' or '\u201E' => '"',
                '\uFF3F' => '_',
                '\u2013' or '\u2014' or '\u2011' or '\u2012' or '\u2212' => '-',
                '\u00B5' => '\u03BC',
                _ => character
            });
        return result.ToString();
    }

    /// <summary>Return Unicode scalar offsets of exact or consolidated approximate occurrences.</summary>
    /// <remarks>Inputs are already normalized. Exact matches retain overlaps, as fuzzysearch does.</remarks>
    public static IReadOnlyList<int> FindNearStarts(string needle, string haystack, int maxDiffs)
    {
        ArgumentNullException.ThrowIfNull(needle);
        ArgumentNullException.ThrowIfNull(haystack);
        ValidateDifferences(maxDiffs);
        var query = Scalars(needle);
        if (query.Length == 0)
            throw new ArgumentException("A search needle must not be empty.", nameof(needle));
        return Search(query, Scalars(haystack), maxDiffs, stopAfterFirst: false);
    }

    /// <summary>Match normalized text, optionally restricted to Unicode scalar prefix/suffix slices.</summary>
    /// <remarks>
    /// Zero differences reproduces symmetric exact partial matching. Nonzero differences use a
    /// symmetric Levenshtein substring adaptation. Empty observed content never matches.
    /// Case-insensitive comparison uses .NET invariant lowercase, not Python's full Unicode casing.
    /// </remarks>
    public static bool MatchText(string expected, string actual, int maxDiffs, bool caseSensitive = true,
        int? firstN = null, int? lastN = null)
    {
        ValidateDifferences(maxDiffs);
        if (firstN < 0)
            throw new ArgumentOutOfRangeException(nameof(firstN));
        if (lastN < 0)
            throw new ArgumentOutOfRangeException(nameof(lastN));
        var reference = Normalize(expected);
        var observed = Normalize(actual);
        if (string.IsNullOrWhiteSpace(reference))
            throw new ArgumentException("Expected text must contain non-whitespace content.", nameof(expected));
        if (!caseSensitive)
        {
            reference = reference.ToLowerInvariant();
            observed = observed.ToLowerInvariant();
        }
        if (firstN > 0 || lastN > 0)
        {
            var runes = observed.EnumerateRunes().ToArray();
            var sliced = new StringBuilder();
            if (firstN > 0)
                Append(sliced, runes, 0, Math.Min(firstN.Value, runes.Length));
            if (lastN > 0)
                Append(sliced, runes, Math.Max(0, runes.Length - lastN.Value), runes.Length);
            observed = sliced.ToString();
        }
        if (observed.Length == 0)
            return false;
        if (observed.Contains(reference, StringComparison.Ordinal) || reference.Contains(observed, StringComparison.Ordinal))
            return true;
        if (maxDiffs == 0)
            return false;
        var query = Scalars(reference);
        var text = Scalars(observed);
        if (query.Length > text.Length)
            (query, text) = (text, query);
        return Search(query, text, maxDiffs, stopAfterFirst: true).Count > 0;
    }

    /// <summary>Normalized, case-sensitive whole-string Indel similarity (twice LCS / total length).</summary>
    public static double IndelSimilarity(string lhs, string rhs)
    {
        var left = Scalars(Normalize(lhs));
        var right = Scalars(Normalize(rhs));
        if (left.Length == 0 || right.Length == 0)
            return left.Length == right.Length ? 1 : 0;
        EnsureWorkFits((long)left.Length * right.Length);
        if (left.Length < right.Length)
            (left, right) = (right, left);
        var longest = new int[right.Length + 1];
        foreach (var character in left)
        {
            var diagonal = 0;
            for (var column = 1; column <= right.Length; column++)
            {
                var previous = longest[column];
                longest[column] = character == right[column - 1]
                    ? diagonal + 1 : Math.Max(longest[column], longest[column - 1]);
                diagonal = previous;
            }
        }
        return 2.0 * longest[right.Length] / ((long)left.Length + right.Length);
    }

    public static (bool Passed, string Reason) CheckBaseline(string content, int? maxLength = null,
        bool skipImageAlt = false, int maxRepeats = 30, bool checkDisallowed = true)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxLength < 0)
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        var lengthText = maxLength.HasValue && skipImageAlt ? ImageTags.Replace(content, "") : content;
        var alphanumericCount = lengthText.EnumerateRunes().Count(IsAlphanumeric);
        if (maxLength.HasValue)
            return alphanumericCount <= maxLength.Value
                ? (true, "") : (false, $"Blank-page output contains {alphanumericCount} alphanumeric characters; limit {maxLength.Value}.");
        if (maxRepeats < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRepeats));
        if (alphanumericCount == 0)
            return (false, "Output contains no alphanumeric characters.");
        var suffixText = Scalars(Whitespace.Replace(content, " "));
        for (var size = 1; size <= Math.Min(5, suffixText.Length); size++)
        {
            var count = 0;
            var terminal = suffixText.AsSpan(suffixText.Length - size, size);
            for (var offset = suffixText.Length - size; offset >= 0; offset -= size)
            {
                if (!suffixText.AsSpan(offset, size).SequenceEqual(terminal))
                    break;
                count++;
            }
            if (count > maxRepeats)
                return (false, $"Output ends with {count} repeated {size}-character blocks; limit {maxRepeats}.");
        }
        if (checkDisallowed)
            foreach (var rune in content.EnumerateRunes())
                if (IsDisallowed(rune.Value))
                    return (false, $"Output contains disallowed character U+{rune.Value:X4}.");
        return (true, "");
    }

    private static IReadOnlyList<int> Search(int[] query, int[] text, int differences, bool stopAfterFirst)
    {
        if (differences == 0)
            return ExactStarts(query, text, stopAfterFirst);
        var minimumLength = Math.Max(1, query.Length - differences);
        if (text.Length < minimumLength)
            return Array.Empty<int>();
        var startCount = text.Length - minimumLength + 1;
        var bandWidth = Math.Min((long)query.Length + differences + 1, 2L * differences + 1);
        // Division avoids overflow while enforcing the work limit before allocating DP buffers.
        if ((long)startCount * query.Length > MaximumMatchingCells / bandWidth)
            throw new ArgumentException("Approximate matching exceeds the adapter's 100 million cell work limit.");
        var previous = new int[(int)bandWidth];
        var current = new int[(int)bandWidth];
        var candidates = new List<NearInterval>();
        var infinity = differences + 1;
        for (var start = 0; start < startCount; start++)
        {
            var window = (int)Math.Min((long)query.Length + differences, text.Length - start);
            var previousMin = 0;
            var previousMax = Math.Min(window, differences);
            for (var column = 0; column <= previousMax; column++)
                previous[column] = column;
            var completed = true;
            for (var row = 1; row <= query.Length; row++)
            {
                var currentMin = Math.Max(0, row - differences);
                var currentMax = (int)Math.Min(window, (long)row + differences);
                var rowMinimum = infinity;
                for (var column = currentMin; column <= currentMax; column++)
                {
                    var value = row;
                    if (column != 0)
                    {
                        var deletion = column >= previousMin && column <= previousMax
                            ? previous[column - previousMin] + 1 : infinity;
                        var insertion = column > currentMin ? current[column - currentMin - 1] + 1 : infinity;
                        var substitution = column - 1 >= previousMin && column - 1 <= previousMax
                            ? previous[column - 1 - previousMin] + (query[row - 1] == text[start + column - 1] ? 0 : 1) : infinity;
                        value = Math.Min(infinity, Math.Min(substitution, Math.Min(deletion, insertion)));
                    }
                    current[column - currentMin] = value;
                    rowMinimum = Math.Min(rowMinimum, value);
                }
                (previous, current) = (current, previous);
                previousMin = currentMin;
                previousMax = currentMax;
                if (rowMinimum > differences)
                {
                    completed = false;
                    break;
                }
            }
            if (!completed)
                continue;
            var bestDistance = infinity;
            var bestEnd = 0;
            for (var column = Math.Max(minimumLength, previousMin); column <= previousMax; column++)
            {
                var distance = previous[column - previousMin];
                if (distance > differences)
                    continue;
                if (stopAfterFirst)
                    return new[] { start };
                if (distance < bestDistance || distance == bestDistance && start + column > bestEnd)
                {
                    bestDistance = distance;
                    bestEnd = start + column;
                }
            }
            if (bestDistance <= differences)
                candidates.Add(new NearInterval(start, bestEnd, bestDistance));
        }
        return Consolidate(candidates);
    }

    private static IReadOnlyList<int> ExactStarts(int[] query, int[] text, bool stopAfterFirst)
    {
        var prefix = new int[query.Length];
        for (var index = 1; index < query.Length; index++)
        {
            var matched = prefix[index - 1];
            while (matched > 0 && query[index] != query[matched])
                matched = prefix[matched - 1];
            if (query[index] == query[matched])
                matched++;
            prefix[index] = matched;
        }
        var starts = new List<int>();
        var length = 0;
        for (var index = 0; index < text.Length; index++)
        {
            while (length > 0 && text[index] != query[length])
                length = prefix[length - 1];
            if (text[index] == query[length])
                length++;
            if (length != query.Length)
                continue;
            starts.Add(index - query.Length + 1);
            if (stopAfterFirst)
                break;
            length = prefix[length - 1];
        }
        return starts;
    }

    private static IReadOnlyList<int> Consolidate(List<NearInterval> candidates)
    {
        if (candidates.Count == 0)
            return Array.Empty<int>();
        // Discard an interval when a strictly better alignment is contained inside it.
        // Otherwise a leading insertion can bridge two adjacent, distinct exact occurrences.
        var retained = new List<NearInterval>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var dominated = false;
            if (candidate.Distance > 0)
                for (var next = index + 1; next < candidates.Count && candidates[next].Start < candidate.End; next++)
                    if (candidates[next].End <= candidate.End && candidates[next].Distance < candidate.Distance)
                    {
                        dominated = true;
                        break;
                    }
            if (!dominated)
                retained.Add(candidate);
        }
        var starts = new List<int>();
        var best = retained[0];
        var extent = best.End;
        foreach (var candidate in retained.Skip(1))
        {
            if (candidate.Start >= extent)
            {
                starts.Add(best.Start);
                best = candidate;
                extent = candidate.End;
                continue;
            }
            extent = Math.Max(extent, candidate.End);
            if (candidate.Distance < best.Distance || candidate.Distance == best.Distance &&
                (candidate.End - candidate.Start > best.End - best.Start ||
                 candidate.End - candidate.Start == best.End - best.Start && candidate.Start < best.Start))
                best = candidate;
        }
        starts.Add(best.Start);
        return starts;
    }

    private static void ValidateDifferences(int differences)
    {
        if (differences < 0 || differences > MaximumDifferences)
            throw new ArgumentOutOfRangeException(nameof(differences), "The adapter supports maxDiffs between 0 and 256.");
    }

    private static void EnsureWorkFits(long cells)
    {
        if (cells > MaximumMatchingCells)
            throw new ArgumentException("Similarity matching exceeds the adapter's 100 million cell work limit.");
    }

    private static bool IsAlphanumeric(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or
        UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or
        UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

    private static bool IsDisallowed(int scalar) => scalar is
        >= 0x4E00 and <= 0x9FFF or >= 0x3040 and <= 0x309F or >= 0x30A0 and <= 0x30FF or
        >= 0x1F600 and <= 0x1F64F or >= 0x1F300 and <= 0x1F5FF or
        >= 0x1F680 and <= 0x1F6FF or >= 0x1F1E0 and <= 0x1F1FF;

    private static int[] Scalars(string text)
    {
        var result = new List<int>();
        for (var offset = 0; offset < text.Length;)
        {
            if (!Rune.TryGetRuneAt(text, offset, out var rune))
                throw new ArgumentException("Benchmark text must contain well-formed UTF-16.", nameof(text));
            result.Add(rune.Value);
            offset += rune.Utf16SequenceLength;
        }
        return result.ToArray();
    }

    private static void Append(StringBuilder builder, Rune[] runes, int start, int end)
    {
        for (var index = start; index < end; index++)
            builder.Append(runes[index].ToString());
    }

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.CultureInvariant, RegexTimeout);
    private readonly record struct NearInterval(int Start, int End, int Distance);
}
