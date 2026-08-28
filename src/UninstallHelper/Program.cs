using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

using ContextMole.Core;

namespace ContextMole.UninstallHelper;

internal static class Program
{
    private const string AutomatedErrorUiSuppressionVariable =
        "CONTEXTMOLE_UNINSTALL_TEST_SUPPRESS_ERROR_UI";
    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxIconError = 0x00000010;
    private const uint MessageBoxSetForeground = 0x00010000;
    private static readonly TimeSpan MarkerRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MarkerLifetime = TimeSpan.FromMinutes(15);

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        HelperOptions? options = null;
        CancellationTokenSource? markerRefreshCancellation = null;
        Task markerRefreshTask = Task.CompletedTask;
        var markerRefreshHealthy = 1;

        async Task StopMarkerRefreshAsync()
        {
            var cancellation = Interlocked.Exchange(ref markerRefreshCancellation, null);
            if (cancellation is not null)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                cancellation.Dispose();
            }
            try
            {
                await markerRefreshTask.ConfigureAwait(false);
            }
            catch
            {
                // Renewal health is tracked separately and checked synchronously. A stop-task fault
                // must not prevent marker removal, native error reporting, or helper self-cleanup.
            }
            markerRefreshTask = Task.CompletedTask;
        }

        try
        {
            options = HelperOptions.Parse(args);
            ValidateOptions(options);
            using var uninstallGate = ContextMoleExternalUninstallGate.AcquireForUninstall(
                options.DataDirectory,
                TimeSpan.FromSeconds(Math.Min(options.TimeoutSeconds, 10)));
            markerRefreshCancellation = new CancellationTokenSource();
            markerRefreshTask = RefreshShutdownMarkerAsync(
                options,
                () => Interlocked.Exchange(ref markerRefreshHealthy, 0),
                markerRefreshCancellation.Token);

            bool RefreshExactShutdownRequest()
            {
                if (Volatile.Read(ref markerRefreshHealthy) == 0) return false;
                if (ContextMoleProcessCoordination.RefreshShutdownRequest(
                        options.DataDirectory,
                        options.RequestId,
                        MarkerLifetime))
                    return true;
                Interlocked.Exchange(ref markerRefreshHealthy, 0);
                return false;
            }

            var request = new UninstallWorkflowRequest(
                options.DataDirectory,
                options.RequestId,
                options.DeleteData,
                TimeSpan.FromSeconds(options.TimeoutSeconds));
            var operations = new UninstallWorkflowOperations(
                async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
                    return await WaitForExactParentExitAsync(
                        options.ParentProcessId,
                        options.ParentStartTicks,
                        timeout.Token).ConfigureAwait(false);
                },
                () => RunVelopackUninstallerAsync(options.UpdateExecutablePath),
                StopMarkerRefreshAsync,
                RefreshExactShutdownRequest,
                ContextMoleProcessCoordination.RemoveShutdownRequest,
                () =>
                {
                    if (OperatingSystem.IsWindows())
                        WindowsStartupRegistration.RemoveForSuccessfulUninstall();
                },
                (dataDirectory, timeout) =>
                    SafeWindowsDataDeletion.DeleteCanonicalDirectoryAsync(dataDirectory, timeout),
                ShowError,
                () => ScheduleTemporaryDirectoryCleanup(options.TemporaryDirectory),
                ContextMoleProcessCoordination.IsShutdownRequestActive);
            return await UninstallWorkflow.ExecuteAsync(request, operations).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await StopMarkerRefreshAsync().ConfigureAwait(false);
            var errorMessage =
                $"Context Mole uninstall could not be completed. Local data was kept.\n\n{exception.Message}";
            if (options is not null)
            {
                if (!ContextMoleProcessCoordination.TryRemoveShutdownRequestWithRetry(
                        options.DataDirectory,
                        options.RequestId,
                        out var markerCleanupError))
                    errorMessage += "\n\n" + markerCleanupError;
            }
            try
            {
                ShowError(errorMessage);
            }
            finally
            {
                ScheduleTemporaryDirectoryCleanup(options?.TemporaryDirectory);
            }
            return 1;
        }
    }

    private static async Task RefreshShutdownMarkerAsync(
        HelperOptions options,
        Action markUnhealthy,
        CancellationToken cancellationToken)
    {
        try
        {
            while (ContextMoleProcessCoordination.RefreshShutdownRequest(
                       options.DataDirectory,
                       options.RequestId,
                       MarkerLifetime))
            {
                await Task.Delay(MarkerRefreshInterval, cancellationToken).ConfigureAwait(false);
            }
            markUnhealthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            markUnhealthy();
            // The workflow observes this state synchronously before launch and again before cleanup,
            // so a failed renewal cannot permit uninstall or data deletion to continue unnoticed.
        }
    }

    private static async Task<bool> WaitForExactParentExitAsync(
        int processId,
        long expectedStartTicks,
        CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            try
            {
                if (process.StartTime.ToUniversalTime().Ticks != expectedStartTicks) return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    private static async Task<int> RunVelopackUninstallerAsync(string updateExecutablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = updateExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(updateExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        startInfo.ArgumentList.Add("uninstall");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Velopack Update.exe could not be started.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static void ValidateOptions(HelperOptions options)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Context Mole uninstall helper runs only on Windows.");
        if (!ContextMoleLocalData.IsCanonicalWindowsDataDirectory(options.DataDirectory) && options.DeleteData)
            throw new InvalidOperationException("Only the canonical Context Mole local-data directory can be deleted.");
        if (!string.Equals(Path.GetFileName(options.UpdateExecutablePath), "Update.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(options.UpdateExecutablePath))
            throw new FileNotFoundException("The Velopack Update.exe path is invalid.", options.UpdateExecutablePath);

        var updateRoot = Path.GetDirectoryName(Path.GetFullPath(options.UpdateExecutablePath))
            ?? throw new InvalidOperationException("The Velopack installation root could not be resolved.");
        if (!File.Exists(Path.Combine(updateRoot, "current", "sq.version")))
            throw new InvalidOperationException("The selected Update.exe is not part of a Velopack installation.");

        var actualTemporaryDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Path.GetDirectoryName(Environment.ProcessPath)
            ?? throw new InvalidOperationException("The uninstall helper path is unavailable.")));
        var expectedTemporaryDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.TemporaryDirectory));
        if (!string.Equals(actualTemporaryDirectory, expectedTemporaryDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(actualTemporaryDirectory).StartsWith("ContextMole-uninstall-", StringComparison.Ordinal))
            throw new InvalidOperationException("The uninstall helper is not running from its expected temporary directory.");
    }

    private static void ScheduleTemporaryDirectoryCleanup(string? temporaryDirectory)
    {
        if (string.IsNullOrWhiteSpace(temporaryDirectory) || !OperatingSystem.IsWindows()) return;
        try
        {
            var cleanupScript = Path.Combine(
                Path.GetTempPath(),
                $"ContextMole-helper-cleanup-{Guid.NewGuid():N}.cmd");
            File.WriteAllLines(cleanupScript,
            [
                "@echo off",
                "for /L %%i in (1,1,120) do (",
                "  rmdir /s /q \"%~1\" 2>nul",
                "  if not exist \"%~1\" goto finished",
                "  ping -n 2 127.0.0.1 >nul",
                ")",
                ":finished",
                "del \"%~f0\"",
            ]);

            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("/D");
            startInfo.ArgumentList.Add("/Q");
            startInfo.ArgumentList.Add("/C");
            startInfo.ArgumentList.Add(cleanupScript);
            startInfo.ArgumentList.Add(temporaryDirectory);
            Process.Start(startInfo)?.Dispose();
        }
        catch
        {
            // The helper is already outside the install and data directories. A failed best-effort
            // self-cleanup leaves only the uniquely named temporary helper copy.
        }
    }

    private static void ShowError(string message)
    {
        Console.Error.WriteLine(message);
        if (!OperatingSystem.IsWindows() ||
            string.Equals(
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
                "true",
                StringComparison.Ordinal) &&
            string.Equals(
                Environment.GetEnvironmentVariable(AutomatedErrorUiSuppressionVariable),
                "true",
                StringComparison.Ordinal))
            return;
        _ = MessageBoxW(
            IntPtr.Zero,
            message,
            "Context Mole uninstall",
            MessageBoxOk | MessageBoxIconError | MessageBoxSetForeground);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr windowHandle, string text, string caption, uint type);

    private sealed record HelperOptions(
        int ParentProcessId,
        long ParentStartTicks,
        string UpdateExecutablePath,
        string DataDirectory,
        Guid RequestId,
        bool DeleteData,
        int TimeoutSeconds,
        string TemporaryDirectory)
    {
        public static HelperOptions Parse(IReadOnlyList<string> arguments)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < arguments.Count; index += 2)
            {
                if (index + 1 >= arguments.Count || !arguments[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("The uninstall helper received invalid arguments.");
                if (!values.TryAdd(arguments[index], arguments[index + 1]))
                    throw new ArgumentException($"The uninstall helper received duplicate option '{arguments[index]}'.");
            }

            return new HelperOptions(
                ParseInt(values, "--parent-pid", minimum: 1, maximum: int.MaxValue),
                ParseLong(values, "--parent-start-ticks", minimum: 1),
                Get(values, "--update-exe"),
                Get(values, "--data-dir"),
                Guid.Parse(Get(values, "--request-id")),
                bool.Parse(Get(values, "--delete-data")),
                ParseInt(values, "--timeout-seconds", minimum: 30, maximum: 600),
                Get(values, "--temporary-dir"));
        }

        private static string Get(IReadOnlyDictionary<string, string> values, string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException($"The uninstall helper requires '{name}'.");

        private static int ParseInt(IReadOnlyDictionary<string, string> values, string name, int minimum, int maximum)
        {
            if (!int.TryParse(Get(values, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value < minimum || value > maximum)
                throw new ArgumentException($"The uninstall helper received an invalid '{name}' value.");
            return value;
        }

        private static long ParseLong(IReadOnlyDictionary<string, string> values, string name, long minimum)
        {
            if (!long.TryParse(Get(values, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value < minimum)
                throw new ArgumentException($"The uninstall helper received an invalid '{name}' value.");
            return value;
        }
    }
}
