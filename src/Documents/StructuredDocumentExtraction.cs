using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

using ContextMole.Core;

namespace ContextMole.Documents;

public sealed partial class DocumentExtractionRegistry
{
    private static readonly XNamespace OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace DrawNs = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";

    private ExtractedNode DelimitedTextNode(byte[] bytes, string name, string? mimeType, string relationship,
        string extension)
    {
        var delimiter = extension == ".tsv" ? '\t' : ',';
        var sheetName = extension == ".tsv" ? "TSV" : "CSV";
        var sections = new List<ExtractedSection>();
        var rowNumber = 0;
        foreach (var row in ParseDelimited(DecodeText(bytes), delimiter))
        {
            rowNumber++;
            var populated = row.Select((value, index) => (Value: value.Trim(), Column: index + 1))
                .Where(cell => !string.IsNullOrWhiteSpace(cell.Value)).ToArray();
            if (populated.Length == 0) continue;
            var text = string.Join('\t', populated.Select(cell => $"{SpreadsheetColumn(cell.Column)}: {cell.Value}"));
            var first = $"{SpreadsheetColumn(populated[0].Column)}{rowNumber}";
            var last = $"{SpreadsheetColumn(populated[^1].Column)}{rowNumber}";
            sections.Add(new ExtractedSection(text,
                new SourceLocation(LocationKind.Sheet, Sheet: sheetName, CellRange: first == last ? first : $"{first}:{last}"),
                ExtractionMethod.NativeText));
        }
        return new ExtractedNode(name, mimeType, relationship, sections, []);
    }

