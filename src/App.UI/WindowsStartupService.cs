using System.Runtime.Versioning;
using System.Security;

using ContextMole.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ContextMole.App.UI;

internal sealed class WindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Context Mole";
    private const string LegacyValueName = "MCPIndexSearch";
    private const string EnabledPreference = "enabled";
    private const string DisabledPreference = "disabled";
    private readonly ILogger<WindowsStartupService> _logger;
    private readonly string _preferencePath;

    public WindowsStartupService(IAppPaths paths, ILogger<WindowsStartupService> logger)
    {
        _logger = logger;
        _preferencePath = Path.Combine(paths.DataDirectory, "ui-state", "start-with-windows.txt");
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                return ReadRegistryEnabled();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
            {
                _logger.LogWarning(exception, "Could not read the Windows startup preference");
                return false;
            }
        }
    }

    public void Initialize()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var enabled = ReadPreference() ?? true;
            SetRegistryEnabled(enabled);
            SavePreference(enabled);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Could not initialize the start-with-Windows preference");
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Starting with Windows is available only on Windows.");

        var previous = ReadRegistryEnabled();
        try
        {
            SetRegistryEnabled(enabled);
            SavePreference(enabled);
        }
        catch
        {
            try
            {
                if (OperatingSystem.IsWindows()) SetRegistryEnabled(previous);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException)
            {
                _logger.LogWarning(exception, "Could not restore the previous Windows startup registration");
            }
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool ReadRegistryEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value) ||
               key?.GetValue(LegacyValueName) is string legacy && !string.IsNullOrWhiteSpace(legacy);
    }

    [SupportedOSPlatform("windows")]
    private static void SetRegistryEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new IOException("The Windows startup registry key could not be opened.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("The application executable path is unavailable.");
        key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    private bool? ReadPreference()
    {
        if (!File.Exists(_preferencePath)) return null;
        return File.ReadAllText(_preferencePath).Trim().ToLowerInvariant() switch
        {
            EnabledPreference => true,
            DisabledPreference => false,
            _ => null,
        };
    }

    private void SavePreference(bool enabled)
    {
        var directory = Path.GetDirectoryName(_preferencePath)
            ?? throw new IOException("The UI state directory could not be resolved.");
        Directory.CreateDirectory(directory);

        var temporaryPath = _preferencePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, enabled ? EnabledPreference : DisabledPreference);
            File.Move(temporaryPath, _preferencePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "Could not clean up the temporary startup preference file");
            }
        }
    }
}