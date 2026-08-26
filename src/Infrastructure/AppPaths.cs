using MCPIndexSearch.Core;

namespace MCPIndexSearch.Infrastructure;

public sealed class AppPaths : IAppPaths
{
    public AppPaths()
    {
        var overridePath = Environment.GetEnvironmentVariable("MCPINDEXSEARCH_DATA_DIR");
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
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCPIndexSearch");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MCPIndexSearch");
        }

        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(xdgData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "MCPIndexSearch")
            : Path.Combine(xdgData, "MCPIndexSearch");
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
            throw new McpIndexException("already_running", "Another MCPIndexSearch UI/indexer process is already using this data directory.", false)
            {
                Source = exception.Source
            };
        }
    }

    public void Dispose() => _stream.Dispose();
}
