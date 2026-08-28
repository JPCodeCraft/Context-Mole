using ContextMole.Core;

using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace ContextMole.Documents;

public sealed partial class ContentMaterializationService
{
    private async Task<RawAttachment?> ExtractArchiveChildAsync(byte[] bytes, string containerName, string extension,
        int requestedOrdinal, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        var options = ReaderOptions.ForExternalStream.WithExtensionHint(extension);
        var ordinal = 0;

        async Task<RawAttachment> MaterializeEntryAsync(IEntry entry, string entryName,
            Func<CancellationToken, ValueTask<Stream>> openEntryStreamAsync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsEncrypted)
                throw new ContextMoleException("extraction_failed", "The indexed archive entry is encrypted and cannot be materialized.");
            if (entry.Size > _maxBytes)
                throw new MaterializationSizeLimitException();

            await using var entryStream = await openEntryStreamAsync(cancellationToken).ConfigureAwait(false);
            var entryBytes = await ReadBoundedAsync(entryStream, cancellationToken).ConfigureAwait(false);
            return new RawAttachment(entryName, SupportedContent.MimeTypeForPath(entryName), "archive-entry", entryBytes);
        }

        if (string.Equals(extension, ".7z", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ArchiveFactory.OpenArchive(input, options);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                    continue;
                var entryName = MaterializedArchiveEntryName(entry.Key, ordinal, containerName, extension);
                if (ordinal++ != requestedOrdinal)
                    continue;
                return await MaterializeEntryAsync(entry, entryName, entry.OpenEntryStreamAsync).ConfigureAwait(false);
            }
        }
        else
        {
            using var reader = ReaderFactory.OpenReader(input, options);
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                if (entry.IsDirectory)
                    continue;
                var entryName = MaterializedArchiveEntryName(entry.Key, ordinal, containerName, extension);
                if (ordinal++ != requestedOrdinal)
                    continue;
                return await MaterializeEntryAsync(entry, entryName, token =>
                {
                    token.ThrowIfCancellationRequested();
                    return ValueTask.FromResult<Stream>(reader.OpenEntryStream());
                }).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static string MaterializedArchiveEntryName(string? key, int ordinal, string containerName,
        string extension)
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
