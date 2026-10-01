using ContextMole.Benchmarks;

namespace ContextMole.Tests;

public sealed class OlmOcrBenchmarkTextTests
{
    [Fact]
    public void NormalizationPreservesBoundarySpacesAndCombinesUnicode()
    {
        Assert.Equal(" 'caf\u00E9' - _ \u03BC ",
            OlmOcrBenchmarkText.Normalize("\t\u2018**cafe\u0301**\u2019<br/>\u2014 \uFF3F \u00B5\r\n"));
        Assert.Equal("bold italic both emphasis",
            OlmOcrBenchmarkText.Normalize("<b>bold</b> <i>italic</i> __both__ _emphasis_"));
        Assert.Equal("a b ", OlmOcrBenchmarkText.Normalize("a\u001Cb\u001F"));
    }

    [Fact]
    public void NormalizationRetainsUnrecognizedMarkupAndSingleEmphasisAcrossLines()
    {
        Assert.Equal("<BR><br />*two lines*",
            OlmOcrBenchmarkText.Normalize("<BR><br />*two\nlines*"));
    }

    [Fact]
    public void PrefixSuffixSlicesUseCodePointsAndConcatenateBothRegions()
    {
        Assert.True(OlmOcrBenchmarkText.MatchText("\U00010400", "\U00010400middleZ", 0, firstN: 1));
        Assert.True(OlmOcrBenchmarkText.MatchText("\U00010400", "Zmiddle\U00010400", 0, lastN: 1));
        Assert.False(OlmOcrBenchmarkText.MatchText("CX", "ABCmiddleXYZ", 0));
        Assert.True(OlmOcrBenchmarkText.MatchText("CX", "ABCmiddleXYZ", 0, firstN: 3, lastN: 3));
        Assert.True(OlmOcrBenchmarkText.MatchText("middle", "ABCmiddleXYZ", 0, firstN: 0, lastN: 0));
    }

    [Fact]
    public void ExactPartialMatchIsSymmetricAndRespectsCase()
    {
        Assert.True(OlmOcrBenchmarkText.MatchText("abcdef", "abc", 0));
        Assert.True(OlmOcrBenchmarkText.MatchText("abc", "xxabcdef", 0));
        Assert.False(OlmOcrBenchmarkText.MatchText("ABC", "xxabc", 0));
        Assert.True(OlmOcrBenchmarkText.MatchText("ABC", "xxabc", 0, caseSensitive: false));
        Assert.False(OlmOcrBenchmarkText.MatchText("abc", "", 3));
    }

    [Theory]
    [InlineData("contract", "xx contrct yy", 1, true)]
    [InlineData("contract", "xx contracxt yy", 1, true)]
    [InlineData("contract", "xx contrxct yy", 1, true)]
    [InlineData("contract", "xx conzzact yy", 1, false)]
    public void ApproximatePresenceSupportsInsertionsDeletionsAndSubstitutions(
        string query, string content, int differences, bool expected)
    {
        Assert.Equal(expected, OlmOcrBenchmarkText.MatchText(query, content, differences));
    }

    [Fact]
    public void ExactSearchUsesScalarOffsetsAndRetainsOverlappingOccurrences()
    {
        Assert.Equal<int>(new[] { 1, 5 }, OlmOcrBenchmarkText.FindNearStarts("abc", "\U00010400abc abc", 0));
        Assert.Equal<int>(new[] { 0, 1 }, OlmOcrBenchmarkText.FindNearStarts("aa", "aaa", 0));
        Assert.Empty(OlmOcrBenchmarkText.FindNearStarts("abc", "ab", 0));
    }

