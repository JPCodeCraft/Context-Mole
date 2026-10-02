using ContextMole.Core;
using ContextMole.Search;

namespace ContextMole.Tests;

public sealed class LexicalWrapRegressionTests
{
    [Theory]
    [InlineData("re\u00ad\nentry", "reentry")]
    [InlineData("re\u00ad\r\n  entry", "reentry")]
    [InlineData("re\u00ad\u2028entry", "reentry")]
    [InlineData("cafe\u0301\u00ad\nteria", "cafe\u0301teria")]
    [InlineData("re\u00ad\n\nentry", "re\n\nentry")]
    [InlineData("re\u00ad\nEntry", "re\nEntry")]
    public void DisplayCleanupRetainsDiscretionaryWrapMeaning(string source, string expected)
    {
        Assert.Equal(expected, TextNormalization.ForDisplay(source));
    }

    [Fact]
    public void TableCleanupNeverJoinsAcrossRowsOrCellBoundaries()
    {
        const string rows = "Label\tinter\u00ad\nnational\tValue";
        Assert.Equal("Label\tinter\nnational\tValue", TextNormalization.ForDisplay(rows, preserveTableWhitespace: true));
        const string cells = "inter\u00ad\t\u2028national\tValue";
        Assert.Equal("inter\t\u2028national\tValue", TextNormalization.ForDisplay(cells, preserveTableWhitespace: true));
    }

    [Fact]
    public void TableCleanupResolvesOnlyExplicitInCellDiscretionaryWraps()
    {
        const string table = "\tName\tNotes\t\n\tAda\tinter\u00ad\u2028national\t";
        Assert.Equal("\tName\tNotes\t\n\tAda\tinternational\t", TextNormalization.ForDisplay(table, preserveTableWhitespace: true));
    }

    [Theory]
    [InlineData("re-\nentry")]
    [InlineData("re-\r\n  entry")]
    [InlineData("re-\rentry")]
    [InlineData("re\u2010\nentry")]
    [InlineData("re\u00ad\nentry")]
    [InlineData("re-\u2028entry")]
    [InlineData("re\u2010\u2028entry")]
    public void WrappedWordsMatchOneCanonicalTokenAndRetainLiteralSourceSpan(string wrapped)
    {
        var text = "Procedure for " + wrapped + " approval";
        Assert.Equal("procedure for reentry approval", LexicalText.Canonicalize(text));
        var spans = StructuredSearchQuery.FindBodyMatches(text,
            [new SearchClause("word", "reentry", SearchClauseOccur.Must, Fields: [SearchField.Body])]);
        var span = Assert.Single(spans);
        Assert.Equal(wrapped, text.Substring(span.Start, span.Length));
    }

    [Theory]
    [InlineData("re-\n\nentry")]
    [InlineData("re-\r\n \t\r\nentry")]
    [InlineData("re\u2010\n\nentry")]
    [InlineData("re-\nEntry")]
    [InlineData("re-entry")]
    [InlineData("re-\u2028\u2028entry")]
    public void SeparateParagraphsCapitalsAndOrdinaryCompoundsDoNotCreateFusedWords(string text)
    {
        Assert.Equal(["re", "entry"], LexicalText.Tokens(text));
        Assert.Empty(StructuredSearchQuery.FindBodyMatches(text,
            [new SearchClause("word", "reentry", SearchClauseOccur.Must, Fields: [SearchField.Body])]));
    }
    [Fact]
    public void CombiningAccentAtWrapRetainsItsCanonicalWordAndSourceSpan()
    {
        const string text = "cafe\u0301-\nteria";
        Assert.Equal("cafeteria", LexicalText.Canonicalize(text));
        var span = Assert.Single(StructuredSearchQuery.FindBodyMatches(text,
            [new SearchClause("word", "cafeteria", SearchClauseOccur.Must, Fields: [SearchField.Body])]));
        Assert.Equal(text, text.Substring(span.Start, span.Length));
    }

}
