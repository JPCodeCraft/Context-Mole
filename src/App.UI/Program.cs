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
    internal static string StartupRecoveryPath => Path.Combine(AppContext.BaseDirectory, "docs", "LEGACY-INDEX-UPGRADES.md");
    private static IHost? _host;
    private static SingleInstanceLock? _instanceLock;
    private static string[] _startupArguments = [];
    private static readonly CancellationTokenSource StartupCancellation = new();
    private static Task? _startupTask;
    public static event Action<string>? StartupProgressChanged;

    public static IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("The application host has not started.");
    public static bool LaunchInBackground { get; private set; }
    public static string? StartupFailureMessage { get; private set; }
    public static string? StartupDatabasePath { get; private set; }
    public static string? StartupLogsDirectory { get; private set; }
    public static RetiredModelCacheCleanupResult RetiredModelCleanupResult { get; private set; } = RetiredModelCacheCleanupResult.Empty;

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        LaunchInBackground = args.Any(argument =>
            string.Equals(argument, WindowsStartupRegistration.BackgroundArgument, StringComparison.OrdinalIgnoreCase));

        _startupArguments = args;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Context Mole could not start: {exception.Message}");
            Log.Fatal(exception, "Application stopped unexpectedly");
            Environment.ExitCode = 1;
        }
        finally
        {
            CancelStartup();
            try { _startupTask?.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            ShutdownHostAsync().GetAwaiter().GetResult();
        }
    }

    public static void CancelStartup() => StartupCancellation.Cancel();

    public static Task InitializeHostAsync() => _startupTask ??= Task.Run(async () =>
    {
        IAppPaths? paths = null;
        try
        {
            StartupProgressChanged?.Invoke("Opening your local data directory…");
            StartupCancellation.Token.ThrowIfCancellationRequested();
            paths = new AppPaths();
            StartupDatabasePath = paths.DatabasePath;
            StartupLogsDirectory = paths.LogsDirectory;
            await StartHostAsync(_startupArguments, paths, StartupCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (StartupCancellation.IsCancellationRequested)
        {
            Log.Information("Application startup canceled");
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Context Mole could not start: {exception.Message}");
            Log.Fatal(exception, "Application startup failed at {DatabasePath}; logs {LogsDirectory}",
                paths?.DatabasePath, paths?.LogsDirectory);
            StartupFailureMessage = FormatStartupFailure(exception.Message, paths?.DatabasePath, paths?.LogsDirectory);
            Environment.ExitCode = 1;
            await ShutdownHostAsync(closeLog: false).ConfigureAwait(false);
        }
    });

    internal static string FormatStartupFailure(string cause, string? databasePath, string? logsDirectory) =>
        $"Context Mole could not initialize.\n\n{cause}\n\n" +
        $"Database: {databasePath ?? "Unavailable"}\n" +
        $"Logs: {logsDirectory ?? "Logging could not be initialized"}\n\n" +
        "Keep this database; do not delete it or reinstall to recreate it. Existing indexed evidence has not been replaced. " +
        "Close Context Mole before making a backup, and keep any database WAL/SHM companion files. " +
        "Review the log details to resolve the error, then retry. Use Read recovery guide for the bundled upgrade and backup instructions.";

    private static async Task StartHostAsync(string[] args, IAppPaths paths, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _instanceLock = SingleInstanceLock.Acquire(paths);
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(paths.LogsDirectory, "ui-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14, shared: true)
            .CreateLogger();
        // Keep the logger alive if host startup fails so error-window failures are also recorded.
        builder.Services.AddSerilog(dispose: false);
        Log.Information("Starting Context Mole {ApplicationVersion}; data directory {DataDirectory}, database {DatabasePath}, logs {LogsDirectory}",
            typeof(Program).Assembly.GetName().Version, paths.DataDirectory, paths.DatabasePath, paths.LogsDirectory);
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
        // Only the native UI upgrades its app-managed model cache. Broker/settings/tool
        // metadata readers do not clean caches, including historical benchmark assets.
        StartupProgressChanged?.Invoke("Checking the app-managed model cache…");
        cancellationToken.ThrowIfCancellationRequested();
        RetiredModelCleanupResult = RunRetiredModelCleanup(_host.Services.GetRequiredService<RetiredModelCacheCleanup>());
        StartupProgressChanged?.Invoke("Opening the local index and checking its saved-data format. Any required upgrade runs here before the workspace opens…");
        await _host.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static RetiredModelCacheCleanupResult RunRetiredModelCleanup(RetiredModelCacheCleanup cleanup) => cleanup.TryCleanup();

    public static async Task ShutdownHostAsync(bool closeLog = true)
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
                if (closeLog) Log.CloseAndFlush();
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
