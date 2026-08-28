using ContextMole.Core;

namespace ContextMole.Infrastructure;

public sealed class AppPaths : IAppPaths
{
    public const string DataDirectoryEnvironmentVariable = ContextMoleLocalData.DataDirectoryEnvironmentVariable;

    public AppPaths() : this(ResolveDataDirectory())
    {
    }

    internal AppPaths(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        DatabasePath = Path.Combine(DataDirectory, "index.db");
        AssetsDirectory = Path.Combine(DataDirectory, "assets");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        TempDirectory = Path.Combine(DataDirectory, "temp");

        using var uninstallAdmission = ContextMoleExternalUninstallGate.EnterLeaseAdmission(DataDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(AssetsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
        ApplyPrivatePermissions();
    }

    private static string ResolveDataDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        return string.IsNullOrWhiteSpace(overridePath)
            ? ContextMoleLocalData.GetDefaultDataDirectory()
            : overridePath;
    }

    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string AssetsDirectory { get; }
    public string LogsDirectory { get; }
    public string TempDirectory { get; }

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
