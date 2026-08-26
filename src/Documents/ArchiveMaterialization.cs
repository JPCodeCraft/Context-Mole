using MCPIndexSearch.Core;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace MCPIndexSearch.Documents;

public sealed partial class ContentMaterializationService
{
    private async Task<RawAttachment?> ExtractArchiveChildAsync(byte[] bytes, string extension,
        int requestedOrdinal, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = ReaderFactory.OpenReader(input, ReaderOptions.ForExternalStream.WithExtensionHint(extension));
        var ordinal = 0;
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            if (entry.IsDirectory)
                continue;
            var entryName = string.IsNullOrWhiteSpace(entry.Key)
                ? $"entry-{ordinal + 1}"
                : entry.Key;
            if (ordinal++ != requestedOrdinal)
                continue;
            if (entry.IsEncrypted)
                throw new McpIndexException("extraction_failed", "The indexed archive entry is encrypted and cannot be materialized.");
            if (entry.Size > _maxBytes)
                throw new MaterializationSizeLimitException();

            await using var entryStream = reader.OpenEntryStream();
            var entryBytes = await ReadBoundedAsync(entryStream, cancellationToken).ConfigureAwait(false);
            return new RawAttachment(entryName, MimeFor(entryName), "archive-entry", entryBytes);
        }
        return null;
    }
}
