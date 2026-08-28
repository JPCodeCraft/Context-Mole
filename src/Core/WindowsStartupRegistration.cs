using System.Runtime.Versioning;

using Microsoft.Win32;

namespace ContextMole.Core;

public static class WindowsStartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Context Mole";
    public const string BackgroundArgument = "--background";

    [SupportedOSPlatform("windows")]
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    [SupportedOSPlatform("windows")]
    public static void SetEnabled(bool enabled, string? executablePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new IOException("The Windows startup registry key could not be opened.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = executablePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("The application executable path is unavailable.");
        key.SetValue(ValueName, BuildCommand(executable), RegistryValueKind.String);
        key.Flush();
    }

    internal static string BuildCommand(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        if (executablePath.Contains('"', StringComparison.Ordinal))
            throw new ArgumentException("The executable path cannot contain a quotation mark.", nameof(executablePath));

        return $"\"{Path.GetFullPath(executablePath)}\" {BackgroundArgument}";
    }

    [SupportedOSPlatform("windows")]
    public static void RemoveForSuccessfulUninstall() => SetEnabled(false);
}
