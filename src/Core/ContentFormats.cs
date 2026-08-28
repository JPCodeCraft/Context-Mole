using System.Collections.ObjectModel;

namespace ContextMole.Core;

public enum ContentFormatKind
{
    PlainText,
    Markdown,
    Html,
    Pdf,
    WordOpenXml,
    SpreadsheetOpenXml,
    PresentationOpenXml,
    DelimitedText,
    Json,
    JsonLines,
    Xml,
    RichText,
    OpenDocumentText,
    OpenDocumentSpreadsheet,
    OpenDocumentPresentation,
    Epub,
    Image,
    Eml,
    Mhtml,
    Msg,
    Archive
}

public sealed record ContentFormatDescriptor(
    string Extension,
    string MimeType,
    ContentFormatKind Kind,
    IReadOnlyList<string> MimeAliases);

public static class SupportedContent
{
    private static readonly ContentFormatDescriptor[] CatalogData =
    [
        Format(".pdf", "application/pdf", ContentFormatKind.Pdf),

        Format(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ContentFormatKind.WordOpenXml),
        Format(".docm", "application/vnd.ms-word.document.macroenabled.12", ContentFormatKind.WordOpenXml),
        Format(".dotx", "application/vnd.openxmlformats-officedocument.wordprocessingml.template", ContentFormatKind.WordOpenXml),
        Format(".dotm", "application/vnd.ms-word.template.macroenabled.12", ContentFormatKind.WordOpenXml),
        Format(".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ContentFormatKind.SpreadsheetOpenXml),
        Format(".xlsm", "application/vnd.ms-excel.sheet.macroenabled.12", ContentFormatKind.SpreadsheetOpenXml),
        Format(".xltx", "application/vnd.openxmlformats-officedocument.spreadsheetml.template", ContentFormatKind.SpreadsheetOpenXml),
        Format(".xltm", "application/vnd.ms-excel.template.macroenabled.12", ContentFormatKind.SpreadsheetOpenXml),
        Format(".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", ContentFormatKind.PresentationOpenXml),
        Format(".pptm", "application/vnd.ms-powerpoint.presentation.macroenabled.12", ContentFormatKind.PresentationOpenXml),
        Format(".ppsx", "application/vnd.openxmlformats-officedocument.presentationml.slideshow", ContentFormatKind.PresentationOpenXml),
        Format(".ppsm", "application/vnd.ms-powerpoint.slideshow.macroenabled.12", ContentFormatKind.PresentationOpenXml),
        Format(".potx", "application/vnd.openxmlformats-officedocument.presentationml.template", ContentFormatKind.PresentationOpenXml),
        Format(".potm", "application/vnd.ms-powerpoint.template.macroenabled.12", ContentFormatKind.PresentationOpenXml),

        Format(".odt", "application/vnd.oasis.opendocument.text", ContentFormatKind.OpenDocumentText),
        Format(".ods", "application/vnd.oasis.opendocument.spreadsheet", ContentFormatKind.OpenDocumentSpreadsheet),
        Format(".odp", "application/vnd.oasis.opendocument.presentation", ContentFormatKind.OpenDocumentPresentation),
        Format(".rtf", "application/rtf", ContentFormatKind.RichText, "text/rtf"),

        Format(".txt", "text/plain", ContentFormatKind.PlainText),
        Format(".log", "text/plain", ContentFormatKind.PlainText),
        Format(".rst", "text/x-rst", ContentFormatKind.PlainText),
        Format(".adoc", "text/asciidoc", ContentFormatKind.PlainText),
        Format(".tex", "application/x-tex", ContentFormatKind.PlainText),
        Format(".md", "text/markdown", ContentFormatKind.Markdown),
        Format(".markdown", "text/markdown", ContentFormatKind.Markdown),
        Format(".csv", "text/csv", ContentFormatKind.DelimitedText),
        Format(".tsv", "text/tab-separated-values", ContentFormatKind.DelimitedText),
        Format(".json", "application/json", ContentFormatKind.Json, "text/json"),
        Format(".jsonl", "application/x-ndjson", ContentFormatKind.JsonLines, "application/jsonl", "application/ndjson"),
        Format(".xml", "application/xml", ContentFormatKind.Xml, "text/xml"),
        Format(".yaml", "application/yaml", ContentFormatKind.PlainText, "text/yaml", "application/x-yaml", "text/x-yaml"),
        Format(".yml", "application/yaml", ContentFormatKind.PlainText),
        Format(".toml", "application/toml", ContentFormatKind.PlainText),

        Format(".html", "text/html", ContentFormatKind.Html),
        Format(".htm", "text/html", ContentFormatKind.Html),
        Format(".mht", "multipart/related", ContentFormatKind.Mhtml, "application/x-mimearchive"),
        Format(".mhtml", "multipart/related", ContentFormatKind.Mhtml),
        Format(".epub", "application/epub+zip", ContentFormatKind.Epub),

        Format(".png", "image/png", ContentFormatKind.Image),
        Format(".jpg", "image/jpeg", ContentFormatKind.Image),
        Format(".jpeg", "image/jpeg", ContentFormatKind.Image),
        Format(".bmp", "image/bmp", ContentFormatKind.Image, "image/x-ms-bmp"),
        Format(".gif", "image/gif", ContentFormatKind.Image),
        Format(".webp", "image/webp", ContentFormatKind.Image),
        Format(".tif", "image/tiff", ContentFormatKind.Image),
        Format(".tiff", "image/tiff", ContentFormatKind.Image),

        Format(".eml", "message/rfc822", ContentFormatKind.Eml),
        Format(".msg", "application/vnd.ms-outlook", ContentFormatKind.Msg),

        Format(".zip", "application/zip", ContentFormatKind.Archive, "application/x-zip", "application/x-zip-compressed"),
        Format(".rar", "application/vnd.rar", ContentFormatKind.Archive, "application/x-rar", "application/x-rar-compressed"),
        Format(".7z", "application/x-7z-compressed", ContentFormatKind.Archive),
        Format(".tar", "application/x-tar", ContentFormatKind.Archive),
        Format(".gz", "application/gzip", ContentFormatKind.Archive, "application/x-gzip"),
        Format(".tgz", "application/gzip", ContentFormatKind.Archive),
        Format(".tar.gz", "application/gzip", ContentFormatKind.Archive)
    ];

    private static readonly ReadOnlyCollection<ContentFormatDescriptor> CatalogView = Array.AsReadOnly(CatalogData);
    private static readonly Dictionary<string, ContentFormatDescriptor> ByExtension =
        CatalogData.ToDictionary(format => format.Extension, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ContentFormatDescriptor> ByMimeType = BuildMimeMap();
    private static readonly string[] MultiPartExtensions =
        CatalogData.Select(format => format.Extension).Where(extension => extension.Count(character => character == '.') > 1)
            .OrderByDescending(extension => extension.Length).ToArray();

    public static IReadOnlyList<ContentFormatDescriptor> Catalog => CatalogView;
    public static ReadOnlyCollection<string> Extensions { get; } =
        Array.AsReadOnly(CatalogData.Select(format => format.Extension).ToArray());

    public static bool IsSupported(string path) => FindByPath(path) is not null;

    public static ContentFormatDescriptor? Resolve(string name, string? mimeType = null) =>
        FindByPath(name) ?? FindByMimeType(mimeType);

    public static ContentFormatDescriptor? FindByPath(string path)
    {
        var extension = ExtensionForPath(path);
        return extension is not null && ByExtension.TryGetValue(extension, out var format) ? format : null;
    }

    public static ContentFormatDescriptor? FindByExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        var normalized = extension.StartsWith('.') ? extension : "." + extension;
        return ByExtension.TryGetValue(normalized, out var format) ? format : null;
    }

    public static ContentFormatDescriptor? FindByMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return null;
        var normalized = mimeType.Split(';', 2)[0].Trim();
        return ByMimeType.TryGetValue(normalized, out var format) ? format : null;
    }

