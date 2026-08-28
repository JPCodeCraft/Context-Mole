using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

using ContextMole.Core;

namespace ContextMole.Infrastructure;

public sealed class SystemMemorySnapshotProvider : ISystemMemorySnapshotProvider
{
    private readonly IPhysicalMemorySource _physicalMemory;
    private readonly ContextMoleProcessMemoryAggregator _processMemory;

    public SystemMemorySnapshotProvider() : this(ResolveDataDirectory())
    {
    }

    public SystemMemorySnapshotProvider(IAppPaths paths) : this(paths.DataDirectory)
    {
    }

    private SystemMemorySnapshotProvider(string dataDirectory) : this(
        new NativePhysicalMemorySource(),
        new ContextMoleProcessMemoryAggregator(dataDirectory, new RuntimeProcessMemoryReader(),
            Environment.ProcessId))
    {
    }

    internal SystemMemorySnapshotProvider(
        IPhysicalMemorySource physicalMemory,
        ContextMoleProcessMemoryAggregator processMemory)
    {
        _physicalMemory = physicalMemory;
        _processMemory = processMemory;
    }

    public SystemMemorySnapshot Capture()
    {
        var physical = _physicalMemory.Capture();
        return new SystemMemorySnapshot(physical.TotalBytes, physical.AvailableBytes,
            _processMemory.CapturePrivateBytes());
    }

    private static string ResolveDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(ContextMoleLocalData.DataDirectoryEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? ContextMoleLocalData.GetDefaultDataDirectory()
            : Path.GetFullPath(configured);
    }
}

internal readonly record struct PhysicalMemorySnapshot(long TotalBytes, long AvailableBytes);

internal interface IPhysicalMemorySource
{
    PhysicalMemorySnapshot Capture();
}

