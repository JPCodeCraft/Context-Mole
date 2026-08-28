using ContextMole.Core;

using Microsoft.Extensions.Hosting;

namespace ContextMole.Mcp;

internal sealed class McpAppPaths : IAppPaths
{
    public McpAppPaths()
    {
        var overridePath = Environment.GetEnvironmentVariable(ContextMoleLocalData.DataDirectoryEnvironmentVariable);
        DataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(overridePath)
            ? ContextMoleLocalData.GetDefaultDataDirectory()
            : overridePath);
        DatabasePath = Path.Combine(DataDirectory, "index.db");
        AssetsDirectory = Path.Combine(DataDirectory, "assets");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        TempDirectory = Path.Combine(DataDirectory, "temp");
    }

    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string AssetsDirectory { get; }
    public string LogsDirectory { get; }
    public string TempDirectory { get; }
}

internal sealed class McpProcessLifetimeService(
    IAppPaths paths,
    IHostApplicationLifetime applicationLifetime) : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private readonly IAppPaths _paths = paths;
    private readonly IHostApplicationLifetime _applicationLifetime = applicationLifetime;
    private CancellationTokenSource? _monitorCancellation;
    private ContextMoleProcessLease? _lease;
    private Task? _monitorTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lease = ContextMoleProcessCoordination.AcquireLease(_paths.DataDirectory, "mcp");
        ApplyPrivateDataDirectoryPermissions();
        _monitorCancellation = new CancellationTokenSource();
        _monitorTask = MonitorShutdownRequestAsync(_monitorCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var monitorCancellation = Interlocked.Exchange(ref _monitorCancellation, null);
        if (monitorCancellation is not null)
        {
            await monitorCancellation.CancelAsync().ConfigureAwait(false);
            monitorCancellation.Dispose();
        }

        var monitorTask = Interlocked.Exchange(ref _monitorTask, null);
        if (monitorTask is not null)
        {
            try
            {
                await monitorTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Interlocked.Exchange(ref _lease, null)?.Dispose();
    }

    public void Dispose()
    {
        var cancellation = Interlocked.Exchange(ref _monitorCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        Interlocked.Exchange(ref _lease, null)?.Dispose();
    }

    private async Task MonitorShutdownRequestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!ContextMoleProcessCoordination.IsShutdownRequested(_paths.DataDirectory)) continue;
                _applicationLifetime.StopApplication();
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ApplyPrivateDataDirectoryPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(_paths.DataDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception) when (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // The containing user profile normally already provides equivalent protection.
        }
    }
}
