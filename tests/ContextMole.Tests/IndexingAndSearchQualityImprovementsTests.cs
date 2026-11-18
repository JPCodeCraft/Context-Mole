using System.IO.Compression;
using System.Text;
using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Indexing;
using ContextMole.Infrastructure;
using ContextMole.Search;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed partial class IndexingQualityImprovementsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ExtractionLayoutOrdersColumnsUnderTightFullWidthRunningHeaderAndAcrossParagraphGaps()
    {
        var items = new[]
        {
            new OcrTextLine("header", 99, new SourceRegion(0.08, 0.04, 0.84, 0.014)),
            new OcrTextLine("L1", 99, new SourceRegion(0.08, 0.09, 0.36, 0.015)),
            new OcrTextLine("R1", 99, new SourceRegion(0.54, 0.09, 0.36, 0.015)),
            new OcrTextLine("L2", 99, new SourceRegion(0.08, 0.115, 0.36, 0.015)),
            new OcrTextLine("R2", 99, new SourceRegion(0.54, 0.115, 0.36, 0.015)),
            // Aligned paragraph gap across both columns:
            new OcrTextLine("L3", 99, new SourceRegion(0.08, 0.165, 0.36, 0.015)),
            new OcrTextLine("R3", 99, new SourceRegion(0.54, 0.165, 0.36, 0.015)),
            new OcrTextLine("L4", 99, new SourceRegion(0.08, 0.190, 0.36, 0.015)),
            new OcrTextLine("R4", 99, new SourceRegion(0.54, 0.190, 0.36, 0.015)),
            new OcrTextLine("footer", 99, new SourceRegion(0.08, 0.93, 0.84, 0.014))
        };

        var ordered = ExtractionLayout.OrderLines(items).Select(item => item.Text).ToArray();
        Assert.Equal(["header", "L1", "L2", "L3", "L4", "R1", "R2", "R3", "R4", "footer"], ordered);
    }

    [Fact]
    public void ExtractionLayoutSeparatesDenseTwoColumnPageWithNarrowGutter()
    {
        var lines = new List<OcrTextLine>
        {
            new("713   tender", 99, new SourceRegion(0.08, 0.03, 0.82, 0.014))
        };
        for (var i = 0; i < 6; i++)
        {
            var y = 0.08 + i * 0.025;
            // Center gutter is 0.500 - 0.484 = 0.016 (< 0.025).
            lines.Add(new($"R{i + 1}", 99, new SourceRegion(0.500, y, 0.40, 0.016)));
            lines.Add(new($"L{i + 1}", 99, new SourceRegion(0.084, y, 0.40, 0.016)));
        }

        var ordered = ExtractionLayout.OrderLines(lines).Select(item => item.Text).ToArray();
        Assert.Equal(["713   tender", "L1", "L2", "L3", "L4", "L5", "L6", "R1", "R2", "R3", "R4", "R5", "R6"], ordered);
    }

    [Fact]
    public async Task CorruptedSandwichPdfRegionIsReplacedByHighConfidenceOcrWhileValidNativeSurvives()
    {
        using var workspace = new Workspace();
        var path = workspace.File("sandwich-corrupted.pdf");
        const string validHeader = "Sahara Dust Sample #7 bacterial identification report with clean native typography.";
        const string garbledRow = "Cot)'I\"JCCll(teflum nucii«<CM 0Qio5)58) t.'oc:lc _£lin 0()1,53572 O(J/.5)577";
        const string cleanOcrRow = "Corynebacterium mucifaciens DQ453573 DQ453583 Propionibacterium acnes DQ453572";

        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 700);
        page.AddText(validHeader, 10, new PdfPoint(40, 640), font);
        page.AddText(garbledRow, 10, new PdfPoint(40, 420), font);
        using (var bitmap = new SKBitmap(400, 200))
        {
            bitmap.Erase(SKColors.White);
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            page.AddPng(png.ToArray(), new PdfRectangle(40, 80, 520, 260));
        }
        await File.WriteAllBytesAsync(path, builder.Build(), Token);

        var ocr = new StaticOcrEngine(new OcrResult(validHeader + "\n" + cleanOcrRow, 95,
            Lines:
            [
                new OcrTextLine(validHeader, 98, new SourceRegion(0.06, 0.06, 0.85, 0.03)),
                new OcrTextLine(cleanOcrRow, 95, new SourceRegion(0.06, 0.38, 0.85, 0.04))
            ]));
        var extractor = new DocumentExtractionRegistry(ocr);
        var result = await extractor.ExtractAsync(new ExtractionRequest(path), Token);

        Assert.Empty(result.Errors);
        Assert.Contains(result.Root.Sections, s => s.Method == ExtractionMethod.NativeText && s.Text == validHeader);
        var replaced = Assert.Single(result.Root.Sections, s => s.Text.Contains("Corynebacterium mucifaciens", StringComparison.Ordinal));
        Assert.Equal(ExtractionMethod.Ocr, replaced.Method);
        Assert.Contains("native_text_replaced:", replaced.Location.LayoutWarning ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Root.Sections, s => s.Text.Contains("Cot)'I\"JCCll(teflum", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImageOcrKeepsSpacedCompleteProseParagraphsDistinctWhileMergingShortCardPairs()
    {
        using var workspace = new Workspace();
        var prosePath = workspace.File("scan-prose.png");
        await WriteBlankPngAsync(prosePath);

        var proseLines = new[]
        {
            new OcrTextLine("O contrato principal estabelece prazos rigorosos para entrega regional.", 99,
                new SourceRegion(0.08, 0.10, 0.78, 0.024)),
            new OcrTextLine("Cada fornecedor deve confirmar conformidade técnica antes do embarque.", 99,
                new SourceRegion(0.08, 0.155, 0.78, 0.024)),
            new OcrTextLine("Relatórios mensais permanecem arquivados para auditoria independente.", 99,
                new SourceRegion(0.08, 0.210, 0.78, 0.024))
        };
        var proseExtractor = new DocumentExtractionRegistry(new StaticOcrEngine(
            new OcrResult(string.Join('\n', proseLines.Select(l => l.Text)), 99, Lines: proseLines)));
        var proseResult = await proseExtractor.ExtractAsync(new ExtractionRequest(prosePath), Token);
        Assert.Equal(3, proseResult.Root.Sections.Count);

        var cardPath = workspace.File("dispatch-card.png");
        await WriteBlankPngAsync(cardPath);
        var cardLines = new[]
        {
            new OcrTextLine("Dispatch Code:", 99, new SourceRegion(0.10, 0.20, 0.28, 0.030)),
            new OcrTextLine("ZK-8841", 99, new SourceRegion(0.10, 0.268, 0.22, 0.030))
        };
        var cardExtractor = new DocumentExtractionRegistry(new StaticOcrEngine(
            new OcrResult("Dispatch Code:\nZK-8841", 99, Lines: cardLines)));
        var cardResult = await cardExtractor.ExtractAsync(new ExtractionRequest(cardPath), Token);
        var single = Assert.Single(cardResult.Root.Sections);
        Assert.Contains("Dispatch Code:", single.Text, StringComparison.Ordinal);
        Assert.Contains("ZK-8841", single.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocxExtractsSdtContentBlocksAndMergedTableSpanWarnings()
    {
        using var workspace = new Workspace();
        var path = workspace.File("governance.docx");
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new W.Body();
            body.Append(new W.SdtBlock(new W.SdtProperties(),
                new W.SdtContentBlock(new W.Paragraph(new W.Run(
                    new W.Text("Structured control KMS-ROTATE-90D applies to all production keys."))))));

            var table = new W.Table();
            table.Append(new W.TableRow(
                new W.TableCell(new W.TableCellProperties(new W.GridSpan { Val = 2 }),
                    new W.Paragraph(new W.Run(new W.Text("Merged Header"))))));
            table.Append(new W.TableRow(
                new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Policy")))),
                new W.TableCell(new W.Paragraph(new W.Run(new W.Text("KMS-ROTATE-90D"))))));
            body.Append(table);
            main.Document = new W.Document(body);
            main.Document.Save();
        }

        var extractor = new DocumentExtractionRegistry(new NoCallOcrEngine());
        var result = await extractor.ExtractAsync(new ExtractionRequest(path), Token);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Root.Sections, s => s.Text.Contains("Structured control KMS-ROTATE-90D", StringComparison.Ordinal));
        var tableSection = Assert.Single(result.Root.Sections, s => s.Location.StructurePath == "document/table[1]");
        Assert.Contains("table_spans", tableSection.Location.LayoutWarning ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PptxExtractsSlideTitleHeadingAndStructuredGraphicFrameTables()
    {
        using var workspace = new Workspace();
        var path = workspace.File("recovery.pptx");
        using (var pres = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
        {
            var presPart = pres.AddPresentationPart();
            var slidePart = presPart.AddNewPart<SlidePart>("rId1");
            var titleShape = new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 2U, Name = "Title 1" },
                    new P.NonVisualShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties(new PlaceholderShape { Type = PlaceholderValues.Title })),
                new P.ShapeProperties(),
                new P.TextBody(new A.BodyProperties(), new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text("Disaster Recovery Matrix")))));

            var bodyShape = new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 3U, Name = "Content 2" },
                    new P.NonVisualShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new P.ShapeProperties(),
                new P.TextBody(new A.BodyProperties(), new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text("Quarterly failover drill verified.")))));

            var table = new A.Table(
                new A.TableProperties(),
                new A.TableGrid(new A.GridColumn { Width = 2000000L }, new A.GridColumn { Width = 2000000L }),
                PptxRow("Service", "RTO"),
                PptxRow("Ledger Core", "5 min"));

            var frame = new P.GraphicFrame(
                new P.NonVisualGraphicFrameProperties(
                    new P.NonVisualDrawingProperties { Id = 4U, Name = "Table 3" },
                    new P.NonVisualGraphicFrameDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new P.Transform(),
                new A.Graphic(new A.GraphicData(table) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" }));

            slidePart.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties(),
                titleShape, bodyShape, frame)));
            slidePart.Slide.Save();
            presPart.Presentation = new P.Presentation(
                new P.SlideIdList(new P.SlideId { Id = 256U, RelationshipId = "rId1" }));
            presPart.Presentation.Save();
        }

        var extractor = new DocumentExtractionRegistry(new NoCallOcrEngine());
        var result = await extractor.ExtractAsync(new ExtractionRequest(path), Token);
        Assert.Empty(result.Errors);
        var slideText = Assert.Single(result.Root.Sections, s => s.Location.Kind == LocationKind.Slide && s.Location.StructurePath is null);
        Assert.Equal("Disaster Recovery Matrix", slideText.Heading);
        Assert.Contains("Quarterly failover drill verified.", slideText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Ledger Core", slideText.Text, StringComparison.Ordinal);

        var slideTable = Assert.Single(result.Root.Sections, s => s.Location.StructurePath == "slide[1]/table[1]");
        Assert.Equal("Disaster Recovery Matrix", slideTable.Heading);
        Assert.Equal("Service\tRTO\nLedger Core\t5 min", slideTable.Text);

        static A.TableRow PptxRow(params string[] cells)
        {
            var r = new A.TableRow { Height = 300000L };
            foreach (var c in cells)
                r.Append(new A.TableCell(
                    new A.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text(c)))),
                    new A.TableCellProperties()));
            return r;
        }
    }

    [Fact]
    public async Task OpenDocumentTextAndPresentationExtractStructuredTablesWithoutDuplicatingCells()
    {
        using var workspace = new Workspace();
        var odtPath = workspace.File("audit.odt");
        WriteOpenDocumentZip(odtPath, "application/vnd.oasis.opendocument.text",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                                     xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
                                     xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0">
              <office:body>
                <office:text>
                  <text:h text:outline-level="1">Audit Controls</text:h>
                  <text:p>Introductory scope note.</text:p>
                  <table:table table:name="Findings">
                    <table:table-row>
                      <table:table-cell table:number-columns-spanned="2"><text:p>Control Banner</text:p></table:table-cell>
                    </table:table-row>
                    <table:table-row>
                      <table:table-cell><text:p>IAM-04</text:p></table:table-cell>
                      <table:table-cell><text:p>Platform Security</text:p></table:table-cell>
                    </table:table-row>
                  </table:table>
                </office:text>
              </office:body>
            </office:document-content>
            """);

        var odpPath = workspace.File("capacity.odp");
        WriteOpenDocumentZip(odpPath, "application/vnd.oasis.opendocument.presentation",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                                     xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0"
                                     xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
                                     xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0">
              <office:body>
                <office:presentation>
                  <draw:page draw:name="Capacity">
                    <draw:frame><draw:text-box><text:p>Regional Forecast</text:p></draw:text-box></draw:frame>
                    <draw:frame>
                      <table:table>
                        <table:table-row>
                          <table:table-cell><text:p>Region</text:p></table:table-cell>
                          <table:table-cell><text:p>Headroom</text:p></table:table-cell>
                        </table:table-row>
                        <table:table-row>
                          <table:table-cell><text:p>eu-north-1</text:p></table:table-cell>
                          <table:table-cell><text:p>38%</text:p></table:table-cell>
                        </table:table-row>
                      </table:table>
                    </draw:frame>
                  </draw:page>
                </office:presentation>
              </office:body>
            </office:document-content>
            """);

        var extractor = new DocumentExtractionRegistry(new NoCallOcrEngine());
        var odt = await extractor.ExtractAsync(new ExtractionRequest(odtPath), Token);
        Assert.Empty(odt.Errors);
        Assert.Equal(1, odt.Root.Sections.Count(s => s.Text.Contains("IAM-04", StringComparison.Ordinal)));
        var odtTable = Assert.Single(odt.Root.Sections, s => s.Location.StructurePath == "document/table[1]");
        Assert.Equal("Audit Controls", odtTable.Heading);
        Assert.Contains("IAM-04\tPlatform Security", odtTable.Text, StringComparison.Ordinal);
        Assert.Contains("table_spans", odtTable.Location.LayoutWarning ?? string.Empty, StringComparison.Ordinal);

        var odp = await extractor.ExtractAsync(new ExtractionRequest(odpPath), Token);
        Assert.Empty(odp.Errors);
        var odpTable = Assert.Single(odp.Root.Sections, s => s.Location.StructurePath == "slide[1]/table[1]");
        Assert.Equal("Region\tHeadroom\neu-north-1\t38%", odpTable.Text);
    }

    [Fact]
    public async Task SpreadsheetBannerRowAndMultiTableSubheadersPairWithDataRowsAndEmailHeadersEmbed()
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var xlsxPath = Path.Combine(database.Paths.SourceDirectory, "capex.xlsx");
        using (var wbDoc = SpreadsheetDocument.Create(xlsxPath, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = wbDoc.AddWorkbookPart();
            var wsPart = wbPart.AddNewPart<WorksheetPart>("rId1");
            wsPart.Worksheet = new S.Worksheet(new S.SheetData(
                SheetRow(1, ("A1", "FY2026 Consolidated Capex Report")),
                SheetRow(2, ("A2", "Project"), ("B2", "Owner"), ("C2", "BudgetUSD")),
                SheetRow(3, ("A3", "Subsea Fiber"), ("B3", "Henrik Holm"), ("C3", "920000")),
                SheetRow(5, ("A5", "Vendor"), ("B5", "RenewalDate")),
                SheetRow(6, ("A6", "Nordic Cable AS"), ("B6", "2027-03-31"))
            ));
            wsPart.Worksheet.Save();
            wbPart.Workbook = new S.Workbook(new S.Sheets(
                new S.Sheet { Id = "rId1", SheetId = 1U, Name = "Summary" }));
            wbPart.Workbook.Save();
        }

        var emlPath = Path.Combine(database.Paths.SourceDirectory, "alert.eml");
        await File.WriteAllTextAsync(emlPath,
            "From: noc-oncall@gridops.example\r\n" +
            "To: clara.lindqvist@gridops.example\r\n" +
            "Subject: Telemetry alert\r\n" +
            "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n" +
            "Investigating packet loss on link B7.", Token);

        var (project, folder) = await database.CreateProjectAsync("Banner and email headers", Token);
        foreach (var filePath in new[] { xlsxPath, emlPath })
        {
            var info = new FileInfo(filePath);
            await database.Writer.ObserveFileAsync(new FileObservation(project, folder, filePath, info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)), Token);
        }

        var embeddings = new CapturingEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new DocumentExtractionRegistry(new StorageNoOcr()), embeddings, new IndexingActivityTracker(),
            new EmbeddingPolicyRefreshTracker(), budget, NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(Token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while ((await database.Store.ListProjectsAsync(timeout.Token)).Single(item => item.Id == project)
                   is not { DocumentCount: 2, PendingCount: 0 })
                await Task.Delay(20, timeout.Token);

            Assert.Contains(embeddings.Inputs, input => input.Contains("clara.lindqvist@gridops.example", StringComparison.Ordinal));

            await using var conn = new SqliteConnection($"Data Source={database.Paths.DatabasePath};Mode=ReadOnly");
            await conn.OpenAsync(Token);
            var row3 = await ReadSearchTextAsync(conn, "A3:C3");
            Assert.Contains("Project: Subsea Fiber", row3, StringComparison.Ordinal);
            Assert.Contains("BudgetUSD: 920000", row3, StringComparison.Ordinal);
            Assert.Contains("FY2026 Consolidated Capex Report", row3, StringComparison.Ordinal);

            var row6 = await ReadSearchTextAsync(conn, "A6:B6");
            Assert.Contains("Vendor: Nordic Cable AS", row6, StringComparison.Ordinal);
            Assert.Contains("RenewalDate: 2027-03-31", row6, StringComparison.Ordinal);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }

        static async Task<string> ReadSearchTextAsync(SqliteConnection conn, string cellRange)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT search_text FROM passages WHERE cell_range=$cr LIMIT 1";
            cmd.Parameters.AddWithValue("$cr", cellRange);
            return (await cmd.ExecuteScalarAsync())?.ToString() ?? string.Empty;
        }

        static S.Row SheetRow(uint rowIndex, params (string Ref, string Val)[] cells)
        {
            var r = new S.Row { RowIndex = rowIndex };
            foreach (var (cellRef, val) in cells)
                r.Append(new S.Cell
                {
                    CellReference = cellRef,
                    DataType = S.CellValues.InlineString,
                    InlineString = new S.InlineString(new S.Text(val))
                });
            return r;
        }
    }

    private static async Task WriteBlankPngAsync(string path)
    {
        using var bmp = new SKBitmap(400, 500);
        bmp.Erase(SKColors.White);
        using var encoded = bmp.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, encoded.ToArray(), Token);
    }

    private static void WriteOpenDocumentZip(string path, string mimeType, string contentXml)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var s = mime.Open()) s.Write(Encoding.ASCII.GetBytes(mimeType));
        var content = zip.CreateEntry("content.xml", CompressionLevel.Optimal);
        using (var s = content.Open()) s.Write(Encoding.UTF8.GetBytes(contentXml));
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ContextMole.QualityTests", Guid.NewGuid().ToString("N"));
        public Workspace() => Directory.CreateDirectory(_root);
        public string File(string name) => Path.Combine(_root, name);
        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class StaticOcrEngine(OcrResult result) : IOcrEngine
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class NoCallOcrEngine : IOcrEngine
    {
        public bool IsAvailable => false;
        public string? UnavailableReason => "OCR should not be invoked for native Office/ODF tests.";
        public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(UnavailableReason);
    }

    private sealed class StorageFixedCpuSettings : ICpuUsageSettings
    {
        public CpuUsageProfile Profile => CpuUsageProfile.Normal;
        public int LogicalProcessorCount => Environment.ProcessorCount;
        public int ThreadLimit => 2;
        public int MaximumThreadLimit => 2;
        public event EventHandler? Changed { add { } remove { } }
        public void SetProfile(CpuUsageProfile profile) { }
    }

    private sealed class StorageNoOcr : IOcrEngine
    {
        public bool IsAvailable => false;
        public string? UnavailableReason => "Disabled";
        public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult(string.Empty, null));
    }

    private sealed class CapturingEmbeddings : IEmbeddingGenerator
    {
        private static readonly EmbeddingPolicy ActivePolicy =
            new("granite-test", "1", "sha", "tok", "fp32", 384, 384, "cls", "l2");
        public List<string> Inputs { get; } = [];
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public EmbeddingPolicy? Policy => ActivePolicy;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public int CountTokens(string text) => text.Length;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken)
        {
            lock (Inputs) Inputs.AddRange(passages);
            var vectors = passages.Select(_ =>
            {
                var v = new float[384];
                v[0] = 1f;
                return v;
            }).ToArray();
            return Task.FromResult(new EmbeddingBatch(vectors, ActivePolicy));
        }
        public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken)
        {
            var v = new float[384];
            v[0] = 1f;
            return Task.FromResult(new QueryEmbedding(v, ActivePolicy));
        }
    }
}