internal sealed class NativePhysicalMemorySource : IPhysicalMemorySource
{
    public PhysicalMemorySnapshot Capture()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx();
            if (GlobalMemoryStatusEx(status))
                return new PhysicalMemorySnapshot(Saturate(status.TotalPhysical),
                    Saturate(status.AvailablePhysical));
        }

        if (OperatingSystem.IsLinux() && TryReadLinuxMemory(out var linux)) return linux;
        if (OperatingSystem.IsMacOS() && TryReadMacMemory(out var mac)) return mac;
        return ReadGcFallback();
    }

    private static PhysicalMemorySnapshot ReadGcFallback()
    {
        var gc = GC.GetGCMemoryInfo();
        var total = gc.TotalAvailableMemoryBytes;
        if (total <= 0) total = Math.Max(Environment.WorkingSet * 4, 1L << 30);
        var load = Math.Max(0, gc.MemoryLoadBytes);
        return new PhysicalMemorySnapshot(total, Math.Max(0, total - load));
    }

    private static bool TryReadLinuxMemory(out PhysicalMemorySnapshot memory)
    {
        memory = default;
        try
        {
            long total = 0;
            long available = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    total = ParseKilobytes(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    available = ParseKilobytes(line);
                if (total > 0 && available > 0) break;
            }

            if (total <= 0 || available <= 0) return false;
            memory = new PhysicalMemorySnapshot(total, available);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          FormatException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadMacMemory(out PhysicalMemorySnapshot memory)
    {
        memory = default;
        if (!TryReadSysctl("hw.memsize", out var total) || total == 0) return false;

        var pageSize = (ulong)Math.Max(Environment.SystemPageSize, 1);
        if (TryReadSysctl("hw.pagesize", out var nativePageSize) && nativePageSize > 0)
            pageSize = nativePageSize;
        if (!TryReadSysctl("vm.page_free_count", out var freePages)) return false;

        var availablePages = freePages;
        AddOptionalPages("vm.page_inactive_count", ref availablePages);
        var available = SaturatingMultiply(availablePages, pageSize);
        memory = new PhysicalMemorySnapshot(Saturate(total), Saturate(Math.Min(total, available)));
        return true;
    }

    private static void AddOptionalPages(string name, ref ulong total)
    {
        if (!TryReadSysctl(name, out var pages)) return;
        total = ulong.MaxValue - total < pages ? ulong.MaxValue : total + pages;
    }

    private static bool TryReadSysctl(string name, out ulong value)
    {
        value = 0;
        nuint length = sizeof(ulong);
        try
        {
            return SysctlByName(name, ref value, ref length, IntPtr.Zero, 0) == 0 &&
                   length is > 0 and <= sizeof(ulong);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            value = 0;
            return false;
        }
    }

    private static long ParseKilobytes(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 && long.TryParse(fields[1], out var kilobytes)
            ? checked(kilobytes * 1024)
            : 0;
    }

    private static ulong SaturatingMultiply(ulong left, ulong right) =>
        left != 0 && right > ulong.MaxValue / left ? ulong.MaxValue : left * right;

    private static long Saturate(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx status);

    [DllImport("libSystem.B.dylib", EntryPoint = "sysctlbyname", SetLastError = true)]
    private static extern int SysctlByName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        ref ulong oldValue,
        ref nuint oldLength,
        IntPtr newValue,
        nuint newLength);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

internal readonly record struct ProcessMemorySample(
    int ProcessId,
    DateTimeOffset ProcessStartUtc,
    long PrivateBytes);

internal enum ProcessMemoryReadStatus
{
    Success,
    NotFound,
    Unavailable
}

internal interface IProcessMemoryReader
{
    ProcessMemoryReadStatus Read(int processId, out ProcessMemorySample sample);
}

internal sealed class RuntimeProcessMemoryReader : IProcessMemoryReader
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint StillActive = 259;
    private const int ErrorInvalidHandle = 6;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotFound = 1168;
    private const int ErrorNoSuchProcess = 3;

    public ProcessMemoryReadStatus Read(int processId, out ProcessMemorySample sample)
    {
        sample = default;
        if (processId <= 0) return ProcessMemoryReadStatus.NotFound;
        if (OperatingSystem.IsWindows()) return ReadWindows(processId, out sample);

        if (OperatingSystem.IsLinux() && !Directory.Exists($"/proc/{processId}"))
            return ProcessMemoryReadStatus.NotFound;
        if (OperatingSystem.IsMacOS())
        {
            if (Kill(processId, 0) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorNoSuchProcess) return ProcessMemoryReadStatus.NotFound;
                return ProcessMemoryReadStatus.Unavailable;
            }
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return ProcessMemoryReadStatus.NotFound;
            sample = new ProcessMemorySample(processId, process.StartTime.ToUniversalTime(),
                Math.Max(0, process.PrivateMemorySize64));
            return process.HasExited ? ProcessMemoryReadStatus.NotFound : ProcessMemoryReadStatus.Success;
        }
        catch (ArgumentException)
        {
            return ProcessMemoryReadStatus.NotFound;
        }
        catch (InvalidOperationException)
        {
            return ProcessMemoryReadStatus.NotFound;
        }
        catch (Exception exception) when (exception is NotSupportedException or Win32Exception or
                                          UnauthorizedAccessException)
        {
            return ProcessMemoryReadStatus.Unavailable;
        }
    }

    private static ProcessMemoryReadStatus ReadWindows(int processId, out ProcessMemorySample sample)
    {
        sample = default;
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
            return IsMissingProcessError(Marshal.GetLastWin32Error())
                ? ProcessMemoryReadStatus.NotFound
                : ProcessMemoryReadStatus.Unavailable;

        try
        {
            var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
            if (!GetProcessTimes(handle, out var creation, out _, out _, out _) ||
                !GetProcessMemoryInfo(handle, ref counters, counters.Size))
            {
                return GetExitCodeProcess(handle, out var exitCode) && exitCode != StillActive
                    ? ProcessMemoryReadStatus.NotFound
                    : ProcessMemoryReadStatus.Unavailable;
            }

            var fileTime = ((long)creation.HighDateTime << 32) | creation.LowDateTime;
            if (fileTime <= 0) return ProcessMemoryReadStatus.Unavailable;
            var privateBytes = counters.PrivateUsage.ToUInt64();
            sample = new ProcessMemorySample(processId, DateTimeOffset.FromFileTime(fileTime),
                privateBytes > long.MaxValue ? long.MaxValue : (long)privateBytes);
            return ProcessMemoryReadStatus.Success;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool IsMissingProcessError(int error) =>
        error is ErrorInvalidHandle or ErrorInvalidParameter or ErrorNotFound;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out NativeFileTime creation,
        out NativeFileTime exit, out NativeFileTime kernel, out NativeFileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process,
        ref ProcessMemoryCounters counters, uint size);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }
}

