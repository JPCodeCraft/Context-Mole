using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Indexing;
using ContextMole.Infrastructure;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace ContextMole.Tests;

// Regressions found in review of the layout-v7 extraction and table changes.
public sealed partial class IndexingQualityImprovementsTests
{
    [Fact]
    public async Task CleanCodeListingPdfPageKeepsNativeTextWithoutOcr()
    {
        using var workspace = new Workspace();
        var path = workspace.File("code-listing.pdf");
        string[] lines =
        [
            "The following listing shows how the indexer prepares sections before embedding them.",
            "var sections = IndexingCoordinator.PrepareSections(document);",
            "std::map<std::string, int> counts; counts[key]++;",
            "if (ptr->next != nullptr) ptr = ptr->next;",
            "return isValid ? getUserName() : getDefaultUserNameForTenant();",
            "Senior C#/.NET developers maintain the TypeScript and PostgreSQL tooling.",
            "* * *",
        ];
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 700);
        for (var i = 0; i < lines.Length; i++)
            page.AddText(lines[i], 10, new PdfPoint(40, 640 - i * (i < 6 ? 14 : 60)), font);
        await File.WriteAllBytesAsync(path, builder.Build(), Token);

        var ocr = new CountingOcrEngine();
        var result = await new DocumentExtractionRegistry(ocr).ExtractAsync(new ExtractionRequest(path), Token);

