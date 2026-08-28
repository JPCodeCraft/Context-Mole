using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

using ContextMole.Core;
using ContextMole.Documents;

using SharpCompress.Writers.GZip;
using SharpCompress.Writers.SevenZip;

namespace ContextMole.Tests;

public sealed class ExpandedFormatTests
{
    [Fact]
    public void CentralCatalogResolvesCompoundExtensionsAndMimeAliases()
    {
        Assert.Equal(".tar.gz", SupportedContent.ExtensionForPath("backup.DATA.TAR.GZ"));
        Assert.Equal(".json", SupportedContent.ExtensionForMimeType("text/json; charset=utf-8"));
        Assert.Equal(".jsonl", SupportedContent.ExtensionForMimeType("application/ndjson"));
        Assert.Equal(".rtf", SupportedContent.ExtensionForMimeType("text/rtf"));
        Assert.Equal(".zip", SupportedContent.ExtensionForMimeType("application/x-zip-compressed"));
        Assert.Equal("application/epub+zip", SupportedContent.MimeTypeForPath("BOOK.EPUB"));
        Assert.Equal(ContentFormatKind.WordOpenXml, SupportedContent.FindByExtension("DOCM")?.Kind);
        Assert.Equal(ContentFormatKind.OpenDocumentSpreadsheet, SupportedContent.FindByPath("ledger.ods")?.Kind);
        Assert.True(SupportedContent.IsValidExtensionFilter("tar.gz"));
        Assert.False(SupportedContent.IsValidExtensionFilter("tar..gz"));
    }

    [Fact]
    public void CentralCatalogClassifiesEveryModernOpenXmlExtension()
    {
        Assert.All(new[] { ".docx", ".docm", ".dotx", ".dotm" }, extension =>
            Assert.Equal(ContentFormatKind.WordOpenXml, SupportedContent.FindByExtension(extension)?.Kind));
        Assert.All(new[] { ".xlsx", ".xlsm", ".xltx", ".xltm" }, extension =>
            Assert.Equal(ContentFormatKind.SpreadsheetOpenXml, SupportedContent.FindByExtension(extension)?.Kind));
        Assert.All(new[] { ".pptx", ".pptm", ".ppsx", ".ppsm", ".potx", ".potm" }, extension =>
            Assert.Equal(ContentFormatKind.PresentationOpenXml, SupportedContent.FindByExtension(extension)?.Kind));
    }

    [Theory]
    [InlineData(".log", "Log searchable evidence")]
    [InlineData(".rst", "RST searchable evidence")]
    [InlineData(".adoc", "AsciiDoc searchable evidence")]
    [InlineData(".tex", "TeX searchable evidence")]
    [InlineData(".yaml", "YAML searchable evidence")]
    [InlineData(".yml", "YML searchable evidence")]
    [InlineData(".toml", "TOML searchable evidence")]
    public async Task AddedPlainTextFormatsRemainSearchable(string extension, string evidence)
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("notes" + extension);
        await File.WriteAllTextAsync(path, evidence, TestContext.Current.CancellationToken);

        var result = await ExtractAsync(path);