    public static string? ExtensionForPath(string path)
    {
        var fileName = Path.GetFileName(path);
        foreach (var extension in MultiPartExtensions)
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return extension;

        var ordinary = Path.GetExtension(fileName);
        return string.IsNullOrWhiteSpace(ordinary) ? null : ordinary.ToLowerInvariant();
    }

    public static string? ExtensionForMimeType(string? mimeType) => FindByMimeType(mimeType)?.Extension;
    public static string? MimeTypeForPath(string path) => FindByPath(path)?.MimeType;

    public static bool IsValidExtensionFilter(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return false;
        var normalized = extension.StartsWith('.') ? extension : "." + extension;
        if (normalized.Length is < 2 or > 32) return false;
        var segments = normalized[1..].Split('.');
        return segments.All(segment => segment.Length > 0 && segment.All(char.IsLetterOrDigit));
    }

    private static ContentFormatDescriptor Format(string extension, string mimeType, ContentFormatKind kind,
        params string[] mimeAliases) => new(extension, mimeType, kind, mimeAliases);

    private static Dictionary<string, ContentFormatDescriptor> BuildMimeMap()
    {
        var result = new Dictionary<string, ContentFormatDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var format in CatalogData)
        {
            result.TryAdd(format.MimeType, format);
            foreach (var alias in format.MimeAliases)
                result.TryAdd(alias, format);
        }
        return result;
    }
}
