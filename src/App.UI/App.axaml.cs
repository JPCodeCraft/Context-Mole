using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

using MCPIndexSearch.App.UI.ViewModels;
using MCPIndexSearch.App.UI.Views;
using MCPIndexSearch.Indexing;

using Microsoft.Extensions.DependencyInjection;

namespace MCPIndexSearch.App.UI;

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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Program.Services.GetService<MainViewModel>()?.StopPolling();
            await Program.ShutdownHostAsync();
            desktop.Shutdown();
        }
    }

    private void ConfigureTray(Window window)
    {
        try
        {
            var show = new NativeMenuItem("Show MCPIndexSearch");
            show.Click += (_, _) => ShowWindow(window);
            var quit = new NativeMenuItem("Quit");
            quit.Click += async (_, _) => await QuitAsync();
            var menu = new NativeMenu();
            menu.Add(show);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(quit);
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MCPIndexSearch.App.UI/Assets/mcp-index-search.ico"))),
                ToolTipText = "MCPIndexSearch",
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
}