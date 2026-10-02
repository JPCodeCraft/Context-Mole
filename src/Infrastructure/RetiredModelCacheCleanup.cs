using System.ComponentModel;
using System.Runtime.InteropServices;

using ContextMole.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace ContextMole.Infrastructure;

public sealed record RetiredModelCacheCleanupResult(
    IReadOnlyList<string> DeletedFiles,
    IReadOnlyList<string> SkippedFiles,
    IReadOnlyList<string> PendingFiles,
    string? RootIssue = null)
{
    public bool NeedsAttention => RootIssue is not null || SkippedFiles.Count > 0 || PendingFiles.Count > 0;
    public static RetiredModelCacheCleanupResult Empty { get; } = new([], [], []);
}

/// <summary>
/// Best-effort native-UI upgrade cleanup. Only direct, ordinary files emitted by the old
/// installer in the pinned app-owned 311M cache are eligible. No directory is deleted.
/// Settings readers, broker processes and benchmark tools never invoke this automatically.
/// </summary>
public sealed class RetiredModelCacheCleanup
{
    public const string RetiredRevision = "44399559930365213510b1ee2eb15ded83374f0e";
    internal static IReadOnlyList<string> FileNames { get; } = Array.AsReadOnly(new[]
    {
        "tokenizer.json", "tokenizer.json.partial",
        "model.onnx", "model.onnx.partial",
        "model_quint8_avx2.onnx", "model_quint8_avx2.onnx.partial",
        "validation.json", "validation.json.partial",
        "installation-complete", "installation-complete.partial",
        "repair-required", "quantization-disabled"
    });
    private static readonly object Gate = new();
    private readonly IAppPaths _paths;
    private readonly ILogger<RetiredModelCacheCleanup>? _logger;
    private readonly Action<string>? _beforeFileOpen;
    private readonly Action<string>? _beforeDelete;

    public RetiredModelCacheCleanup(IAppPaths paths, ILogger<RetiredModelCacheCleanup>? logger = null)
        : this(paths, logger, null, null) { }

    internal RetiredModelCacheCleanup(IAppPaths paths, ILogger<RetiredModelCacheCleanup>? logger,
        Action<string>? beforeFileOpen, Action<string>? beforeDelete)
    {
        _paths = paths;
        _logger = logger;
        _beforeFileOpen = beforeFileOpen;
        _beforeDelete = beforeDelete;
    }

