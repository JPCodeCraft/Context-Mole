using ContextMole.Core;

namespace ContextMole.Infrastructure;

public sealed class AppPaths : IAppPaths
{
    public const string DataDirectoryEnvironmentVariable = "CONTEXTMOLE_DATA_DIR";
    public const string LegacyDataDirectoryEnvironmentVariable = "MCPINDEXSEARCH_DATA_DIR";

    public AppPaths()
    {
        var overridePath = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(overridePath))
            overridePath = Environment.GetEnvironmentVariable(LegacyDataDirectoryEnvironmentVariable);
        DataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(overridePath) ? GetDefaultDataDirectory() : overridePath);
        DatabasePath = Path.Combine(DataDirectory, "index.db");
        AssetsDirectory = Path.Combine(DataDirectory, "assets");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        TempDirectory = Path.Combine(DataDirectory, "temp");

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(AssetsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
        ApplyPrivatePermissions();
    }

    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string AssetsDirectory { get; }
    public string LogsDirectory { get; }
    public string TempDirectory { get; }

    private static string GetDefaultDataDirectory()
    {
        var current = GetPlatformDataDirectory("ContextMole");
        var legacy = GetPlatformDataDirectory("MCPIndexSearch");
        return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
    }

    private static string GetPlatformDataDirectory(string applicationDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), applicationDirectory);
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", applicationDirectory);
        }

        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(xdgData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", applicationDirectory)
            : Path.Combine(xdgData, applicationDirectory);
    }

    private void ApplyPrivatePermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(DataDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception) when (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // The containing user profile normally already supplies equivalent protection.
        }
    }
}

public sealed class SingleInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private SingleInstanceLock(FileStream stream) => _stream = stream;

    public static SingleInstanceLock Acquire(IAppPaths paths)
    {
        var path = Path.Combine(paths.DataDirectory, "ui.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(Environment.ProcessId);
            writer.Flush();
            stream.Flush(true);
            return new SingleInstanceLock(stream);
        }
        catch (IOException exception)
        {
            throw new ContextMoleException("already_running", "Another Context Mole UI/indexer process is already using this data directory.", false)
            {
                Source = exception.Source
            };
        }
    }

    public void Dispose() => _stream.Dispose();
}