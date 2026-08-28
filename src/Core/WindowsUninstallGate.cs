using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ContextMole.Core;

/// <summary>
/// Provides a filesystem-independent admission gate for the interval where the in-data shutdown
/// marker is being removed with the application data directory.
/// </summary>
internal static class ContextMoleExternalUninstallGate
{
    private const string GateNamePrefix = @"Global\ContextMole.Uninstall.";

    public static IDisposable AcquireForUninstall(string dataDirectory, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (!OperatingSystem.IsWindows()) return NoopLease.Instance;

        try
        {
            return ProcessWideGateLease.Acquire(BuildGateName(dataDirectory), timeout);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           WaitHandleCannotBeOpenedException or TimeoutException or
                                           System.Security.SecurityException)
        {
            throw new IOException(
                "The Context Mole uninstall gate could not be acquired. Local data was kept.", exception);
        }
    }

    public static IDisposable EnterLeaseAdmission(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!OperatingSystem.IsWindows()) return NoopLease.Instance;

        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, BuildGateName(dataDirectory));
            var acquired = false;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                throw ShuttingDown();
            }

            return new MutexAdmission(mutex);
        }
        catch (ContextMoleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           WaitHandleCannotBeOpenedException or System.Security.SecurityException)
        {
            mutex?.Dispose();
            throw new ContextMoleException(
                "application_shutting_down",
                $"Context Mole could not verify uninstall coordination and will not open local data: {exception.Message}",
                false);
        }
    }

    private static string BuildGateName(string dataDirectory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The process-wide uninstall gate is Windows-only.");
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory))
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .ToUpperInvariant();
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User?.Value
            ?? throw new IOException("The current Windows user SID is unavailable.");
        // Global\ spans interactive/RDP sessions. Including the owner SID isolates users, while the
        // default security descriptor keeps access with the account that created the application gate.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{userSid}\n{normalized}")));
        return GateNamePrefix + hash;
    }

    private static ContextMoleException ShuttingDown() => new(
        "application_shutting_down",
        "Context Mole is being uninstalled. This local process will not start while cleanup is in progress.",
        false);

    private sealed class MutexAdmission(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _mutex, null);
            if (owned is null) return;
            try
            {
                owned.ReleaseMutex();
            }
            finally
            {
                owned.Dispose();
            }
        }
    }

    /// <summary>
    /// Windows mutex ownership is thread-affine. A dedicated thread owns the helper's mutex so it
    /// remains valid while the async workflow resumes on arbitrary pool threads.
    /// </summary>
    private sealed class ProcessWideGateLease : IDisposable
    {
        private readonly string _name;
        private readonly TimeSpan _timeout;
        private readonly ManualResetEventSlim _acquired = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _ownerThread;
        private Exception? _error;
        private int _disposed;

        private ProcessWideGateLease(string name, TimeSpan timeout)
        {
            _name = name;
            _timeout = timeout;
            _ownerThread = new Thread(OwnGate)
            {
                IsBackground = true,
                Name = "Context Mole uninstall gate"
            };
        }

        public static ProcessWideGateLease Acquire(string name, TimeSpan timeout)
        {
            var lease = new ProcessWideGateLease(name, timeout);
            lease._ownerThread.Start();
            var signalTimeout = timeout.Add(TimeSpan.FromSeconds(1));
            if (!lease._acquired.Wait(signalTimeout))
            {
                lease._release.Set();
                lease._ownerThread.Join();
                lease._acquired.Dispose();
                lease._release.Dispose();
                throw new TimeoutException("Timed out while starting the Context Mole uninstall gate owner.");
            }
            if (lease._error is not null)
            {
                lease._ownerThread.Join();
                lease._acquired.Dispose();
                lease._release.Dispose();
                throw lease._error;
            }
            return lease;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _release.Set();
            _ownerThread.Join();
            _acquired.Dispose();
            _release.Dispose();
        }

        private void OwnGate()
        {
            try
            {
                using var mutex = new Mutex(initiallyOwned: false, _name);
                var ownsMutex = false;
                try
                {
                    try
                    {
                        ownsMutex = mutex.WaitOne(_timeout);
                    }
                    catch (AbandonedMutexException)
                    {
                        ownsMutex = true;
                    }

                    if (!ownsMutex)
                        throw new TimeoutException("Another Context Mole uninstall helper already owns the gate.");
                    _acquired.Set();
                    _release.Wait();
                }
                finally
                {
                    if (ownsMutex) mutex.ReleaseMutex();
                }
            }
            catch (Exception exception)
            {
                _error = exception;
                _acquired.Set();
            }
        }
    }

    private sealed class NoopLease : IDisposable
    {
        public static NoopLease Instance { get; } = new();
        public void Dispose() { }
    }
}
