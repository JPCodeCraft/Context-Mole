using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

using ContextMole.App.UI.ViewModels;
using ContextMole.Core;
using ContextMole.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

namespace ContextMole.App.UI.Views;

public partial class SettingsView : UserControl
{
    private const string ReleaseNotesUrl = "https://github.com/JPCodeCraft/Context-Mole/releases";
    private const string ManualSetupUrl = "https://github.com/JPCodeCraft/Context-Mole#manual-mcp-setup";

    public SettingsView()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private async void CheckAiConnections(object? sender, RoutedEventArgs args) =>
        await RunUiActionAsync(ViewModel.CheckAiConnectionsAsync);

    private async void ToggleAiConnection(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { CommandParameter: AiConnectionItemViewModel connection }) return;
        try
        {
            var result = await ViewModel.ToggleAiConnectionAsync(connection);
            if (result.State is AiConnectionState.Conflict or AiConnectionState.ServerUnavailable)
            {
                await ConfirmWindow.ShowErrorAsync(Owner, result.Message);
                return;
            }

            if (result.RestartRequired)
            {
                var title = result.State == AiConnectionState.Connected
                    ? $"Configured for {result.Client.DisplayName}"
                    : $"Removed from {result.Client.DisplayName}";
                await ConfirmWindow.ShowMessageAsync(Owner, title, result.Message);
            }
        }
        catch (Exception exception)
        {
            await ConfirmWindow.ShowErrorAsync(Owner, exception.Message);
        }
    }

    private async void OpenManualSetupGuide(object? sender, RoutedEventArgs args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ManualSetupUrl) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception)
        {
            await ConfirmWindow.ShowErrorAsync(Owner, $"Could not open the setup guide: {exception.Message}");
        }
    }

    private async void CpuUsageProfileChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: CpuUsageProfile profile } ||
            profile == ViewModel.SelectedCpuUsageProfile) return;
        await RunUiActionAsync(() => ViewModel.SetCpuUsageProfileAsync(profile));
    }

    private void CancelOcrSetup(object? sender, RoutedEventArgs args) => ViewModel.CancelOcrSetup();

    private async void CheckApplicationUpdates(object? sender, RoutedEventArgs args) =>
        await RunUiActionAsync(ViewModel.CheckApplicationUpdatesAsync);

    private async void CopyConfigurationPath(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { CommandParameter: AiConnectionItemViewModel connection } ||
            TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await RunUiActionAsync(() => clipboard.SetTextAsync(connection.ConfigPath));
    }

    private async void OpenReleaseNotes(object? sender, RoutedEventArgs args)
    {
        await RunUiActionAsync(() =>
        {
            Process.Start(new ProcessStartInfo(ReleaseNotesUrl) { UseShellExecute = true })?.Dispose();
            return Task.CompletedTask;
        });
    }

    private async void RetryOcrSetup(object? sender, RoutedEventArgs args) =>
        await RunUiActionAsync(() => ViewModel.RetryOcrSetupAsync());

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
        if (!ViewModel.CanSetUpSemanticSearch) return;
        var installer = Program.Services.GetRequiredService<GraniteModelInstaller>();
        if (!installer.IsSupported) return;
        var model = ViewModel.SelectedEmbeddingModel;
        try
        {
            await ViewModel.SetUpSemanticSearchAsync(selected =>
                new ModelSetupWindow(installer, selected).ShowDialog<bool>(Owner));
        }
        catch (Exception exception)
        {
            var message = installer.IsModelInstalled(model.Choice)
                ? exception.Message
                : $"{exception.Message}\n\nUse the selected model's setup button again to verify and repair its local files.";
            await ConfirmWindow.ShowErrorAsync(Owner, message);
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

    private async void UninstallContextMole(object? sender, RoutedEventArgs args)
    {
        if (!ViewModel.CanUninstallFromSettings || Application.Current is not App app) return;

        var availability = Program.Services.GetRequiredService<WindowsUninstallService>().Availability;
        var choice = await new UninstallWindow(availability).ShowDialog<UninstallChoice?>(Owner);
        if (choice is null) return;

        try
        {
            await app.UninstallAsync(choice == UninstallChoice.DeleteData);
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
