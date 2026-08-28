using System.Security.Cryptography;

using ContextMole.Core;

using DocumentFormat.OpenXml.Packaging;

using MimeKit;

using MsgReader.Outlook;

using UglyToad.PdfPig;

using Storage = MsgReader.Outlook.Storage;

namespace ContextMole.Documents;

public sealed partial class ContentMaterializationService(
    ISearchStore store,
    IAppPaths paths,
    IGlobalCpuBudget cpuBudget) : IContentMaterializer
{
    public const long DefaultMaxBytes = 250L * 1024 * 1024;
    public const string MaxBytesEnvironmentVariable = "CONTEXTMOLE_MATERIALIZE_MAX_BYTES";

    private readonly ISearchStore _store = store;
    private readonly IAppPaths _paths = paths;
    private readonly IGlobalCpuBudget _cpuBudget = cpuBudget;
    private readonly long _maxBytes = ReadConfiguredMaxBytes();

    public async Task<MaterializedContent> MaterializeAsync(Guid projectId, Guid contentId,
        CancellationToken cancellationToken = default)
    {
        using var worker = await _cpuBudget.AcquireWorkerAsync(cancellationToken).ConfigureAwait(false);
        using var activeWorker = worker.Activate();
        var indexed = await _store.GetContentMaterializationAsync(projectId, contentId, cancellationToken).ConfigureAwait(false)
            ?? throw new ContextMoleException("content_not_found", "The content ID was not found in the active index revision for this project.");

        ValidateContentChain(indexed);
        var sourcePath = ValidateAuthorizedSource(indexed.SourcePath, indexed.ProjectFolderPath);
        var verifiedSource = await ReadVerifiedSourceAsync(sourcePath, indexed, cancellationToken).ConfigureAwait(false);
        var sourceBytes = verifiedSource.Bytes;
        var sourceHash = verifiedSource.Sha256;
        var target = indexed.ContentChain[^1];
        var attachmentChain = indexed.ContentChain.Skip(1).Select(node => node.Name).ToArray();

        if (indexed.ContentChain.Count == 1)
        {
            await EnsureIndexStillCurrentAsync(indexed, cancellationToken).ConfigureAwait(false);
            return new MaterializedContent(sourcePath, sourcePath, attachmentChain, target.MimeType,
                sourceBytes.LongLength, sourceHash, false, indexed.IndexRevisionId, indexed.IndexFingerprint);
        }

        byte[] materializedBytes;
        try
        {
            var currentBytes = sourceBytes;
            for (var depth = 1; depth < indexed.ContentChain.Count; depth++)
            {
                var parent = indexed.ContentChain[depth - 1];
                var expected = indexed.ContentChain[depth];
                var extracted = await ExtractChildAsync(currentBytes, parent.Name, parent.MimeType, expected.Ordinal,
                    cancellationToken).ConfigureAwait(false);
                if (extracted is null || !MatchesIndexedNode(extracted, expected))
                    throw new ContextMoleException("attachment_not_found", "The indexed attachment could not be located in the verified source container.");
                currentBytes = extracted.Bytes;
            }
            materializedBytes = currentBytes;
        }
        catch (ContextMoleException)
        {
            throw;
        }
        catch (MaterializationSizeLimitException)
        {
            throw SizeLimitExceeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ContextMoleException("extraction_failed", SafeExtractionMessage(exception));
        }

        if (materializedBytes.LongLength > _maxBytes)
            throw SizeLimitExceeded();

        var materializedHash = Sha256(materializedBytes);
        await EnsureIndexStillCurrentAsync(indexed, cancellationToken).ConfigureAwait(false);
        var localPath = await WriteTemporaryFileAsync(contentId, target.Name, target.MimeType, materializedHash,
            materializedBytes, cancellationToken).ConfigureAwait(false);
        return new MaterializedContent(localPath, sourcePath, attachmentChain, target.MimeType,
            materializedBytes.LongLength, materializedHash, true, indexed.IndexRevisionId, indexed.IndexFingerprint);
    }

    private async Task EnsureIndexStillCurrentAsync(IndexedContentMaterialization indexed,
        CancellationToken cancellationToken)
    {
        var current = await _store.GetContentMaterializationAsync(indexed.ProjectId, indexed.ContentId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
            throw new ContextMoleException("content_not_found", "The content ID is no longer part of the active index revision.");
        if (current.IndexRevisionId != indexed.IndexRevisionId ||
            !string.Equals(current.IndexFingerprint, indexed.IndexFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !PathsEqual(current.SourcePath, indexed.SourcePath))
            throw new ContextMoleException("source_changed", "The active index revision changed during materialization.");
    }

    private async Task<VerifiedSource> ReadVerifiedSourceAsync(string sourcePath, IndexedContentMaterialization indexed,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists)
                throw new ContextMoleException("source_missing", "The indexed source file no longer exists.");
            if (info.Length != indexed.IndexedSizeBytes)
                throw new ContextMoleException("source_changed", "The source file size no longer matches the active index revision.");
            if (info.Length > _maxBytes)
                throw SizeLimitExceeded();

            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length != indexed.IndexedSizeBytes)
                throw new ContextMoleException("source_changed", "The source file changed while it was being validated.");
            var bytes = await ReadBoundedAsync(source, cancellationToken).ConfigureAwait(false);
            var sha256 = Sha256(bytes);
            if (!string.Equals(sha256, indexed.IndexFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new ContextMoleException("source_changed", "The source fingerprint no longer matches the active index revision.");
            return new VerifiedSource(bytes, sha256);
        }
        catch (ContextMoleException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            throw new ContextMoleException("source_missing", "The indexed source file no longer exists.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new ContextMoleException("source_missing", "The indexed source folder no longer exists.");
        }
        catch (MaterializationSizeLimitException)
        {
            throw SizeLimitExceeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ContextMoleException("extraction_failed", "The indexed source file could not be read for validation.");
        }
    }

    private async Task<RawAttachment?> ExtractChildAsync(byte[] containerBytes, string containerName,
        string? containerMimeType, int ordinal, CancellationToken cancellationToken)
    {
        if (ordinal < 0)
            return null;
        var format = SupportedContent.Resolve(containerName, containerMimeType);
        return format?.Kind switch
        {
            ContentFormatKind.Pdf => ExtractPdfChild(containerBytes, ordinal),
            ContentFormatKind.WordOpenXml => await ExtractWordChildAsync(containerBytes, ordinal, cancellationToken).ConfigureAwait(false),
            ContentFormatKind.SpreadsheetOpenXml => await ExtractSpreadsheetChildAsync(containerBytes, ordinal, cancellationToken).ConfigureAwait(false),
            ContentFormatKind.PresentationOpenXml => await ExtractPresentationChildAsync(containerBytes, ordinal, cancellationToken).ConfigureAwait(false),
            ContentFormatKind.Eml or ContentFormatKind.Mhtml => await ExtractEmlChildAsync(containerBytes, ordinal, cancellationToken).ConfigureAwait(false),
            ContentFormatKind.Msg => ExtractMsgChild(containerBytes, ordinal),
            ContentFormatKind.Archive => await ExtractArchiveChildAsync(containerBytes, containerName, format.Extension,
                ordinal, cancellationToken).ConfigureAwait(false),
            _ => throw new ContextMoleException("unsupported_container", "The indexed parent content is not a supported attachment container.")
        };
    }

    private RawAttachment? ExtractPdfChild(byte[] bytes, int requestedOrdinal)
    {
        using var pdf = PdfDocument.Open(bytes);
        if (!pdf.Advanced.TryGetEmbeddedFiles(out var embeddedFiles))
            return null;
        var ordinal = 0;
        var fallbackOrdinal = 0;
        foreach (var embedded in embeddedFiles)
        {
            var name = string.IsNullOrWhiteSpace(embedded.Name) ? $"embedded-{++fallbackOrdinal}" : embedded.Name;
            if (ordinal++ != requestedOrdinal)
                continue;
            var attachmentBytes = embedded.Bytes.ToArray();
            if (attachmentBytes.LongLength > _maxBytes)
                throw new MaterializationSizeLimitException();
            return new RawAttachment(name, MimeFor(name), "pdf-embedded-file", attachmentBytes);
        }
        return null;
    }

    private async Task<RawAttachment?> ExtractWordChildAsync(byte[] bytes, int ordinal,
        CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var document = WordprocessingDocument.Open(input, false);
        return document.MainDocumentPart is null
            ? null
            : await ExtractOpenXmlChildAsync(document.MainDocumentPart, ordinal, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RawAttachment?> ExtractSpreadsheetChildAsync(byte[] bytes, int ordinal,
        CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var document = SpreadsheetDocument.Open(input, false);
        return document.WorkbookPart is null
            ? null
            : await ExtractOpenXmlChildAsync(document.WorkbookPart, ordinal, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RawAttachment?> ExtractPresentationChildAsync(byte[] bytes, int ordinal,
        CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var document = PresentationDocument.Open(input, false);
        return document.PresentationPart is null
            ? null
            : await ExtractOpenXmlChildAsync(document.PresentationPart, ordinal, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RawAttachment?> ExtractOpenXmlChildAsync(OpenXmlPartContainer root, int requestedOrdinal,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<OpenXmlPart>();
        var queue = new Queue<OpenXmlPart>(root.Parts.Select(pair => pair.OpenXmlPart));
        var ordinal = 0;
        var fallbackOrdinal = 0;
        while (queue.TryDequeue(out var part))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(part))
                continue;
            foreach (var child in part.Parts)
                queue.Enqueue(child.OpenXmlPart);
            if (part is not EmbeddedPackagePart && part is not ImagePart)
                continue;

            var extension = ExtensionFromContentType(part.ContentType);
            var name = Path.GetFileName(Uri.UnescapeDataString(part.Uri.OriginalString));
            if (string.IsNullOrWhiteSpace(Path.GetExtension(name)))
                name = $"embedded-{++fallbackOrdinal}{extension}";
            if (ordinal++ != requestedOrdinal)
                continue;
            await using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
            var partBytes = await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
            return new RawAttachment(name, part.ContentType,
                part is ImagePart ? "embedded-image" : "embedded-package", partBytes);
        }
        return null;
    }

    private async Task<RawAttachment?> ExtractEmlChildAsync(byte[] bytes, int requestedOrdinal,
        CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        var message = await MimeMessage.LoadAsync(input, cancellationToken).ConfigureAwait(false);
        var ordinal = 0;
        var sourceOrdinal = 0;
        foreach (var entity in message.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceOrdinal++;
            RawAttachment? attachment = null;
            switch (entity)
            {
                case MessagePart messagePart when messagePart.Message is not null:
                    {
                        await using var output = new LimitedMemoryStream(_maxBytes);
                        await messagePart.Message.WriteToAsync(output, cancellationToken).ConfigureAwait(false);
                        var suppliedName = messagePart.ContentDisposition?.FileName ?? messagePart.ContentType.Name;
                        var name = string.IsNullOrWhiteSpace(suppliedName) ? $"message-{sourceOrdinal}.eml" : suppliedName;
                        attachment = new RawAttachment(name, "message/rfc822", "email-attachment", output.ToArray());
                        break;
                    }
                case MimePart mimePart when mimePart.Content is not null:
                    {
                        await using var output = new LimitedMemoryStream(_maxBytes);
                        await mimePart.Content.DecodeToAsync(output, cancellationToken).ConfigureAwait(false);
                        var name = string.IsNullOrWhiteSpace(mimePart.FileName) ? $"attachment-{sourceOrdinal}" : mimePart.FileName;
                        attachment = new RawAttachment(name, mimePart.ContentType.MimeType,
                            mimePart.IsAttachment ? "email-attachment" : "email-inline", output.ToArray());
                        break;
                    }
            }
            if (attachment is not null && ordinal++ == requestedOrdinal)
                return attachment;
        }
        return null;
    }

    private RawAttachment? ExtractMsgChild(byte[] bytes, int requestedOrdinal)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var message = new Storage.Message(input, FileAccess.Read, leaveStreamOpen: true);
        var ordinal = 0;
        var sourceOrdinal = 0;
        foreach (var item in message.Attachments ?? [])
        {
            sourceOrdinal++;
            RawAttachment? attachment = null;
            switch (item)
            {
                case Storage.Attachment file when file.Data is { Length: > 0 } data:
                    {
                        if (data.LongLength > _maxBytes)
                            throw new MaterializationSizeLimitException();
                        var name = string.IsNullOrWhiteSpace(file.FileName) ? $"attachment-{sourceOrdinal}" : file.FileName;
                        attachment = new RawAttachment(name, file.MimeType,
                            file.IsInline ? "email-inline" : "email-attachment", data);
                        break;
                    }
                case Storage.Message nested:
                    {
                        using var output = new LimitedMemoryStream(_maxBytes);
                        nested.Save(output);
                        var name = string.IsNullOrWhiteSpace(nested.FileName) ? $"message-{sourceOrdinal}.msg" : nested.FileName;
                        attachment = new RawAttachment(name, "application/vnd.ms-outlook", "email-attachment", output.ToArray());
                        break;
                    }
            }
            if (attachment is not null && ordinal++ == requestedOrdinal)
                return attachment;
        }
        return null;
    }

    private async Task<string> WriteTemporaryFileAsync(Guid contentId, string indexedName, string? mimeType,
        string sha256, byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            var tempRoot = Path.GetFullPath(_paths.TempDirectory);
            Directory.CreateDirectory(tempRoot);
            RejectLinkedDirectory(tempRoot);
            var materializedRoot = Path.GetFullPath(Path.Combine(tempRoot, "materialized"));
            if (!IsPathWithin(tempRoot, materializedRoot))
                throw new IOException("The materialization root escaped controlled temporary storage.");
            Directory.CreateDirectory(materializedRoot);
            RejectLinkedDirectory(materializedRoot);
            var contentDirectory = Path.GetFullPath(Path.Combine(materializedRoot, contentId.ToString("N")));
            if (!IsPathWithin(materializedRoot, contentDirectory))
                throw new IOException("The materialization directory escaped controlled temporary storage.");
            Directory.CreateDirectory(contentDirectory);
            RejectLinkedDirectory(contentDirectory);

            var fileName = SafeFileName(indexedName, mimeType);
            var extension = Path.GetExtension(fileName);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            if (stem.Length > 80)
                stem = stem[..80];
            var prefix = $"{stem}-{contentId:N}-{sha256[..12]}";
            var preferredPath = Path.GetFullPath(Path.Combine(contentDirectory, prefix + extension));
            if (!IsPathWithin(contentDirectory, preferredPath))
                throw new IOException("The materialized file path escaped controlled temporary storage.");

            if (File.Exists(preferredPath) && await ExistingFileMatchesAsync(preferredPath, sha256, bytes.LongLength,
                    cancellationToken).ConfigureAwait(false))
                return preferredPath;

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var path = attempt == 0 && !File.Exists(preferredPath)
                    ? preferredPath
                    : Path.Combine(contentDirectory, $"{prefix}-{Guid.NewGuid():N}{extension}");
                try
                {
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return path;
                }
                catch (IOException) when (File.Exists(path))
                {
                    if (await ExistingFileMatchesAsync(path, sha256, bytes.LongLength, cancellationToken).ConfigureAwait(false))
                        return path;
                }
                catch
                {
                    TryDeleteOwnedOutput(path);
                    throw;
                }
            }
            throw new IOException("A collision-free temporary output path could not be created.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ContextMoleException("extraction_failed", "The attachment could not be written to controlled temporary storage.");
        }
    }

    private async Task<bool> ExistingFileMatchesAsync(string path, string expectedHash, long expectedSize,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedSize || info.Length > _maxBytes || IsFileSystemLink(info))
            return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<byte[]> ReadBoundedAsync(Stream source, CancellationToken cancellationToken)
    {
        if (source.CanSeek && source.Length > _maxBytes)
            throw new MaterializationSizeLimitException();
        await using var output = new LimitedMemoryStream(_maxBytes);
        await source.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    private static void ValidateContentChain(IndexedContentMaterialization indexed)
    {
        if (indexed.ContentChain.Count == 0 || indexed.ContentChain[0].ParentContentId is not null ||
            indexed.ContentChain[0].Depth != 0 || indexed.ContentChain[^1].ContentId != indexed.ContentId)
            throw new ContextMoleException("content_not_found", "The indexed content hierarchy is incomplete.");
        for (var index = 1; index < indexed.ContentChain.Count; index++)
        {
            var parent = indexed.ContentChain[index - 1];
            var child = indexed.ContentChain[index];
            if (child.ParentContentId != parent.ContentId || child.Depth != parent.Depth + 1)
                throw new ContextMoleException("content_not_found", "The indexed content hierarchy is invalid.");
        }
    }

    private static string ValidateAuthorizedSource(string indexedPath, string projectFolderPath)
    {
        string sourcePath;
        string folderPath;
        try
        {
            sourcePath = Path.GetFullPath(indexedPath);
            folderPath = Path.GetFullPath(projectFolderPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ContextMoleException("source_missing", "The indexed source path is invalid.");
        }

        if (!IsPathWithin(folderPath, sourcePath))
            throw new ContextMoleException("source_not_authorized", "The indexed source is outside its authorized project folder.");
        if (!File.Exists(sourcePath))
            throw new ContextMoleException("source_missing", "The indexed source file no longer exists.");
        if (IsFileSystemLink(new DirectoryInfo(folderPath)))
            throw new ContextMoleException("source_not_authorized", "The authorized project folder is now a file-system link.");

        var current = new FileInfo(sourcePath) as FileSystemInfo;
        while (current is not null && !PathsEqual(current.FullName, folderPath))
        {
            if (IsFileSystemLink(current))
                throw new ContextMoleException("source_not_authorized", "The indexed source now traverses a file-system link.");
            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            };
        }
        return sourcePath;
    }

    private static bool MatchesIndexedNode(RawAttachment extracted, IndexedMaterializationNode indexed) =>
        string.Equals(extracted.Name, indexed.Name, StringComparison.Ordinal) &&
        string.Equals(extracted.Relationship, indexed.Relationship, StringComparison.Ordinal) &&
        (string.IsNullOrWhiteSpace(indexed.MimeType) || string.IsNullOrWhiteSpace(extracted.MimeType) ||
         string.Equals(extracted.MimeType, indexed.MimeType, StringComparison.OrdinalIgnoreCase));

    private sealed record VerifiedSource(byte[] Bytes, string Sha256);

    private static string SafeFileName(string indexedName, string? mimeType)
    {
        var leaf = indexedName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(leaf) || leaf is "." or "..")
            leaf = "attachment" + ExtensionFromMimeType(mimeType);
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var characters = leaf.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray();
        var result = new string(characters).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(result) || result is "." or "..")
            result = "attachment" + ExtensionFromMimeType(mimeType);
        var extension = Path.GetExtension(result);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ExtensionFromMimeType(mimeType);
        else if (extension.Length > 16 || extension.Skip(1).Any(character => !char.IsLetterOrDigit(character)))
            extension = ExtensionFromMimeType(mimeType);
        var stem = string.IsNullOrEmpty(Path.GetExtension(result)) ? result : Path.GetFileNameWithoutExtension(result);
        return (string.IsNullOrWhiteSpace(stem) ? "attachment" : stem) + extension;
    }

    private static string ExtensionFromMimeType(string? mimeType) =>
        SupportedContent.ExtensionForMimeType(mimeType) ?? string.Empty;

    private static string ExtensionFromContentType(string contentType) => ExtensionFromMimeType(contentType);

    private static string? MimeFor(string name) => SupportedContent.MimeTypeForPath(name);

    private static bool IsPathWithin(string parentPath, string candidatePath)
    {
        var relative = Path.GetRelativePath(parentPath, candidatePath);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) &&
               relative != ".";
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), PathComparison());

    private static StringComparison PathComparison() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static bool IsFileSystemLink(FileSystemInfo info)
    {
        try
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0 || !string.IsNullOrEmpty(info.LinkTarget);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void RejectLinkedDirectory(string path)
    {
        if (IsFileSystemLink(new DirectoryInfo(path)))
            throw new IOException("Controlled temporary storage is a file-system link.");
    }

    private static void TryDeleteOwnedOutput(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private ContextMoleException SizeLimitExceeded() => new("size_limit_exceeded",
        $"The requested content exceeds the configured {_maxBytes} byte materialization limit.");

    private static string SafeExtractionMessage(Exception exception) => string.IsNullOrWhiteSpace(exception.Message)
        ? "The indexed attachment could not be extracted from its source container."
        : $"The indexed attachment could not be extracted from its source container: {exception.Message}";

    private static long ReadConfiguredMaxBytes()
    {
        var configured = Environment.GetEnvironmentVariable(MaxBytesEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
            return DefaultMaxBytes;
        if (!long.TryParse(configured, out var value) || value <= 0 || value > int.MaxValue)
            throw new InvalidOperationException($"{MaxBytesEnvironmentVariable} must be between 1 and {int.MaxValue}.");
        return value;
    }

    private sealed record RawAttachment(string Name, string? MimeType, string Relationship, byte[] Bytes);

    private sealed class MaterializationSizeLimitException : IOException;

    private sealed class LimitedMemoryStream(long maxBytes) : MemoryStream
    {
        private readonly long _maxBytes = maxBytes;

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
            if (value < 0 || value > _maxBytes)
                throw new MaterializationSizeLimitException();
            base.SetLength(value);
        }

        private void EnsureCapacityFor(int count)
        {
            if (count < 0 || Position > _maxBytes - count)
                throw new MaterializationSizeLimitException();
        }
    }
}
