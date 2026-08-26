using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

using MCPIndexSearch.App.UI.ViewModels;
using MCPIndexSearch.Core;
using MCPIndexSearch.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

namespace MCPIndexSearch.App.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, args) =>
        {
            if (Application.Current is App app && app.ShouldHideOnClose)
            {
                args.Cancel = true;
                Hide();
            }
            else if (Application.Current is App fallbackApp && !fallbackApp.IsQuitting)
            {
                args.Cancel = true;
                WindowState = WindowState.Minimized;
            }
        };
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    private async void AddProject(object? sender, RoutedEventArgs args)
    {
        var result = await new ProjectEditorWindow().ShowDialog<ProjectEditorResult?>(this);
        if (result is null) return;
        await RunUiActionAsync(() => ViewModel.CreateAsync(result.Name, result.Folders));
    }

    private async void ToggleCodexConnection(object? sender, RoutedEventArgs args)
    {
        try
        {
            var result = await ViewModel.ToggleCodexConnectionAsync();
            if (result.State is CodexMcpConnectionState.Conflict or CodexMcpConnectionState.ServerUnavailable)
            {
                await ConfirmWindow.ShowErrorAsync(this, result.Message);
                return;
            }

            if (result.RestartRequired)
            {
                var title = result.State == CodexMcpConnectionState.Connected
                    ? "Connected to Codex"
                    : "Disconnected from Codex";
                await ConfirmWindow.ShowMessageAsync(this, title, result.Message);
            }
        }
        catch (Exception exception)
        {
            await ConfirmWindow.ShowErrorAsync(this, exception.Message);
        }
    }

    private async void SetupSemanticSearch(object? sender, RoutedEventArgs args)
    {
        var installer = Program.Services.GetRequiredService<GraniteModelInstaller>();
        if (!installer.IsSupported) return;
        var generator = Program.Services.GetRequiredService<IEmbeddingGenerator>();
        var installed = await new ModelSetupWindow(installer, generator).ShowDialog<bool>(this);
        ViewModel.RefreshAssetAvailability();
        if (installed)
        {
            ViewModel.StatusMessage = "Semantic search is enabled. Existing projects will be re-embedded in the background.";
        }
    }

    private async void EditProject(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedProject is not { } project) return;
        var result = await new ProjectEditorWindow(project.ToSummary()).ShowDialog<ProjectEditorResult?>(this);
        if (result is null) return;
        await RunUiActionAsync(() => ViewModel.UpdateAsync(project.Id, result.Name, result.Folders));
    }

    private async void TogglePause(object? sender, RoutedEventArgs args) => await RunUiActionAsync(ViewModel.TogglePauseAsync);

    private async void ReindexProject(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedProject is null) return;
        if (!await ConfirmWindow.AskAsync(this, "Reindex project?", "A fresh index will be built. Original files remain untouched.")) return;
        await RunUiActionAsync(ViewModel.ReindexAsync);
    }

    private async void RemoveProject(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedProject is null) return;
        if (!await ConfirmWindow.AskAsync(this, "Remove project?", "Only local index records will be removed. Original files remain untouched.")) return;
        await RunUiActionAsync(ViewModel.RemoveAsync);
    }

    private async void QuitApplication(object? sender, RoutedEventArgs args)
    {
        if (Application.Current is App app) await app.QuitAsync();
    }

    private async void RestartToUpdate(object? sender, RoutedEventArgs args)
    {
        if (!ViewModel.CanRestartForUpdate || Application.Current is not App app) return;

        try
        {
            await app.RestartForUpdateAsync();
        }
        catch (Exception exception)
        {
            await ConfirmWindow.ShowErrorAsync(this, exception.Message);
        }
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { await ConfirmWindow.ShowErrorAsync(this, exception.Message); }
    }
}