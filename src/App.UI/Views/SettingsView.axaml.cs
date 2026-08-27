using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

using MCPIndexSearch.App.UI.ViewModels;
using MCPIndexSearch.Core;
using MCPIndexSearch.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

namespace MCPIndexSearch.App.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private async void ToggleCodexConnection(object? sender, RoutedEventArgs args)
    {
        try
        {
            var result = await ViewModel.ToggleCodexConnectionAsync();
            if (result.State is CodexMcpConnectionState.Conflict or CodexMcpConnectionState.ServerUnavailable)
            {
                await ConfirmWindow.ShowErrorAsync(Owner, result.Message);
                return;
            }

            if (result.RestartRequired)
            {
                var title = result.State == CodexMcpConnectionState.Connected
                    ? "Connected to Codex"
                    : "Disconnected from Codex";
                await ConfirmWindow.ShowMessageAsync(Owner, title, result.Message);
            }
        }
        catch (Exception exception)
        {
            await ConfirmWindow.ShowErrorAsync(Owner, exception.Message);
        }
    }

    private async void CpuUsageProfileChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: CpuUsageProfile profile } ||
            profile == ViewModel.SelectedCpuUsageProfile) return;
        await RunUiActionAsync(() => ViewModel.SetCpuUsageProfileAsync(profile));
    }

    private async void StartWithWindowsChanged(object? sender, RoutedEventArgs args)
    {
        if (sender is not CheckBox checkBox) return;
        try
        {
            ViewModel.SetStartWithWindows(checkBox.IsChecked == true);
        }
        catch (Exception exception)
        {
            checkBox.IsChecked = ViewModel.StartWithWindowsEnabled;
            await ConfirmWindow.ShowErrorAsync(Owner, exception.Message);
        }
    }

    private async void SetupSemanticSearch(object? sender, RoutedEventArgs args)
    {
        var installer = Program.Services.GetRequiredService<GraniteModelInstaller>();
        if (!installer.IsSupported) return;
        var generator = Program.Services.GetRequiredService<IEmbeddingGenerator>();
        var installed = await new ModelSetupWindow(installer, generator).ShowDialog<bool>(Owner);
        ViewModel.RefreshAssetAvailability();
        if (installed)
        {
            ViewModel.StatusMessage = "Semantic search is enabled. Existing projects will be re-embedded in the background.";
        }
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
            await ConfirmWindow.ShowErrorAsync(Owner, exception.Message);
        }
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            await ConfirmWindow.ShowErrorAsync(Owner, exception.Message);
        }
    }
}
