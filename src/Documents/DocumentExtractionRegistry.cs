using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using BitMiracle.LibTiff.Classic;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Markdig;
using MCPIndexSearch.Core;
using MimeKit;
using MsgReader.Outlook;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;

namespace MCPIndexSearch.Documents;

public sealed partial class DocumentExtractionRegistry(IOcrEngine ocrEngine) : IDocumentExtractor
{
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
            var node = await ExtractStreamAsync(stream, Path.GetFileName(request.SourcePath), MimeFor(request.SourcePath), "root", 0, context, cancellationToken);
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

        var extension = ExtensionFor(name, mimeType);
        try
        {
            return extension switch
            {
                ".txt" => TextNode(name, mimeType, relationship, DecodeText(bytes), ExtractionMethod.NativeText),
                ".md" or ".markdown" => MarkdownNode(name, mimeType, relationship, DecodeText(bytes)),
                ".html" or ".htm" => HtmlNode(name, mimeType, relationship, DecodeText(bytes)),
                ".pdf" => await PdfNodeAsync(bytes, name, mimeType, relationship, depth, context, cancellationToken),
                ".docx" => await WordNodeAsync(bytes, name, mimeType, relationship, depth, context, cancellationToken),
                ".xlsx" => await SpreadsheetNodeAsync(bytes, name, mimeType, relationship, depth, context, cancellationToken),
                ".pptx" => await PresentationNodeAsync(bytes, name, mimeType, relationship, depth, context, cancellationToken),
                ".eml" => await EmlNodeAsync(bytes, name, mimeType, relationship, depth, context, cancellationToken),
                ".msg" => await MsgNodeAsync(bytes, name, mimeType, relationship, depth, context, cancellationToken),
                ".zip" or ".rar" => await ArchiveNodeAsync(bytes, name, mimeType, relationship, extension, depth, context, cancellationToken),
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tif" or ".tiff" =>
                    await ImageNodeAsync(bytes, name, mimeType, relationship, cancellationToken),
                _ => Rejected(name, mimeType, relationship, context, "unsupported_format", $"Unsupported attachment format: {extension ?? "unknown"}.")
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
        using var pdf = PdfDocument.Open(bytes);
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

            try
            {
                await _ocrEngine.EnsureAvailableAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!string.IsNullOrWhiteSpace(text))
                    sections.Add(new ExtractedSection(text, new SourceLocation(LocationKind.Page, Page: page.Number), ExtractionMethod.NativeText));
                context.Errors.Add(new ExtractionError(ErrorCode(ex),
                    $"PDF page {page.Number} needs OCR. {SafeMessage(ex)}", IsTemporary(ex), name));
                continue;
            }

            await PdfRenderGate.WaitAsync(cancellationToken);
            try
            {
                if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                    throw new PlatformNotSupportedException("PDFium rendering is supported only on the desktop target platforms.");
                await using var input = new MemoryStream(bytes, writable: false);
                await using var png = new MemoryStream();
#pragma warning disable CA1416 // Guarded above; all four supported desktop RIDs are supported by PDFtoImage.
                Conversion.SavePng(png, input, page.Number - 1, leaveOpen: true, password: null, options: new RenderOptions { Dpi = 300 });
#pragma warning restore CA1416
                var ocr = await _ocrEngine.RecognizeAsync(new OcrRequest(png.ToArray(), ".png", TimeSpan.FromSeconds(120)), cancellationToken);
                if (!string.IsNullOrWhiteSpace(ocr.Text))
                    sections.Add(new ExtractedSection(ocr.Text, new SourceLocation(LocationKind.Page, Page: page.Number), ExtractionMethod.Ocr, ocr.Confidence));
                else if (ocr.TimedOut)
                    context.Errors.Add(new ExtractionError("ocr_timeout", $"OCR timed out for PDF page {page.Number}.", true, name));
            }
            finally
            {
                PdfRenderGate.Release();
            }
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
                attachments.Add(await ExtractStreamAsync(embeddedStream, embeddedName, MimeFor(embeddedName), "pdf-embedded-file",
                    depth + 1, context, cancellationToken));
            }
        }

        return new ExtractedNode(name, mimeType, relationship, sections, attachments);
    }

    private async Task<ExtractedNode> ImageNodeAsync(byte[] bytes, string name, string? mimeType, string relationship,
        CancellationToken cancellationToken)
    {
        await _ocrEngine.EnsureAvailableAsync(cancellationToken);

        if (Path.GetExtension(name).Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".tiff", StringComparison.OrdinalIgnoreCase))
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
                if (width <= 0 || height <= 0 || (long)width * height > 200_000_000)
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

        var ocr = await _ocrEngine.RecognizeAsync(new OcrRequest(bytes, Path.GetExtension(name), TimeSpan.FromSeconds(120)), cancellationToken);
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
        foreach (var element in document.QuerySelectorAll("script,style,template,noscript,iframe,object,embed,svg,canvas"))
            element.Remove();
        return TextNode(name, mimeType, relationship, document.Body?.TextContent ?? document.DocumentElement.TextContent, ExtractionMethod.Html);
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

        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Windows1252.GetString(bytes); }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, long maxBytes, CancellationToken cancellationToken)
    {
        if (source.CanSeek && source.Length > maxBytes)
            throw new InvalidDataException($"Attachment exceeds the {maxBytes} byte limit.");
        await using var output = new MemoryStream(source.CanSeek ? checked((int)Math.Min(source.Length, int.MaxValue)) : 0);
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

    private static string? ExtensionFor(string name, string? mimeType)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (SupportedContent.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return extension;
        return mimeType?.ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
            "application/zip" or "application/x-zip" or "application/x-zip-compressed" => ".zip",
            "application/vnd.rar" or "application/x-rar" or "application/x-rar-compressed" => ".rar",
            "message/rfc822" => ".eml",
            "text/plain" => ".txt",
            "text/html" => ".html",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/tiff" => ".tiff",
            _ => string.IsNullOrWhiteSpace(extension) ? null : extension
        };
    }

    private static string? MimeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".eml" => "message/rfc822",
        ".msg" => "application/vnd.ms-outlook",
        ".zip" => "application/zip",
        ".rar" => "application/vnd.rar",
        ".html" or ".htm" => "text/html",
        ".md" or ".markdown" => "text/markdown",
        ".txt" => "text/plain",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".tif" or ".tiff" => "image/tiff",
        ".webp" => "image/webp",
        _ => null
    };

    private static ExtractedNode Rejected(string name, string? mimeType, string relationship, ExpansionContext context, string code, string message)
    {
        context.Errors.Add(new ExtractionError(code, message, false, name));
        return new ExtractedNode(name, mimeType, relationship, [], [], code);
    }

    private static string ErrorCode(Exception ex) => ex switch
    {
        McpIndexException mcp => mcp.Code,
        UnauthorizedAccessException => "access_denied",
        IOException => "io_error",
        InvalidDataException => "malformed_document",
        NotSupportedException => "unsupported_format",
        _ => "extraction_failed"
    };

    private static bool IsTemporary(Exception ex) => ex is McpIndexException mcp ? mcp.Retryable : ex is IOException;
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