internal sealed class ContextMoleProcessMemoryAggregator(
    string dataDirectory,
    IProcessMemoryReader processes,
    int currentProcessId)
{
    private const long MaximumLeaseBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dataDirectory = Path.GetFullPath(dataDirectory);

    public long CapturePrivateBytes()
    {
        var counted = new HashSet<(int ProcessId, long StartTicks)>();
        var readings = new Dictionary<int, (ProcessMemoryReadStatus Status, ProcessMemorySample Sample)>();
        long total = 0;
        if (Read(currentProcessId) is { Status: ProcessMemoryReadStatus.Success } current) Add(current.Sample);

        foreach (var lease in ReadLeaseIdentities())
        {
            var reading = Read(lease.ProcessId);
            if (reading.Status == ProcessMemoryReadStatus.NotFound ||
                reading.Status == ProcessMemoryReadStatus.Success &&
                UtcTicks(reading.Sample.ProcessStartUtc) != UtcTicks(lease.ProcessStartUtc))
            {
                TryRemoveStaleLease(lease.Path);
                continue;
            }
            if (reading.Status == ProcessMemoryReadStatus.Unavailable)
            {
                // An active Context Mole process holds its lease with sharing that rejects this
                // exclusive cleanup attempt. A closed lease tied to a protected/reused PID does not.
                TryRemoveStaleLease(lease.Path);
                continue;
            }
            if (reading.Status == ProcessMemoryReadStatus.Success) Add(reading.Sample);
        }
        return total;

        (ProcessMemoryReadStatus Status, ProcessMemorySample Sample) Read(int processId)
        {
            if (readings.TryGetValue(processId, out var cached)) return cached;
            var status = processes.Read(processId, out var sample);
            var reading = (status, sample);
            readings.Add(processId, reading);
            return reading;
        }

        void Add(ProcessMemorySample sample)
        {
            if (sample.ProcessId <= 0 || sample.PrivateBytes < 0 ||
                !counted.Add((sample.ProcessId, UtcTicks(sample.ProcessStartUtc)))) return;
            total = sample.PrivateBytes > long.MaxValue - total ? long.MaxValue : total + sample.PrivateBytes;
        }
    }

    private IReadOnlyList<LeaseIdentity> ReadLeaseIdentities()
    {
        var result = new List<LeaseIdentity>();
        if (!ContextMoleProcessCoordination.TryValidateExistingCoordinationDirectories(
                _dataDirectory, includeLeases: true, out _)) return result;
        var leasesDirectory = ContextMoleProcessCoordination.GetLeasesDirectory(_dataDirectory);
        if (!Directory.Exists(leasesDirectory)) return result;

        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(leasesDirectory));
            foreach (var candidate in Directory.EnumerateFiles(normalizedRoot, "*.lease", SearchOption.TopDirectoryOnly))
            {
                string? path = null;
                var ordinaryFile = false;
                try
                {
                    path = Path.GetFullPath(candidate);
                    if (!string.Equals(Path.GetDirectoryName(path), normalizedRoot, PathComparison())) continue;
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) continue;
                    ordinaryFile = true;
                    LeasePayload? payload;
                    var invalidLength = false;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete))
                    {
                        invalidLength = stream.Length is <= 0 or > MaximumLeaseBytes;
                        payload = invalidLength
                            ? null
                            : JsonSerializer.Deserialize<LeasePayload>(stream, JsonOptions);
                    }
                    if (invalidLength)
                    {
                        TryRemoveStaleLease(path);
                        continue;
                    }
                    if (payload is not { ProcessId: > 0 } || payload.ProcessStartUtc <= DateTimeOffset.UnixEpoch ||
                        string.IsNullOrWhiteSpace(payload.Role))
                    {
                        TryRemoveStaleLease(path);
                        continue;
                    }
                    result.Add(new LeaseIdentity(payload.ProcessId, payload.ProcessStartUtc, path));
                }
                catch (JsonException) when (ordinaryFile && path is not null)
                {
                    TryRemoveStaleLease(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  ArgumentException or NotSupportedException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
        }
        return result;
    }

    private static void TryRemoveStaleLease(string path)
    {
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A lease that is still held, or cannot be safely opened exclusively, is retained.
        }
    }

    private static long UtcTicks(DateTimeOffset value) => value.UtcDateTime.Ticks;
    private static StringComparison PathComparison() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record LeasePayload(
        int ProcessId,
        string Role,
        DateTimeOffset ProcessStartUtc,
        DateTimeOffset AcquiredUtc);
    private sealed record LeaseIdentity(int ProcessId, DateTimeOffset ProcessStartUtc, string Path);
}