        Assert.Empty(result.Errors);
        Assert.Contains(evidence, RecursiveText(result.Root));
        Assert.Equal(ExtractionMethod.NativeText, Assert.Single(result.Root.Sections).Method);
    }

    [Theory]
    [InlineData(".csv", ",")]
    [InlineData(".tsv", "\t")]
    public async Task DelimitedTextPreservesValuesAndRowProvenance(string extension, string delimiter)
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("records" + extension);
        var content = string.Join("\r\n",
            $"Name{delimiter}Department{delimiter}Count",
            $"Alice{delimiter}Research{delimiter}42",
            $"Bob{delimiter}Operations{delimiter}7");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);

        var result = await ExtractAsync(path);
        var text = RecursiveText(result.Root);

        Assert.Empty(result.Errors);
        Assert.Contains("Alice", text);
        Assert.Contains("Research", text);
        Assert.Contains("42", text);
        Assert.Contains(result.Root.Sections, section =>
            section.Location.Kind == LocationKind.Sheet && !string.IsNullOrWhiteSpace(section.Location.CellRange));
    }

    [Fact]
    public async Task CsvParserHandlesQuotedDelimitersAndMultilineFields()
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("quoted.csv");
        await File.WriteAllTextAsync(path,
            "Name,Notes\r\n\"Beta, Inc.\",\"first line\nsecond line\"\r\n",
            TestContext.Current.CancellationToken);

        var result = await ExtractAsync(path);
        var text = RecursiveText(result.Root);

        Assert.Empty(result.Errors);
        Assert.Contains("Beta, Inc.", text);
        Assert.Contains("first line", text);
        Assert.Contains("second line", text);
    }

    [Fact]
    public async Task JsonPreservesNestedValuesAndStructuralProvenance()
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("record.json");
        await File.WriteAllTextAsync(path,
            """
            {
              "customer": { "name": "August", "active": true },
              "tags": ["local", "private"]
            }
            """, TestContext.Current.CancellationToken);

        var result = await ExtractAsync(path);
        var text = RecursiveText(result.Root);

        Assert.Empty(result.Errors);
        Assert.Contains("August", text);
        Assert.Contains("private", text);
        Assert.Contains("$.customer.name", text);
        Assert.Contains("$.tags[1]", text);
        Assert.Equal("$", Assert.Single(result.Root.Sections).Location.StructurePath);
    }

    [Fact]
    public async Task JsonLinesKeepsRecordsIndependentlyAddressable()
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("events.jsonl");
        await File.WriteAllTextAsync(path,
            "{\"id\":1,\"message\":\"first event\"}\n{\"id\":2,\"message\":\"second event\"}\n",
            TestContext.Current.CancellationToken);

        var result = await ExtractAsync(path);

        Assert.Empty(result.Errors);
        Assert.Contains("first event", RecursiveText(result.Root));
        Assert.Contains("second event", RecursiveText(result.Root));
        Assert.True(result.Root.Sections.Count >= 2);
        Assert.All(result.Root.Sections,
            section => Assert.False(string.IsNullOrWhiteSpace(section.Location.StructurePath)));
    }

    [Fact]
    public async Task XmlExtractsAttributesAndTextWithStructuralProvenance()
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("catalog.xml");
        await File.WriteAllTextAsync(path,
            "<catalog><book id=\"bk-7\"><title>XML searchable evidence</title></book></catalog>",
            TestContext.Current.CancellationToken);

        var result = await ExtractAsync(path);
        var text = RecursiveText(result.Root);

        Assert.Empty(result.Errors);
        Assert.Contains("bk-7", text);
        Assert.Contains("XML searchable evidence", text);
        Assert.Contains(result.Root.Sections,
            section => !string.IsNullOrWhiteSpace(section.Location.StructurePath));
    }

    [Fact]
    public async Task RtfExtractsReadableTextWithoutControlSyntax()
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("legacy.rtf");
        await File.WriteAllTextAsync(path,
            @"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0 RTF searchable evidence.\par Second paragraph.}",
            Encoding.ASCII, TestContext.Current.CancellationToken);

        var result = await ExtractAsync(path);
        var text = RecursiveText(result.Root);

        Assert.Empty(result.Errors);
        Assert.Contains("RTF searchable evidence", text);
        Assert.Contains("Second paragraph", text);
        Assert.DoesNotContain("fonttbl", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".odt", "ODT searchable evidence")]
    [InlineData(".ods", "ODS searchable evidence")]
    [InlineData(".odp", "ODP searchable evidence")]
    public async Task OpenDocumentFormatsExtractContentAndProvenance(string extension, string evidence)
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("open-document" + extension);
        CreateOpenDocument(path, extension, evidence);

        var result = await ExtractAsync(path);

        Assert.Empty(result.Errors);
        Assert.Contains(evidence, RecursiveText(result.Root));
        Assert.Contains(result.Root.Sections, section =>
            section.Location.Kind is LocationKind.Structure or LocationKind.Sheet or LocationKind.Slide);
    }

    [Fact]
    public async Task EpubUsesItsSpineOrderAndIgnoresScripts()
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("manual.epub");
        CreateEpub(path);

        var result = await ExtractAsync(path);
        var text = RecursiveText(result.Root);

        Assert.Empty(result.Errors);
        Assert.Contains("First EPUB chapter", text);
        Assert.Contains("Second EPUB chapter", text);
        Assert.True(text.IndexOf("First EPUB chapter", StringComparison.Ordinal) <
                    text.IndexOf("Second EPUB chapter", StringComparison.Ordinal));
        Assert.DoesNotContain("hidden executable text", text);
        Assert.All(result.Root.Sections,
            section => Assert.False(string.IsNullOrWhiteSpace(section.Location.StructurePath)));
    }

    [Theory]
    [InlineData(".docm", "Macro Word evidence")]
    [InlineData(".dotx", "Word template evidence")]
    public async Task WordOpenXmlAliasesUseTheWordExtractor(string extension, string evidence)
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("word" + extension);
        CreateWordPackage(path, extension == ".docm", evidence);

        var result = await ExtractAsync(path);

        Assert.Empty(result.Errors);
        Assert.Contains(evidence, RecursiveText(result.Root));
        Assert.Contains(result.Root.Sections, section => section.Location.Kind == LocationKind.Structure);
    }

    [Fact]
    public async Task SpreadsheetMacroAliasUsesTheSpreadsheetExtractor()
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("ledger.xlsm");
        CreateSpreadsheetPackage(path, "Macro spreadsheet evidence");

        var result = await ExtractAsync(path);

        Assert.Empty(result.Errors);
        Assert.Contains("Macro spreadsheet evidence", RecursiveText(result.Root));
        Assert.Contains(result.Root.Sections, section =>
            section.Location.Kind == LocationKind.Sheet && section.Location.Sheet == "Data");
    }

    [Fact]
    public async Task PresentationSlideshowAliasUsesThePresentationExtractor()
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("briefing.ppsx");
        CreatePresentationPackage(path, "Slideshow evidence");

        var result = await ExtractAsync(path);

        Assert.Empty(result.Errors);
        Assert.Contains("Slideshow evidence", RecursiveText(result.Root));
        Assert.Contains(result.Root.Sections, section =>
            section.Location.Kind == LocationKind.Slide && section.Location.Slide == 1);
    }

    [Fact]
    public async Task EmailAttachmentCanBeDispatchedByCentralMimeCatalogWithoutAnExtension()
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("mime-attachment.eml");
        await File.WriteAllTextAsync(path, BuildJsonAttachmentEmail(), Encoding.ASCII,
            TestContext.Current.CancellationToken);

        var result = await ExtractAsync(path);
        var attachment = Assert.Single(result.Root.Attachments, item => item.Name == "payload.data");

        Assert.Empty(result.Errors);
        Assert.Contains("MIME-dispatched JSON evidence", RecursiveText(attachment));
        Assert.Contains("$.message", RecursiveText(attachment));
        Assert.Equal("$", Assert.Single(attachment.Sections).Location.StructurePath);
    }

    [Fact]
    public async Task ZipDispatchesNewStructuredFormatsInsideTheArchive()
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("structured.zip");
        CreateZip(path,
            ("nested/data.json", "{\"message\":\"Nested JSON evidence\"}"),
            ("nested/table.csv", "Name,Value\r\nNested CSV evidence,9"));

        var result = await ExtractAsync(path);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Root.Attachments.Count);
        Assert.Contains("Nested JSON evidence", RecursiveText(result.Root));
        Assert.Contains("Nested CSV evidence", RecursiveText(result.Root));
    }

    [Theory]
    [InlineData(".tar", false)]
    [InlineData(".tar.gz", true)]
    [InlineData(".tgz", true)]
    public async Task TarVariantsExpandAndDispatchTheirEntries(string extension, bool compressed)
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("bundle" + extension);
        await CreateTarAsync(path, compressed);

        var result = await ExtractAsync(path);

        Assert.Empty(result.Errors);
        Assert.Contains("TAR JSON evidence", RecursiveText(result.Root));
        Assert.Contains(result.Root.Attachments, item => item.Name.EndsWith("evidence.json", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(".7z")]
    [InlineData(".gz")]
    public async Task SingleFileArchiveVariantsExpandAndDispatchTheirEntry(string extension)
    {
        using var workspace = new ExpandedFormatTestDirectory();
        var path = workspace.File("single" + extension);
        CreateSingleFileArchive(path, extension, "payload.json",
            Encoding.UTF8.GetBytes("{\"message\":\"Single archive evidence\"}"));

        var result = await ExtractAsync(path);

        Assert.Empty(result.Errors);
        Assert.Contains("Single archive evidence", RecursiveText(result.Root));
        Assert.Contains(result.Root.Attachments, item => item.Name == "payload.json");
    }

    private static Task<ExtractionResult> ExtractAsync(string path) =>
        new DocumentExtractionRegistry(new ExpandedFormatNoOcr())
            .ExtractAsync(new ExtractionRequest(path), TestContext.Current.CancellationToken);

    private static string RecursiveText(ExtractedNode node) =>
        string.Join('\n', node.Sections.Select(section => section.Text)
            .Concat(node.Attachments.Select(RecursiveText)));

    private static void CreateOpenDocument(string path, string extension, string evidence)
    {
        var mime = extension switch
        {
            ".odt" => "application/vnd.oasis.opendocument.text",
            ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
            ".odp" => "application/vnd.oasis.opendocument.presentation",
            _ => throw new ArgumentOutOfRangeException(nameof(extension))
        };
        var body = extension switch
        {
            ".odt" => $"<office:text><text:h text:outline-level=\"1\">Heading</text:h><text:p>{evidence}</text:p></office:text>",
            ".ods" => $"<office:spreadsheet><table:table table:name=\"Data\"><table:table-row><table:table-cell office:value-type=\"string\"><text:p>{evidence}</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet>",
            ".odp" => $"<office:presentation><draw:page draw:name=\"Slide 1\"><draw:frame><draw:text-box><text:p>{evidence}</text:p></draw:text-box></draw:frame></draw:page></office:presentation>",
            _ => throw new ArgumentOutOfRangeException(nameof(extension))
        };
        var content = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content
                xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
                xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0"
                xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0"
                office:version="1.3">
              <office:body>{body}</office:body>
            </office:document-content>
            """;
        CreateZip(path, ("mimetype", mime), ("content.xml", content));
    }

    private static void CreateEpub(string path)
    {
        const string container = """
            <?xml version="1.0"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """;
        const string package = """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="book-id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="book-id">fixture</dc:identifier><dc:title>Fixture</dc:title></metadata>
              <manifest>
                <item id="chapter-1" href="chapter-1.xhtml" media-type="application/xhtml+xml"/>
                <item id="chapter-2" href="chapter-2.xhtml" media-type="application/xhtml+xml"/>
              </manifest>
              <spine><itemref idref="chapter-1"/><itemref idref="chapter-2"/></spine>
            </package>
            """;
        const string first = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>First EPUB chapter</h1><script>hidden executable text</script></body></html>";
        const string second = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>Second EPUB chapter</p></body></html>";
        CreateZip(path,
            ("mimetype", "application/epub+zip"),
            ("META-INF/container.xml", container),
            ("OEBPS/content.opf", package),
            ("OEBPS/chapter-1.xhtml", first),
            ("OEBPS/chapter-2.xhtml", second));
    }

    private static void CreateWordPackage(string path, bool macroEnabled, string evidence)
    {
        var contentType = macroEnabled
            ? "application/vnd.ms-word.document.macroEnabled.main+xml"
            : "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml";
        CreateZip(path,
            ("[Content_Types].xml", ContentTypes(("/word/document.xml", contentType))),
            ("_rels/.rels", RootRelationship("word/document.xml")),
            ("word/document.xml", $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>{evidence}</w:t></w:r></w:p></w:body></w:document>"));
    }

    private static void CreateSpreadsheetPackage(string path, string evidence) =>
        CreateZip(path,
            ("[Content_Types].xml", ContentTypes(
                ("/xl/workbook.xml", "application/vnd.ms-excel.sheet.macroEnabled.main+xml"),
                ("/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))),
            ("_rels/.rels", RootRelationship("xl/workbook.xml")),
            ("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>{evidence}</t></is></c></row></sheetData></worksheet>"));

    private static void CreatePresentationPackage(string path, string evidence) =>
        CreateZip(path,
            ("[Content_Types].xml", ContentTypes(
                ("/ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideshow.main+xml"),
                ("/ppt/slides/slide1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml"))),
            ("_rels/.rels", RootRelationship("ppt/presentation.xml")),
            ("ppt/presentation.xml", "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/></p:sldIdLst></p:presentation>"),
            ("ppt/_rels/presentation.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide1.xml\"/></Relationships>"),
            ("ppt/slides/slide1.xml", $"<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree><p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Text\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>{evidence}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>"));

    private static string ContentTypes(params (string PartName, string ContentType)[] overrides) =>
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        string.Concat(overrides.Select(item =>
            $"<Override PartName=\"{item.PartName}\" ContentType=\"{item.ContentType}\"/>")) +
        "</Types>";

    private static string RootRelationship(string target) =>
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        $"<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"{target}\"/>" +
        "</Relationships>";

    private static string BuildJsonAttachmentEmail() =>
        ("From: sender@example.test\n" +
         "To: reader@example.test\n" +
         "Subject: MIME dispatch\n" +
         "MIME-Version: 1.0\n" +
         "Content-Type: multipart/mixed; boundary=fixture\n\n" +
         "--fixture\nContent-Type: text/plain; charset=utf-8\n\nParent body.\n" +
         "--fixture\nContent-Type: application/json; name=\"payload.data\"\n" +
         "Content-Disposition: attachment; filename=\"payload.data\"\n" +
         "Content-Transfer-Encoding: base64\n\n" +
         Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"message\":\"MIME-dispatched JSON evidence\"}")) + "\n" +
         "--fixture--\n")
        .Replace("\n", "\r\n", StringComparison.Ordinal);

    private static void CreateZip(string path, params (string Name, string Text)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(text);
        }
    }

    private static async Task CreateTarAsync(string path, bool compressed)
    {
        await using var destination = File.Create(path);
        Stream output = destination;
        GZipStream? gzip = null;
        if (compressed)
        {
            gzip = new GZipStream(destination, CompressionLevel.SmallestSize, leaveOpen: true);
            output = gzip;
        }

        using (var writer = new TarWriter(output, leaveOpen: true))
        using (var data = new MemoryStream(Encoding.UTF8.GetBytes("{\"message\":\"TAR JSON evidence\"}")))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "nested/evidence.json")
            {
                DataStream = data
            };
            writer.WriteEntry(entry);
        }

        if (gzip is not null)
            await gzip.DisposeAsync();
    }

    private static void CreateSingleFileArchive(string path, string extension, string entryName, byte[] payload)
    {
        using var output = File.Create(path);
        using var input = new MemoryStream(payload, writable: false);
        if (extension == ".7z")
        {
            using var writer = SevenZipWriter.OpenWriter(output,
                new SevenZipWriterOptions { LeaveStreamOpen = true });
            writer.Write(entryName, input, null);
            return;
        }

        using var gzip = GZipWriter.OpenWriter(output,
            new GZipWriterOptions { LeaveStreamOpen = true });
        gzip.Write(entryName, input, null);
    }
}

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class ExpandedFormatStorageTests
{
    [Fact]
    public async Task ObservedCompoundExtensionIsRetainedInInventoryAndFilters()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Compound extension", cancellationToken);
        var path = Path.Combine(database.Paths.SourceDirectory, "bundle.tar.gz");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], cancellationToken);

        var pending = await database.ObserveAndLeaseAsync(projectId, folderId, path, false, cancellationToken);
        var inventory = await database.Store.ListDocumentsAsync(new DocumentListRequest(projectId), cancellationToken);
        var document = Assert.Single(inventory.Documents);
        var filtered = await database.Store.ListDocumentsAsync(
            new DocumentListRequest(projectId, Extensions: ["tar.gz"]), cancellationToken);

        Assert.Equal(".tar.gz", pending.Job.Extension);
        Assert.Equal(".tar.gz", document.FileType);
        Assert.Equal(document.DocumentId, Assert.Single(filtered.Documents).DocumentId);
        Assert.Equal(".tar.gz", Assert.Single(await database.Store.ListProjectFileTypeCountsAsync(
            projectId, cancellationToken)).Extension);

        await database.Writer.FailJobAsync(pending.Job, "cleanup", "Deliberate cleanup", retryable: false,
            cancellationToken: cancellationToken);
    }
}

file sealed class ExpandedFormatNoOcr : IOcrEngine
{
    public bool IsAvailable => false;
    public string UnavailableReason => "OCR must not be reached by expanded format fixtures.";
    public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException(UnavailableReason));
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
        Task.FromException<OcrResult>(new InvalidOperationException(UnavailableReason));
}

file sealed class ExpandedFormatTestDirectory : IDisposable
{
    public ExpandedFormatTestDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "ContextMole.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string File(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
