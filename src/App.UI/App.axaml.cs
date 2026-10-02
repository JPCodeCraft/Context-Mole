using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

using ContextMole.App.UI.ViewModels;
using ContextMole.App.UI.Views;
using ContextMole.Indexing;

using Microsoft.Extensions.DependencyInjection;

namespace ContextMole.App.UI;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private bool _quitting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            // Avalonia 12.1.1 posts its async Linux tray-watch cancellation after Dispose.
            // Only that exact expected shutdown path is handled; runtime/startup failures
            // and cancellation from our own services still follow the normal error path.
            if (DesktopShutdownExceptionPolicy.IsExpectedTrayCancellation(_quitting,
                    OperatingSystem.IsLinux(), args.Exception)) args.Handled = true;
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var startup = new StartupWindow(Program.LaunchInBackground);
            desktop.MainWindow = startup;
            var cancelRequested = false;
            startup.CancelRequested += () =>
            {
                cancelRequested = true;
                Program.CancelStartup();
                if (Program.StartupFailureMessage is not null)
                {
                    startup.CompleteAndClose();
                    desktop.Shutdown(1);
                }
            };
            Action<string> report = stage => Dispatcher.UIThread.Post(() => startup.Report(stage));
            Program.StartupProgressChanged += report;
            // Post after framework initialization so a visible startup window can render before
            // any migration/cache work. The normal workspace cannot open against an unready host.
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await Program.InitializeHostAsync();
                    if (cancelRequested)
                    {
                        await Program.ShutdownHostAsync();
                        startup.CompleteAndClose();
                        desktop.Shutdown();
                        return;
                    }
                    if (Program.StartupFailureMessage is { } failure)
                    {
                        startup.ShowFailure(failure);
                        return;
                    }
                    OpenWorkspace(desktop);
                    startup.CompleteAndClose();
                }
                catch (OperationCanceledException)
                {
                    await Program.ShutdownHostAsync();
                    startup.CompleteAndClose();
                    desktop.Shutdown();
                }
                catch (Exception exception)
                {
                    await Program.ShutdownHostAsync(closeLog: false);
                    startup.ShowFailure(Program.FormatStartupFailure(exception.Message, Program.StartupDatabasePath, Program.StartupLogsDirectory));
                    startup.CancelRequested += () =>
                    {
                        startup.CompleteAndClose();
                        desktop.Shutdown(1);
                    };
                }
                finally
                {
                    Program.StartupProgressChanged -= report;
                }
            });
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OpenWorkspace(IClassicDesktopStyleApplicationLifetime desktop)
    {
            var viewModel = Program.Services.GetRequiredService<MainViewModel>();
            viewModel.ReportRetiredModelCleanup(Program.RetiredModelCleanupResult);
            var window = new MainWindow { DataContext = viewModel };
            if (Program.LaunchInBackground)
            {
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Opacity = 0;
            }
            desktop.MainWindow = window;
            ConfigureTray(window);
            if (Program.LaunchInBackground && _trayIcon is not null)
            {
                EventHandler? hideInitialWindow = null;
                hideInitialWindow = (_, _) =>
                {
                    window.Opened -= hideInitialWindow;
                    window.Hide();
                    window.Opacity = 1;
                };
                window.Opened += hideInitialWindow;
            }
            else
            {
                window.ShowActivated = true;
                window.ShowInTaskbar = true;
                window.Opacity = 1;
            }
            viewModel.StartPolling();
            window.Show();
    }

    public async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        var desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        try
        {
            var pollingStop = Program.Services.GetService<MainViewModel>() is { } viewModel
                ? viewModel.StopPollingAsync()
                : Task.CompletedTask;
            await Task.WhenAll(pollingStop, Program.ShutdownHostAsync());
        }
        finally
        {
            desktop?.Shutdown();
        }
    }

    private void ConfigureTray(Window window)
    {
        try
        {
            var show = new NativeMenuItem("Show Context Mole");
            show.Click += (_, _) => ShowWindow(window);
            var quit = new NativeMenuItem("Quit");
            quit.Click += async (_, _) => await QuitAsync();
            var menu = new NativeMenu();
            menu.Add(show);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(quit);
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ContextMole.App.UI/Assets/context-mole.ico"))),
                ToolTipText = "Context Mole",
                Menu = menu,
                IsVisible = true
            };
            _trayIcon.Clicked += (_, _) => ShowWindow(window);
        }
        catch (Exception)
        {
            _trayIcon = null;
        }
    }

    private static void ShowWindow(Window window)
    {
        window.Opacity = 1;
        window.ShowActivated = true;
        window.ShowInTaskbar = true;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public bool ShouldHideOnClose => !_quitting && _trayIcon is not null && !OperatingSystem.IsLinux();
    public bool IsQuitting => _quitting;

    public async Task RestartForUpdateAsync()
    {
        if (Program.Services.GetRequiredService<IndexingActivityTracker>().HasActiveItems)
        {
            throw new InvalidOperationException("Wait for active indexing to finish before restarting to update.");
        }

        var updateService = Program.Services.GetRequiredService<ApplicationUpdateService>();
        if (!updateService.PrepareRestart())
        {
            throw new InvalidOperationException("No downloaded application update is ready to install.");
        }

        await QuitAsync();
    }

    public async Task UninstallAsync(bool deleteLocalData)
    {
        var uninstallService = Program.Services.GetRequiredService<WindowsUninstallService>();
        uninstallService.StartUninstall(deleteLocalData);
        await QuitAsync();
    }
}
