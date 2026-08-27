using System.Globalization;
using System.Text;

using AngleSharp.Dom;
using AngleSharp.Html.Parser;

using ContextMole.Core;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

using MimeKit;

using MsgReader.Outlook;

using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ContextMole.Documents;

public sealed partial class DocumentExtractionRegistry
{
    private async Task<ExtractedNode> WordNodeAsync(byte[] bytes, string name, string? mimeType, string relationship,
        int depth, ExpansionContext context, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var document = WordprocessingDocument.Open(input, false);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("DOCX has no main document part.");
        var body = main.Document?.Body ?? throw new InvalidDataException("DOCX has no document body.");
        var sections = new List<ExtractedSection>();
        var paragraphNumber = 0;
        var tableNumber = 0;
        string? currentHeading = null;

        foreach (var element in body.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (element)
            {
                case W.Paragraph paragraph:
                    {
                        paragraphNumber++;
                        var text = OpenXmlText(paragraph);
                        if (string.IsNullOrWhiteSpace(text)) break;
                        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                        if (style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true)
                            currentHeading = text;
                        sections.Add(new ExtractedSection(text,
                            new SourceLocation(LocationKind.Structure, StructurePath: $"document/paragraph[{paragraphNumber}]"),
                            ExtractionMethod.NativeText, Heading: currentHeading));
                        break;
                    }
                case W.Table table:
                    {
                        tableNumber++;
                        var rows = table.Elements<W.TableRow>()
                            .Select(row => string.Join("\t", row.Elements<W.TableCell>().Select(OpenXmlText)))
                            .Where(row => !string.IsNullOrWhiteSpace(row));
                        var text = string.Join(Environment.NewLine, rows);
                        if (!string.IsNullOrWhiteSpace(text))
                            sections.Add(new ExtractedSection(text,
                                new SourceLocation(LocationKind.Structure, StructurePath: $"document/table[{tableNumber}]"),
                                ExtractionMethod.NativeText, Heading: currentHeading));
                        break;
                    }
            }
        }

        var headerNumber = 0;
        foreach (var header in main.HeaderParts)
            if (header.Header is { } headerRoot) AddPartText(sections, headerRoot, $"header[{++headerNumber}]");
        var footerNumber = 0;
        foreach (var footer in main.FooterParts)
            if (footer.Footer is { } footerRoot) AddPartText(sections, footerRoot, $"footer[{++footerNumber}]");
        if (main.FootnotesPart?.Footnotes is { } footnotes)
            AddPartText(sections, footnotes, "footnotes");
        if (main.EndnotesPart?.Endnotes is { } endnotes)
            AddPartText(sections, endnotes, "endnotes");
        if (main.WordprocessingCommentsPart?.Comments is { } comments)
            AddPartText(sections, comments, "comments");

        var altOrdinal = 0;
        foreach (var property in main.Document.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>())
        {
            var alt = string.Join(" ", new[] { property.Title?.Value, property.Description?.Value }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!string.IsNullOrWhiteSpace(alt))
                sections.Add(new ExtractedSection(alt,
                    new SourceLocation(LocationKind.Structure, StructurePath: $"document/alt-text[{++altOrdinal}]"), ExtractionMethod.NativeText));
        }

        var attachments = await ExtractPackageAttachmentsAsync(main, name, depth, context, cancellationToken);
        return new ExtractedNode(name, mimeType, relationship, sections, attachments);
    }

