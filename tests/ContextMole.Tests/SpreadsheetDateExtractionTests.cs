using ContextMole.Core;
using ContextMole.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace ContextMole.Tests;

public sealed class SpreadsheetDateExtractionTests
{
    [Theory]
    [InlineData(false, "46345", "2026-11-19")]
    [InlineData(true, "44883", "2026-11-19")]
    [InlineData(false, "1", "1900-01-01")]
    [InlineData(true, "0", "1904-01-01")]
    [InlineData(false, "59", "1900-02-28")]
    [InlineData(false, "61", "1900-03-01")]
    public async Task DateStyleUsesWorkbookEpochAndRetainsRawStoredValue(bool date1904, string serial, string expected)
    {
        var text = await ExtractRow(date1904, 14, null, serial);
        Assert.Contains(expected, text); Assert.Contains("Excel serial: " + serial, text);
    }
    [Theory]
    [InlineData(164, "yyyy-mm-dd", "46345", "2026-11-19")]
    [InlineData(164, "mm", "46345", "2026-11-19")]
    [InlineData(164, "mmm hh:mm", "46345.5", "2026-11-19T12:00:00")]
    [InlineData(164, "AM/PM", "0.5", "12:00:00")]
    [InlineData(164, "[$-409]dd/mm/yyyy", "46345", "2026-11-19")]
    [InlineData(22, null, "46345.5", "2026-11-19T12:00:00")]
    [InlineData(20, null, "0.5", "12:00:00")]
    [InlineData(46, null, "1.5", "36:00:00")]
    [InlineData(164, "[h]:mm:ss", "1.5", "36:00:00")]
    [InlineData(14, null, "60", "1900-02-29 (Excel 1900 leap-day)")]
    public async Task BuiltInAndCustomDateTimeStylesProduceExplicitCanonicalValues(uint format, string? code, string serial, string expected)
    {
        var text = await ExtractRow(false, format, code, serial); Assert.Contains(expected, text);
    }
    [Theory]
    [InlineData(0, null, "46345")]
    [InlineData(164, "0.00 \"days\"", "46345")]
    [InlineData(164, "0.00\\m", "46345")]
    [InlineData(164, "[Red]0.00", "46345")]
    [InlineData(164, "[>=1000]yyyy-mm-dd;0", "12")]
    [InlineData(164, "[>=1000]yyyy-mm-dd;0", "46345")]
    [InlineData(164, "hh:mm;hh:mm;0", "0")]
    [InlineData(14, null, "-1")]
    [InlineData(14, null, "999999999")]
    public async Task NumbersAndUnusableDatesAreNotInventedAsCalendarValues(uint format, string? code, string serial)
    {
        var text = await ExtractRow(false, format, code, serial); Assert.Equal("A1: " + serial, text);
    }
    [Fact]
    public async Task FormulasUseCachedValuesOnlyAndUncachedExpressionsAreExplicit()
    {
        var cached = await ExtractRow(false, 0, null, "1250", "SUM(B1:B2)");
        Assert.Equal("A1: 1250", cached);
        var uncached = await ExtractRow(false, 14, null, null, "DATE(2026,11,19)");
        Assert.Contains("DATE(2026,11,19)", uncached);
        Assert.Contains("no cached value", uncached);
        Assert.DoesNotContain("2026-11-19", uncached);
    }
    [Fact]
    public async Task MissingStyleUsesDefaultFormatAndEmptyCacheDoesNotEvaluateFormula()
    {
        Assert.Contains("2026-11-19", await ExtractRow(false, 14, null, "46345", omitStyle: true));
        Assert.Equal("A1: 0", await ExtractRow(true, 164, "yyyy-mm-dd;yyyy-mm-dd;0", "0"));
        var text = await ExtractRow(false, 14, null, "", "DATE(2026,11,19)");
        Assert.Contains("no cached value", text); Assert.DoesNotContain("2026-11-19", text);
    }
    private static async Task<string> ExtractRow(bool date1904, uint format, string? code, string? serial, string? formula = null, bool omitStyle = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ContextMole-date-regression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "dates.xlsx");
        try
        {
            using (var package = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
            {
                var workbook = package.AddWorkbookPart();
                workbook.Workbook = new Workbook(new WorkbookProperties { Date1904 = date1904 });
                var styles = workbook.AddNewPart<WorkbookStylesPart>();
                styles.Stylesheet = new Stylesheet();
                if (code is not null) styles.Stylesheet.AppendChild(new NumberingFormats(new NumberingFormat { NumberFormatId = format, FormatCode = code }));
                styles.Stylesheet.AppendChild(new CellFormats(new CellFormat { NumberFormatId = format }));
                var sheet = workbook.AddNewPart<WorksheetPart>();
                var cell = new Cell { CellReference = "A1", StyleIndex = 0 };
                if (omitStyle) cell.StyleIndex = null;
                if (formula is not null) cell.CellFormula = new CellFormula(formula);
                if (serial is not null) cell.CellValue = new CellValue(serial);
                sheet.Worksheet = new Worksheet(new SheetData(new Row(cell) { RowIndex = 1 }));
                workbook.Workbook.AppendChild(new Sheets(new Sheet { Id = workbook.GetIdOfPart(sheet), Name = "Dates", SheetId = 1 }));
            }
            var result = await new DocumentExtractionRegistry(new NoDateOcr()).ExtractAsync(new ExtractionRequest(path), TestContext.Current.CancellationToken);
            Assert.Empty(result.Errors); var section = Assert.Single(result.Root.Sections);
            Assert.Equal(new SourceLocation(LocationKind.Sheet, Sheet: "Dates", CellRange: "A1"), section.Location);
            return section.Text;
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class NoDateOcr : IOcrEngine
    {
        public bool IsAvailable => false; public string UnavailableReason => "Spreadsheet dates must not call OCR.";
        public Task EnsureAvailableAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken token) => throw new InvalidOperationException(UnavailableReason);
    }
}
