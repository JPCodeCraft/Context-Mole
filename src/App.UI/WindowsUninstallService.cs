using System.Diagnostics;

using ContextMole.Core;

using Microsoft.Extensions.Logging;

using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace ContextMole.App.UI;

internal sealed record WindowsUninstallAvailability(
    bool IsVisible,
    bool CanUninstall,
    bool CanDeleteData,
    string DataDirectory,
    string Message);

internal sealed class WindowsUninstallService
{
    private const string RepositoryUrl = "https://github.com/JPCodeCraft/Context-Mole";
    private const string HelperDirectoryName = "uninstall-helper";
    private const string HelperExecutableName = "ContextMole.UninstallHelper.exe";
    private static readonly TimeSpan ShutdownMarkerLifetime = TimeSpan.FromMinutes(15);

    private readonly IAppPaths _paths;
    private readonly ILogger<WindowsUninstallService> _logger;
    private readonly string? _updateExecutablePath;
    private readonly string _bundledHelperPath;

    public WindowsUninstallService(IAppPaths paths, ILogger<WindowsUninstallService> logger)
    {
        _paths = paths;
        _logger = logger;
        _bundledHelperPath = Path.Combine(AppContext.BaseDirectory, HelperDirectoryName, HelperExecutableName);
        Availability = DetectAvailability(out _updateExecutablePath);
    }

    public WindowsUninstallAvailability Availability { get; }

    public void StartUninstall(bool deleteData)
    {
        if (!Availability.CanUninstall || string.IsNullOrWhiteSpace(_updateExecutablePath))
            throw new InvalidOperationException(Availability.Message);
        if (deleteData && !Availability.CanDeleteData)
            throw new InvalidOperationException(
                "Context Mole is using a custom data directory. It will be kept and must be removed manually.");

        var request = ContextMoleProcessCoordination.RequestShutdown(_paths, ShutdownMarkerLifetime);
        string? temporaryDirectory = null;
        try
        {
            temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"ContextMole-uninstall-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            var temporaryHelperPath = Path.Combine(temporaryDirectory, HelperExecutableName);
            File.Copy(_bundledHelperPath, temporaryHelperPath, overwrite: false);

            using var currentProcess = Process.GetCurrentProcess();
            var processStart = currentProcess.StartTime.ToUniversalTime().Ticks;
            var startInfo = new ProcessStartInfo
            {
                FileName = temporaryHelperPath,
                WorkingDirectory = temporaryDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--parent-start-ticks");
            startInfo.ArgumentList.Add(processStart.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--update-exe");
            startInfo.ArgumentList.Add(_updateExecutablePath);
            startInfo.ArgumentList.Add("--data-dir");
            startInfo.ArgumentList.Add(_paths.DataDirectory);
            startInfo.ArgumentList.Add("--request-id");
            startInfo.ArgumentList.Add(request.RequestId.ToString("D"));
            startInfo.ArgumentList.Add("--delete-data");
            startInfo.ArgumentList.Add(deleteData ? "true" : "false");
            startInfo.ArgumentList.Add("--timeout-seconds");
            startInfo.ArgumentList.Add("120");
            startInfo.ArgumentList.Add("--temporary-dir");
            startInfo.ArgumentList.Add(temporaryDirectory);

            var helper = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The Context Mole uninstall helper could not be started.");
            helper.Dispose();
            _logger.LogInformation(
                "Started the in-app Windows uninstaller. DeleteLocalData={DeleteLocalData}, RequestId={RequestId}",
                deleteData,
                request.RequestId);
        }
        catch (Exception exception)
        {
            var markerRemoved = ContextMoleProcessCoordination.TryRemoveShutdownRequestWithRetry(
                _paths.DataDirectory,
                request.RequestId,
                out var markerCleanupError);
            TryRemoveTemporaryDirectory(temporaryDirectory);
            if (!markerRemoved)
            {
                throw new InvalidOperationException(
                    exception.Message + Environment.NewLine + Environment.NewLine + markerCleanupError,
                    exception);
            }
            throw;
        }
    }

    private WindowsUninstallAvailability DetectAvailability(out string? updateExecutablePath)
    {
        updateExecutablePath = null;
        if (!OperatingSystem.IsWindows())
        {
            return new(false, false, false, _paths.DataDirectory,
                "In-app uninstall is available only on Windows.");
        }

        var canDeleteData = ContextMoleLocalData.IsCanonicalWindowsDataDirectory(_paths.DataDirectory);
        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));
            if (!manager.IsInstalled)
            {
                return new(true, false, canDeleteData, _paths.DataDirectory,
                    "In-app uninstall is available after Context Mole is installed with the Windows installer.");
            }
            if (manager.IsPortable)
            {
                return new(true, false, canDeleteData, _paths.DataDirectory,
                    "This portable copy is not managed by the Windows installer. Close it and remove its files manually.");
            }

            updateExecutablePath = VelopackLocator.Current.UpdateExePath;
            if (string.IsNullOrWhiteSpace(updateExecutablePath) || !File.Exists(updateExecutablePath))
            {
                updateExecutablePath = null;
                return new(true, false, canDeleteData, _paths.DataDirectory,
                    "The installed Windows uninstaller could not be found. Use Windows Installed apps instead.");
            }
            if (!File.Exists(_bundledHelperPath))
            {
                return new(true, false, canDeleteData, _paths.DataDirectory,
                    "The uninstall helper is missing. Use Windows Installed apps instead.");
            }

            var dataMessage = canDeleteData
                ? "Choose whether to keep or permanently delete local application data."
                : $"The custom data directory '{_paths.DataDirectory}' will be kept and must be removed manually.";
            return new(true, true, canDeleteData, _paths.DataDirectory, dataMessage);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The installed Windows uninstaller could not be initialized.");
            return new(true, false, canDeleteData, _paths.DataDirectory,
                "The installed Windows uninstaller is unavailable. Use Windows Installed apps instead.");
        }
    }

    private void TryRemoveTemporaryDirectory(string? temporaryDirectory)
    {
        if (string.IsNullOrWhiteSpace(temporaryDirectory)) return;
        try
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "Could not remove the unused uninstall-helper directory.");
        }
    }
}
