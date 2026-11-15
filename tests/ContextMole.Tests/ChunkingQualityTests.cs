using System.Collections.Concurrent;

using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Indexing;
using ContextMole.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ContextMole.Tests;

/// <summary>
/// Passage quality for retrieval: PDF headings and table structure, passages that keep
/// related blocks together, chunks that end on whole rows and sentences, and embedding text
/// that names what each table value measures.
/// </summary>
[Collection(nameof(SqliteIntegrationCollection))]
public sealed class ChunkingQualityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void TableHeaderNamesEachValueAndIsLeftOutOfTheRows()
    {
        const string table = "State\tn\tIlliterate\nKerala\t2400\t7.2\nTotal\t9999";
        var header = Assert.IsType<TableSemantics.Header>(TableSemantics.ParseHeader(table, cellReferences: false));
        Assert.Equal("State | n | Illiterate", header.Display);
        // A row with a different cell count cannot be paired by position and keeps plain values.
        Assert.Equal("State: Kerala; n: 2400; Illiterate: 7.2.\nTotal; 9999.",
            TableSemantics.Linearize(table, header, cellReferences: false));
        // A spreadsheet row or chunk holding only the header still embeds the column names.
        Assert.Equal("State; n; Illiterate.", TableSemantics.Linearize("State\tn\tIlliterate", header, cellReferences: false));
    }

    [Theory]
    [InlineData("2400\t7.2\t0.5")]
    [InlineData("Item\t12\t13")]
    [InlineData("\t\t")]
    public void DataRowsAreNotMistakenForColumnNames(string firstRow) =>
        Assert.Null(TableSemantics.ParseHeader(firstRow + "\nKerala\t2400\t7.2", cellReferences: false));

    [Fact]
    public void YearsAndSpreadsheetColumnsNameTheirValues()
    {
        var years = Assert.IsType<TableSemantics.Header>(TableSemantics.ParseHeader("Item\t2019\t2020", false));
        Assert.Equal("Item: Rent; 2019: 1,200; 2020: 1,250.", TableSemantics.Linearize("Rent\t1,200\t1,250", years, false));

        var sheet = Assert.IsType<TableSemantics.Header>(TableSemantics.ParseHeader("A1: Name\tB1: Total", true));
        Assert.Equal("Name: Ada; Total: 42; extra.",
            TableSemantics.Linearize("A1: Name\tB1: Total\nA2: Ada\tB2: 42\tC2: extra", sheet, cellReferences: true));
    }

    [Theory]
    [InlineData("§", false, ExtractionMethod.NativeText, null, false)]
    [InlineData("2.1", false, ExtractionMethod.NativeText, null, false)]
    [InlineData(":=", false, ExtractionMethod.NativeText, null, false)]
    [InlineData("Ok", false, ExtractionMethod.NativeText, null, true)]
    [InlineData("2400 7.2", true, ExtractionMethod.NativeText, null, true)]
    [InlineData("Approved by the board", false, ExtractionMethod.Ocr, 39.9, false)]
    [InlineData("Approved by the board", false, ExtractionMethod.Ocr, 40.0, true)]
    [InlineData("Approved by the board", false, ExtractionMethod.Ocr, null, true)]
    public void OnlyTextWithMeaningGetsAVector(string body, bool isTable, ExtractionMethod method, double? confidence,
        bool expected) =>
        Assert.Equal(expected, SemanticEvidenceEligibility.HasSemanticSubstance(body, isTable, method, confidence));

    [Fact]
    public async Task PdfTableTakesItsWrappedColumnLabelsAndLinksItsCaption()
    {
        using var workspace = new Workspace();
        var path = workspace.File("literacy.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 800);
        void Add(string text, double x, double y, double size = 9) => page.AddText(text, size, new PdfPoint(x, y), font);
        Add("Table 3: Household literacy by state", 50, 650);
        // Two label lines whose cells do not line up with the data columns.
        Add("State", 50, 614); Add("Sample", 140, 614); Add("Read &", 300, 614);
        Add("size", 140, 604); Add("Illiterate", 220, 604); Add("Write", 300, 604); Add("Total", 380, 604);
        var rows = new[]
        {
            new[] { "Kerala", "2400", "7.2", "0.5", "41.5" }, new[] { "Tamil Nadu", "2400", "21.4", "2.3", "33.1" },
            new[] { "Karnataka", "2399", "37.4", "1.6", "24.0" }, new[] { "Gujarat", "2390", "28.6", "0.1", "26.9" }
        };
        for (var row = 0; row < rows.Length; row++)
            for (var column = 0; column < rows[row].Length; column++)
                Add(rows[row][column], new[] { 50, 140, 220, 300, 380 }[column], 588 - row * 14);
        Add("The survey covered rural households in every state listed above and reports literacy shares as percentages.",
            50, 480);
        await File.WriteAllBytesAsync(path, builder.Build(), Token);

        var result = await Extract(path);
        Assert.Empty(result.Errors);
        var table = Assert.Single(result.Root.Sections, section => section.Location.StructurePath!.EndsWith(']') &&
            section.Location.StructurePath.Contains("/table[", StringComparison.Ordinal));
        Assert.Equal("State\tSample size\tIlliterate\tRead & Write\tTotal\n" +
            string.Join('\n', rows.Select(row => string.Join('\t', row))), table.Text);
        var caption = Assert.Single(result.Root.Sections, section => section.Text.StartsWith("Table 3", StringComparison.Ordinal));
        Assert.Equal(table.Location.StructurePath + "/caption", caption.Location.StructurePath);

        var passages = await Chunk(result.Root, path);
        var tablePassage = Assert.Single(passages, passage => passage.Location.StructurePath == table.Location.StructurePath);
        Assert.Equal(table.Text, tablePassage.DisplayText);
        Assert.StartsWith("State: Kerala; Sample size: 2400; Illiterate: 7.2; Read & Write: 0.5; Total: 41.5.",
            tablePassage.SearchText, StringComparison.Ordinal);
        Assert.Contains("Table caption: Table 3: Household literacy by state", tablePassage.SearchText, StringComparison.Ordinal);
        Assert.Contains("Table headers: State | Sample size | Illiterate | Read & Write | Total", tablePassage.SearchText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PdfHeadingsComeFromTypeSizeWeightAndNumbering()
    {
        using var workspace = new Workspace();
        var path = workspace.File("headings.pdf");
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var page = builder.AddPage(600, 800);
        var y = 740d;
        void Line(string text, PdfDocumentBuilder.AddedFont font, double size, double gap)
        {
            page.AddText(text, size, new PdfPoint(50, y), font);
            y -= gap;
        }
        void Paragraph()
        {
            for (var line = 0; line < 4; line++)
                Line("Drivers who avoid hard acceleration and long idling use measurably less fuel per mile.", regular, 10, 13);
            y -= 14;
        }
        Line("Quantifying Fuel Savings from", bold, 16, 19);
        Line("Driving Behavior Changes", bold, 16, 30);
        Paragraph();
        Line("2.1 Savings from Driving Profiles", bold, 10, 24);
        Paragraph();
        Line("2.1.1 Drive Profile Subsample", bold, 9, 22);
        Paragraph();
        Line("Prepared by", bold, 10, 22);
        Line("Ada Lovelace", regular, 10, 22);
        await File.WriteAllBytesAsync(path, builder.Build(), Token);

        var result = await Extract(path);
        Assert.Empty(result.Errors);
        const string title = "Quantifying Fuel Savings from Driving Behavior Changes";
        string[] HeadingsOf(string text) => Assert.Single(result.Root.Sections,
            section => section.Text.StartsWith(text, StringComparison.Ordinal)).HeadingPath!.ToArray();
        Assert.Equal(new[] { title }, HeadingsOf("Quantifying"));
        Assert.Equal(new[] { title, "2.1 Savings from Driving Profiles" }, HeadingsOf("2.1 Savings"));
        // Numbered subheadings may be set a point below body text.
        Assert.Equal(new[] { title, "2.1 Savings from Driving Profiles", "2.1.1 Drive Profile Subsample" },
            HeadingsOf("2.1.1"));
        // A bold label without numbering or a following paragraph is not a heading.
        Assert.Equal(HeadingsOf("2.1.1"), HeadingsOf("Prepared by"));
    }

    [Fact]
    public async Task PdfNumberedStepsAndBulletsStayWithTheirLeadIn()
    {
        using var workspace = new Workspace();
        var path = workspace.File("steps.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 800);
        page.AddText("Each recorded trip was converted into an ideal cycle using the following steps:", 10,
            new PdfPoint(50, 700), font);
        var steps = new[] { "Calculate the trip distance.", "Remove stops and idling.", "Set the cruising speed to 40 mph." };
        for (var step = 0; step < steps.Length; step++)
        {
            page.AddText($"{step + 1}.", 10, new PdfPoint(60, 684 - step * 14), font);
            page.AddText(steps[step], 10, new PdfPoint(84, 684 - step * 14), font);
        }
        page.AddText("The ideal cycles were then simulated with the same midsize vehicle model as the originals.", 10,
            new PdfPoint(50, 630), font);
        await File.WriteAllBytesAsync(path, builder.Build(), Token);

        var result = await Extract(path);
        Assert.Empty(result.Errors);
        Assert.DoesNotContain(result.Root.Sections, section => section.Location.StructurePath!.Contains("table[", StringComparison.Ordinal));
        Assert.Contains(result.Root.Sections, section => section.Text ==
            "1. Calculate the trip distance.\n2. Remove stops and idling.\n3. Set the cruising speed to 40 mph.");
        var passage = Assert.Single(await Chunk(result.Root, path));
        Assert.StartsWith("Each recorded trip was converted into an ideal cycle using the following steps:\n1. Calculate",
            passage.DisplayText, StringComparison.Ordinal);
        Assert.Matches(@"^page\[1\]/block\[\d+\]\.\.page\[1\]/block\[\d+\]$", passage.Location.StructurePath);
    }

    [Fact]
    public async Task TableChunksEndOnWholeRowsAndRepeatOnlyWholeRows()
    {
        var rows = Enumerable.Range(1, 160).Select(row => $"Item {row}\t{row * 10}\tWarehouse {row % 7}").ToArray();
        var text = "Name\tQuantity\tLocation\n" + string.Join('\n', rows);
        var location = new SourceLocation(LocationKind.Page, Page: 1, StructurePath: "page[1]/table[1]",
            Region: new SourceRegion(0.1, 0.1, 0.8, 0.8));
        var root = new ExtractedNode("stock.pdf", "application/pdf", "root",
            [new ExtractedSection(text, location, ExtractionMethod.NativeText)], []);
        var passages = await Chunk(root, "stock.pdf");

        Assert.True(passages.Count > 1);
        var known = text.Split('\n').ToHashSet(StringComparer.Ordinal);
        foreach (var passage in passages)
        {
            Assert.All(passage.DisplayText.Split('\n'), line => Assert.Contains(line, known));
            Assert.Equal(passage.DisplayText, text.Substring(passage.SectionOffset, passage.DisplayText.Length));
            Assert.Contains("Table headers: Name | Quantity | Location", passage.SearchText, StringComparison.Ordinal);
        }
        Assert.StartsWith("Name: Item 1; Quantity: 10; Location: Warehouse 1.", passages[0].SearchText, StringComparison.Ordinal);
        // Every row is covered, and a following chunk starts at a row inside the previous one.
        Assert.Equal(known, passages.SelectMany(passage => passage.DisplayText.Split('\n')).ToHashSet(StringComparer.Ordinal));
        for (var index = 1; index < passages.Count; index++)
            Assert.Contains(passages[index].DisplayText.Split('\n')[0], passages[index - 1].DisplayText.Split('\n'));
    }

    [Fact]
    public async Task OversizedRowsDoNotLeaveTheOverlapAsAChunkOfItsOwn()
    {
        // The last ordinary row is repeated as overlap; the next row alone exceeds the budget.
        var rows = Enumerable.Range(1, 4).Select(row => $"Office {row}\t" + string.Join(' ', Enumerable.Repeat("vote", 40)));
        var text = "Office\tResult\n" + string.Join('\n', rows) + "\nJudges\t" + string.Join(' ', Enumerable.Repeat("candidate", 450));
        var root = new ExtractedNode("bulletin.pdf", "application/pdf", "root",
            [new ExtractedSection(text, new SourceLocation(LocationKind.Page, Page: 1, StructurePath: "page[1]/table[1]",
                Region: new SourceRegion(0.1, 0.1, 0.8, 0.8)), ExtractionMethod.NativeText)], []);
        var passages = await Chunk(root, "bulletin.pdf");
        Assert.StartsWith("Office\tResult\nOffice 1", passages[0].DisplayText, StringComparison.Ordinal);
        Assert.All(passages.Skip(1), passage => Assert.Contains("candidate", passage.DisplayText, StringComparison.Ordinal));
        Assert.EndsWith("candidate", passages[^1].DisplayText, StringComparison.Ordinal);
        Assert.All(passages, passage =>
            Assert.Equal(passage.DisplayText, text.Substring(passage.SectionOffset, passage.DisplayText.Length)));
    }

    [Fact]
    public async Task ProseChunksStartAndEndOnSentences()
    {
        var sentences = Enumerable.Range(1, 120)
            .Select(index => $"Sentence {index} explains how the maintenance team inspects pump number {index} each week.")
            .ToArray();
        sentences[3] = "Inspections follow the schedule in Fig. 4 and the vendor manual, e.g. section 2 of it.";
        var text = string.Join(' ', sentences);
        var root = new ExtractedNode("manual.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "root", [new ExtractedSection(text, new SourceLocation(LocationKind.Structure, StructurePath: "document/paragraph[1]"),
                ExtractionMethod.NativeText)], []);
        var passages = await Chunk(root, "manual.docx");

        Assert.True(passages.Count > 2);
        Assert.All(passages, passage =>
        {
            Assert.EndsWith(".", passage.DisplayText, StringComparison.Ordinal);
            Assert.Contains(sentences, sentence => passage.DisplayText.StartsWith(sentence, StringComparison.Ordinal));
        });
        // Overlap repeats the closing sentence of the previous chunk.
        for (var index = 1; index < passages.Count; index++)
            Assert.True(passages[index].SectionOffset < passages[index - 1].SectionOffset + passages[index - 1].DisplayText.Length);
    }

    private static async Task<IReadOnlyList<PassageDraft>> Chunk(ExtractedNode root, string sourcePath)
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new DocumentExtractionRegistry(new StorageNoOcr()), new WordEmbeddings(), new IndexingActivityTracker(),
            new EmbeddingPolicyRefreshTracker(), budget, NullLogger<IndexingCoordinator>.Instance);
        var flatten = typeof(IndexingCoordinator).GetMethod("FlattenAndChunk",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var draft = ((List<ContentNodeDraft> Nodes, List<PassageDraft> Passages, List<SectionDraft> Sections))
            flatten.Invoke(coordinator, [root, sourcePath, Guid.NewGuid()])!;
        return draft.Passages;
    }

    private static Task<ExtractionResult> Extract(string path) =>
        new DocumentExtractionRegistry(new StorageNoOcr()).ExtractAsync(new ExtractionRequest(path), Token);

    private sealed class WordEmbeddings : IEmbeddingGenerator
    {
        public ConcurrentBag<string> Inputs { get; } = [];
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public EmbeddingPolicy? Policy => StorageTestDatabase.TestEmbeddingPolicy;
        public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingBatch(passages.Select(_ => StorageTestDatabase.TestVector()).ToArray(), Policy!));
        public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult(new QueryEmbedding(StorageTestDatabase.TestVector(), Policy!));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "ContextMole-ChunkingTests-" + Guid.NewGuid().ToString("N"));
        public Workspace() => Directory.CreateDirectory(_path);
        public string File(string name) => System.IO.Path.Combine(_path, name);
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
