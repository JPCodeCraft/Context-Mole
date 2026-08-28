using ContextMole.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ContextMole.Infrastructure;

public static class ProcessLifetimeServices
{
    /// <summary>
    /// Registers a lifetime lease and an expiring-uninstall-marker monitor for this process.
    /// MCP and UI entry points should call this exactly once after registering <see cref="IAppPaths"/>.
    /// </summary>
    public static IServiceCollection AddContextMoleProcessLifetime(
        this IServiceCollection services,
        string role,
        bool stopOnShutdownRequest = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        services.AddSingleton<IHostedService>(provider => new ContextMoleProcessLifetimeService(
            provider.GetRequiredService<IAppPaths>(),
            provider.GetRequiredService<IHostApplicationLifetime>(),
            role,
            stopOnShutdownRequest));
        return services;
    }
}

internal sealed class ContextMoleProcessLifetimeService : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IAppPaths _paths;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly string _role;
    private readonly bool _stopOnShutdownRequest;
    private CancellationTokenSource? _monitorCancellation;
    private ContextMoleProcessLease? _lease;
    private Task? _monitorTask;

    public ContextMoleProcessLifetimeService(
        IAppPaths paths,
        IHostApplicationLifetime applicationLifetime,
        string role,
        bool stopOnShutdownRequest)
    {
        _paths = paths;
        _applicationLifetime = applicationLifetime;
        _role = role;
        _stopOnShutdownRequest = stopOnShutdownRequest;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lease = ContextMoleProcessCoordination.AcquireLease(_paths, _role);
        if (!_stopOnShutdownRequest) return Task.CompletedTask;
        _monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
                if (!ContextMoleProcessCoordination.IsShutdownRequested(_paths)) continue;
                _applicationLifetime.StopApplication();
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
