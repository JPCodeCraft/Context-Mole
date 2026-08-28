using Avalonia;

using ContextMole.Broker.Protocol;
using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Indexing;
using ContextMole.Infrastructure;
using ContextMole.Storage;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog;

using Velopack;

namespace ContextMole.App.UI;

internal static class Program
{
    private static IHost? _host;
    private static SingleInstanceLock? _instanceLock;

    public static IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("The application host has not started.");
    public static bool LaunchInBackground { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        LaunchInBackground = args.Any(argument =>
            string.Equals(argument, WindowsStartupRegistration.BackgroundArgument, StringComparison.OrdinalIgnoreCase));

        try
        {
            var paths = new AppPaths();
            _instanceLock = SingleInstanceLock.Acquire(paths);
            var builder = Host.CreateApplicationBuilder(args);
            builder.Logging.ClearProviders();
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .Enrich.FromLogContext()
                .WriteTo.File(Path.Combine(paths.LogsDirectory, "ui-.log"), rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14, shared: true)
                .CreateLogger();
            builder.Services.AddSerilog(dispose: true);
            builder.Services.AddSingleton<IAppPaths>(paths);
            builder.Services.AddContextMoleInfrastructure(includeOcr: true);
            builder.Services.AddSingleton(_ => new BrokerRpcClient(paths.DataDirectory,
                static () => BrokerLaunchCommand.Resolve()));
            builder.Services.Replace(ServiceDescriptor.Singleton<IEmbeddingGenerator, BrokerEmbeddingGenerator>());
            // The UI initiates and drains its own uninstall. It holds a lease, while only MCP
            // sidecars need the marker monitor that stops a host started by an AI client.
            builder.Services.AddContextMoleProcessLifetime("ui", stopOnShutdownRequest: false);
            builder.Services.AddSingleton<McpServerDeploymentService>();
            builder.Services.AddSingleton<AiConnectionsService>();
            builder.Services.AddContextMoleDocuments();
            builder.Services.AddWritableContextMoleStorage();
            builder.Services.AddContextMoleIndexing();
            builder.Services.AddSingleton<ApplicationUpdateService>();
            builder.Services.AddSingleton<WindowsStartupService>();
            builder.Services.AddSingleton<WindowsUninstallService>();
            builder.Services.AddSingleton<ProjectOrderService>();
            builder.Services.AddSingleton<ViewModels.MainViewModel>();
            _host = builder.Build();
            _host.StartAsync().GetAwaiter().GetResult();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Context Mole could not start: {exception.Message}");
            Log.Fatal(exception, "Application startup failed");
        }
        finally
        {
            ShutdownHostAsync().GetAwaiter().GetResult();
        }
    }

    public static async Task ShutdownHostAsync()
    {
        var host = Interlocked.Exchange(ref _host, null);
        try
        {
            if (host is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await host.StopAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    Log.Warning("Timed out while draining application services");
                }
                catch (Exception exception)
                {
                    Log.Error(exception, "Application services did not stop cleanly");
                }
                finally
                {
                    try
                    {
                        if (host is IAsyncDisposable asyncDisposable)
                            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        else
                            host.Dispose();
                    }
                    catch (Exception exception)
                    {
                        Log.Error(exception, "Application services could not be disposed cleanly");
                    }
                }
            }
        }
        finally
        {
            try
            {
                Interlocked.Exchange(ref _instanceLock, null)?.Dispose();
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
