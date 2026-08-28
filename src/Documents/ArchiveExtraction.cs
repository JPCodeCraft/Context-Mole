using ContextMole.Core;

using SharpCompress.Archives;
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
        async Task<bool> ExtractEntryAsync(IEntry entry,
            Func<CancellationToken, ValueTask<Stream>> openEntryStreamAsync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
                return true;
            if (!context.TryAddAttachment(name))
                return false;

            var entryName = ArchiveEntryName(entry.Key, attachments.Count, name, extension);
            var entryMimeType = SupportedContent.MimeTypeForPath(entryName);
            if (entry.IsEncrypted)
            {
                attachments.Add(Rejected(entryName, entryMimeType, "archive-entry", context,
                    "encrypted_archive_entry", "Encrypted archive entries are not supported."));
                return true;
            }
            if (entry.Size > context.Request.MaxAttachmentBytes)
            {
                attachments.Add(Rejected(entryName, entryMimeType, "archive-entry", context,
                    "attachment_size_limit", $"Attachment exceeds the {context.Request.MaxAttachmentBytes} byte limit."));
                return true;
            }

            try
            {
                await using var entryStream = await openEntryStreamAsync(cancellationToken).ConfigureAwait(false);
                attachments.Add(await ExtractStreamAsync(entryStream, entryName, entryMimeType, "archive-entry",
                    depth + 1, context, cancellationToken).ConfigureAwait(false));
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

            return true;
        }

        using var input = new MemoryStream(bytes, writable: false);
        var options = ReaderOptions.ForExternalStream.WithExtensionHint(extension);
        if (string.Equals(extension, ".7z", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ArchiveFactory.OpenArchive(input, options);
            foreach (var entry in archive.Entries)
            {
                if (!await ExtractEntryAsync(entry, entry.OpenEntryStreamAsync).ConfigureAwait(false))
                    break;
            }
        }
        else
        {
            using var reader = ReaderFactory.OpenReader(input, options);
            while (reader.MoveToNextEntry())
            {
                if (!await ExtractEntryAsync(reader.Entry, token =>
                    {
                        token.ThrowIfCancellationRequested();
                        return ValueTask.FromResult<Stream>(reader.OpenEntryStream());
                    }).ConfigureAwait(false))
                    break;
            }
        }

        return new ExtractedNode(name, mimeType, relationship, [], attachments);
    }

    private static string ArchiveEntryName(string? key, int ordinal, string containerName, string extension)
    {
        if (!string.IsNullOrWhiteSpace(key)) return key;
        if (extension == ".gz" && containerName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            var inferred = Path.GetFileName(containerName[..^3]);
            if (!string.IsNullOrWhiteSpace(inferred)) return inferred;
        }
        return $"entry-{ordinal + 1}";
    }
}