    [Fact]
    public void ApproximateSearchConsolidatesOneOccurrenceButRetainsSeparateOccurrences()
    {
        Assert.Equal<int>(new[] { 0 }, OlmOcrBenchmarkText.FindNearStarts("abc", "abc", 1));
        Assert.Equal<int>(new[] { 0, 4 }, OlmOcrBenchmarkText.FindNearStarts("abc", "abc abc", 1));
        Assert.Equal<int>(new[] { 0, 3 }, OlmOcrBenchmarkText.FindNearStarts("abc", "abcabc", 1));
        Assert.Equal<int>(new[] { 1, 6 }, OlmOcrBenchmarkText.FindNearStarts("abc", "\U00010400abxc abc", 1));
    }

    [Theory]
    [InlineData("abc", "bc", 1)]
    [InlineData("abc", "ab", 1)]
    [InlineData("abc", "abxc", 1)]
    [InlineData("\U00010400ab", "\U00010400xb", 1)]
    public void ApproximateSearchHandlesTerminalDeletionsAndSupplementaryCharacters(
        string query, string content, int differences)
    {
        Assert.Equal<int>(new[] { 0 }, OlmOcrBenchmarkText.FindNearStarts(query, content, differences));
    }

    [Fact]
    public void IndelSimilarityUsesWholeCellsAndLcsRatherThanSubstringOrSubstitutionDistance()
    {
        Assert.Equal(2.0 / 3, OlmOcrBenchmarkText.IndelSimilarity("abc", "axc"), precision: 10);
        Assert.Equal(6.0 / 7, OlmOcrBenchmarkText.IndelSimilarity("abc", "abcd"), precision: 10);
        Assert.Equal(0, OlmOcrBenchmarkText.IndelSimilarity("ABC", "abc"));
        Assert.Equal(1, OlmOcrBenchmarkText.IndelSimilarity("**cafe\u0301**", "caf\u00E9"));
        Assert.Equal(0.5, OlmOcrBenchmarkText.IndelSimilarity("\U00010400a", "\U00010400b"));
        Assert.Equal(1, OlmOcrBenchmarkText.IndelSimilarity("", ""));
        Assert.Equal(0, OlmOcrBenchmarkText.IndelSimilarity("", "a"));
    }

    [Fact]
    public void BlankPageLimitsCountAllUnicodeAlphanumericsAndShortCircuitOtherChecks()
    {
        Assert.True(OlmOcrBenchmarkText.CheckBaseline("", maxLength: 0).Passed);
        Assert.True(OlmOcrBenchmarkText.CheckBaseline("\U00010400", maxLength: 1).Passed);
        Assert.False(OlmOcrBenchmarkText.CheckBaseline("\U00010400\u00B2\u2163", maxLength: 2).Passed);
        Assert.True(OlmOcrBenchmarkText.CheckBaseline("\u4E2D\u6587\U0001F600" + new string('x', 31), maxLength: 33).Passed);
        Assert.True(OlmOcrBenchmarkText.CheckBaseline("\u00B2\u2163").Passed);
        Assert.False(OlmOcrBenchmarkText.CheckBaseline(" -- \n").Passed);
    }

    [Fact]
    public void BlankPageImageExclusionRemovesWholeMarkdownImageTags()
    {
        const string content = "![Cover description](cover123.png)";
        Assert.False(OlmOcrBenchmarkText.CheckBaseline(content, maxLength: 0).Passed);
        Assert.True(OlmOcrBenchmarkText.CheckBaseline(content, maxLength: 0, skipImageAlt: true).Passed);
        Assert.False(OlmOcrBenchmarkText.CheckBaseline(content + " caption", maxLength: 0, skipImageAlt: true).Passed);
        Assert.False(OlmOcrBenchmarkText.CheckBaseline("![multi\nline](img)", maxLength: 0, skipImageAlt: true).Passed);
    }

