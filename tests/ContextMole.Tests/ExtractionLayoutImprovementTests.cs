using ContextMole.Core;
using ContextMole.Documents;

using SkiaSharp;

using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ContextMole.Tests;

public sealed class ExtractionLayoutImprovementTests
{
    [Fact]
    public async Task HtmlRetainsCaptionsEmptyEdgeCellsAndTextInsideCellBlocks()
    {
        using var workspace = new Workspace();
        var path = workspace.File("cells.html");
        await File.WriteAllTextAsync(path,
            "<h1>Accounts</h1><table><caption>Quarterly <em>cash</em> totals</caption>" +
            "<tr><th></th><th>Account</th><th>Notes</th><th></th></tr>" +
            "<tr><td></td><td>Ada</td><td><p>First item</p><p>Second<br>item</p>" +
            "<img alt='Receipt attached'></td><td></td></tr></table>" +
            "<custom-block><p>Separate one</p><p>Separate two</p></custom-block>",
            TestContext.Current.CancellationToken);
        var result = await Extract(path);
        Assert.Empty(result.Errors);
        var caption = Assert.Single(result.Root.Sections, section => section.Text == "Quarterly cash totals");
        Assert.Equal("html/table[1]/caption", caption.Location.StructurePath);
        Assert.Equal("Accounts", caption.Heading);
        var table = Assert.Single(result.Root.Sections, section => section.Location.StructurePath == "html/table[1]");
        Assert.Equal("\tAccount\tNotes\t\n\tAda\tFirst item Second item Receipt attached\t", table.Text);
        Assert.All(table.Text.Split('\n'), row => Assert.Equal(4, row.Split('\t').Length));
        Assert.Contains(result.Root.Sections, section => section.Text == "Separate one\n\nSeparate two");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PdfRulesRetainBlankCellsAndMultilineTextRegardlessOfAlignment(bool segmented)
    {
        using var workspace = new Workspace();
        var path = workspace.File("grid.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 700);
        var xs = new[] { 40, 210, 320, 490 };
        var ys = new[] { 420, 470, 520, 570, 620 };
        foreach (var x in xs)
            for (var part = 0; part < (segmented ? ys.Length - 1 : 1); part++)
                page.DrawLine(new PdfPoint(x, segmented ? ys[part] : ys[0]),
                    new PdfPoint(x, segmented ? ys[part + 1] : ys[^1]), 1);
        foreach (var y in ys)
            for (var part = 0; part < (segmented ? xs.Length - 1 : 1); part++)
                page.DrawLine(new PdfPoint(segmented ? xs[part] : xs[0], y),
                    new PdfPoint(segmented ? xs[part + 1] : xs[^1], y), 1);
        Add("Owner", 50, 600); Add("Total", 252, 600); Add("Notes", 380, 600);
        Add("Ada", 50, 550); Add("1234", 278, 550); Add("Delivery sq. ft.", 332, 550); Add("next week", 332, 534);
        Add("Grace", 50, 500); Add("42", 291, 500);
        Add("Linus", 50, 450); Add("Pending", 364, 450);
        Add("This source preserves invoice details and supplier obligations with enough native text for reliable extraction.", 40, 350);
        await File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
        var result = await Extract(path);
        Assert.Empty(result.Errors);
        var table = Assert.Single(result.Root.Sections, section => section.Location.StructurePath!.Contains("table[", StringComparison.Ordinal));
        Assert.Equal("Owner\tTotal\tNotes\nAda\t1234\tDelivery sq. ft.\u2028next week\nGrace\t42\t\nLinus\t\tPending", table.Text);
        Assert.Contains("grid rules", table.Location.LayoutWarning);
        Assert.NotNull(table.Location.Region);
        Assert.Equal(1, table.Location.Page);

        void Add(string text, double x, double y) => page.AddText(text, 9, new PdfPoint(x, y), font);
    }

    [Fact]
    public async Task CompactNativeTableKeepsNumericCellsAndMixedAlignments()
    {
        using var workspace = new Workspace();
        var path = workspace.File("compact.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 700);
        for (var row = 0; row < 4; row++)
        {
            var y = 600 - row * 18;
            page.AddText(row == 0 ? "N" : (row * 100).ToString(), 10, new PdfPoint(row == 0 ? 106 : 95, y), font);
            page.AddText(row == 0 ? "A" : "2.80", 10, new PdfPoint(row == 0 ? 133 : 127, y), font);
            page.AddText(row == 0 ? "B" : "3.53", 10, new PdfPoint(row == 0 ? 163 : 157, y), font);
            page.AddText(row == 0 ? "C" : "3.27", 10, new PdfPoint(row == 0 ? 193 : 187, y), font);
        }
        page.AddText("This document provides enough additional native text to exercise the compact numerical table extraction accurately.",
            8, new PdfPoint(40, 450), font);
        await File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
        var result = await Extract(path);
        Assert.Empty(result.Errors);
        var table = Assert.Single(result.Root.Sections, section => section.Location.StructurePath!.Contains("table[", StringComparison.Ordinal));
        Assert.Equal("N\tA\tB\tC\n100\t2.80\t3.53\t3.27\n200\t2.80\t3.53\t3.27\n300\t2.80\t3.53\t3.27", table.Text);
    }

    [Fact]
    public async Task GridDividersBelowMergedHeaderRetainIndividualNumericColumns()
    {
        using var workspace = new Workspace();
        var path = workspace.File("merged-header.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 700);
        foreach (var y in new[] { 400, 440, 480, 520, 560 })
            page.DrawLine(new PdfPoint(40, y), new PdfPoint(450, y), 1);
        foreach (var x in new[] { 40, 240, 310, 380, 450 })
            page.DrawLine(new PdfPoint(x, 400), new PdfPoint(x, x is 310 or 380 ? 520 : 560), 1);
        page.AddText("Region", 10, new PdfPoint(50, 540), font);
        page.AddText("Population percentages", 10, new PdfPoint(255, 540), font);
        var rows = new[] { new[] { "North", "17,6", "17,8", "18,2" },
            new[] { "South", "31,4", "32,2", "33,0" }, new[] { "East", "28,7", "30,8", "32,4" } };
        for (var row = 0; row < rows.Length; row++)
            for (var column = 0; column < rows[row].Length; column++)
                page.AddText(rows[row][column], 10, new PdfPoint(column == 0 ? 50 : 250 + (column - 1) * 70,
                    500 - row * 40), font);
        page.AddText("This document preserves individual regional figures and every numeric neighbour in the original tabular evidence.",
            8, new PdfPoint(40, 350), font);
        await File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
        var result = await Extract(path);
        Assert.Empty(result.Errors);
        var table = Assert.Single(result.Root.Sections, section => section.Location.StructurePath!.Contains("table[", StringComparison.Ordinal));
        Assert.Equal(new[] { "North\t17,6\t17,8\t18,2", "South\t31,4\t32,2\t33,0", "East\t28,7\t30,8\t32,4" },
            table.Text.Split('\n').Skip(1));
    }

    [Fact]
    public async Task AlignedTableKeepsSummaryRowWithDifferentLabelIndentation()
    {
        using var workspace = new Workspace();
        var path = workspace.File("indented-labels.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 700);
        var rows = new[] { new[] { "First subgroup", "2.0", "3.8%", "7.8%" },
            new[] { "Second subgroup", "9.4", "17.8%", "15.5%" },
            new[] { "Overall ratio", "16.0", "n/a", "15.1" } };
        for (var row = 0; row < rows.Length; row++)
        {
            page.AddText(rows[row][0], 8, new PdfPoint(row == 2 ? 40 : 47.2, 550 - row * 18), font);
            for (var column = 1; column < rows[row].Length; column++)
                page.AddText(rows[row][column], 8, new PdfPoint(200 + column * 80, 550 - row * 18), font);
        }
        page.AddText("This source includes sufficient native evidence describing the independent subgroup figures and overall summary ratio.",
            8, new PdfPoint(40, 400), font);
        await File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
        var result = await Extract(path);
        Assert.Empty(result.Errors);
        var table = Assert.Single(result.Root.Sections, section => section.Location.StructurePath!.Contains("table[", StringComparison.Ordinal));
        Assert.Equal(string.Join('\n', rows.Select(row => string.Join('\t', row))), table.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BibliographyColumnsWithScatteredNumericFragmentsAreNotTables(bool ocr)
    {
        using var workspace = new Workspace();
        var path = workspace.File(ocr ? "references.png" : "references.pdf");
        var rows = new[] { new[] { "Research evidence about adaptation.", "Transcription-associated mutation." },
            new[] { "Proceedings of the Academy.", "3321-3328." },
            new[] { "America 99, 2164-2169.", "Continued bibliography evidence." } };
        IOcrEngine? engine = null;
        if (ocr)
        {
            using var bitmap = new SKBitmap(600, 700);
            bitmap.Erase(SKColors.White);
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(path, png.ToArray(), TestContext.Current.CancellationToken);
            var lines = rows.SelectMany((row, index) => row.Select((text, column) =>
                new OcrTextLine(text, 96, new SourceRegion(.05 + column * .5, .2 + index * .03, .4, .02)))).ToArray();
            engine = new FixedOcr(new OcrResult(string.Join('\n', lines.Select(line => line.Text)), 96, Lines: lines));
        }
        else
        {
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            var page = builder.AddPage(600, 700);
            for (var row = 0; row < rows.Length; row++)
                for (var column = 0; column < rows[row].Length; column++)
                    page.AddText(rows[row][column], 10, new PdfPoint(40 + column * 300, 550 - row * 18), font);
            await File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
        }
        var result = await Extract(path, engine);
        Assert.Empty(result.Errors);
        Assert.DoesNotContain(result.Root.Sections, section => section.Location.StructurePath!.Contains("table[", StringComparison.Ordinal));
        var text = string.Join('\n', result.Root.Sections.Select(section => section.Text));
        Assert.True(text.IndexOf("America 99", StringComparison.Ordinal) <
                    text.IndexOf("Transcription-associated", StringComparison.Ordinal), text);
    }

    [Fact]
    public async Task OcrProseGroupsWithinColumnsWhileRetainingCanonicalLinesAndRegions()
    {
        using var workspace = new Workspace();
        var path = workspace.File("columns.png");
        using var bitmap = new SKBitmap(600, 700);
        bitmap.Erase(SKColors.White);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, png.ToArray(), TestContext.Current.CancellationToken);
        var lines = new[]
        {
            Line("Left inter-", .05, .2, .35), Line("national evidence", .05, .23, .35),
            Line("Right column", .55, .2, .35), Line("other evidence", .55, .23, .35),
            Line("Separate paragraph", .05, .38, .35)
        };
        var result = await Extract(path, new FixedOcr(new OcrResult(string.Join('\n', lines.Select(line => line.Text)), 96, Lines: lines)));
        Assert.Empty(result.Errors);
        var left = Assert.Single(result.Root.Sections, section => section.Text.StartsWith("Left", StringComparison.Ordinal));
        Assert.Equal("Left inter-\nnational evidence", left.Text);
        Assert.Equal("Left international evidence", TextNormalization.ForSearch(left.Text, dehyphenateLineBreaks: true));
        Assert.Equal(.05, left.Location.Region!.X, 8);
        Assert.Equal(.2, left.Location.Region.Y, 8);
        Assert.Equal(.05, left.Location.Region.Height, 8);
        Assert.Equal(1, left.Location.ImageFrame);
        Assert.Contains(result.Root.Sections, section => section.Text == "Right column\nother evidence");
        Assert.Contains(result.Root.Sections, section => section.Text == "Separate paragraph");
        Assert.Equal(3, result.Root.Sections.Count);

        static OcrTextLine Line(string text, double x, double y, double width) =>
            new(text, 96, new SourceRegion(x, y, width, .02));
    }

    [Theory]
    [InlineData("inter-\nnational", "international")]
    [InlineData("inter\u2010\nnational", "international")]
    [InlineData("inter-\u2028national", "international")]
    [InlineData("inter\u00ad\r\nnational", "international")]
    [InlineData("inter-\n\nnational", "inter- national")]
    [InlineData("inter-\nNational", "inter- National")]
    [InlineData("well-known", "well-known")]
    [InlineData("non\u2011\nbreaking", "non\u2010 breaking")]
    public void SearchJoinsOnlySingleLineDiscretionaryBreaks(string text, string expected)
    {
        Assert.Equal(expected, TextNormalization.ForSearch(text, dehyphenateLineBreaks: true));
        if (text.Contains('-')) Assert.Contains('-', TextNormalization.ForDisplay(text));
    }

    private static Task<ExtractionResult> Extract(string path, IOcrEngine? ocr = null) =>
        new DocumentExtractionRegistry(ocr ?? new FixedOcr(new OcrResult("", null))).ExtractAsync(
            new ExtractionRequest(path), TestContext.Current.CancellationToken);

    private sealed class FixedOcr(OcrResult result) : IOcrEngine
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) => Task.FromResult(result);
        public Task<OcrResult> RecognizeAsync(Func<CancellationToken, Task<OcrRequest>> prepareRequest,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "ContextMole-LayoutTests-" + Guid.NewGuid().ToString("N"));
        public Workspace() => Directory.CreateDirectory(_path);
        public string File(string name) => Path.Combine(_path, name);
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
