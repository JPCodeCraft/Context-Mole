using ContextMole.Search;

namespace ContextMole.Tests;

public sealed class SemanticPreviewRegressionTests
{
    [Theory]
    [InlineData("What percentage applies to ZXTR?", "ZXTR")]
    [InlineData("How did product Ax42 perform?", "Ax42")]
    [InlineData("ZXTR", "ZXTR")]
    [InlineData("Qual é a proporção de ZXTR?", "ｚｘｔｒ")]
    public void SalientLiteralQueryAnchorsCanRevealLateContext(string query, string sourceAnchor)
    {
        var text = new string('x', 1100) + " " + sourceAnchor + " has a 9% share. " + new string('y', 450);
        var window = Assert.IsType<SemanticPreviewAnchors.Window>(new SemanticPreviewAnchors(query).FindWindow(text, 800));
        Assert.True(window.Start > 0);
        Assert.Contains(sourceAnchor + " has a 9% share", text.Substring(window.Start, 800), StringComparison.Ordinal);
        var anchor = Assert.Single(window.Anchors);
        Assert.Equal(sourceAnchor, text.Substring(anchor.Start, anchor.Length));
    }

    [Theory]
    [InlineData("What percentage of current jobs is represented?", "percentage current jobs represented")]
    [InlineData("How did the EU perform in 2024?", "EU 2024")]
    [InlineData("WHAT PERCENTAGE OF CURRENT JOBS?", "WHAT PERCENTAGE CURRENT JOBS")]
    [InlineData("What percentage applies to ZXTR?", "ZXTRplus preZXTR")]
    [InlineData("What percentage applies to ZXTR?", "a differently worded translated concept")]
    [InlineData("What percentage applies to zxtr?", "zxtr")]
    public void CommonWordsBareYearsShortAbbreviationsAndNoMatchesDoNotMovePreview(string query, string tail)
    {
        var text = new string('x', 1100) + " " + tail;
        Assert.Null(new SemanticPreviewAnchors(query).FindWindow(text, 800));
    }

    [Fact]
    public void ExistingPrefixWinsTiesAndRepeatedAnchorsDoNotOutvoteDistinctCoverage()
    {
        var text = "ZXTR QWRT " + new string('x', 1200) + string.Concat(Enumerable.Repeat(" ZXTR", 100));
        var window = Assert.IsType<SemanticPreviewAnchors.Window>(
            new SemanticPreviewAnchors("Compare ZXTR with QWRT").FindWindow(text, 800));
        Assert.Equal(0, window.Start);
        Assert.Equal(2, window.Anchors.Count);
    }

    [Fact]
    public void DistinctLateAnchorsCanOutvoteASingleEarlyAnchor()
    {
        var text = "ZXTR " + new string('x', 1200) + " ZXTR QWRT " + new string('y', 500);
        var window = Assert.IsType<SemanticPreviewAnchors.Window>(
            new SemanticPreviewAnchors("Compare ZXTR with QWRT").FindWindow(text, 800));
        Assert.True(window.Start > 0);
        Assert.Equal(["zxtr", "qwrt"], window.Anchors.Select(token => token.Value));
    }

    [Fact]
    public void QueriesWithoutSalientAnchorsDoNotTokenizeLargePassages()
    {
        var text = string.Concat(Enumerable.Repeat("ordinary question context ", 10_000));
        var query = new SemanticPreviewAnchors("How do ordinary questions work?");
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(query.FindWindow(text, 800));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024);
    }

    [Fact]
    public void RepeatedAnchorsKeepThePrefixAndRespectTheWindowLimit()
    {
        var text = string.Concat(Enumerable.Repeat("ZXTR ", 10_000));
        var window = Assert.IsType<SemanticPreviewAnchors.Window>(
            new SemanticPreviewAnchors("ZXTR").FindWindow(text, 800));
        Assert.Equal(0, window.Start);
        Assert.All(window.Anchors, token => Assert.InRange(token.Start + token.Length, 1, 800));
    }

    [Fact]
    public void SlidingWindowsAgreeWithExhaustiveDistinctAnchorCoverage()
    {
        var random = new Random(1031);
        string[] choices = ["ZXTR", "QWRT", "AB42", "ordinary"];
        var query = new SemanticPreviewAnchors("Compare ZXTR and QWRT with AB42");
        for (var sample = 0; sample < 100; sample++)
        {
            var text = string.Join(' ', Enumerable.Range(0, 200).Select(_ =>
                choices[random.Next(choices.Length)] + new string(' ', random.Next(0, 100))));
            var matches = ContextMole.Core.LexicalText.TokenizeWithOffsets(text)
                .Where(token => token.Value is "zxtr" or "qwrt" or "ab42").ToArray();
            var expected = matches.Select(token => Math.Clamp(token.Start - 400, 0, text.Length - 800))
                .Prepend(0).Distinct().OrderByDescending(start => matches
                    .Where(token => token.Start >= start && token.Start + token.Length <= start + 800)
                    .Select(token => token.Value).Distinct().Count()).ThenBy(start => start).First();
            Assert.Equal(expected, query.FindWindow(text, 800)!.Start);
        }
    }
}