    /// <summary>Failures are returned for reporting and retried on a later UI startup.</summary>
    public RetiredModelCacheCleanupResult TryCleanup()
    {
        lock (Gate)
        {
            var deleted = new List<string>();
            var skipped = new List<string>();
            var pending = new List<string>();
            try
            {
                var data = CanonicalRoot(_paths.DataDirectory);
                var assets = CanonicalRoot(_paths.AssetsDirectory);
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!string.Equals(assets, Path.Combine(data, "assets"), comparison))
                    throw new IOException("The assets directory is outside the app-owned data/assets cache; cleanup was skipped.");
                var revisionDirectory = Path.Combine(assets, "granite", RetiredRevision);
                using var directory = NoFollowDirectory.Open(revisionDirectory, data);
                if (directory is null) return RetiredModelCacheCleanupResult.Empty;
                foreach (var name in FileNames)
                {
                    try
                    {
                        _beforeFileOpen?.Invoke(Path.Combine(revisionDirectory, name));
                        var disposition = directory.DeleteOrdinaryFile(name, _beforeDelete);
                        if (disposition == FileDisposition.Deleted) deleted.Add(name);
                        else if (disposition == FileDisposition.Protected) skipped.Add(name);
                    }
                    catch (Exception exception) when (IsRecoverable(exception))
                    {
                        pending.Add(name);
                        LogSafely(exception, "Retired 311M cache file {FileName} is still in use or unavailable; retrying on next UI startup", name);
                    }
                }
                return deleted.Count == 0 && skipped.Count == 0 && pending.Count == 0
                    ? RetiredModelCacheCleanupResult.Empty : new(deleted, skipped, pending);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                LogSafely(exception, "Retired 311M cache cleanup was skipped; application startup can continue");
                return new(deleted, skipped, pending, exception.Message);
            }
        }
    }

    private static bool IsRecoverable(Exception exception) => exception is not (OutOfMemoryException or StackOverflowException);

    private void LogSafely(Exception exception, string message, params object?[] arguments)
    {
        try { _logger?.LogWarning(exception, message, arguments); }
        catch (Exception loggingFailure) when (IsRecoverable(loggingFailure)) { /* Reporting must not fail startup. */ }
    }

    private static string CanonicalRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            throw new IOException("The app cache root must be an absolute path without traversal components.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A filesystem root cannot be used as an app cache root.");
        if (OperatingSystem.IsWindows() && (full.StartsWith(@"\\", StringComparison.Ordinal) || full[2..].Contains(':')))
            throw new IOException("Network and device paths cannot be used for retired-model cleanup.");
        return full;
    }

    private enum FileDisposition { Missing, Deleted, Protected }

    private abstract class NoFollowDirectory : IDisposable
    {
        public abstract FileDisposition DeleteOrdinaryFile(string name, Action<string>? beforeDelete);
        public abstract void Dispose();
        public static NoFollowDirectory? Open(string directory, string data) => OperatingSystem.IsWindows()
            ? WindowsDirectory.Open(directory)
            : OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
                ? UnixDirectory.Open(directory, data)
                : throw new PlatformNotSupportedException("Retired-model cleanup is not supported on this platform.");
    }

    private sealed class WindowsDirectory : NoFollowDirectory
    {
        private readonly List<SafeFileHandle> _handles = [];
        private readonly string _path;
        private WindowsDirectory(string path) => _path = path;

        public static WindowsDirectory? Open(string path)
        {
            var directory = new WindowsDirectory(path);
            try
            {
                var current = Path.GetPathRoot(path)!;
                if (!directory.Pin(current)) { directory.Dispose(); return null; }
                foreach (var part in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, part);
                    if (!directory.Pin(current, part)) { directory.Dispose(); return null; }
                }
                return directory;
            }
            catch { directory.Dispose(); throw; }
        }

        private bool Pin(string path, string? childName = null)
        {
            // No delete sharing pins every ancestor, including parents of DataDirectory.
            ValidatePinnedAttributes();
            var handle = childName is null
                ? CreateFileW(path, 0x80 | 0x20, FileShare.Read, IntPtr.Zero,
                    FileMode.Open, 0x00200000 | 0x02000000, IntPtr.Zero)
                : OpenRelative(_handles[^1], childName, 0x80 | 0x20, FileShare.Read);
            if (handle is null) return false;
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                if (error is 2 or 3) return false;
                throw NativeError("Could not pin an app-cache directory", error);
            }
            _handles.Add(handle);
            ValidatePinnedAttributes();
            return true;
        }

        private void ValidatePinnedAttributes()
        {
            foreach (var handle in _handles)
            {
                var attributes = File.GetAttributes(handle);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                    throw new IOException("A cache ancestor is a link, reparse point, or non-directory; cleanup was skipped.");
            }
        }

        public override FileDisposition DeleteOrdinaryFile(string name, Action<string>? beforeDelete)
        {
            var path = Path.Combine(_path, name);
            ValidatePinnedAttributes();
            SafeFileHandle? opened;
            try { opened = OpenRelative(_handles[^1], name, 0x10000 | 0x80, FileShare.None); }
            catch (ProtectedCacheEntryException) { return FileDisposition.Protected; }
            using var file = opened;
            if (file is null) return FileDisposition.Missing;
            ValidatePinnedAttributes();
            var attributes = File.GetAttributes(file);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                return FileDisposition.Protected;
            if (!GetFileInformationByHandle(file, out var info)) throw NativeError("Could not inspect a retired-cache file");
            if (info.NumberOfLinks != 1) return FileDisposition.Protected;
            beforeDelete?.Invoke(path);
            ValidatePinnedAttributes();
            // Delete the inspected handle, never a newly resolved path. Respect read-only/locked files.
            var disposition = new WindowsFileDisposition { DeleteFile = 1 };
            if (!SetFileInformationByHandle(file, 4, ref disposition, (uint)Marshal.SizeOf<WindowsFileDisposition>()))
                throw NativeError("Could not delete a retired-cache file");
            return FileDisposition.Deleted;
        }

        private static SafeFileHandle? OpenRelative(SafeFileHandle parent, string name, uint access, FileShare share)
        {
            // Resolve only a direct child of the inspected directory handle. In-place reparse
            // changes cannot redirect a subsequent full-path traversal because there is none.
            var buffer = Marshal.StringToHGlobalUni(name);
            var unicodeAddress = Marshal.AllocHGlobal(Marshal.SizeOf<WindowsUnicodeString>());
            try
            {
                var unicode = new WindowsUnicodeString
                {
                    Length = checked((ushort)(name.Length * 2)),
                    MaximumLength = checked((ushort)((name.Length + 1) * 2)),
                    Buffer = buffer
                };
                Marshal.StructureToPtr(unicode, unicodeAddress, false);
                var attributes = new WindowsObjectAttributes
                {
                    Length = (uint)Marshal.SizeOf<WindowsObjectAttributes>(),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = unicodeAddress,
                    Attributes = 0x40 | 0x1000 // OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE
                };
                var raw = IntPtr.Zero;
                var status = NtCreateFile(ref raw, access, ref attributes, out _, IntPtr.Zero, 0,
                    (uint)share, 1, 0x00200000 | 0x4000, IntPtr.Zero, 0); // FILE_OPEN, OPEN_REPARSE_POINT, BACKUP_INTENT
                if (status >= 0) return new SafeFileHandle(raw, ownsHandle: true);
                if (raw != IntPtr.Zero && raw != new IntPtr(-1)) new SafeFileHandle(raw, true).Dispose();
                var error = checked((int)RtlNtStatusToDosError(status));
                if (error is 2 or 3) return null;
                if (error is 4395 or 681) throw new ProtectedCacheEntryException(); // Reparse encountered / stopped on symlink.
                throw NativeError("Could not open an anchored app-cache entry", error);
            }
            finally
            {
                Marshal.FreeHGlobal(unicodeAddress);
                Marshal.FreeHGlobal(buffer);
                GC.KeepAlive(parent);
            }
        }

        public override void Dispose()
        {
            for (var index = _handles.Count - 1; index >= 0; index--) _handles[index].Dispose();
            _handles.Clear();
        }
    }

    private sealed class UnixDirectory : NoFollowDirectory
    {
        private readonly List<int> _handles = [];
        private readonly List<string> _names = [];
        private readonly string _path;
        private int Handle => _handles[^1];
        private static bool Mac => OperatingSystem.IsMacOS();
        private static int NoFollow => Mac ? 0x100 : 0x20000;
        private static int CloseOnExec => Mac ? 0x1000000 : 0x80000;
        private static int DirectoryFlag => Mac ? 0x100000 : 0x10000;
        private UnixDirectory(string path) => _path = path;

        public new static UnixDirectory? Open(string path, string data)
        {
            var directory = new UnixDirectory(path);
            try
            {
                var root = open("/", NoFollow | DirectoryFlag | CloseOnExec);
                if (root < 0) throw NativeError("Could not pin the filesystem root");
                directory._handles.Add(root);
                var current = "/";
                UnixIdentity? dataIdentity = null;
                foreach (var part in path[1..].Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, part);
                    var child = openat(directory.Handle, part, NoFollow | DirectoryFlag | CloseOnExec);
                    if (child < 0)
                    {
                        var error = Marshal.GetLastPInvokeError();
                        if (error == 2) { directory.Dispose(); return null; }
                        throw NativeError("A cache ancestor is unavailable or a link; cleanup was skipped", error);
                    }
                    directory._handles.Add(child);
                    directory._names.Add(part);
                    var identity = Inspect(child, null);
                    if ((identity.Mode & 0xf000) != 0x4000) throw new IOException("A cache ancestor is not an ordinary directory.");
                    if (current == data) dataIdentity = identity;
                    if (dataIdentity is { } owned && !identity.SameFilesystem(owned))
                        throw new IOException("A shared or mounted assets directory cannot be used for retired-model cleanup.");
                }
                return directory;
            }
            catch { directory.Dispose(); throw; }
        }

        public override FileDisposition DeleteOrdinaryFile(string name, Action<string>? beforeDelete)
        {
            ValidatePinnedNames();
            UnixIdentity identity;
            try { identity = Inspect(Handle, name); }
            catch (FileNotFoundException) { return FileDisposition.Missing; }
            if (!identity.IsPrivateOrdinaryFile || !identity.SameFilesystem(Inspect(Handle, null))) return FileDisposition.Protected;
            var file = openat(Handle, name, NoFollow | CloseOnExec | (Mac ? 4 : 0x800));
            if (file < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 2) return FileDisposition.Missing;
                if (error == (Mac ? 62 : 40)) return FileDisposition.Protected; // ELOOP, a new symlink.
                throw NativeError("Could not open a retired-cache file", error);
            }
            try
            {
                var opened = Inspect(file, null);
                if (!opened.IsPrivateOrdinaryFile || !opened.SameFile(identity)) return FileDisposition.Protected;
                if (flock(file, 2 | 4) != 0) throw NativeError("A retired-cache file is still in use"); // LOCK_EX | LOCK_NB
                beforeDelete?.Invoke(Path.Combine(_path, name));
                ValidatePinnedNames();
                var current = Inspect(Handle, name);
                if (!current.IsPrivateOrdinaryFile || !current.SameFile(opened)) return FileDisposition.Protected;
                // Anchored unlink never follows a target link or a swapped parent. POSIX lacks exact-handle
                // unlink: a concurrent same-user cache-directory or leaf rename after the final
                // identity checks remains possible, but cannot redirect to an external link target.
                // Normal app callers are serialized, and the native UI holds its single-instance lock.
                if (unlinkat(Handle, name, 0) != 0) throw NativeError("Could not delete a retired-cache file");
                return FileDisposition.Deleted;
            }
            finally { close(file); }
        }

        private void ValidatePinnedNames()
        {
            // If an ancestor is renamed or replaced after admission, preserve both trees.
            // The final unlink still has the documented same-user POSIX rename window.
            for (var index = 0; index < _names.Count; index++)
            {
                var named = Inspect(_handles[index], _names[index]);
                var pinned = Inspect(_handles[index + 1], null);
                if ((named.Mode & 0xf000) != 0x4000 || !named.SameFile(pinned))
                    throw new IOException("The app-cache path changed during cleanup; files were preserved.");
            }
        }

        private static UnixIdentity Inspect(int handle, string? name)
        {
            if (!Mac)
            {
                const uint required = 1 | 4 | 0x100; // STATX_TYPE, STATX_NLINK, STATX_INO
                if (statx(handle, name ?? "", name is null ? 0x1000 | 0x100 : 0x100,
                        required | 0x1000, out var info) != 0)
                    throw InspectionError();
                if ((info.Mask & required) != required) throw new IOException("The filesystem cannot verify safe cache-file identity.");
                return new(info.Mode, info.Links, info.Inode, ((ulong)info.DeviceMajor << 32) | info.DeviceMinor,
                    (info.Mask & 0x1000) != 0 ? info.MountId : null);
            }
            DarwinStat darwinInfo;
            var result = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? name is null ? DarwinFStat(handle, out darwinInfo) : DarwinFStatAt(handle, name, out darwinInfo, 0x20)
                : name is null ? DarwinFStat64(handle, out darwinInfo) : DarwinFStatAt64(handle, name, out darwinInfo, 0x20);
            if (result != 0) throw InspectionError();
            return new(darwinInfo.Mode, darwinInfo.Links, darwinInfo.Inode, darwinInfo.Device, null);
        }

        private static IOException InspectionError() => Marshal.GetLastPInvokeError() == 2
            ? new FileNotFoundException("The cache entry no longer exists.")
            : NativeError("Could not inspect an anchored cache entry");

        public override void Dispose()
        {
            for (var index = _handles.Count - 1; index >= 0; index--) close(_handles[index]);
            _handles.Clear();
            _names.Clear();
        }
    }

    private readonly record struct UnixIdentity(ushort Mode, uint Links, ulong Inode, ulong Device, ulong? Mount)
    {
        public bool IsPrivateOrdinaryFile => (Mode & 0xf000) == 0x8000 && Links == 1;
        public bool SameFile(UnixIdentity other) => Device == other.Device && Inode == other.Inode;
        public bool SameFilesystem(UnixIdentity other) => Device == other.Device && (Mount is null || other.Mount is null || Mount == other.Mount);
    }

    private sealed class ProtectedCacheEntryException : IOException
    {
        public ProtectedCacheEntryException() : base("A linked cache entry was preserved.") { }
    }

    private static IOException NativeError(string message, int? error = null)
    {
        var code = error ?? Marshal.GetLastPInvokeError();
        return new IOException($"{message}: {new Win32Exception(code).Message} (native error {code}).");
    }

    // Native ABI layouts: Linux uapi/linux/stat.h (statx, 256 bytes); Darwin bsd/sys/stat.h
    // (__DARWIN_STRUCT_STAT64, 144 bytes). Explicit layouts avoid architecture-dependent Linux stat.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
        [FieldOffset(144)] public ulong MountId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinStat
    {
        [FieldOffset(0)] public uint Device;
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(6)] public ushort Links;
        [FieldOffset(8)] public ulong Inode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential, Size = 1)]
    private struct WindowsFileDisposition { public byte DeleteFile; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsUnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory, ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsIoStatusBlock { public IntPtr Status; public UIntPtr Information; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security,
        FileMode disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out WindowsFileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref WindowsFileDisposition info, uint size);
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(ref IntPtr handle, uint access, ref WindowsObjectAttributes attributes,
        out WindowsIoStatusBlock status, IntPtr allocationSize, uint fileAttributes, uint share,
        uint disposition, uint options, IntPtr extendedAttributes, uint extendedAttributesLength);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int openat(int directory, string name, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int close(int handle);
    [DllImport("libc", SetLastError = true)] private static extern int flock(int handle, int operation);
    [DllImport("libc", SetLastError = true)] private static extern int unlinkat(int directory, string name, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int statx(int directory, string name, int flags, uint mask, out LinuxStatx info);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int DarwinFStat(int handle, out DarwinStat info);
    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)] private static extern int DarwinFStatAt(int handle, string name, out DarwinStat info, int flags);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)] private static extern int DarwinFStat64(int handle, out DarwinStat info);
    [DllImport("libc", EntryPoint = "fstatat$INODE64", SetLastError = true)] private static extern int DarwinFStatAt64(int handle, string name, out DarwinStat info, int flags);
}
