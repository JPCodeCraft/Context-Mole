using Microsoft.Extensions.Logging;

using Velopack;
using Velopack.Sources;

namespace ContextMole.App.UI;

internal enum ApplicationUpdateState
{
    Disabled,
    Checking,
    Downloading,
    Ready,
    Current,
    Error,
}

internal sealed record ApplicationUpdateSnapshot(
    ApplicationUpdateState State,
    string? CurrentVersion,
    string? AvailableVersion,
    int ProgressPercent,
    string Message)
{
    public bool HasPendingPackage => State == ApplicationUpdateState.Ready;
    public DateTimeOffset? LastAttemptUtc { get; init; }

    public static ApplicationUpdateSnapshot Disabled { get; } = new(
        ApplicationUpdateState.Disabled,
        null,
        null,
        0,
        "Automatic updates are available in installed Windows builds.");
}

internal sealed class ApplicationUpdateService : IDisposable
{
    private const string RepositoryUrl = "https://github.com/JPCodeCraft/Context-Mole";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly ILogger<ApplicationUpdateService> _logger;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private UpdateManager? _updateManager;
    private VelopackAsset? _pendingAsset;
    private ApplicationUpdateSnapshot _snapshot = ApplicationUpdateSnapshot.Disabled;
    private int _started;
    private DateTimeOffset? _lastAttemptUtc;

    public ApplicationUpdateService(ILogger<ApplicationUpdateService> logger)
    {
        _logger = logger;
    }

    public event EventHandler<ApplicationUpdateSnapshot>? SnapshotChanged;

    public ApplicationUpdateSnapshot Snapshot
    {
        get
        {
            lock (_sync) return _snapshot;
        }
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            Publish(ApplicationUpdateSnapshot.Disabled);
            return;
        }

        try
        {
            _updateManager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));

            if (!_updateManager.IsInstalled || _updateManager.IsPortable)
            {
                Publish(ApplicationUpdateSnapshot.Disabled with
                {
                    CurrentVersion = GetCurrentVersion(),
                });
                return;
            }

            _lifetime = new CancellationTokenSource();
            _ = MonitorAsync(_lifetime.Token);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Automatic application updates could not be initialized.");
            PublishError(exception);
        }
    }

    public bool CanCheckNow => OperatingSystem.IsWindows() && Snapshot.State != ApplicationUpdateState.Disabled;

    public Task CheckNowAsync()
    {
        if (!CanCheckNow || Snapshot.State is ApplicationUpdateState.Checking or ApplicationUpdateState.Downloading)
            return Task.CompletedTask;
        if (_updateManager is null || _lifetime is null)
        {
            // Initialization itself may have failed before a periodic monitor could be created.
            Interlocked.Exchange(ref _started, 0);
            Start();
            return Task.CompletedTask;
        }
        return CheckAndDownloadAsync(_lifetime.Token);
    }

    public void Stop()
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is null)
        {
            return;
        }

        lifetime.Cancel();
        lifetime.Dispose();
    }

    public bool PrepareRestart()
    {
        UpdateManager? manager;
        VelopackAsset? pending;

        lock (_sync)
        {
            manager = _updateManager;
            pending = _pendingAsset ?? manager?.UpdatePendingRestart;
        }

        if (manager is null || pending is null)
        {
            return false;
        }

        manager.WaitExitThenApplyUpdates(pending, silent: false, restart: true);
        return true;
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CheckAndDownloadAsync(cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(CheckInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await CheckAndDownloadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task CheckAndDownloadAsync(CancellationToken cancellationToken)
    {
        var manager = _updateManager;
        if (manager is null)
        {
            return;
        }

        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VelopackAsset? existingPending;
            lock (_sync)
            {
                existingPending = _pendingAsset ?? manager.UpdatePendingRestart;
                _pendingAsset = existingPending;
            }

            if (existingPending is not null)
            {
                PublishReady(existingPending);
                return;
            }

            _lastAttemptUtc = DateTimeOffset.UtcNow;
            Publish(new ApplicationUpdateSnapshot(
                ApplicationUpdateState.Checking,
                GetCurrentVersion(),
                null,
                0,
                "Checking GitHub Releases for updates…"));

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                Publish(new ApplicationUpdateSnapshot(
                    ApplicationUpdateState.Current,
                    GetCurrentVersion(),
                    null,
                    0,
                    "Context Mole is up to date."));
                return;
            }

            var availableVersion = update.TargetFullRelease.Version.ToString();
            PublishDownload(availableVersion, 0);
            await manager.DownloadUpdatesAsync(
                    update,
                    progress => PublishDownload(availableVersion, progress),
                    cancellationToken)
                .ConfigureAwait(false);

            var downloadedAsset = manager.UpdatePendingRestart ?? update.TargetFullRelease;
            lock (_sync) _pendingAsset = downloadedAsset;
            PublishReady(downloadedAsset);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Automatic application update check or download failed.");
            PublishError(exception);
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private void PublishDownload(string availableVersion, int progress)
    {
        Publish(new ApplicationUpdateSnapshot(
            ApplicationUpdateState.Downloading,
            GetCurrentVersion(),
            availableVersion,
            progress,
            $"Downloading version {availableVersion}…"));
    }

    private void PublishReady(VelopackAsset pending)
    {
        Publish(new ApplicationUpdateSnapshot(
            ApplicationUpdateState.Ready,
            GetCurrentVersion(),
            pending.Version.ToString(),
            100,
            $"Version {pending.Version} is ready to install."));
    }

    private void PublishError(Exception exception)
    {
        var previous = Snapshot;
        _lastAttemptUtc ??= DateTimeOffset.UtcNow;
        Publish(CreateErrorSnapshot(GetCurrentVersion(), previous.State == ApplicationUpdateState.Downloading
            ? previous.AvailableVersion : null, exception.Message, _lastAttemptUtc.Value));
    }

    internal static ApplicationUpdateSnapshot CreateErrorSnapshot(string? currentVersion, string? availableVersion,
        string cause, DateTimeOffset attemptUtc)
    {
        cause = cause.Length > 500 ? cause[..500] + "…" : cause;
        var action = availableVersion is null ? "Could not check for updates" : $"Could not download version {availableVersion}";
        return new(ApplicationUpdateState.Error, currentVersion, availableVersion, 0,
            $"{action}: {cause} Check your connection, then try again. The app also checks every 6 hours.")
        { LastAttemptUtc = attemptUtc };
    }

    private void Publish(ApplicationUpdateSnapshot snapshot)
    {
        snapshot = snapshot with { LastAttemptUtc = snapshot.LastAttemptUtc ?? _lastAttemptUtc };
        ApplicationUpdateState previousState;
        lock (_sync)
        {
            if (_snapshot == snapshot) return;
            previousState = _snapshot.State;
            _snapshot = snapshot;
        }

        if (snapshot.State != previousState)
        {
            _logger.LogInformation(
                "Application update state changed to {UpdateState}. Current={CurrentVersion}, Available={AvailableVersion}, Pending={HasPendingPackage}",
                snapshot.State,
                snapshot.CurrentVersion,
                snapshot.AvailableVersion,
                snapshot.HasPendingPackage);
        }

        SnapshotChanged?.Invoke(this, snapshot);
    }

    private string? GetCurrentVersion()
    {
        try
        {
            return _updateManager?.CurrentVersion?.ToString();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "The current Velopack version could not be read.");
            return null;
        }
    }
}