using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace ContextMole.Core;

public sealed record ContextMoleDataDeletionResult(bool Deleted, string? Error)
{
    public static ContextMoleDataDeletionResult Success { get; } = new(true, null);
}

/// <summary>
/// Deletes only Context Mole's canonical Windows local-data directory. It never consumes the
/// search database or indexed source paths, and it never follows reparse points.
/// </summary>
public static class SafeWindowsDataDeletion
{
    private const uint DeleteAccess = 0x00010000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileDispositionFlagDelete = 0x00000001;
    private const uint FileDispositionFlagIgnoreReadOnlyAttribute = 0x00000010;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorDeletePending = 303;

    public static async Task<ContextMoleDataDeletionResult> DeleteCanonicalDirectoryAsync(
        string dataDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, "Permanent local-data deletion is available only on Windows.");
        if (!ContextMoleLocalData.IsCanonicalWindowsDataDirectory(dataDirectory))
            return new(false, "The requested directory is not Context Mole's canonical Windows local-data directory.");
        if (timeout <= TimeSpan.Zero)
            return new(false, "The cleanup timeout must be greater than zero.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        var delay = TimeSpan.FromMilliseconds(100);
        string? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryReleaseStaleLeases(root, out var leaseError))
            {
                try
                {
                    DeleteTreeWithoutFollowingReparsePoints(root, root, deleteRoot: true);
                    if (!Directory.Exists(root)) return ContextMoleDataDeletionResult.Success;
                    lastError = "The data directory still exists after cleanup.";
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    lastError = exception.Message;
                }
            }
            else
            {
                lastError = leaseError;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < delay ? remaining : delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.6, 2000));
        }

        return new(false, lastError ?? "One or more Context Mole processes or files are still in use.");
    }

    internal static bool TryReleaseStaleLeases(
        string dataDirectory,
        out string? error,
        Action<string>? beforeLeaseOpen = null)
    {
        if (!OperatingSystem.IsWindows())
            return TryReleaseStaleLeasesPortable(dataDirectory, out error);

        error = null;
        try
        {
            var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
            using var root = TryOpenNoFollow(rootPath, directoryAccess: true);
            if (root is null) return true;
            RequireOrdinaryDirectory(root, rootPath, "data directory");

            var lifecyclePath = ContextMoleProcessCoordination.GetCoordinationDirectory(rootPath);
            EnsureDirectChild(rootPath, lifecyclePath);
            using var lifecycle = TryOpenNoFollow(lifecyclePath, directoryAccess: true);
            if (lifecycle is null) return true;
            RequireOrdinaryDirectory(lifecycle, lifecyclePath, "lifecycle directory");

            var leasesPath = ContextMoleProcessCoordination.GetLeasesDirectory(rootPath);
            EnsureDirectChild(lifecyclePath, leasesPath);
            using var leases = TryOpenNoFollow(leasesPath, directoryAccess: true);
            if (leases is null) return true;
            RequireOrdinaryDirectory(leases, leasesPath, "process-leases directory");

            // Every ancestor stays open without delete sharing while names are enumerated. A path can
            // therefore neither be renamed out from under this check nor be replaced by a junction.
            foreach (var leasePath in Directory.EnumerateFiles(leasesPath, "*.lease", SearchOption.TopDirectoryOnly))
            {
                var fullLeasePath = Path.GetFullPath(leasePath);
                EnsureDirectChild(leasesPath, fullLeasePath);
                beforeLeaseOpen?.Invoke(fullLeasePath);

                using var lease = TryOpenNoFollow(fullLeasePath, directoryAccess: false);
                if (lease is null) continue;
                var leaseInfo = GetAttributeTagInfo(lease, fullLeasePath);
                if (IsReparsePoint(leaseInfo))
                    throw new IOException($"The Context Mole process lease is a reparse point: {fullLeasePath}");
                if (IsDirectory(leaseInfo))
                    throw new IOException($"The Context Mole process lease is not a file: {fullLeasePath}");

                // Opening with DELETE access and without delete sharing fails while the owning process
                // still holds its lease. A stale lease is removed through this exact, already-inspected
                // handle, so a path swap can never redirect cleanup to another file.
                MarkForDeletion(lease, fullLeasePath);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    internal static void DeleteTreeWithoutFollowingReparsePoints(
        string path,
        string allowedRoot,
        bool deleteRoot,
        Action<string>? beforeEntryOpen = null)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        EnsureWithinRoot(fullPath, fullRoot);

        if (!OperatingSystem.IsWindows())
        {
            DeleteTreePortable(fullPath, fullRoot, deleteRoot);
            return;
        }

        using var handle = TryOpenNoFollow(fullPath, directoryAccess: true);
        if (handle is null) return;
        DeleteOpenedTree(handle, fullPath, fullRoot, deleteRoot,
            isCleanupRoot: string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase),
            beforeEntryOpen);
    }

    internal static bool TryReadAndDeleteDirectFileWithoutFollowingReparsePoints(
        string rootPath,
        string parentPath,
        string filePath,
        Func<string, bool> shouldDelete,
        out string? error,
        Action? afterCoordinationGateAcquired = null)
    {
        ArgumentNullException.ThrowIfNull(shouldDelete);
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                var contents = File.ReadAllText(filePath);
                if (!shouldDelete(contents)) return false;
                File.Delete(filePath);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error = exception.Message;
                return false;
            }
        }

        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
            var fullFile = Path.GetFullPath(filePath);
            EnsureDirectChild(fullRoot, fullParent);
            EnsureDirectChild(fullParent, fullFile);

            using var root = TryOpenNoFollow(fullRoot, directoryAccess: true);
            if (root is null) return false;
            RequireOrdinaryDirectory(root, fullRoot, "data directory");

            using var parent = TryOpenNoFollow(fullParent, directoryAccess: true);
            if (parent is null) return false;
            RequireOrdinaryDirectory(parent, fullParent, "lifecycle directory");

            var gatePath = Path.Combine(fullParent, "coordination.lock");
            EnsureDirectChild(fullParent, gatePath);
            using var gate = TryOpenNoFollow(
                gatePath,
                directoryAccess: false,
                GenericRead | GenericWrite,
                FileShare.None,
                FileMode.OpenOrCreate);
            if (gate is null) return false;
            RequireOrdinaryFile(gate, gatePath, "coordination gate");
            afterCoordinationGateAcquired?.Invoke();

            using var file = TryOpenNoFollow(fullFile, directoryAccess: false, GenericRead);
            if (file is null) return false;
            var info = GetAttributeTagInfo(file, fullFile);
            if (IsReparsePoint(info))
                throw new IOException($"The Context Mole shutdown marker is a reparse point: {fullFile}");
            if (IsDirectory(info))
                throw new IOException($"The Context Mole shutdown marker is not a file: {fullFile}");

            var length = RandomAccess.GetLength(file);
            if (length is < 0 or > 64 * 1024)
                throw new IOException($"The Context Mole shutdown marker has an invalid size: {fullFile}");
            var bytes = new byte[(int)length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = RandomAccess.Read(file, bytes.AsSpan(offset), offset);
                if (read == 0) throw new EndOfStreamException("The shutdown marker changed while it was read.");
                offset += read;
            }
            if (!shouldDelete(Encoding.UTF8.GetString(bytes))) return false;

            MarkForDeletion(file, fullFile);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static void DeleteOpenedTree(
        SafeFileHandle handle,
        string path,
        string allowedRoot,
        bool deleteCurrent,
        bool isCleanupRoot,
        Action<string>? beforeEntryOpen)
    {
        var info = GetAttributeTagInfo(handle, path);
        if (IsReparsePoint(info))
        {
            if (isCleanupRoot) throw new IOException("The cleanup root is a reparse point.");
            if (deleteCurrent) MarkForDeletion(handle, path);
            return;
        }
        if (!IsDirectory(info))
        {
            if (isCleanupRoot) throw new IOException("The cleanup root is not a directory.");
            if (deleteCurrent) MarkForDeletion(handle, path);
            return;
        }

        // The directory handle deliberately omits FILE_SHARE_DELETE and remains open for the entire
        // enumeration. The directory path is consequently stable while each child is opened with
        // OPEN_REPARSE_POINT and inspected from that exact handle.
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly))
        {
            var fullEntry = Path.GetFullPath(entry);
            EnsureWithinRoot(fullEntry, allowedRoot);
            EnsureDirectChild(path, fullEntry);
            beforeEntryOpen?.Invoke(fullEntry);

            using var child = TryOpenNoFollow(fullEntry, directoryAccess: true);
            if (child is null) continue;
            DeleteOpenedTree(child, fullEntry, allowedRoot, deleteCurrent: true,
                isCleanupRoot: false, beforeEntryOpen);
        }

        if (deleteCurrent) MarkForDeletion(handle, path);
    }

    private static SafeFileHandle? TryOpenNoFollow(
        string path,
        bool directoryAccess,
        uint additionalAccess = 0,
        FileShare shareMode = FileShare.Read,
        FileMode creationDisposition = FileMode.Open)
    {
        var desiredAccess = DeleteAccess | FileReadAttributes | additionalAccess;
        if (directoryAccess) desiredAccess |= FileListDirectory;
        var handle = CreateFileW(
            ToExtendedLengthPath(path),
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            creationDisposition,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;

        var nativeError = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (nativeError is ErrorFileNotFound or ErrorPathNotFound or ErrorDeletePending) return null;
        throw NativeIOException($"Could not securely open '{path}' for cleanup", nativeError);
    }

    private static string ToExtendedLengthPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal)) return fullPath;
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal))
            return "\\\\?\\UNC\\" + fullPath[2..];
        return "\\\\?\\" + fullPath;
    }

    private static FileAttributeTagInformation GetAttributeTagInfo(SafeFileHandle handle, string path)
    {
        if (GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out FileAttributeTagInformation info,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
            return info;

        throw NativeIOException($"Could not inspect '{path}' for cleanup", Marshal.GetLastWin32Error());
    }

    private static void MarkForDeletion(SafeFileHandle handle, string path)
    {
        var disposition = new FileDispositionInformationEx
        {
            Flags = FileDispositionFlagDelete | FileDispositionFlagIgnoreReadOnlyAttribute,
        };
        if (SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileDispositionInfoEx,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformationEx>()))
            return;

        throw NativeIOException($"Could not delete '{path}'", Marshal.GetLastWin32Error());
    }

    private static void RequireOrdinaryDirectory(
        SafeFileHandle handle,
        string path,
        string description)
    {
        var info = GetAttributeTagInfo(handle, path);
        if (!IsDirectory(info))
            throw new IOException($"The Context Mole {description} is not a directory: {path}");
        if (IsReparsePoint(info))
            throw new IOException($"The Context Mole {description} is a reparse point and cannot be used: {path}");
    }

    private static void RequireOrdinaryFile(
        SafeFileHandle handle,
        string path,
        string description)
    {
        var info = GetAttributeTagInfo(handle, path);
        if (IsDirectory(info))
            throw new IOException($"The Context Mole {description} is not a file: {path}");
        if (IsReparsePoint(info))
            throw new IOException($"The Context Mole {description} is a reparse point and cannot be used: {path}");
    }

    private static bool IsDirectory(FileAttributeTagInformation info) =>
        ((FileAttributes)info.FileAttributes & FileAttributes.Directory) != 0;

    private static bool IsReparsePoint(FileAttributeTagInformation info) =>
        ((FileAttributes)info.FileAttributes & FileAttributes.ReparsePoint) != 0;

    private static IOException NativeIOException(string operation, int nativeError)
    {
        var nativeMessage = new Win32Exception(nativeError).Message;
        return new IOException($"{operation}: {nativeMessage} (Windows error {nativeError}).");
    }

    private static bool TryReleaseStaleLeasesPortable(string dataDirectory, out string? error)
    {
        error = null;
        if (!ContextMoleProcessCoordination.TryValidateExistingCoordinationDirectories(
                dataDirectory,
                includeLeases: true,
                out error))
            return false;
        var leasesDirectory = ContextMoleProcessCoordination.GetLeasesDirectory(dataDirectory);
        if (!Directory.Exists(leasesDirectory)) return true;

        try
        {
            foreach (var leasePath in Directory.EnumerateFiles(leasesDirectory, "*.lease", SearchOption.TopDirectoryOnly))
            {
                using (new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }
                File.Delete(leasePath);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static void DeleteTreePortable(string path, string allowedRoot, bool deleteRoot)
    {
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            if (string.Equals(path, allowedRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The cleanup root is a reparse point.");
            Directory.Delete(path, recursive: false);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly))
        {
            var fullEntry = Path.GetFullPath(entry);
            EnsureWithinRoot(fullEntry, allowedRoot);
            var attributes = File.GetAttributes(fullEntry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                    Directory.Delete(fullEntry, recursive: false);
                else
                    File.Delete(fullEntry);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteTreePortable(fullEntry, allowedRoot, deleteRoot: true);
            }
            else
            {
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(fullEntry, attributes & ~FileAttributes.ReadOnly);
                File.Delete(fullEntry);
            }
        }

        if (deleteRoot) Directory.Delete(path, recursive: false);
    }

    private static void EnsureWithinRoot(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return;
        var prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cleanup attempted to leave the approved Context Mole data directory.");
    }

    private static void EnsureDirectChild(string parent, string child)
    {
        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var fullChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        if (!string.Equals(Path.GetDirectoryName(fullChild), fullParent, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cleanup attempted to leave its inspected parent directory.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileAttributeTagInformation
    {
        public readonly uint FileAttributes;
        public readonly uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformationEx
    {
        public uint Flags;
    }

    private enum FileInfoByHandleClass
    {
        FileAttributeTagInfo = 9,
        FileDispositionInfoEx = 21,
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInformationEx fileInformation,
        uint bufferSize);
}