    [Fact]
    public void RepetitionRejectsThirtyOneBlocksButAcceptsThirty()
    {
        Assert.True(OlmOcrBenchmarkText.CheckBaseline(new string('a', 30)).Passed);
        var repeated = OlmOcrBenchmarkText.CheckBaseline(new string('a', 31));
        Assert.False(repeated.Passed);
        Assert.Contains("31", repeated.Reason);
        Assert.True(OlmOcrBenchmarkText.CheckBaseline(string.Concat(Enumerable.Repeat("abc", 30))).Passed);
        Assert.False(OlmOcrBenchmarkText.CheckBaseline(string.Concat(Enumerable.Repeat("abc", 31))).Passed);
        Assert.True(OlmOcrBenchmarkText.CheckBaseline(string.Concat(Enumerable.Repeat("abcdef", 100))).Passed);
    }

    [Fact]
    public void SuffixDetectionCollapsesWhitespaceWithoutTrimmingOrScanningEarlierText()
    {
        Assert.False(OlmOcrBenchmarkText.CheckBaseline(string.Concat(Enumerable.Repeat("a\t\n", 31))).Passed);
        Assert.True(OlmOcrBenchmarkText.CheckBaseline(new string('a', 31) + " ").Passed);
        Assert.True(OlmOcrBenchmarkText.CheckBaseline(new string('a', 100) + " END").Passed);
        Assert.False(OlmOcrBenchmarkText.CheckBaseline(string.Concat(Enumerable.Repeat("\U00010400", 31))).Passed);
    }

    [Theory]
    [InlineData("a\u4E2D")]
    [InlineData("a\u3042")]
    [InlineData("a\u30A2")]
    [InlineData("a\U0001F600")]
    [InlineData("a\U0001F300")]
    [InlineData("a\U0001F680")]
    [InlineData("a\U0001F1E6")]
    public void DisallowedCharacterRangesIncludeSupplementaryEmoji(string content)
    {
        Assert.False(OlmOcrBenchmarkText.CheckBaseline(content).Passed);
        Assert.True(OlmOcrBenchmarkText.CheckBaseline(content, checkDisallowed: false).Passed);
    }

    [Fact]
    public void MatchingRejectsInvalidLimitsAndExcessiveWorkExplicitly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OlmOcrBenchmarkText.MatchText("a", "a", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => OlmOcrBenchmarkText.FindNearStarts("a", "a", 257));
        Assert.Throws<ArgumentOutOfRangeException>(() => OlmOcrBenchmarkText.MatchText("a", "a", 0, firstN: -1));
        Assert.Throws<ArgumentException>(() => OlmOcrBenchmarkText.FindNearStarts("", "abc", 0));
        Assert.Throws<ArgumentException>(() => OlmOcrBenchmarkText.FindNearStarts(new string('a', 1_000), new string('b', 30_000), 2));
        Assert.Throws<ArgumentException>(() => OlmOcrBenchmarkText.IndelSimilarity(new string('a', 10_001), new string('b', 10_000)));
    }

    [Fact]
    public void MalformedUtf16CannotBecomeAnExactReplacementCharacterMatch()
    {
        Assert.Throws<ArgumentException>(() => OlmOcrBenchmarkText.FindNearStarts("\uD800", "\uD801", 0));
        Assert.Throws<ArgumentException>(() => OlmOcrBenchmarkText.FindNearStarts("a", "a\uDC00", 0));
        Assert.Equal<int>(new[] { 0 }, OlmOcrBenchmarkText.FindNearStarts("\uFFFD", "\uFFFD", 0));
    }

    [Fact]
    public void OrderingIdenticalTextRequiresDistinctOccurrencesAfterApproximateConsolidation()
    {
        var oneOccurrence = OlmOcrBenchmarkText.FindNearStarts("abc", "abc", 1);
        Assert.DoesNotContain(oneOccurrence, before => oneOccurrence.Any(after => before < after));
        var adjacentOccurrences = OlmOcrBenchmarkText.FindNearStarts("abc", "abcabc", 1);
        Assert.Contains(adjacentOccurrences, before => adjacentOccurrences.Any(after => before < after));
    }
}