        Assert.Empty(result.Errors);
        Assert.Equal(0, ocr.Calls);
        var text = string.Join('\n', result.Root.Sections.Select(section => section.Text));
        Assert.All(result.Root.Sections, section => Assert.Equal(ExtractionMethod.NativeText, section.Method));
        Assert.Contains("std::map<std::string, int> counts;", text, StringComparison.Ordinal);
        Assert.Contains("getDefaultUserNameForTenant()", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenDocumentTableKeepsRowsInsideRowWrappersAndIgnoresDefaultSlideNames()
    {
        using var workspace = new Workspace();
        var odtPath = workspace.File("wrapped-rows.odt");
        WriteOpenDocumentZip(odtPath, "application/vnd.oasis.opendocument.text",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                                     xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
                                     xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0">
              <office:body>
                <office:text>
                  <table:table table:name="Owners">
                    <table:table-header-rows>
                      <table:table-row>
                        <table:table-cell><text:p>Region</text:p></table:table-cell>
                        <table:table-cell><text:p>Owner</text:p></table:table-cell>
                      </table:table-row>
                    </table:table-header-rows>
                    <table:table-rows>
                      <table:table-row>
                        <table:table-cell><text:p>eu-north-1</text:p></table:table-cell>
                        <table:table-cell><text:p>Alice</text:p></table:table-cell>
                      </table:table-row>
                    </table:table-rows>
                    <table:table-row-group>
                      <table:table-row-group>
                        <table:table-row>
                          <table:table-cell><text:p>us-east-2</text:p></table:table-cell>
                          <table:table-cell><text:p>Bob</text:p></table:table-cell>
                        </table:table-row>
                      </table:table-row-group>
                    </table:table-row-group>
                  </table:table>
                </office:text>
              </office:body>
            </office:document-content>
            """);

        var odpPath = workspace.File("unnamed-slides.odp");
        WriteOpenDocumentZip(odpPath, "application/vnd.oasis.opendocument.presentation",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                                     xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0"
                                     xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">
              <office:body>
                <office:presentation>
                  <draw:page draw:name="page1">
                    <draw:frame><draw:text-box><text:p>Quarterly results</text:p></draw:text-box></draw:frame>
                  </draw:page>
                </office:presentation>
              </office:body>
            </office:document-content>
            """);

        var extractor = new DocumentExtractionRegistry(new NoCallOcrEngine());
        var odt = await extractor.ExtractAsync(new ExtractionRequest(odtPath), Token);
        Assert.Empty(odt.Errors);
        var table = Assert.Single(odt.Root.Sections, s => s.Location.StructurePath == "document/table[1]");
        Assert.Equal("Region\tOwner\neu-north-1\tAlice\nus-east-2\tBob", table.Text);

        var odp = await extractor.ExtractAsync(new ExtractionRequest(odpPath), Token);
        Assert.Empty(odp.Errors);
        var slide = Assert.Single(odp.Root.Sections);
        Assert.Equal("Quarterly results", slide.Text);
        Assert.Null(slide.Heading);
        Assert.Empty(slide.HeadingPath ?? []);
    }

    [Fact]
    public async Task TextOnlyDataRowAfterBlankRowKeepsTheSheetHeader()
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var xlsxPath = Path.Combine(database.Paths.SourceDirectory, "people.xlsx");
        using (var workbook = SpreadsheetDocument.Create(xlsxPath, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = workbook.AddWorkbookPart();
            var sheetPart = workbookPart.AddNewPart<WorksheetPart>("rId1");
            sheetPart.Worksheet = new S.Worksheet(new S.SheetData(
                SheetRow(1, ("A1", "Name"), ("B1", "City"), ("C1", "Country")),
                SheetRow(2, ("A2", "Alice"), ("B2", "Paris"), ("C2", "France")),
                SheetRow(3, ("A3", "Ana"), ("B3", "Lisbon"), ("C3", "Portugal")),
                SheetRow(5, ("A5", "Bob"), ("B5", "London"), ("C5", "UK")),
                SheetRow(6, ("A6", "Carol"), ("B6", "Berlin"), ("C6", "Germany")),
                SheetRow(8, ("A8", "Dave")),
                SheetRow(9, ("A9", "Eve"), ("B9", "Rome"), ("C9", "Italy")),
                SheetRow(10, ("A10", "Frank"), ("B10", "Oslo"), ("C10", "Norway")),
                SheetRow(12, ("A12", "Grace"), ("B12", "Madrid")),
                SheetRow(13, ("A13", "Heidi"), ("B13", "Vienna"), ("C13", "Austria"))));
            sheetPart.Worksheet.Save();
            workbookPart.Workbook = new S.Workbook(new S.Sheets(
                new S.Sheet { Id = "rId1", SheetId = 1U, Name = "People" }));
            workbookPart.Workbook.Save();
        }

        var (project, folder) = await database.CreateProjectAsync("Text-only data rows", Token);
        var info = new FileInfo(xlsxPath);
        await database.Writer.ObserveFileAsync(new FileObservation(project, folder, xlsxPath, info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)), Token);

        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new DocumentExtractionRegistry(new StorageNoOcr()), new CapturingEmbeddings(), new IndexingActivityTracker(),
            new EmbeddingPolicyRefreshTracker(), budget, NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(Token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while ((await database.Store.ListProjectsAsync(timeout.Token)).Single(item => item.Id == project)
                   is not { DocumentCount: 1, PendingCount: 0 })
                await Task.Delay(20, timeout.Token);

            await using var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath};Mode=ReadOnly");
            await connection.OpenAsync(Token);
            foreach (var (range, expected) in new[]
                     {
                         ("A5:C5", "Name: Bob"), ("A6:C6", "Name: Carol"), ("A9:C9", "Name: Eve"),
                         ("A10:C10", "Name: Frank"), ("A12:B12", "Name: Grace"), ("A13:C13", "Name: Heidi")
                     })
            {
                var searchText = await SearchTextAsync(connection, range);
                Assert.Contains(expected, searchText, StringComparison.Ordinal);
                Assert.DoesNotContain("Bob: ", searchText, StringComparison.Ordinal);
                Assert.DoesNotContain("Eve: ", searchText, StringComparison.Ordinal);
                Assert.DoesNotContain("Grace: ", searchText, StringComparison.Ordinal);
                Assert.DoesNotContain("Dave", searchText, StringComparison.Ordinal);
            }
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }

        static async Task<string> SearchTextAsync(SqliteConnection connection, string cellRange)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT search_text FROM passages WHERE cell_range=$range LIMIT 1";
            command.Parameters.AddWithValue("$range", cellRange);
            return (await command.ExecuteScalarAsync(Token))?.ToString() ?? string.Empty;
        }

        static S.Row SheetRow(uint rowIndex, params (string Reference, string Value)[] cells)
        {
            var row = new S.Row { RowIndex = rowIndex };
            foreach (var (reference, value) in cells)
                row.Append(new S.Cell
                {
                    CellReference = reference,
                    DataType = S.CellValues.InlineString,
                    InlineString = new S.InlineString(new S.Text(value))
                });
            return row;
        }
    }

    private sealed class CountingOcrEngine : IOcrEngine
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new OcrResult(string.Empty, null));
        }
    }
}
