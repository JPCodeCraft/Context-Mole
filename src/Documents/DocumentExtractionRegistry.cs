using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;
using System.Text.RegularExpressions;

using AngleSharp.Dom;
using AngleSharp.Html.Parser;

using BitMiracle.LibTiff.Classic;

using ContextMole.Core;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

using Markdig;

using MimeKit;

using MsgReader.Outlook;

using PDFtoImage;
using PDFtoImage.Exceptions;

using SkiaSharp;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace ContextMole.Documents;

public sealed partial class DocumentExtractionRegistry(IOcrEngine ocrEngine) : IDocumentExtractor
{
    private const long MaxRasterPixels = 25_000_000;
    private const int PdfOcrDpi = 300;
    private const int MinimumPdfOcrDpi = 72;
    private static readonly Encoding Windows1252;
    private static readonly SemaphoreSlim PdfRenderGate = new(1, 1);
    private readonly IOcrEngine _ocrEngine = ocrEngine;

    static DocumentExtractionRegistry()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Windows1252 = Encoding.GetEncoding(1252);
    }

    public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;

    public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken)
    {
        var context = new ExpansionContext(request);
        try
        {
            await using var stream = new FileStream(request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var node = await ExtractStreamAsync(stream, Path.GetFileName(request.SourcePath),
                SupportedContent.MimeTypeForPath(request.SourcePath), "root", 0, context, cancellationToken);
            return new ExtractionResult(node, context.Errors);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ExtractionResult.Failure(Path.GetFileName(request.SourcePath), ErrorCode(ex), SafeMessage(ex), IsTemporary(ex));
        }
    }

    private async Task<ExtractedNode> ExtractStreamAsync(
        Stream source,
        string name,
        string? mimeType,
        string relationship,
        int depth,
        ExpansionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > context.Request.MaxDepth)
            return Rejected(name, mimeType, relationship, context, "attachment_depth_limit", "Attachment depth exceeds the limit.");

        byte[] bytes;
        try
        {
            bytes = await ReadBoundedAsync(source,
                depth == 0 ? context.Request.MaxAggregateBytes : context.Request.MaxAttachmentBytes, cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            return Rejected(name, mimeType, relationship, context, "attachment_size_limit", ex.Message);
        }

        if (depth > 0) context.AggregateBytes += bytes.LongLength;
        if (depth > 0 && context.AggregateBytes > context.Request.MaxAggregateBytes)
            return Rejected(name, mimeType, relationship, context, "attachment_aggregate_limit", "Expanded attachment bytes exceed the per-document limit.");

        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        if (!context.Hashes.Add(digest))
            return Rejected(name, mimeType, relationship, context, "attachment_cycle", "Duplicate attachment content was skipped to prevent a cycle.");

        var format = ContentFormatFor(bytes, name, mimeType);
        try
        {
            return format?.Kind switch
            {
                ContentFormatKind.PlainText => TextNode(name, mimeType, relationship, DecodeText(bytes), ExtractionMethod.NativeText),
                ContentFormatKind.Markdown => MarkdownNode(name, mimeType, relationship, DecodeText(bytes)),
                ContentFormatKind.Html => HtmlNode(name, mimeType, relationship, DecodeText(bytes)),
                ContentFormatKind.Pdf => await PdfNodeAsync(bytes, name, mimeType, relationship, depth, context, cancellationToken),
                ContentFormatKind.WordOpenXml => await WordNodeAsync(bytes, name, mimeType, relationship, depth, context, cancellationToken),
                ContentFormatKind.SpreadsheetOpenXml => await SpreadsheetNodeAsync(bytes, name, mimeType, relationship, depth, context, cancellationToken),
                ContentFormatKind.PresentationOpenXml => await PresentationNodeAsync(bytes, name, mimeType, relationship, depth, context, cancellationToken),
                ContentFormatKind.DelimitedText => DelimitedTextNode(bytes, name, mimeType, relationship, format.Extension),
                ContentFormatKind.Json => JsonNode(bytes, name, mimeType, relationship),
                ContentFormatKind.JsonLines => JsonLinesNode(bytes, name, mimeType, relationship),
                ContentFormatKind.Xml => XmlNode(bytes, name, mimeType, relationship),
                ContentFormatKind.RichText => RichTextNode(bytes, name, mimeType, relationship),
                ContentFormatKind.OpenDocumentText => await OpenDocumentTextNodeAsync(bytes, name, mimeType, relationship, context, cancellationToken),
                ContentFormatKind.OpenDocumentSpreadsheet => await OpenDocumentSpreadsheetNodeAsync(bytes, name, mimeType, relationship, context, cancellationToken),
                ContentFormatKind.OpenDocumentPresentation => await OpenDocumentPresentationNodeAsync(bytes, name, mimeType, relationship, context, cancellationToken),
                ContentFormatKind.Epub => await EpubNodeAsync(bytes, name, mimeType, relationship, context, cancellationToken),
                ContentFormatKind.Eml => await EmlNodeAsync(bytes, name, mimeType, relationship, depth, context, cancellationToken),
                ContentFormatKind.Mhtml => await MhtmlNodeAsync(bytes, name, mimeType, relationship, depth, context, cancellationToken),
                ContentFormatKind.Msg => await MsgNodeAsync(bytes, name, mimeType, relationship, depth, context, cancellationToken),
                ContentFormatKind.Archive => await ArchiveNodeAsync(bytes, name, mimeType, relationship, format.Extension, depth, context, cancellationToken),
                ContentFormatKind.Image => await ImageNodeAsync(bytes, name, mimeType, relationship, format.Extension, cancellationToken),
                _ => UnsupportedNode(name, mimeType, relationship, context,
                    SupportedContent.ExtensionForPath(name) ?? Path.GetExtension(name))
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Errors.Add(new ExtractionError(ErrorCode(ex), SafeMessage(ex), IsTemporary(ex), name));
            return ExtractedNode.Empty(name, relationship) with { MimeType = mimeType, Status = ErrorCode(ex) };
        }
        finally
        {
            context.Hashes.Remove(digest);
        }
    }

    private async Task<ExtractedNode> PdfNodeAsync(byte[] bytes, string name, string? mimeType, string relationship,
        int depth, ExpansionContext context, CancellationToken cancellationToken)
    {
        var sections = new List<ExtractedSection>();
        var attachments = new List<ExtractedNode>();
        using var pdf = PdfDocument.Open(bytes, new ParsingOptions { SkipMissingFonts = true });
        var title = MetadataTitle(pdf.Information.Title);
        foreach (var page in pdf.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = page.Text ?? string.Empty;
            var normalized = TextNormalization.ForSearch(text, dehyphenateLineBreaks: true);
            var alphanumerics = normalized.Count(char.IsLetterOrDigit);
            var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (alphanumerics >= 80 && tokens >= 10)
            {
                sections.Add(new ExtractedSection(text, new SourceLocation(LocationKind.Page, Page: page.Number), ExtractionMethod.NativeText));
                continue;
            }

            var renderDpi = SafePdfRenderDpi((double)page.Width, (double)page.Height);
            if (renderDpi is null)
            {
                if (!string.IsNullOrWhiteSpace(text))
                    sections.Add(new ExtractedSection(text, new SourceLocation(LocationKind.Page, Page: page.Number), ExtractionMethod.NativeText));
                context.Errors.Add(new ExtractionError("image_dimensions_limit",
                    $"PDF page {page.Number} is too large to render safely for OCR.", false, name));
                continue;
            }

            byte[] renderedPage;
            await PdfRenderGate.WaitAsync(cancellationToken);
            try
            {
                if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                    throw new PlatformNotSupportedException("PDFium rendering is supported only on the desktop target platforms.");
                await using var input = new MemoryStream(bytes, writable: false);
                await using var png = new MemoryStream();
#pragma warning disable CA1416 // Guarded above; all four supported desktop RIDs are supported by PDFtoImage.
                Conversion.SavePng(png, input, page.Number - 1, leaveOpen: true, password: null,
                    options: new RenderOptions { Dpi = renderDpi.Value });
#pragma warning restore CA1416
                renderedPage = png.ToArray();
            }
            finally
            {
                PdfRenderGate.Release();
            }

            OcrResult ocr;
            try
            {
                ocr = await _ocrEngine.RecognizeAsync(
                    new OcrRequest(renderedPage, ".png", TimeSpan.FromSeconds(120)), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!string.IsNullOrWhiteSpace(text))
                    sections.Add(new ExtractedSection(text,
                        new SourceLocation(LocationKind.Page, Page: page.Number), ExtractionMethod.NativeText));
                context.Errors.Add(new ExtractionError(ErrorCode(ex),
                    $"PDF page {page.Number} needs OCR. {SafeMessage(ex)}", IsTemporary(ex), name));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(ocr.Text))
                sections.Add(new ExtractedSection(ocr.Text,
                    new SourceLocation(LocationKind.Page, Page: page.Number), ExtractionMethod.Ocr, ocr.Confidence));
            else if (ocr.TimedOut)
                context.Errors.Add(new ExtractionError("ocr_timeout", $"OCR timed out for PDF page {page.Number}.", true, name));
        }

        if (pdf.Advanced.TryGetEmbeddedFiles(out var embeddedFiles))
        {
            var ordinal = 0;
            foreach (var embedded in embeddedFiles)
            {
                if (!context.TryAddAttachment(name))
                    break;
                var embeddedName = string.IsNullOrWhiteSpace(embedded.Name) ? $"embedded-{++ordinal}" : embedded.Name;
                await using var embeddedStream = new MemoryStream(embedded.Bytes.ToArray(), writable: false);
                attachments.Add(await ExtractStreamAsync(embeddedStream, embeddedName,
                    SupportedContent.MimeTypeForPath(embeddedName), "pdf-embedded-file",
                    depth + 1, context, cancellationToken));
            }
        }

        return new ExtractedNode(name, mimeType, relationship, sections, attachments, Title: title);
    }

    private static int? SafePdfRenderDpi(double widthPoints, double heightPoints)
    {
        widthPoints = Math.Abs(widthPoints);
        heightPoints = Math.Abs(heightPoints);
        var area = widthPoints * heightPoints;
        if (!double.IsFinite(area) || area <= 0) return null;

        var safeDpi = (int)Math.Min(PdfOcrDpi,
            Math.Floor(72d * Math.Sqrt(MaxRasterPixels / area)));
        for (var dpi = safeDpi; dpi >= MinimumPdfOcrDpi; dpi--)
        {
            var widthPixels = Math.Ceiling(widthPoints * dpi / 72d);
            var heightPixels = Math.Ceiling(heightPoints * dpi / 72d);
            if (widthPixels * heightPixels <= MaxRasterPixels) return dpi;
        }

        return null;
    }

    private async Task<ExtractedNode> ImageNodeAsync(byte[] bytes, string name, string? mimeType, string relationship,
        string extension, CancellationToken cancellationToken)
    {
        if (extension is ".tif" or ".tiff")
        {
            var tiffSections = new List<ExtractedSection>();
            var frame = 0;
            using var input = new MemoryStream(bytes, writable: false);
            using var tiff = Tiff.ClientOpen(name, "r", input, new TiffStream()) ?? throw new InvalidDataException("Unable to open TIFF image.");
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                frame++;
                var width = tiff.GetField(TiffTag.IMAGEWIDTH)?[0].ToInt() ?? 0;
                var height = tiff.GetField(TiffTag.IMAGELENGTH)?[0].ToInt() ?? 0;
                if (width <= 0 || height <= 0 || (long)width * height > MaxRasterPixels)
                    throw new InvalidDataException("TIFF frame dimensions are invalid or exceed the safety limit.");
                var raster = new int[width * height];
                if (!tiff.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT, stopOnError: true))
                    throw new InvalidDataException($"Unable to decode TIFF frame {frame}.");
                using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var rgba = unchecked((uint)raster[(y * width) + x]);
                        bitmap.SetPixel(x, y, new SKColor((byte)rgba, (byte)(rgba >> 8), (byte)(rgba >> 16), (byte)(rgba >> 24)));
                    }
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                var frameOcr = await _ocrEngine.RecognizeAsync(new OcrRequest(encoded.ToArray(), ".png", TimeSpan.FromSeconds(120)), cancellationToken);
                if (!string.IsNullOrWhiteSpace(frameOcr.Text))
                    tiffSections.Add(new ExtractedSection(frameOcr.Text,
                        new SourceLocation(LocationKind.ImageFrame, Page: frame, ImageFrame: frame), ExtractionMethod.Ocr, frameOcr.Confidence));
            } while (tiff.ReadDirectory());
            return new ExtractedNode(name, mimeType, relationship, tiffSections, []);
        }

        ValidateRasterImage(bytes);
        var ocr = await _ocrEngine.RecognizeAsync(new OcrRequest(bytes, extension, TimeSpan.FromSeconds(120)), cancellationToken);
        var sections = string.IsNullOrWhiteSpace(ocr.Text)
            ? Array.Empty<ExtractedSection>()
            : [new ExtractedSection(ocr.Text, new SourceLocation(LocationKind.ImageFrame, Page: 1, ImageFrame: 1), ExtractionMethod.Ocr, ocr.Confidence)];
        return new ExtractedNode(name, mimeType, relationship, sections, []);
    }

    private static ExtractedNode TextNode(string name, string? mimeType, string relationship, string text, ExtractionMethod method) =>
        new(name, mimeType, relationship,
            string.IsNullOrWhiteSpace(text) ? [] : [new ExtractedSection(text, new SourceLocation(LocationKind.Document), method)], []);

    private static ExtractedNode MarkdownNode(string name, string? mimeType, string relationship, string markdown)
    {
        var pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();
        var text = Markdown.ToPlainText(markdown, pipeline);
        return TextNode(name, mimeType, relationship, text, ExtractionMethod.Markdown);
    }

    private static ExtractedNode HtmlNode(string name, string? mimeType, string relationship, string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        var title = MetadataTitle(document.Title);
        foreach (var element in document.QuerySelectorAll("script,style,template,noscript,iframe,object,embed,svg,canvas"))
            element.Remove();
        return TextNode(name, mimeType, relationship,
            document.Body?.TextContent ?? document.DocumentElement.TextContent, ExtractionMethod.Html) with
        {
            Title = title
        };
    }

    private static string? MetadataTitle(string? value)
    {
        var title = TextNormalization.ForDisplay(value);
        return title.Length == 0 ? null : title.Length <= 500 ? title : title[..500];
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            return Encoding.UTF8.GetString(bytes, Encoding.UTF8.Preamble.Length, bytes.Length - Encoding.UTF8.Preamble.Length);
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
            return Encoding.Unicode.GetString(bytes, Encoding.Unicode.Preamble.Length, bytes.Length - Encoding.Unicode.Preamble.Length);
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
            return Encoding.BigEndianUnicode.GetString(bytes, Encoding.BigEndianUnicode.Preamble.Length, bytes.Length - Encoding.BigEndianUnicode.Preamble.Length);
        if (bytes.AsSpan().StartsWith(Encoding.UTF32.Preamble))
            return Encoding.UTF32.GetString(bytes, Encoding.UTF32.Preamble.Length, bytes.Length - Encoding.UTF32.Preamble.Length);

        // Invalid UTF-8 is a normal encoding-detection outcome, not an exceptional parsing failure.
        // Validate first so indexing legacy Windows-1252 files does not flood first-chance exception telemetry.
        return Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Windows1252.GetString(bytes);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, long maxBytes, CancellationToken cancellationToken)
    {
        if (source.CanSeek)
        {
            var remaining = source.Length - source.Position;
            if (remaining < 0 || remaining > maxBytes || remaining > int.MaxValue)
                throw new InvalidDataException($"Attachment exceeds the {maxBytes} byte limit.");
            var bytes = GC.AllocateUninitializedArray<byte>((int)remaining);
            await source.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }

        await using var output = new MemoryStream();
        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException($"Attachment exceeds the {maxBytes} byte limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    private static ContentFormatDescriptor? ContentFormatFor(byte[] bytes, string name, string? mimeType)
    {
        if (LooksLikeHtml(bytes))
            return SupportedContent.FindByExtension(".html");
        if (bytes.AsSpan().StartsWith("%PDF-"u8))
            return SupportedContent.FindByExtension(".pdf");
        return SupportedContent.Resolve(name, mimeType);
    }

    private static bool LooksLikeHtml(byte[] bytes)
    {
        var length = Math.Min(bytes.Length, 512);
        if (length == 0) return false;
        var prefix = Encoding.UTF8.GetString(bytes, 0, length)
            .TrimStart('\uFEFF', '\0', ' ', '\t', '\r', '\n');
        return prefix.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith("<head", StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith("<body", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateRasterImage(byte[] bytes)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
                (long)codec.Info.Width * codec.Info.Height > MaxRasterPixels)
                throw new InvalidDataException("The image is malformed or its dimensions exceed the safety limit.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("The image data is malformed or does not match a supported raster format.", exception);
        }
    }

    private static ExtractedNode Rejected(string name, string? mimeType, string relationship, ExpansionContext context, string code, string message)
    {
        context.Errors.Add(new ExtractionError(code, message, false, name));
        return new ExtractedNode(name, mimeType, relationship, [], [], code);
    }

    private static ExtractedNode UnsupportedNode(string name, string? mimeType, string relationship,
        ExpansionContext context, string? extension)
    {
        const string code = "unsupported_format";
        if (string.Equals(relationship, "root", StringComparison.Ordinal))
            context.Errors.Add(new ExtractionError(code, $"Unsupported document format: {extension ?? "unknown"}.", false, name));
        return new ExtractedNode(name, mimeType, relationship, [], [], code);
    }

    private static string ErrorCode(Exception ex) => ex switch
    {
        ContextMoleException mcp => mcp.Code,
        UnauthorizedAccessException => "access_denied",
        IOException => "io_error",
        PdfDocumentFormatException or PdfInvalidFormatException or PdfCannotOpenFileException => "malformed_document",
        PdfPasswordProtectedException or PdfDocumentEncryptedException => "encrypted_document",
        InvalidDataException => "malformed_document",
        NotSupportedException => "unsupported_format",
        _ => "extraction_failed"
    };

    private static bool IsTemporary(Exception ex) => ex is ContextMoleException mcp ? mcp.Retryable : ex is IOException;
    private static string SafeMessage(Exception ex) => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;

    private sealed class ExpansionContext(ExtractionRequest request)
    {
        public ExtractionRequest Request { get; } = request;
        public List<ExtractionError> Errors { get; } = [];
        public HashSet<string> Hashes { get; } = new(StringComparer.Ordinal);
        public long AggregateBytes { get; set; }
        public int AttachmentCount { get; private set; }

        public bool TryAddAttachment(string parentName)
        {
            if (++AttachmentCount <= Request.MaxAttachments)
                return true;
            Errors.Add(new ExtractionError("attachment_count_limit", "Attachment count exceeds the per-document limit.", false, parentName));
            return false;
        }
    }
}
