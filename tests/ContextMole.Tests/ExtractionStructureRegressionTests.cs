using System.Text;

using ContextMole.Core;
using ContextMole.Documents;

using DocumentFormat.OpenXml.Packaging;

using W = DocumentFormat.OpenXml.Wordprocessing;

using SkiaSharp;

using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ContextMole.Tests;

public sealed class ExtractionStructureRegressionTests
{
    [Theory]
    [InlineData(".html", "<h1>Contract</h1><h2>Details</h2><p>First evidence.</p><h2>Details</h2><p>Second evidence.</p><h1>Other</h1><p>Third evidence.</p>")]
    [InlineData(".md", "# Contract\n\n## Details\n\nFirst evidence.\n\n## Details\n\nSecond evidence.\n\n# Other\n\nThird evidence.\n")]
    public async Task HeadingOccurrencesHaveSeparateIdentitiesAndPreserveHierarchy(string extension, string text)
    {
        using var workspace = new Workspace();
        var path = workspace.File("headings" + extension);
        await System.IO.File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        var result = await Extract(path);
        Assert.Empty(result.Errors);
        var first = Assert.Single(result.Root.Sections, section => section.Text == "First evidence.");
        var second = Assert.Single(result.Root.Sections, section => section.Text == "Second evidence.");
        var third = Assert.Single(result.Root.Sections, section => section.Text == "Third evidence.");
        Assert.Equal(new[] { "Contract", "Details" }, first.HeadingPath);
        Assert.Equal(first.HeadingPath, second.HeadingPath);
        Assert.NotEqual(first.SectionKey, second.SectionKey);
        Assert.Equal(new[] { "Other" }, third.HeadingPath);
        Assert.Equal(first.SectionKey, result.Root.Sections[1].SectionKey);
    }

