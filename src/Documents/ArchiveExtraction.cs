using ContextMole.Core;

using SharpCompress.Common;
using SharpCompress.Readers;

namespace ContextMole.Documents;

public sealed partial class DocumentExtractionRegistry
{
    private async Task<ExtractedNode> ArchiveNodeAsync(byte[] bytes, string name, string? mimeType,
        string relationship, string extension, int depth, ExpansionContext context,
        CancellationToken cancellationToken)
    {
        var attachments = new List<ExtractedNode>();
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = ReaderFactory.OpenReader(input, ReaderOptions.ForExternalStream.WithExtensionHint(extension));

        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            if (entry.IsDirectory)
                continue;
            if (!context.TryAddAttachment(name))
                break;

            var entryName = ArchiveEntryName(entry.Key, attachments.Count);
            var entryMimeType = MimeFor(entryName);
            if (entry.IsEncrypted)
            {
                attachments.Add(Rejected(entryName, entryMimeType, "archive-entry", context,
                    "encrypted_archive_entry", "Encrypted archive entries are not supported."));
                continue;
            }
            if (entry.Size > context.Request.MaxAttachmentBytes)
            {
                attachments.Add(Rejected(entryName, entryMimeType, "archive-entry", context,
                    "attachment_size_limit", $"Attachment exceeds the {context.Request.MaxAttachmentBytes} byte limit."));
                continue;
            }

            try
            {
                await using var entryStream = reader.OpenEntryStream();
                attachments.Add(await ExtractStreamAsync(entryStream, entryName, entryMimeType, "archive-entry",
                    depth + 1, context, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                context.Errors.Add(new ExtractionError(ErrorCode(exception), SafeMessage(exception),
                    IsTemporary(exception), entryName));
                attachments.Add(new ExtractedNode(entryName, entryMimeType, "archive-entry", [], [],
                    ErrorCode(exception)));
            }
        }

        return new ExtractedNode(name, mimeType, relationship, [], attachments);
    }

    private static string ArchiveEntryName(string? key, int ordinal) =>
        string.IsNullOrWhiteSpace(key) ? $"entry-{ordinal + 1}" : key;
}