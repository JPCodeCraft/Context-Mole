using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

using ContextMole.App.UI.ViewModels;
using ContextMole.App.UI.Views;
using ContextMole.Indexing;

using Microsoft.Extensions.DependencyInjection;

namespace ContextMole.App.UI;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = Program.Services.GetRequiredService<MainViewModel>();
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;
            ConfigureTray(window);
            viewModel.StartPolling();
        }
        base.OnFrameworkInitializationCompleted();
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