    [Fact]
    public async Task HtmlBlockBoundariesAndTableCellsRemainReadableAndBoilerplateIsRetained()
    {
        using var workspace = new Workspace();
        var path = workspace.File("table.html");
        await System.IO.File.WriteAllTextAsync(path,
            "<header>Reference header</header><h1>Invoices</h1><p>Alpha</p><p>Beta</p>" +
            "<table><tr><th>Vendor</th><th>Amount</th></tr><tr><td>Ada</td><td>42</td></tr></table>" +
            "<table><tr><td colspan='2'>Merged label</td></tr></table><footer>Contact footer</footer>",
            TestContext.Current.CancellationToken);
        var result = await Extract(path);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Root.Sections, section => section.Text == "Alpha");
        Assert.Contains(result.Root.Sections, section => section.Text == "Beta");
        var table = Assert.Single(result.Root.Sections, section => section.Text.Contains("Ada", StringComparison.Ordinal));
        Assert.Equal("Vendor\tAmount\nAda\t42", table.Text);
        Assert.Equal("Invoices", table.Heading);
        Assert.Null(table.Location.LayoutWarning);
        Assert.Contains(result.Root.Sections, section => section.Text.Contains("Merged label", StringComparison.Ordinal) &&
            section.Location.LayoutWarning?.StartsWith("table_spans", StringComparison.Ordinal) == true);
        Assert.Contains(result.Root.Sections, section => section.Text == "Reference header" && section.IsBoilerplate);
        Assert.Contains(result.Root.Sections, section => section.Text == "Contact footer" && section.IsBoilerplate);
    }

    [Fact]
    public async Task NativePdfReadsWholeColumnsInOrderAndCarriesPageRegions()
    {
        using var workspace = new Workspace();
        var path = workspace.File("columns.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 700);
        page.AddText("Contract", 24, new PdfPoint(40, 650), font);
        for (var row = 0; row < 5; row++)
        {
            // Deliberately write the right column first in the content stream.
            page.AddText($"Right column row {row} delivery evidence.", 10, new PdfPoint(330, 590 - row * 18), font);
            page.AddText($"Left column row {row} notice evidence.", 10, new PdfPoint(40, 590 - row * 18), font);
        }
        await System.IO.File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
        var result = await Extract(path);
        Assert.Empty(result.Errors);
        var text = string.Join('\n', result.Root.Sections.Select(section => section.Text));
        Assert.True(text.IndexOf("Left column row 4", StringComparison.Ordinal) < text.IndexOf("Right column row 0", StringComparison.Ordinal), text);
        Assert.All(result.Root.Sections, section =>
        {
            Assert.Equal(1, section.Location.Page);
            Assert.NotNull(section.Location.Region);
            Assert.InRange(section.Location.Region.X, 0, 1);
            Assert.InRange(section.Location.Region.Y, 0, 1);
            Assert.True(section.Location.Region.Width > 0);
            Assert.NotNull(section.SectionKey);
        });
        Assert.Contains(result.Root.Sections, section => section.Text.Contains("notice evidence", StringComparison.Ordinal) &&
            section.Heading == "Contract");
    }

    [Fact]
    public async Task WordInheritedHeadingStylesKeepHierarchyAndHeadingOccurrences()
    {
        using var workspace = new Workspace();
        var path = workspace.File("styles.docx");
        using (var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new W.Styles(
                new W.Style(new W.StyleParagraphProperties(new W.OutlineLevel { Val = 0 })) { StyleId = "Heading1" },
                new W.Style(new W.StyleParagraphProperties(new W.OutlineLevel { Val = 1 })) { StyleId = "Heading2" },
                new W.Style(new W.BasedOn { Val = "Heading1" }) { StyleId = "ContractTitle" },
                new W.Style(new W.BasedOn { Val = "Heading2" }) { StyleId = "ContractSubtitle" });
            main.Document = new W.Document(new W.Body(Paragraph("Contract", "ContractTitle"),
                Paragraph("Details", "ContractSubtitle"), Paragraph("First evidence"),
                Paragraph("Details", "ContractSubtitle"), Paragraph("Second evidence")));
        }
        var result = await Extract(path);
        Assert.Empty(result.Errors);
        var first = Assert.Single(result.Root.Sections, section => section.Text == "First evidence");
        var second = Assert.Single(result.Root.Sections, section => section.Text == "Second evidence");
        Assert.Equal(new[] { "Contract", "Details" }, first.HeadingPath);
        Assert.Equal(first.HeadingPath, second.HeadingPath);
        Assert.NotEqual(first.SectionKey, second.SectionKey);

        static W.Paragraph Paragraph(string text, string? style = null)
        {
            var paragraph = new W.Paragraph(new W.Run(new W.Text(text)));
            if (style is not null) paragraph.PrependChild(new W.ParagraphProperties(new W.ParagraphStyleId { Val = style }));
            return paragraph;
        }
    }

    [Fact]
    public async Task PdfTitleAboveOneLongBodyBlockDefinesLogicalSection()
    {
        using var workspace = new Workspace();
        var path = workspace.File("title.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 700);
        page.AddText("Contract terms", 20, new PdfPoint(40, 650), font);
        const string body = "This contract contains the full notice period and supplier delivery obligations with enough searchable evidence to index.";
        page.AddText(body, 9, new PdfPoint(40, 590), font);
        await System.IO.File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
        var result = await Extract(path);
        Assert.Empty(result.Errors);
        var section = Assert.Single(result.Root.Sections, section => section.Text == body);
        Assert.Equal("Contract terms", section.Heading);
        Assert.Equal(new[] { "Contract terms" }, section.HeadingPath);
        Assert.StartsWith("pdf-heading:", section.SectionKey);
    }

    [Fact]
    public async Task PdfTablesRetainRowsAndCellsWithoutPretendingTheirStructureIsCertain()
    {
        using var workspace = new Workspace();
        var path = workspace.File("table.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 700);
        var rows = new[] { new[] { "Vendor", "Amount", "State" }, new[] { "Ada", "42", "Paid" },
            new[] { "Linus", "17", "Open" }, new[] { "Grace", "93", "Paid" } };
        for (var row = 0; row < rows.Length; row++)
            for (var column = 0; column < rows[row].Length; column++)
                page.AddText(rows[row][column], 12, new PdfPoint(40 + column * 170, 600 - row * 22), font);
        page.AddText("This document records supplier invoice payments for the finance team and retains every original amount.",
            10, new PdfPoint(40, 430), font);
        await System.IO.File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
        var result = await Extract(path);
        Assert.Empty(result.Errors);
        var table = Assert.Single(result.Root.Sections, section => section.Text.Contains("Ada", StringComparison.Ordinal));
        Assert.Equal("Vendor\tAmount\tState\nAda\t42\tPaid\nLinus\t17\tOpen\nGrace\t93\tPaid", table.Text);
        Assert.Contains("table[", table.Location.StructurePath);
        Assert.StartsWith("table_inferred:", table.Location.LayoutWarning);
    }

    [Fact]
    public async Task RotatedNativePdfRetainsBaselineSequenceAndDisplayedPageRegion()
    {
        using var workspace = new Workspace();
        var path = workspace.File("rotated.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 700);
        page.SetRotation(new UglyToad.PdfPig.Content.PageRotationDegrees(90));
        const string evidence = "Rotated page evidence preserves the notice period and final supplier handover obligations for the contract.";
        page.AddText(evidence, 10, new PdfPoint(40, 560), font);
        await System.IO.File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
        var result = await Extract(path);
        Assert.Empty(result.Errors);
        var section = Assert.Single(result.Root.Sections);
        Assert.Equal(evidence, section.Text);
        Assert.Equal(1, section.Location.Page);
        Assert.StartsWith("rotated_layout:", section.Location.LayoutWarning);
        var region = Assert.IsType<SourceRegion>(section.Location.Region);
        Assert.InRange(region.X, 0.79, 0.82);
        Assert.InRange(region.Y, 0.05, 0.09);
        Assert.True(region.Height > region.Width);
    }

    [Fact]
    public async Task PdfGridSupportsTextOnlyTablesWithoutJoiningAlignedProseColumns()
    {
        using var workspace = new Workspace();
        var path = workspace.File("grid.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 700);
        var rows = new[] { new[] { "Owner", "Status", "Action" }, new[] { "Ada", "Paid", "Review" },
            new[] { "Grace", "Open", "Approve" }, new[] { "Linus", "Closed", "Archive" } };
        for (var row = 0; row < rows.Length; row++)
            for (var column = 0; column < rows[row].Length; column++)
                page.AddText(rows[row][column], 12, new PdfPoint(50 + column * 150, 600 - row * 22), font);
        for (var row = 0; row <= rows.Length; row++)
            page.DrawLine(new PdfPoint(40, 615 - row * 22), new PdfPoint(490, 615 - row * 22), 1);
        for (var column = 0; column <= 3; column++)
            page.DrawLine(new PdfPoint(40 + column * 150, 527), new PdfPoint(40 + column * 150, 615), 1);
        page.AddText("This native document retains all supplier decisions for the contract and contains sufficient evidence for indexing.",
            8, new PdfPoint(40, 400), font);
        await System.IO.File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
        var result = await Extract(path);
        Assert.Empty(result.Errors);
        var table = Assert.Single(result.Root.Sections, section => section.Text.Contains("Ada", StringComparison.Ordinal));
        Assert.Equal("Owner\tStatus\tAction\nAda\tPaid\tReview\nGrace\tOpen\tApprove\nLinus\tClosed\tArchive", table.Text);
        Assert.Contains("grid rules", table.Location.LayoutWarning);
    }

    [Fact]
    public async Task PdfRepeatedPageMarginsAreAnnotatedWithoutRemovingExactEvidence()
    {
        using var workspace = new Workspace();
        var path = workspace.File("margins.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var index = 1; index <= 3; index++)
        {
            var page = builder.AddPage(600, 700);
            page.AddText("Reference archive", 10, new PdfPoint(40, 670), font);
            page.AddText($"Page {index}", 10, new PdfPoint(40, 30), font);
            page.AddText($"Unique page {index} evidence covers notice periods and supplier delivery obligations for this particular contract.",
                10, new PdfPoint(40, 500), font);
        }
        await System.IO.File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
        var result = await Extract(path);
        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Root.Sections.Count(section => section.Text == "Reference archive" && section.IsBoilerplate));
        Assert.Equal(3, result.Root.Sections.Count(section => section.Text.StartsWith("Page ", StringComparison.Ordinal) && section.IsBoilerplate));
        Assert.All(result.Root.Sections.Where(section => section.Text.StartsWith("Unique", StringComparison.Ordinal)), section => Assert.False(section.IsBoilerplate));
    }

    [Fact]
    public async Task MixedPdfOcrAddsImageEvidenceAndDeduplicatesNativeText()
    {
        if (!DesktopRenderingSupported()) return;
        using var workspace = new Workspace();
        var path = workspace.File("mixed.pdf");
        const string native = "Native reference evidence contains sufficient words and characters to avoid the old sparse page heuristic entirely.";
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 700);
        page.AddText(native, 8, new PdfPoint(40, 640), font);
        using var bitmap = new SKBitmap(800, 400);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        page.AddPng(png.ToArray(), new PdfRectangle(40, 100, 560, 450));
        await System.IO.File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
        var engine = new FixedOcr(new OcrResult(native + "\nAprovação digital em São Paulo.", 96,
            Lines: [new OcrTextLine(native, 99, new SourceRegion(0.06, 0.07, 0.85, 0.03)),
                new OcrTextLine("Aprovação digital em São Paulo.", 94, new SourceRegion(0.1, 0.5, 0.6, 0.06))]));
        var result = await Extract(path, engine);
        Assert.Empty(result.Errors);
        Assert.True(engine.Calls == 1, string.Join('\n', result.Root.Sections.Select(section => section.Text)));
        Assert.Equal(1, result.Root.Sections.Count(section => section.Text.Contains(native, StringComparison.Ordinal)));
        var scanned = Assert.Single(result.Root.Sections, section => section.Text.Contains("São Paulo", StringComparison.Ordinal));
        Assert.Equal(ExtractionMethod.Ocr, scanned.Method);
        Assert.Equal(94, scanned.OcrConfidence);
        Assert.Equal(1, scanned.Location.Page);
        Assert.Equal(new SourceRegion(0.1, 0.5, 0.6, 0.06), scanned.Location.Region);
    }

    [Fact]
    public async Task SparseValidNativeTextSurvivesLowQualityOcr()
    {
        if (!DesktopRenderingSupported()) return;
        using var workspace = new Workspace();
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(300, 400).AddText("Contract ID AB12", 12, new PdfPoint(20, 300), font);
        var path = workspace.File("sparse.pdf");
        await System.IO.File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
        var result = await Extract(path, new FixedOcr(new OcrResult("unreliable text", 20,
            Lines: [new OcrTextLine("unreliable text", 20, new SourceRegion(0.05, 0.2, 0.6, 0.04))])));
        Assert.Empty(result.Errors);
        Assert.Equal("Contract ID AB12", Assert.Single(result.Root.Sections).Text);
        Assert.Equal(ExtractionMethod.NativeText, result.Root.Sections[0].Method);
    }

    [Fact]
    public void OcrOrderingSeparatesColumnsAndKeepsHeadingFirst()
    {
        var lines = new[]
        {
            new OcrTextLine("Heading", 99, new SourceRegion(0.05, 0.02, 0.9, 0.04)),
            new OcrTextLine("Right one", 99, new SourceRegion(0.55, 0.2, 0.4, 0.04)),
            new OcrTextLine("Left one", 99, new SourceRegion(0.05, 0.2, 0.4, 0.04)),
            new OcrTextLine("Right two", 99, new SourceRegion(0.55, 0.26, 0.4, 0.04)),
            new OcrTextLine("Left two", 99, new SourceRegion(0.05, 0.26, 0.4, 0.04))
        };
        Assert.Equal(new[] { "Heading", "Left one", "Left two", "Right one", "Right two" },
            ExtractionLayout.OrderLines(lines).Select(line => line.Text));
    }

    [Fact]
    public async Task OcrBoxesReconstructConservativeTableRowsAndRetainImageProvenance()
    {
        using var workspace = new Workspace();
        var path = workspace.File("ocr-table.png");
        using var bitmap = new SKBitmap(600, 700);
        bitmap.Erase(SKColors.White);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        await System.IO.File.WriteAllBytesAsync(path, png.ToArray(), TestContext.Current.CancellationToken);
        var rows = new[] { new[] { "Vendor", "Amount", "Status" }, new[] { "Ada", "42", "Paid" },
            new[] { "Grace", "93", "Open" } };
        var lines = rows.SelectMany((row, rowIndex) => row.Select((cell, column) => new OcrTextLine(cell, 96,
            new SourceRegion(0.1 + column * 0.28, 0.2 + rowIndex * 0.04, 0.15, 0.025)))).Reverse().ToArray();
        var result = await Extract(path, new FixedOcr(new OcrResult(string.Join('\n', lines.Select(line => line.Text)), 96, Lines: lines)));
        Assert.Empty(result.Errors);
        var table = Assert.Single(result.Root.Sections);
        Assert.Equal("Vendor\tAmount\tStatus\nAda\t42\tPaid\nGrace\t93\tOpen", table.Text);
        Assert.Equal(ExtractionMethod.Ocr, table.Method);
        Assert.Equal(1, table.Location.ImageFrame);
        Assert.Equal(96, table.OcrConfidence);
        Assert.StartsWith("table_inferred: OCR cells", table.Location.LayoutWarning);
        Assert.Equal(0.1, table.Location.Region!.X, precision: 8);
        Assert.Equal(0.2, table.Location.Region.Y, precision: 8);
        Assert.Equal(0.105, table.Location.Region.Height, precision: 8);
    }

    [Fact]
    public async Task DefectiveFontMappingUsesOcrOnlyInIllegibleRegion()
    {
        if (!DesktopRenderingSupported()) return;
        using var workspace = new Workspace();
        var path = workspace.File("defective-font.pdf");
        const string native = "Native reference evidence contains enough searchable words and characters to keep the correct contract information.";
        const string restored = "Restored supplier delivery and approval evidence from the lower page region.";
        await System.IO.File.WriteAllBytesAsync(path, DefectiveFontPdf(native, restored), TestContext.Current.CancellationToken);
        var engine = new FixedOcr(new OcrResult(native + "\n" + restored, 98,
            Lines: [new OcrTextLine(native, 99, new SourceRegion(0.067, 0.076, 0.7, 0.012)),
                new OcrTextLine(restored, 98, new SourceRegion(0.067, 0.491, 0.6, 0.012))]));
        var result = await Extract(path, engine);
        Assert.Empty(result.Errors);
        Assert.True(engine.Calls == 1, string.Join('\n', result.Root.Sections.Select(section => section.Text)));
        Assert.Contains(result.Root.Sections, section => section.Text == native && section.Method == ExtractionMethod.NativeText);
        var repaired = Assert.Single(result.Root.Sections, section => section.Text == restored);
        Assert.Equal(ExtractionMethod.Ocr, repaired.Method);
        Assert.DoesNotContain(result.Root.Sections, section => section.Text.Contains('\uFFFD'));
        Assert.True(repaired.Location.LayoutWarning?.Contains("native_text_replaced:", StringComparison.Ordinal) == true,
            string.Join('\n', result.Root.Sections.Select(section => $"{section.Method}: {section.Text} @ {section.Location.Region}")));
    }

    [Fact]
    public void TranscriptionMetricsCountMissingWordsAndAccentErrors()
    {
        Assert.Equal(0, ExtractionQuality.CharacterErrorRate("São Paulo\n contrato", "São Paulo contrato"));
        Assert.Equal(1d / 3, ExtractionQuality.CharacterErrorRate("São", "Sao"), precision: 8);
        Assert.Equal(1d / 3, ExtractionQuality.WordErrorRate("notice and delivery", "notice delivery"), precision: 8);
        Assert.Equal(1, ExtractionQuality.CharacterErrorRate("evidence", ""));
    }

    private static Task<ExtractionResult> Extract(string path, IOcrEngine? ocr = null) =>
        new DocumentExtractionRegistry(ocr ?? new UnexpectedOcr()).ExtractAsync(new ExtractionRequest(path),
            TestContext.Current.CancellationToken);

    private static byte[] DefectiveFontPdf(string native, string defective)
    {
        var commands = $"BT /F1 8 Tf 40 640 Td ({native}) Tj ET\nBT /F2 8 Tf 40 350 Td ({defective}) Tj ET";
        var cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def /CMapName /Defective def /CMapType 2 def 1 begincodespacerange <00> <FF> endcodespacerange 95 beginbfchar\n" +
            string.Join('\n', Enumerable.Range(32, 95).Select(value => $"<{value:X2}> <{(value == 32 ? "0020" : "FFFD")}>")) +
            "\nendbfchar endcmap CMapName currentdict /CMap defineresource pop end end";
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Count 1 /Kids [4 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 700] /Resources << /Font << /F1 3 0 R /F2 6 0 R >> >> /Contents 5 0 R >>",
            $"<< /Length {commands.Length} >>\nstream\n{commands}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /CustomDefective /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths [" +
                string.Join(' ', Enumerable.Repeat("600", 95)) + "] /FontDescriptor << /Type /FontDescriptor /FontName /CustomDefective /Flags 32 /FontBBox [0 -200 1000 900] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >> /ToUnicode 7 0 R >>",
            $"<< /Length {cmap.Length} >>\nstream\n{cmap}\nendstream" };
        var document = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++)
        { offsets.Add(document.Length); document.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        var xref = document.Length;
        document.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) document.Append($"{offset:D10} 00000 n \n");
        document.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(document.ToString());
    }

    private static bool DesktopRenderingSupported() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private sealed class UnexpectedOcr : IOcrEngine
    {
        public bool IsAvailable => false;
        public string? UnavailableReason => "This native fixture must not require OCR.";
        public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(UnavailableReason);
    }

    private sealed class FixedOcr(OcrResult result) : IOcrEngine
    {
        public int Calls { get; private set; }
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(result); }
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "ContextMole-StructureTests-" + Guid.NewGuid().ToString("N"));
        public Workspace() => Directory.CreateDirectory(_path);
        public string File(string name) => Path.Combine(_path, name);
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
