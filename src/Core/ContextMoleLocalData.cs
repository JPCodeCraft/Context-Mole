using System.Text.Json;

namespace ContextMole.Core;

/// <summary>
/// Defines the one application-owned directory that the in-app uninstaller may remove.
/// Indexed source locations are deliberately not part of this API.
/// </summary>
public static class ContextMoleLocalData
{
    public const string DataDirectoryEnvironmentVariable = "CONTEXTMOLE_DATA_DIR";
    public const string WindowsApplicationDirectoryName = "ContextMole";

    public static string GetDefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                WindowsApplicationDirectoryName));
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                WindowsApplicationDirectoryName));
        }

        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(xdgData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", WindowsApplicationDirectoryName)
            : Path.Combine(xdgData, WindowsApplicationDirectoryName));
    }

    public static bool IsCanonicalWindowsDataDirectory(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var expected = Path.TrimEndingDirectorySeparator(GetDefaultDataDirectory());
            var localAppData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
            return !string.Equals(candidate, localAppData, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetDirectoryName(candidate), localAppData, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}

public sealed record ContextMoleShutdownRequest(
    Guid RequestId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc);

/// <summary>
/// Coordinates the UI and MCP processes that use one Context Mole data directory.
/// </summary>
public static class ContextMoleProcessCoordination
{
    public const string CoordinationDirectoryName = ".lifecycle";
    public const string LeasesDirectoryName = "leases";
    public const string ShutdownMarkerFileName = "shutdown-request.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UnreadableMarkerLifetime = TimeSpan.FromMinutes(15);

    public static ContextMoleProcessLease AcquireLease(IAppPaths paths, string role) =>
        AcquireLease(paths.DataDirectory, role);

    public static ContextMoleProcessLease AcquireLease(string dataDirectory, string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        using var uninstallAdmission = ContextMoleExternalUninstallGate.EnterLeaseAdmission(dataDirectory);
        var normalizedRole = NormalizeRole(role);
        EnsureSafeCoordinationDirectories(dataDirectory, includeLeases: true);
        var coordinationDirectory = GetCoordinationDirectory(dataDirectory);
        var leasesDirectory = GetLeasesDirectory(dataDirectory);

        using var gate = AcquireGate(coordinationDirectory, GateTimeout);
        if (TryGetActiveShutdownRequest(dataDirectory, out _))
        {
            throw new ContextMoleException(
                "application_shutting_down",
                "Context Mole is being uninstalled. This local process will not start while cleanup is in progress.",
                false);
        }

        var leasePath = Path.Combine(
            leasesDirectory,
            $"{normalizedRole}-{Environment.ProcessId}-{Guid.NewGuid():N}.lease");
        var stream = new FileStream(
            leasePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);
        try
        {
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(JsonSerializer.Serialize(new ProcessLeasePayload(
                Environment.ProcessId,
                normalizedRole,
                GetCurrentProcessStartUtc(),
                DateTimeOffset.UtcNow), JsonOptions));
            writer.Flush();
            stream.Flush(true);
            return new ContextMoleProcessLease(leasePath, stream);
        }
        catch
        {
            stream.Dispose();
            TryDeleteFile(leasePath);
            throw;
        }
    }

    public static ContextMoleShutdownRequest RequestShutdown(IAppPaths paths, TimeSpan lifetime) =>
        RequestShutdown(paths.DataDirectory, lifetime);

    public static ContextMoleShutdownRequest RequestShutdown(string dataDirectory, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));

        EnsureSafeCoordinationDirectories(dataDirectory, includeLeases: false);
        var coordinationDirectory = GetCoordinationDirectory(dataDirectory);
        using var gate = AcquireGate(coordinationDirectory, GateTimeout);

        var now = DateTimeOffset.UtcNow;
        var request = new ContextMoleShutdownRequest(Guid.NewGuid(), now, now.Add(lifetime));
        var markerPath = GetShutdownMarkerPath(dataDirectory);
        var temporaryPath = markerPath + $".{request.RequestId:N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(request, JsonOptions));
            File.Move(temporaryPath, markerPath, overwrite: true);
            return request;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    public static bool IsShutdownRequested(IAppPaths paths) =>
        TryGetActiveShutdownRequest(paths.DataDirectory, out _);

    public static bool IsShutdownRequested(string dataDirectory) =>
        TryGetActiveShutdownRequest(dataDirectory, out _);

    public static bool IsShutdownRequestActive(string dataDirectory, Guid requestId)
    {
        if (requestId == Guid.Empty) return false;
        return TryGetActiveShutdownRequest(dataDirectory, out var request) &&
               (request is null || request.RequestId == requestId);
    }

    public static bool TryGetActiveShutdownRequest(string dataDirectory, out ContextMoleShutdownRequest? request)
    {
        request = null;
        if (!TryValidateExistingCoordinationDirectories(dataDirectory, includeLeases: false, out _))
        {
            // Unsafe coordination paths are treated like an active request so no new process opens
            // the database through a junction while uninstall coordination is uncertain.
            return true;
        }
        var markerPath = GetShutdownMarkerPath(dataDirectory);
        if (!File.Exists(markerPath)) return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<ContextMoleShutdownRequest>(File.ReadAllText(markerPath), JsonOptions);
            if (parsed is null || parsed.RequestId == Guid.Empty || parsed.ExpiresUtc <= DateTimeOffset.UtcNow)
            {
                TryDeleteFile(markerPath);
                return false;
            }

            request = parsed;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A marker that is currently being replaced is treated as active. If a crash left a corrupt
            // marker behind, its file timestamp still gives it a bounded lifetime.
            try
            {
                if (File.GetLastWriteTimeUtc(markerPath).Add(UnreadableMarkerLifetime) <= DateTime.UtcNow)
                {
                    TryDeleteFile(markerPath);
                    return false;
                }
            }
            catch (Exception timestampException) when (timestampException is IOException or UnauthorizedAccessException)
            {
            }
            return true;
        }
    }

    public static bool RemoveShutdownRequest(string dataDirectory, Guid requestId) =>
        RemoveShutdownRequest(dataDirectory, requestId, afterCoordinationGateAcquired: null);

    internal static bool RemoveShutdownRequest(
        string dataDirectory,
        Guid requestId,
        Action? afterCoordinationGateAcquired)
    {
        if (requestId == Guid.Empty) return false;
        try
        {
            if (!TryValidateExistingCoordinationDirectories(dataDirectory, includeLeases: false, out _)) return false;
            var coordinationDirectory = GetCoordinationDirectory(dataDirectory);
            if (!Directory.Exists(coordinationDirectory)) return false;

            if (OperatingSystem.IsWindows())
            {
                if (Directory.Exists(Path.Combine(coordinationDirectory, "coordination.lock"))) return false;
                var windowsMarkerPath = GetShutdownMarkerPath(dataDirectory);
                return SafeWindowsDataDeletion.TryReadAndDeleteDirectFileWithoutFollowingReparsePoints(
                    dataDirectory,
                    coordinationDirectory,
                    windowsMarkerPath,
                    contents =>
                    {
                        var request = JsonSerializer.Deserialize<ContextMoleShutdownRequest>(contents, JsonOptions);
                        return request?.RequestId == requestId;
                    },
                    out _,
                    afterCoordinationGateAcquired);
            }

            using var gate = AcquireGate(coordinationDirectory, GateTimeout);
            afterCoordinationGateAcquired?.Invoke();
            var markerPath = GetShutdownMarkerPath(dataDirectory);
            if (!File.Exists(markerPath)) return false;
            var request = JsonSerializer.Deserialize<ContextMoleShutdownRequest>(File.ReadAllText(markerPath), JsonOptions);
            if (request?.RequestId != requestId) return false;
            File.Delete(markerPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                   or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool TryRemoveShutdownRequestWithRetry(
        string dataDirectory,
        Guid requestId,
        out string? error,
        int maximumAttempts = 3,
        TimeSpan? retryDelay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        var delay = retryDelay ?? TimeSpan.FromMilliseconds(50);
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));

        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            if (RemoveShutdownRequest(dataDirectory, requestId) ||
                !IsShutdownRequestActive(dataDirectory, requestId))
            {
                error = null;
                return true;
            }

            if (attempt < maximumAttempts - 1 && delay > TimeSpan.Zero) Thread.Sleep(delay);
        }

        error = GetShutdownMarkerManualCleanupMessage(dataDirectory);
        return false;
    }

    public static string GetShutdownMarkerManualCleanupMessage(string dataDirectory)
    {
        var markerPath = GetShutdownMarkerPath(dataDirectory);
        return "Context Mole could not remove its shutdown marker. Context Mole may remain unavailable " +
               "until the marker expires. Remove only this marker file manually:\n\n" +
               $"{markerPath}\n\nDo not remove indexed source files.";
    }

    public static bool RefreshShutdownRequest(string dataDirectory, Guid requestId, TimeSpan lifetime)
    {
        if (requestId == Guid.Empty || lifetime <= TimeSpan.Zero) return false;
        try
        {
            if (!TryValidateExistingCoordinationDirectories(dataDirectory, includeLeases: false, out _)) return false;
            var coordinationDirectory = GetCoordinationDirectory(dataDirectory);
            var markerPath = GetShutdownMarkerPath(dataDirectory);
            if (!Directory.Exists(coordinationDirectory) || !File.Exists(markerPath)) return false;

            using var gate = AcquireGate(coordinationDirectory, GateTimeout);
            var request = JsonSerializer.Deserialize<ContextMoleShutdownRequest>(File.ReadAllText(markerPath), JsonOptions);
            if (request?.RequestId != requestId) return false;
            var refreshed = request with { ExpiresUtc = DateTimeOffset.UtcNow.Add(lifetime) };
            var temporaryPath = markerPath + $".{requestId:N}.refresh.tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(refreshed, JsonOptions));
                File.Move(temporaryPath, markerPath, overwrite: true);
                return true;
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                   or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static string GetCoordinationDirectory(string dataDirectory) =>
        Path.Combine(Path.GetFullPath(dataDirectory), CoordinationDirectoryName);

    public static string GetLeasesDirectory(string dataDirectory) =>
        Path.Combine(GetCoordinationDirectory(dataDirectory), LeasesDirectoryName);

    public static string GetShutdownMarkerPath(string dataDirectory) =>
        Path.Combine(GetCoordinationDirectory(dataDirectory), ShutdownMarkerFileName);

    internal static void EnsureSafeCoordinationDirectories(string dataDirectory, bool includeLeases)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        CreateAndValidateDirectory(root, "data directory");

        var coordinationDirectory = GetCoordinationDirectory(root);
        EnsureDirectChild(root, coordinationDirectory);
        CreateAndValidateDirectory(coordinationDirectory, "lifecycle directory");

        if (!includeLeases) return;
        var leasesDirectory = GetLeasesDirectory(root);
        EnsureDirectChild(coordinationDirectory, leasesDirectory);
        CreateAndValidateDirectory(leasesDirectory, "process-leases directory");
    }

    internal static bool TryValidateExistingCoordinationDirectories(
        string dataDirectory,
        bool includeLeases,
        out string? error)
    {
        error = null;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
            if (!Directory.Exists(root)) return true;
            EnsureExistingDirectoryIsNotReparsePoint(root, "data directory");

            var coordinationDirectory = GetCoordinationDirectory(root);
            EnsureDirectChild(root, coordinationDirectory);
            if (!Directory.Exists(coordinationDirectory)) return true;
            EnsureExistingDirectoryIsNotReparsePoint(coordinationDirectory, "lifecycle directory");

            if (!includeLeases) return true;
            var leasesDirectory = GetLeasesDirectory(root);
            EnsureDirectChild(coordinationDirectory, leasesDirectory);
            if (!Directory.Exists(leasesDirectory)) return true;
            EnsureExistingDirectoryIsNotReparsePoint(leasesDirectory, "process-leases directory");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static FileStream AcquireGate(string coordinationDirectory, TimeSpan timeout)
    {
        Directory.CreateDirectory(coordinationDirectory);
        var gatePath = Path.Combine(coordinationDirectory, "coordination.lock");
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        Exception? lastException = null;
        do
        {
            try
            {
                return new FileStream(gatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastException = exception;
                Thread.Sleep(50);
            }
        } while (DateTimeOffset.UtcNow < deadline);

        throw new IOException("Context Mole process coordination is busy.", lastException);
    }

    private static void CreateAndValidateDirectory(string path, string description)
    {
        if (Directory.Exists(path))
        {
            EnsureExistingDirectoryIsNotReparsePoint(path, description);
            return;
        }

        Directory.CreateDirectory(path);
        EnsureExistingDirectoryIsNotReparsePoint(path, description);
    }

    private static void EnsureExistingDirectoryIsNotReparsePoint(string path, string description)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException($"The Context Mole {description} is not a directory: {path}");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The Context Mole {description} is a reparse point and cannot be used: {path}");
    }

    private static void EnsureDirectChild(string parent, string child)
    {
        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var fullChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        if (!string.Equals(Path.GetDirectoryName(fullChild), fullParent, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A Context Mole coordination path left its approved parent directory.");
    }

    private static string NormalizeRole(string role)
    {
        var normalized = new string(role.Trim().ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character == '-' ? character : '-')
            .ToArray()).Trim('-');
        if (normalized.Length == 0 || normalized.Length > 32)
            throw new ArgumentException("The process role must contain 1-32 letters, numbers, or hyphens.", nameof(role));
        return normalized;
    }

    private static DateTimeOffset GetCurrentProcessStartUtc()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record ProcessLeasePayload(
        int ProcessId,
        string Role,
        DateTimeOffset ProcessStartUtc,
        DateTimeOffset AcquiredUtc);
}

public sealed class ContextMoleProcessLease : IDisposable
{
    private readonly string _path;
    private FileStream? _stream;

    internal ContextMoleProcessLease(string path, FileStream stream)
    {
        _path = path;
        _stream = stream;
    }

    public string Path => _path;

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
