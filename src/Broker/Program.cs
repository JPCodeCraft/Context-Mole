using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Infrastructure;
using ContextMole.Storage;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog;

namespace ContextMole.Broker;

public static class BrokerProgram
{
    public static Task<int> Main(string[] args) => RunAsync(args);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var overridePath = Environment.GetEnvironmentVariable(ContextMoleLocalData.DataDirectoryEnvironmentVariable);
        var dataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(overridePath)
            ? ContextMoleLocalData.GetDefaultDataDirectory()
            : overridePath);
        using var startupLease = ContextMoleProcessCoordination.AcquireLease(dataDirectory, "broker-startup");
        var paths = new AppPaths();
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(paths.LogsDirectory, "broker-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, shared: true)
            .CreateLogger();
        builder.Services.AddSerilog(dispose: true);
        builder.Services.AddSingleton<IAppPaths>(paths);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddContextMoleInfrastructure(includeOcr: false);
        builder.Services.AddContextMoleProcessLifetime("broker");
        builder.Services.AddContextMoleDocuments();
        builder.Services.AddReadOnlyContextMoleStorage();
        builder.Services.AddSingleton<BrokerActivityTracker>();
        builder.Services.AddSingleton<BrokerSearchRuntimeManager>();
        builder.Services.AddSingleton<BrokerRequestDispatcher>();
        builder.Services.AddHostedService<BrokerPipeHostedService>();
        builder.Services.AddHostedService<BrokerIdleHostedService>();

        var host = builder.Build();
        try
        {
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Context Mole broker stopped unexpectedly");
            return 1;
        }
        finally
        {
            if (host is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else
                host.Dispose();
            Log.CloseAndFlush();
        }
    }
}