    private static IEnumerable<IReadOnlyList<string>> ParseDelimited(string text, char delimiter)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(character);
                }
                continue;
            }

            if (character == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (character == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                row.Add(field.ToString());
                field.Clear();
                yield return row;
                row = [];
            }
            else
            {
                field.Append(character);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    private static string SpreadsheetColumn(int number)
    {
        var result = string.Empty;
        while (number > 0)
        {
            number--;
            result = (char)('A' + (number % 26)) + result;
            number /= 26;
        }
        return result;
    }

    private static ExtractedNode JsonNode(byte[] bytes, string name, string? mimeType, string relationship)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 128
            });
            var lines = new List<string>();
            FlattenJson(document.RootElement, "$", lines);
            var sections = lines.Count == 0
                ? Array.Empty<ExtractedSection>()
                : [new ExtractedSection(string.Join(Environment.NewLine, lines),
                    new SourceLocation(LocationKind.Structure, StructurePath: "$"), ExtractionMethod.NativeText)];
            return new ExtractedNode(name, mimeType, relationship, sections, []);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("JSON document is malformed.", exception);
        }
    }

    private static ExtractedNode JsonLinesNode(byte[] bytes, string name, string? mimeType, string relationship)
    {
        var sections = new List<ExtractedSection>();
        using var reader = new StringReader(DecodeText(bytes));
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = new List<string>();
            try
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 128
                });
                FlattenJson(document.RootElement, "$", values);
            }
            catch (JsonException)
            {
                values.Add(line);
            }
            sections.Add(new ExtractedSection(string.Join(Environment.NewLine, values),
                new SourceLocation(LocationKind.Structure, StructurePath: $"line[{lineNumber}]"),
                ExtractionMethod.NativeText));
        }
        return new ExtractedNode(name, mimeType, relationship, sections, []);
    }

    private static void FlattenJson(JsonElement element, string path, List<string> lines)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var hasProperties = false;
                foreach (var property in element.EnumerateObject())
                {
                    hasProperties = true;
                    FlattenJson(property.Value, JsonPropertyPath(path, property.Name), lines);
                }
                if (!hasProperties) lines.Add($"{path}: {{}}");
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    FlattenJson(item, $"{path}[{index++}]", lines);
                if (index == 0) lines.Add($"{path}: []");
                break;
            case JsonValueKind.String:
                lines.Add($"{path}: {element.GetString()}");
                break;
            default:
                lines.Add($"{path}: {element.GetRawText()}");
                break;
        }
    }

    private static string JsonPropertyPath(string path, string property) =>
        property.Length > 0 && (char.IsLetter(property[0]) || property[0] == '_') &&
        property.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_')
            ? $"{path}.{property}"
            : $"{path}[{JsonSerializer.Serialize(property)}]";

    private static ExtractedNode XmlNode(byte[] bytes, string name, string? mimeType, string relationship)
    {
        var document = LoadXml(DecodeText(bytes));
        var sections = new List<ExtractedSection>();
        foreach (var element in document.Descendants())
        {
            var attributes = element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)
                .Select(attribute => $"@{attribute.Name.LocalName}: {attribute.Value}");
            var directText = string.Concat(element.Nodes().OfType<XText>().Select(node => node.Value)).Trim();
            var text = string.Join(Environment.NewLine, attributes.Append(directText)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            if (string.IsNullOrWhiteSpace(text)) continue;
            var path = "/" + string.Join('/', element.AncestorsAndSelf().Reverse().Select(item => item.Name.LocalName));
            sections.Add(new ExtractedSection(text,
                new SourceLocation(LocationKind.Structure, StructurePath: path), ExtractionMethod.NativeText));
        }
        return new ExtractedNode(name, mimeType, relationship, sections, []);
    }

    private static ExtractedNode RichTextNode(byte[] bytes, string name, string? mimeType, string relationship) =>
        TextNode(name, mimeType, relationship, RtfToText(DecodeText(bytes)), ExtractionMethod.NativeText);

    private async Task<ExtractedNode> OpenDocumentTextNodeAsync(byte[] bytes, string name, string? mimeType,
        string relationship, ExpansionContext context, CancellationToken cancellationToken)
    {
        var content = await ReadZipXmlAsync(bytes, "content.xml", context.Request.MaxAttachmentBytes, cancellationToken);
        var sections = new List<ExtractedSection>();
        string? heading = null;
        var ordinal = 0;
        foreach (var element in content.Descendants().Where(element => element.Name == TextNs + "h" || element.Name == TextNs + "p"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = OdfText(element).Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (element.Name == TextNs + "h") heading = text;
            sections.Add(new ExtractedSection(text,
                new SourceLocation(LocationKind.Structure, StructurePath: $"document/paragraph[{++ordinal}]"),
                ExtractionMethod.NativeText, Heading: heading));
        }
        return new ExtractedNode(name, mimeType, relationship, sections, []);
    }

    private async Task<ExtractedNode> OpenDocumentSpreadsheetNodeAsync(byte[] bytes, string name, string? mimeType,
        string relationship, ExpansionContext context, CancellationToken cancellationToken)
    {
        var content = await ReadZipXmlAsync(bytes, "content.xml", context.Request.MaxAttachmentBytes, cancellationToken);
        var sections = new List<ExtractedSection>();
        var sheetOrdinal = 0;
        foreach (var table in content.Descendants(TableNs + "table"))
        {
            sheetOrdinal++;
            var sheet = (string?)table.Attribute(TableNs + "name") ?? $"Sheet{sheetOrdinal}";
            var rowNumber = 0;
            foreach (var row in table.Elements(TableNs + "table-row"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowNumber++;
                var column = 1;
                var values = new List<string>();
                string? first = null;
                string? last = null;
                foreach (var cell in row.Elements().Where(element =>
                             element.Name == TableNs + "table-cell" || element.Name == TableNs + "covered-table-cell"))
                {
                    var repeat = Repetition(cell, TableNs + "number-columns-repeated");
                    var value = OdfCellText(cell);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        var start = $"{SpreadsheetColumn(column)}{rowNumber}";
                        var end = $"{SpreadsheetColumn(column + repeat - 1)}{rowNumber}";
                        first ??= start;
                        last = end;
                        values.Add(start == end ? $"{start}: {value}" : $"{start}:{end}: {value}");
                    }
                    column = Math.Min(1_048_577, column + repeat);
                }
                if (values.Count > 0)
                    sections.Add(new ExtractedSection(string.Join('\t', values),
                        new SourceLocation(LocationKind.Sheet, Sheet: sheet,
                            CellRange: first == last ? first : $"{first}:{last}"), ExtractionMethod.NativeText));
                rowNumber = Math.Min(1_048_576,
                    rowNumber + Repetition(row, TableNs + "number-rows-repeated") - 1);
            }
        }
        return new ExtractedNode(name, mimeType, relationship, sections, []);
    }

    private async Task<ExtractedNode> OpenDocumentPresentationNodeAsync(byte[] bytes, string name, string? mimeType,
        string relationship, ExpansionContext context, CancellationToken cancellationToken)
    {
        var content = await ReadZipXmlAsync(bytes, "content.xml", context.Request.MaxAttachmentBytes, cancellationToken);
        var sections = new List<ExtractedSection>();
        var slide = 0;
        foreach (var page in content.Descendants(DrawNs + "page"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            slide++;
            var pageName = (string?)page.Attribute(DrawNs + "name");
            var text = string.Join(Environment.NewLine,
                page.Descendants().Where(element => element.Name == TextNs + "h" || element.Name == TextNs + "p")
                    .Select(OdfText).Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!string.IsNullOrWhiteSpace(text))
                sections.Add(new ExtractedSection(text,
                    new SourceLocation(LocationKind.Slide, Slide: slide, StructurePath: pageName),
                    ExtractionMethod.NativeText));
        }
        return new ExtractedNode(name, mimeType, relationship, sections, []);
    }

    private async Task<ExtractedNode> EpubNodeAsync(byte[] bytes, string name, string? mimeType,
        string relationship, ExpansionContext context, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        var container = await ReadZipXmlAsync(archive, "META-INF/container.xml",
            context.Request.MaxAttachmentBytes, cancellationToken);
        var packagePath = (string?)container.Descendants().FirstOrDefault(element => element.Name.LocalName == "rootfile")?
            .Attribute("full-path") ?? throw new InvalidDataException("EPUB has no package document.");
        packagePath = NormalizeZipPath(packagePath);
        var package = await ReadZipXmlAsync(archive, packagePath, context.Request.MaxAttachmentBytes, cancellationToken);
        var title = MetadataTitle(package.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "title")?.Value);
        var packageDirectory = ZipDirectory(packagePath);
        var manifest = package.Descendants().Where(element => element.Name.LocalName == "item")
            .Select(element => new EpubManifestItem(
                (string?)element.Attribute("id"),
                (string?)element.Attribute("href"),
                (string?)element.Attribute("media-type")))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
            .ToDictionary(item => item.Id!, StringComparer.Ordinal);
        var ordered = package.Descendants().Where(element => element.Name.LocalName == "itemref")
            .Select(element => (string?)element.Attribute("idref"))
            .Where(id => id is not null && manifest.ContainsKey(id)).Select(id => manifest[id!]).ToList();
        if (ordered.Count == 0)
            ordered.AddRange(manifest.Values.Where(IsEpubTextItem));

        var sections = new List<ExtractedSection>();
        var chapter = 0;
        foreach (var item in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsEpubTextItem(item)) continue;
            if (++chapter > context.Request.MaxAttachments)
            {
                context.Errors.Add(new ExtractionError("package_entry_limit",
                    "EPUB contains more readable sections than the configured safety limit.", false, name));
                break;
            }
            var entryPath = ResolveZipPath(packageDirectory, item.Href!);
            var entry = FindZipEntry(archive, entryPath);
            if (entry is null) continue;
            var chapterBytes = await ReadZipEntryAsync(entry, context.Request.MaxAttachmentBytes, cancellationToken);
            var extracted = HtmlNode(entryPath, item.MediaType, "epub-chapter", DecodeText(chapterBytes));
            foreach (var section in extracted.Sections)
                sections.Add(section with
                {
                    Location = new SourceLocation(LocationKind.Structure,
                        StructurePath: $"spine[{chapter}]/{entryPath}")
                });
        }
        return new ExtractedNode(name, mimeType, relationship, sections, [], Title: title);
    }

    private static bool IsEpubTextItem(EpubManifestItem item) =>
        item.MediaType is "application/xhtml+xml" or "text/html" ||
        item.Href?.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) == true ||
        item.Href?.EndsWith(".html", StringComparison.OrdinalIgnoreCase) == true ||
        item.Href?.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<XDocument> ReadZipXmlAsync(byte[] bytes, string path, long maxBytes,
        CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        return await ReadZipXmlAsync(archive, path, maxBytes, cancellationToken);
    }

    private static async Task<XDocument> ReadZipXmlAsync(ZipArchive archive, string path, long maxBytes,
        CancellationToken cancellationToken)
    {
        var entry = FindZipEntry(archive, path) ?? throw new InvalidDataException($"Package is missing {path}.");
        var bytes = await ReadZipEntryAsync(entry, maxBytes, cancellationToken);
        return LoadXml(DecodeText(bytes));
    }

    private static ZipArchiveEntry? FindZipEntry(ZipArchive archive, string path) =>
        archive.Entries.FirstOrDefault(entry => string.Equals(
            entry.FullName.Replace('\\', '/'), path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    private static async Task<byte[]> ReadZipEntryAsync(ZipArchiveEntry entry, long maxBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length > maxBytes || entry.Length > int.MaxValue)
            throw new InvalidDataException($"Package entry exceeds the {maxBytes} byte limit.");
        await using var stream = entry.Open();
        return await ReadBoundedAsync(stream, maxBytes, cancellationToken);
    }

    private static XDocument LoadXml(string xml)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = Math.Max(1024, (long)xml.Length + 1)
            };
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("XML document is malformed.", exception);
        }
    }

    private static string OdfText(XElement element)
    {
        var output = new StringBuilder();
        AppendOdfText(element, output);
        return output.ToString();
    }

    private static void AppendOdfText(XNode node, StringBuilder output)
    {
        if (node is XText text)
        {
            output.Append(text.Value);
            return;
        }
        if (node is not XElement element) return;
        if (element.Name == TextNs + "s")
        {
            output.Append(' ', Repetition(element, TextNs + "c", 1024));
            return;
        }
        if (element.Name == TextNs + "tab")
        {
            output.Append('\t');
            return;
        }
        if (element.Name == TextNs + "line-break")
        {
            output.AppendLine();
            return;
        }
        foreach (var child in element.Nodes()) AppendOdfText(child, output);
    }

    private static string OdfCellText(XElement cell)
    {
        var paragraphs = cell.Descendants().Where(element => element.Name == TextNs + "p")
            .Select(OdfText).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (paragraphs.Length > 0) return string.Join(" ", paragraphs);
        return (string?)cell.Attribute(OfficeNs + "string-value") ??
               (string?)cell.Attribute(OfficeNs + "value") ??
               (string?)cell.Attribute(OfficeNs + "date-value") ??
               (string?)cell.Attribute(OfficeNs + "time-value") ??
               (string?)cell.Attribute(OfficeNs + "boolean-value") ?? string.Empty;
    }

    private static int Repetition(XElement element, XName attribute, int maximum = 1_048_576) =>
        int.TryParse((string?)element.Attribute(attribute), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 1, maximum)
            : 1;

    private static string ZipDirectory(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static string ResolveZipPath(string directory, string href)
    {
        var decoded = Uri.UnescapeDataString(href.Split('#', 2)[0]).Replace('\\', '/');
        return NormalizeZipPath(string.IsNullOrEmpty(directory) ? decoded : $"{directory}/{decoded}");
    }

    private static string NormalizeZipPath(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) throw new InvalidDataException("Package path escapes the container root.");
                segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.Add(segment);
            }
        }
        return string.Join('/', segments);
    }

    private static string RtfToText(string rtf)
    {
        if (!rtf.TrimStart().StartsWith("{\\rtf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("RTF header is missing.");

        var output = new StringBuilder();
        var states = new Stack<RtfState>();
        var state = new RtfState(false, 1);
        var fallback = 0;
        var binary = 0;
        for (var index = 0; index < rtf.Length; index++)
        {
            var character = rtf[index];
            if (binary > 0)
            {
                binary--;
                continue;
            }
            if (character == '{')
            {
                states.Push(state);
                continue;
            }
            if (character == '}')
            {
                if (states.Count > 0) state = states.Pop();
                continue;
            }
            if (character != '\\')
            {
                if (character is '\r' or '\n') continue;
                if (fallback > 0)
                {
                    fallback--;
                    continue;
                }
                if (!state.Ignorable) output.Append(character);
                continue;
            }
            if (++index >= rtf.Length) break;
            character = rtf[index];
            if (character is '\\' or '{' or '}')
            {
                if (fallback > 0) fallback--;
                else if (!state.Ignorable) output.Append(character);
                continue;
            }
            if (character == '*')
            {
                state = state with { Ignorable = true };
                continue;
            }
            if (character == '\'')
            {
                if (index + 2 >= rtf.Length || !byte.TryParse(rtf.AsSpan(index + 1, 2),
                        NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var encoded))
                    throw new InvalidDataException("RTF contains an invalid hexadecimal escape.");
                index += 2;
                if (fallback > 0) fallback--;
                else if (!state.Ignorable) output.Append(Windows1252.GetString([encoded]));
                continue;
            }
            if (!char.IsLetter(character))
            {
                if (!state.Ignorable && character == '~') output.Append(' ');
                continue;
            }

            var start = index;
            while (index + 1 < rtf.Length && char.IsLetter(rtf[index + 1])) index++;
            var word = rtf[start..(index + 1)];
            var sign = 1;
            if (index + 1 < rtf.Length && rtf[index + 1] == '-')
            {
                sign = -1;
                index++;
            }
            var numberStart = index + 1;
            while (index + 1 < rtf.Length && char.IsDigit(rtf[index + 1])) index++;
            int? parameter = numberStart <= index && int.TryParse(rtf[numberStart..(index + 1)], out var value)
                ? value * sign
                : null;
            if (index + 1 < rtf.Length && rtf[index + 1] == ' ') index++;

            if (RtfDestinations.Contains(word)) state = state with { Ignorable = true };
            if (word == "uc" && parameter is { } uc) state = state with { UnicodeFallback = Math.Clamp(uc, 0, 16) };
            else if (word == "u" && parameter is { } unicode)
            {
                if (!state.Ignorable) output.Append((char)(ushort)unicode);
                fallback = state.UnicodeFallback;
            }
            else if (word == "bin" && parameter is > 0) binary = parameter.Value;
            else if (!state.Ignorable)
            {
                if (word is "par" or "line") output.AppendLine();
                else if (word == "tab") output.Append('\t');
                else if (word == "emdash") output.Append('—');
                else if (word == "endash") output.Append('–');
                else if (word is "lquote" or "rquote") output.Append('\'');
                else if (word is "ldblquote" or "rdblquote") output.Append('"');
                else if (word == "bullet") output.Append('•');
            }
        }
        return output.ToString();
    }

    private static readonly HashSet<string> RtfDestinations = new(StringComparer.OrdinalIgnoreCase)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "header", "footer", "headerl",
        "headerr", "footerl", "footerr", "filetbl", "listtable", "listoverridetable", "generator", "xmlnstbl",
        "datastore", "themedata", "colorschememapping"
    };

    private readonly record struct RtfState(bool Ignorable, int UnicodeFallback);
    private sealed record EpubManifestItem(string? Id, string? Href, string? MediaType);
}