    private async Task<ExtractedNode> SpreadsheetNodeAsync(byte[] bytes, string name, string? mimeType, string relationship,
        int depth, ExpansionContext context, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var document = SpreadsheetDocument.Open(input, false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("XLSX has no workbook part.");
        var workbook = workbookPart.Workbook ?? throw new InvalidDataException("XLSX has no workbook XML.");
        var shared = workbookPart.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>().Select(OpenXmlText).ToArray() ?? [];
        var sections = new List<ExtractedSection>();
        var sheetNumber = 0;

        foreach (var sheet in workbook.Sheets?.Elements<Sheet>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            sheetNumber++;
            if (sheet.Id?.Value is not { } relationId || workbookPart.GetPartById(relationId) is not WorksheetPart worksheetPart)
                continue;
            var sheetName = sheet.Name?.Value ?? $"Sheet{sheetNumber}";
            var worksheet = worksheetPart.Worksheet ?? throw new InvalidDataException($"XLSX sheet {sheetName} has no worksheet XML.");
            var rows = worksheet.Descendants<Row>().ToArray();
            foreach (var row in rows)
            {
                var values = new List<string>();
                string? first = null;
                string? last = null;
                foreach (var cell in row.Elements<Cell>())
                {
                    var reference = cell.CellReference?.Value;
                    first ??= reference;
                    last = reference;
                    var value = CellText(cell, shared);
                    if (!string.IsNullOrWhiteSpace(value))
                        values.Add($"{reference}: {value}");
                }
                if (values.Count == 0) continue;
                sections.Add(new ExtractedSection(string.Join("\t", values),
                    new SourceLocation(LocationKind.Sheet, Sheet: sheetName, CellRange: first == last ? first : $"{first}:{last}"),
                    ExtractionMethod.NativeText));
            }

            if (worksheetPart.WorksheetCommentsPart?.Comments is { } comments)
            {
                foreach (var comment in comments.CommentList?.Elements<Comment>() ?? [])
                {
                    var reference = comment.Reference?.Value;
                    var text = OpenXmlText(comment);
                    if (!string.IsNullOrWhiteSpace(text))
                        sections.Add(new ExtractedSection(text,
                            new SourceLocation(LocationKind.Sheet, Sheet: sheetName, CellRange: reference,
                                StructurePath: "comment"), ExtractionMethod.NativeText));
                }
            }
            if (worksheetPart.DrawingsPart?.WorksheetDrawing is { } drawing)
            {
                var drawingText = OpenXmlText(drawing);
                if (!string.IsNullOrWhiteSpace(drawingText))
                    sections.Add(new ExtractedSection(drawingText,
                        new SourceLocation(LocationKind.Sheet, Sheet: sheetName, StructurePath: "drawing-text"),
                        ExtractionMethod.NativeText));
            }
        }

        var attachments = await ExtractPackageAttachmentsAsync(workbookPart, name, depth, context, cancellationToken);
        return new ExtractedNode(name, mimeType, relationship, sections, attachments);
    }

    private async Task<ExtractedNode> PresentationNodeAsync(byte[] bytes, string name, string? mimeType, string relationship,
        int depth, ExpansionContext context, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var document = PresentationDocument.Open(input, false);
        var presentationPart = document.PresentationPart ?? throw new InvalidDataException("PPTX has no presentation part.");
        var presentation = presentationPart.Presentation ?? throw new InvalidDataException("PPTX has no presentation XML.");
        var sections = new List<ExtractedSection>();
        var slideNumber = 0;
        foreach (var slideId in presentation.SlideIdList?.Elements<P.SlideId>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            slideNumber++;
            if (slideId.RelationshipId?.Value is not { } relationId || presentationPart.GetPartById(relationId) is not SlidePart slidePart)
                continue;
            var slide = slidePart.Slide ?? throw new InvalidDataException($"PPTX slide {slideNumber} has no slide XML.");
            var text = string.Join(Environment.NewLine,
                slide.Descendants<A.Paragraph>().Select(OpenXmlText).Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!string.IsNullOrWhiteSpace(text))
                sections.Add(new ExtractedSection(text, new SourceLocation(LocationKind.Slide, Slide: slideNumber), ExtractionMethod.NativeText));

            if (slidePart.NotesSlidePart?.NotesSlide is { } notes)
            {
                var notesText = string.Join(Environment.NewLine,
                    notes.Descendants<A.Paragraph>().Select(OpenXmlText).Where(value => !string.IsNullOrWhiteSpace(value)));
                if (!string.IsNullOrWhiteSpace(notesText))
                    sections.Add(new ExtractedSection(notesText,
                        new SourceLocation(LocationKind.Slide, Slide: slideNumber, StructurePath: "notes"), ExtractionMethod.NativeText));
            }
            if (slidePart.SlideCommentsPart?.CommentList is { } commentList)
            {
                var commentText = OpenXmlText(commentList);
                if (!string.IsNullOrWhiteSpace(commentText))
                    sections.Add(new ExtractedSection(commentText,
                        new SourceLocation(LocationKind.Slide, Slide: slideNumber, StructurePath: "comments"),
                        ExtractionMethod.NativeText));
            }

            var altOrdinal = 0;
            foreach (var properties in slidePart.Slide.Descendants<P.NonVisualDrawingProperties>())
            {
                var alt = string.Join(" ", new[] { properties.Name?.Value, properties.Description?.Value }.Where(value => !string.IsNullOrWhiteSpace(value)));
                if (!string.IsNullOrWhiteSpace(alt))
                    sections.Add(new ExtractedSection(alt,
                        new SourceLocation(LocationKind.Slide, Slide: slideNumber, StructurePath: $"alt-text[{++altOrdinal}]"),
                        ExtractionMethod.NativeText));
            }
        }

        var attachments = await ExtractPackageAttachmentsAsync(presentationPart, name, depth, context, cancellationToken);
        return new ExtractedNode(name, mimeType, relationship, sections, attachments);
    }

    private async Task<List<ExtractedNode>> ExtractPackageAttachmentsAsync(OpenXmlPartContainer root, string parentName, int depth,
        ExpansionContext context, CancellationToken cancellationToken)
    {
        var result = new List<ExtractedNode>();
        var visited = new HashSet<OpenXmlPart>();
        var queue = new Queue<OpenXmlPart>(root.Parts.Select(pair => pair.OpenXmlPart));
        var ordinal = 0;
        while (queue.TryDequeue(out var part))
        {
            if (!visited.Add(part)) continue;
            foreach (var child in part.Parts)
                queue.Enqueue(child.OpenXmlPart);

            if (part is not EmbeddedPackagePart && part is not ImagePart)
                continue;
            if (!context.TryAddAttachment(parentName))
                break;

            var extension = ExtensionFromContentType(part.ContentType);
            var partName = Path.GetFileName(Uri.UnescapeDataString(part.Uri.OriginalString));
            if (string.IsNullOrWhiteSpace(Path.GetExtension(partName)))
                partName = $"embedded-{++ordinal}{extension}";
            await using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
            result.Add(await ExtractStreamAsync(stream, partName, part.ContentType,
                part is ImagePart ? "embedded-image" : "embedded-package", depth + 1, context, cancellationToken));
        }
        return result;
    }

    private Task<ExtractedNode> EmlNodeAsync(byte[] bytes, string name, string? mimeType, string relationship,
        int depth, ExpansionContext context, CancellationToken cancellationToken) =>
        MimeMessageNodeAsync(bytes, name, mimeType, relationship, depth, context, isWebArchive: false, cancellationToken);

    private Task<ExtractedNode> MhtmlNodeAsync(byte[] bytes, string name, string? mimeType, string relationship,
        int depth, ExpansionContext context, CancellationToken cancellationToken) =>
        MimeMessageNodeAsync(bytes, name, mimeType, relationship, depth, context, isWebArchive: true, cancellationToken);

    private async Task<ExtractedNode> MimeMessageNodeAsync(byte[] bytes, string name, string? mimeType,
        string relationship, int depth, ExpansionContext context, bool isWebArchive,
        CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        var message = await MimeMessage.LoadAsync(input, cancellationToken);
        var sections = new List<ExtractedSection>();
        if (!isWebArchive)
        {
            var headers = $"From: {message.From}\nTo: {message.To}\nCc: {message.Cc}\nDate: {message.Date:O}\nSubject: {message.Subject}";
            sections.Add(new ExtractedSection(headers,
                new SourceLocation(LocationKind.EmailPart, EmailPart: "headers"), ExtractionMethod.Email));
        }

        var hasHtml = !string.IsNullOrWhiteSpace(message.HtmlBody);
        var body = isWebArchive && hasHtml ? InertHtmlText(message.HtmlBody) : message.TextBody;
        var method = isWebArchive
            ? (hasHtml ? ExtractionMethod.Html : ExtractionMethod.NativeText)
            : ExtractionMethod.Email;
        if (!isWebArchive && string.IsNullOrWhiteSpace(body) && hasHtml)
        {
            body = InertHtmlText(message.HtmlBody);
            method = ExtractionMethod.Html;
        }
        var location = isWebArchive
            ? new SourceLocation(LocationKind.Document)
            : new SourceLocation(LocationKind.EmailPart, EmailPart: "body");
        if (!string.IsNullOrWhiteSpace(body))
            sections.Add(new ExtractedSection(body, location, method));

        var attachments = new List<ExtractedNode>();
        var ordinal = 0;
        foreach (var entity in message.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.TryAddAttachment(name)) break;
            ordinal++;
            switch (entity)
            {
                case MessagePart messagePart:
                    {
                        if (messagePart.Message is null) break;
                        var suppliedName = messagePart.ContentDisposition?.FileName ?? messagePart.ContentType.Name;
                        var attachmentName = string.IsNullOrWhiteSpace(suppliedName) ? $"message-{ordinal}.eml" : suppliedName;
                        try
                        {
                            await using var output = new AttachmentBuffer(context.Request.MaxAttachmentBytes);
                            await messagePart.Message.WriteToAsync(output, cancellationToken);
                            output.Position = 0;
                            attachments.Add(await ExtractStreamAsync(output, attachmentName, "message/rfc822", "email-attachment",
                                depth + 1, context, cancellationToken));
                        }
                        catch (AttachmentSizeLimitException exception)
                        {
                            attachments.Add(Rejected(attachmentName, "message/rfc822", "email-attachment", context,
                                "attachment_size_limit", exception.Message));
                        }
                        break;
                    }
                case MimePart mimePart:
                    {
                        if (mimePart.Content is null) break;
                        var attachmentName = string.IsNullOrWhiteSpace(mimePart.FileName) ? $"attachment-{ordinal}" : mimePart.FileName;
                        var attachmentRelationship = mimePart.IsAttachment ? "email-attachment" : "email-inline";
                        try
                        {
                            await using var output = new AttachmentBuffer(context.Request.MaxAttachmentBytes);
                            await mimePart.Content.DecodeToAsync(output, cancellationToken);
                            output.Position = 0;
                            attachments.Add(await ExtractStreamAsync(output, attachmentName, mimePart.ContentType.MimeType,
                                attachmentRelationship, depth + 1, context, cancellationToken));
                        }
                        catch (AttachmentSizeLimitException exception)
                        {
                            attachments.Add(Rejected(attachmentName, mimePart.ContentType.MimeType, attachmentRelationship,
                                context, "attachment_size_limit", exception.Message));
                        }
                        break;
                    }
            }
        }
        return new ExtractedNode(name, mimeType, relationship, sections, attachments);
    }

