using Avalonia;

using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Indexing;
using ContextMole.Infrastructure;
using ContextMole.Storage;

using Microsoft.Extensions.DependencyInjection;
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

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

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
            builder.Services.AddSingleton<McpServerDeploymentService>();
            builder.Services.AddSingleton<AiConnectionsService>();
            builder.Services.AddContextMoleDocuments();
            builder.Services.AddWritableContextMoleStorage();
            builder.Services.AddContextMoleIndexing();
            builder.Services.AddSingleton<ApplicationUpdateService>();
            builder.Services.AddSingleton<WindowsStartupService>();
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