public sealed partial class HybridSearchTests
{
    [Fact]
    public async Task KeywordShouldCoordinationRanksBroadClauseCoverageAheadOfSingleTermSpam()
    {
        var spam = Candidate(Guid.NewGuid(), "bgp flap bgp flap bgp flap bgp flap") with { KeywordScore = -12.0 };
        var coordinated = Candidate(Guid.NewGuid(),
            "Enabled bgp route flap dampening with suppress threshold and deterministic holdtime.") with { KeywordScore = -8.0 };
        var candidates = new[] { spam, coordinated };
        var store = new SearchStoreFake(candidates, new VectorSnapshot(40, null, []), candidates);
        var clauses = new[] { "bgp", "flap", "dampening", "suppress", "threshold", "holdtime" }
            .Select((term, i) => new SearchClause($"s{i}", term, SearchClauseOccur.Should)).ToArray();

        var response = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)))
            .SearchAsync(new SearchRequest(Guid.NewGuid(), SearchMode.Keyword, Clauses: clauses, MinimumShouldMatch: 1),
                TestContext.Current.CancellationToken);

        Assert.Equal(coordinated.DocumentId, response.Results[0].DocumentId);
    }

    [Fact]
    public async Task KeywordMultiClauseProximityPrefersTightlyAdjacentMatches()
    {
        var scattered = Candidate(Guid.NewGuid(),
            "The canary environment logged metrics across forty nodes while operators reviewed 45 archival items and 15 seconds polling before manual rollback.")
            with { KeywordScore = -6.0 };
        var adjacent = Candidate(Guid.NewGuid(),
            "Automated canary rollback triggers within 45 seconds when gateway error budgets burn.")
            with { KeywordScore = -5.8 };
        var candidates = new[] { scattered, adjacent };
        var store = new SearchStoreFake(candidates, new VectorSnapshot(41, null, []), candidates);
        var clauses = new[] { "canary", "rollback", "45", "seconds" }
            .Select((term, i) => new SearchClause($"m{i}", term, SearchClauseOccur.Must)).ToArray();

        var response = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)))
            .SearchAsync(new SearchRequest(Guid.NewGuid(), SearchMode.Keyword, Clauses: clauses),
                TestContext.Current.CancellationToken);

        Assert.Equal(adjacent.DocumentId, response.Results[0].DocumentId);
    }

    [Fact]
    public async Task PreviewSelectionDefersBareHeadingRow1HeaderAndEmailHeadersBehindSubstantiveEvidence()
    {
        var docId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var bareHeading = Candidate(Guid.NewGuid(), "Enterprise Burst Policy") with
        {
            DocumentId = docId,
            ContentId = contentId,
            Ordinal = 0,
            Heading = "Enterprise Burst Policy",
            KeywordScore = -9.0
        };
        var bodyPassage = bareHeading with
        {
            PassageId = Guid.NewGuid(),
            Ordinal = 1,
            DisplayText = "Enterprise Burst Policy allows 2,500 requests per second across active regions.",
            KeywordScore = -7.5
        };
        var candidates = new[] { bareHeading, bodyPassage };
        var store = new SearchStoreFake(candidates, new VectorSnapshot(42, null, []), candidates);
        var clauses = new[] { "enterprise", "burst", "policy" }
            .Select((term, i) => new SearchClause($"c{i}", term, SearchClauseOccur.Must)).ToArray();

        var response = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)))
            .SearchAsync(new SearchRequest(Guid.NewGuid(), SearchMode.Keyword, Clauses: clauses,
                ResultOptions: new SearchResultOptions(PreviewsPerGroup: 1)), TestContext.Current.CancellationToken);

        var preview = Assert.Single(Assert.Single(response.Results).Previews);
        Assert.Equal(bodyPassage.PassageId, preview.PassageId);
        Assert.Contains("2,500", preview.Excerpt, StringComparison.Ordinal);

        var emailDocId = Guid.NewGuid();
        var emailContentId = Guid.NewGuid();
        var emailHeaders = Candidate(Guid.NewGuid(),
            "From: ops@example.com\nTo: clara@example.com\nSubject: Substation B7 telemetry") with
        {
            DocumentId = emailDocId,
            ContentId = emailContentId,
            Ordinal = 0,
            SectionId = Guid.NewGuid(),
            Location = new SourceLocation(LocationKind.EmailPart, EmailPart: "headers")
        };
        var emailBody = emailHeaders with
        {
            PassageId = Guid.NewGuid(),
            Ordinal = 1,
            SectionId = Guid.NewGuid(),
            DisplayText = "Substation B7 telemetry shows fiber attenuation stabilized after splice repair.",
            Location = new SourceLocation(LocationKind.EmailPart, EmailPart: "body")
        };
        var emailCandidates = new[] { emailHeaders, emailBody };
        var emailStore = new SearchStoreFake([], new VectorSnapshot(46, Policy,
            [VectorEntry(emailHeaders, 0.72f), VectorEntry(emailBody, 0.70f)]), emailCandidates);
        var emailResponse = await CreateSearch(emailStore, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)))
            .SearchAsync(new SearchRequest(Guid.NewGuid(), SearchMode.Semantic, "Substation B7 telemetry status",
                ResultOptions: new SearchResultOptions(PreviewsPerGroup: 1)), TestContext.Current.CancellationToken);

        var emailPreview = Assert.Single(Assert.Single(emailResponse.Results).Previews);
        Assert.Equal(emailBody.PassageId, emailPreview.PassageId);

        var senderResponse = await CreateSearch(emailStore, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)))
            .SearchAsync(new SearchRequest(Guid.NewGuid(), SearchMode.Semantic, "Who sent the message to clara?",
                ResultOptions: new SearchResultOptions(PreviewsPerGroup: 1)), TestContext.Current.CancellationToken);
        var senderPreview = Assert.Single(Assert.Single(senderResponse.Results).Previews);
        Assert.Equal(emailHeaders.PassageId, senderPreview.PassageId);
    }

    [Fact]
    public async Task MultiPreviewSelectionSkipsOverlappingChunkFromSameSection()
    {
        var docId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var chunk0 = Candidate(Guid.NewGuid(), new string('a', 600)) with
        {
            DocumentId = docId,
            ContentId = contentId,
            Ordinal = 0,
            SectionId = sectionId,
            SectionOffset = 0
        };
        var overlappingChunk1 = chunk0 with
        {
            PassageId = Guid.NewGuid(),
            Ordinal = 1,
            DisplayText = new string('a', 600),
            SectionOffset = 450
        };
        var distinctChunk2 = chunk0 with
        {
            PassageId = Guid.NewGuid(),
            Ordinal = 2,
            DisplayText = "Distinct follow-up quorum verification evidence.",
            SectionOffset = 1200
        };
        var candidates = new[] { chunk0, overlappingChunk1, distinctChunk2 };
        var store = new SearchStoreFake([], new VectorSnapshot(43, Policy,
            [VectorEntry(chunk0, 0.95f), VectorEntry(overlappingChunk1, 0.94f), VectorEntry(distinctChunk2, 0.89f)]), candidates);

        var response = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)))
            .SearchAsync(new SearchRequest(Guid.NewGuid(), SearchMode.Semantic, "quorum verification",
                ResultOptions: new SearchResultOptions(PreviewsPerGroup: 2)), TestContext.Current.CancellationToken);

        var previews = Assert.Single(response.Results).Previews;
        Assert.Equal([chunk0.PassageId, distinctChunk2.PassageId], previews.Select(p => p.PassageId));
    }

    [Fact]
    public async Task SectionScopeSemanticRankingRewardsMultiPassageCorroborationWhileKeepingRawSemanticScore()
    {
        var passingMention = Candidate(Guid.NewGuid(), "Passing mention of logical replication cutover.") with
        {
            SectionId = Guid.NewGuid(),
            SectionOffset = 0,
            SectionText = "Passing mention of logical replication cutover.",
            Heading = "Retrospective"
        };

        var deepDocId = Guid.NewGuid();
        var deepContentId = Guid.NewGuid();
        var deepSectionId = Guid.NewGuid();
        var fullSectionText = new string('p', 1500);
        var deep1 = Candidate(Guid.NewGuid(), new string('p', 400)) with
        {
            DocumentId = deepDocId,
            ContentId = deepContentId,
            SectionId = deepSectionId,
            SectionOffset = 0,
            SectionText = fullSectionText,
            Heading = "Logical Replication Cutover"
        };
        var deep2 = deep1 with { PassageId = Guid.NewGuid(), Ordinal = 1, SectionOffset = 500 };
        var deep3 = deep1 with { PassageId = Guid.NewGuid(), Ordinal = 2, SectionOffset = 1000 };

        var candidates = new[] { passingMention, deep1, deep2, deep3 };
        var store = new SearchStoreFake([], new VectorSnapshot(44, Policy,
            [VectorEntry(passingMention, 0.86f), VectorEntry(deep1, 0.85f), VectorEntry(deep2, 0.84f), VectorEntry(deep3, 0.83f)]),
            candidates);

        var response = await CreateSearch(store, new EmbeddingGeneratorFake(Policy, Policy, Vector(1)))
            .SearchAsync(new SearchRequest(Guid.NewGuid(), SearchMode.Semantic, "logical replication cutover",
                Scope: SearchScope.Section), TestContext.Current.CancellationToken);

        Assert.Equal(deepDocId, response.Results[0].DocumentId);
        Assert.Equal(0.85, response.Results[0].SemanticSimilarity!.Value, precision: 5);
    }

    [Fact]
    public async Task AddContextMoleSearchEnablesSalientSemanticPreviewAnchoringByDefault()
    {
        var text = string.Concat(Enumerable.Repeat("Routine checksum validation runs hourly. ", 30)) +
            "Under ISO27001 controls, production access reviews occur quarterly with hardware attestation.";
        var candidate = Candidate(Guid.NewGuid(), text);
        var store = new SearchStoreFake([], new VectorSnapshot(45, Policy, [VectorEntry(candidate, 0.95f)]), [candidate]);

        var services = new ServiceCollection();
        services.AddSingleton<ISearchStore>(store);
        services.AddSingleton<IEmbeddingGenerator>(new EmbeddingGeneratorFake(Policy, Policy, Vector(1)));
        services.AddSingleton<IGlobalCpuBudget>(new ImmediateCpuBudget());
        services.AddContextMoleSearch();
        await using var provider = services.BuildServiceProvider();
        var search = provider.GetRequiredService<HybridSearchService>();

        var response = await search.SearchAsync(
            new SearchRequest(Guid.NewGuid(), SearchMode.Semantic, "How often do ISO27001 production access reviews occur?"),
            TestContext.Current.CancellationToken);

        var preview = Assert.Single(Assert.Single(response.Results).Previews);
        Assert.Contains("ISO27001", preview.Excerpt, StringComparison.Ordinal);
        Assert.Contains("quarterly", preview.Excerpt, StringComparison.Ordinal);
        Assert.Equal(text.Substring(preview.ExcerptStart, preview.ExcerptLength), preview.Excerpt);
    }
}