    private async Task<ExtractedNode> MsgNodeAsync(byte[] bytes, string name, string? mimeType, string relationship,
        int depth, ExpansionContext context, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var message = new Storage.Message(input, FileAccess.Read, leaveStreamOpen: true);
        var sections = new List<ExtractedSection>
        {
            new($"From: {message.Sender}\nTo: {string.Join("; ", message.Recipients ?? [])}\nDate: {message.SentOn:O}\nSubject: {message.Subject}",
                new SourceLocation(LocationKind.EmailPart, EmailPart: "headers"), ExtractionMethod.Email)
        };
        var body = message.BodyText;
        var method = ExtractionMethod.Email;
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(message.BodyHtml))
        {
            body = InertHtmlText(message.BodyHtml);
            method = ExtractionMethod.Html;
        }
        if (!string.IsNullOrWhiteSpace(body))
            sections.Add(new ExtractedSection(body, new SourceLocation(LocationKind.EmailPart, EmailPart: "body"), method));

        var attachments = new List<ExtractedNode>();
        var ordinal = 0;
        foreach (var item in message.Attachments ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.TryAddAttachment(name)) break;
            ordinal++;
            switch (item)
            {
                case Storage.Attachment attachment when attachment.Data is { Length: > 0 }:
                    {
                        var attachmentName = string.IsNullOrWhiteSpace(attachment.FileName) ? $"attachment-{ordinal}" : attachment.FileName;
                        await using var stream = new MemoryStream(attachment.Data, writable: false);
                        attachments.Add(await ExtractStreamAsync(stream, attachmentName, attachment.MimeType, attachment.IsInline ? "email-inline" : "email-attachment",
                            depth + 1, context, cancellationToken));
                        break;
                    }
                case Storage.Message nested:
                    {
                        var attachmentName = string.IsNullOrWhiteSpace(nested.FileName) ? $"message-{ordinal}.msg" : nested.FileName;
                        attachments.Add(await MsgObjectNodeAsync(nested, attachmentName, "email-attachment", depth + 1, context, cancellationToken));
                        break;
                    }
            }
        }
        return new ExtractedNode(name, mimeType, relationship, sections, attachments);
    }

    private async Task<ExtractedNode> MsgObjectNodeAsync(Storage.Message message, string name, string relationship, int depth,
        ExpansionContext context, CancellationToken cancellationToken)
    {
        if (depth > context.Request.MaxDepth)
            return Rejected(name, "application/vnd.ms-outlook", relationship, context, "attachment_depth_limit", "Attachment depth exceeds the limit.");
        var sections = new List<ExtractedSection>
        {
            new($"From: {message.Sender}\nDate: {message.SentOn:O}\nSubject: {message.Subject}",
                new SourceLocation(LocationKind.EmailPart, EmailPart: "headers"), ExtractionMethod.Email)
        };
        var body = !string.IsNullOrWhiteSpace(message.BodyText) ? message.BodyText : InertHtmlText(message.BodyHtml);
        if (!string.IsNullOrWhiteSpace(body))
            sections.Add(new ExtractedSection(body, new SourceLocation(LocationKind.EmailPart, EmailPart: "body"), ExtractionMethod.Email));
        var attachments = new List<ExtractedNode>();
        var ordinal = 0;
        foreach (var item in message.Attachments ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.TryAddAttachment(name)) break;
            ordinal++;
            if (item is Storage.Attachment attachment && attachment.Data is { Length: > 0 })
            {
                var attachmentName = string.IsNullOrWhiteSpace(attachment.FileName) ? $"attachment-{ordinal}" : attachment.FileName;
                await using var stream = new MemoryStream(attachment.Data, writable: false);
                attachments.Add(await ExtractStreamAsync(stream, attachmentName, attachment.MimeType,
                    attachment.IsInline ? "email-inline" : "email-attachment", depth + 1, context, cancellationToken));
            }
            else if (item is Storage.Message nested)
            {
                var nestedName = string.IsNullOrWhiteSpace(nested.FileName) ? $"message-{ordinal}.msg" : nested.FileName;
                attachments.Add(await MsgObjectNodeAsync(nested, nestedName, "email-attachment", depth + 1, context, cancellationToken));
            }
        }
        return new ExtractedNode(name, "application/vnd.ms-outlook", relationship, sections, attachments);
    }

    private static string InertHtmlText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var document = new HtmlParser().ParseDocument(html);
        foreach (var element in document.QuerySelectorAll("script,style,template,noscript,iframe,object,embed,svg,canvas"))
            element.Remove();
        return document.Body?.TextContent ?? document.DocumentElement.TextContent;
    }

    private static string CellText(Cell cell, IReadOnlyList<string> shared)
    {
        if (cell.DataType?.Value == CellValues.InlineString)
            return OpenXmlText(cell.InlineString);
        var raw = cell.CellValue?.Text ?? cell.InnerText;
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < shared.Count)
            return shared[index];
        return raw;
    }

    private static string OpenXmlText(OpenXmlElement? element) => element is null
        ? string.Empty
        : string.Join(' ', element.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(text => text.Text)
            .Concat(element.Descendants<DocumentFormat.OpenXml.Spreadsheet.Text>().Select(text => text.Text))
            .Concat(element.Descendants<DocumentFormat.OpenXml.Drawing.Text>().Select(text => text.Text))
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static void AddPartText(List<ExtractedSection> sections, OpenXmlElement root, string path)
    {
        var text = OpenXmlText(root);
        if (!string.IsNullOrWhiteSpace(text))
            sections.Add(new ExtractedSection(text, new SourceLocation(LocationKind.Structure, StructurePath: path), ExtractionMethod.NativeText));
    }

    private static string ExtensionFromContentType(string contentType) => contentType.ToLowerInvariant() switch
    {
        "application/pdf" => ".pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
        "application/zip" or "application/x-zip" or "application/x-zip-compressed" => ".zip",
        "application/vnd.rar" or "application/x-rar" or "application/x-rar-compressed" => ".rar",
        "message/rfc822" => ".eml",
        "multipart/related" or "application/x-mimearchive" => ".mhtml",
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tiff",
        "image/webp" => ".webp",
        _ => string.Empty
    };

    private sealed class AttachmentBuffer(long maxBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacityFor(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacityFor(buffer.Length);
            base.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            EnsureCapacityFor(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureCapacityFor(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacityFor(1);
            base.WriteByte(value);
        }

        public override void SetLength(long value)
        {
            if (value > maxBytes) throw LimitExceeded();
            base.SetLength(value);
        }

        private void EnsureCapacityFor(long additionalBytes)
        {
            if (additionalBytes < 0 || Position > maxBytes - additionalBytes) throw LimitExceeded();
        }

        private static AttachmentSizeLimitException LimitExceeded() =>
            new("Decoded attachment exceeds the configured per-attachment size limit.");
    }

    private sealed class AttachmentSizeLimitException(string message) : IOException(message);
}