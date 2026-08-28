using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

using ContextMole.App.UI.ViewModels;
using ContextMole.Core;
using ContextMole.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

namespace ContextMole.App.UI.Views;

public partial class SettingsView : UserControl
{
    private const string ManualSetupUrl = "https://github.com/JPCodeCraft/Context-Mole#manual-mcp-setup";
    private bool _changingEmbeddingModel;

    public SettingsView()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

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

    private async void RetryOcrSetup(object? sender, RoutedEventArgs args) =>
        await RunUiActionAsync(() => ViewModel.RetryOcrSetupAsync());

    private async void EmbeddingModelChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_changingEmbeddingModel ||
            sender is not ComboBox { SelectedItem: GraniteEmbeddingModelDefinition model } comboBox ||
            model.Choice == ViewModel.SelectedEmbeddingModel.Choice) return;

        _changingEmbeddingModel = true;
        var previous = ViewModel.SelectedEmbeddingModel;
        var installer = Program.Services.GetRequiredService<GraniteModelInstaller>();
        try
        {
            var confirmed = await ConfirmWindow.AskAsync(
                Owner,
                "Switch embedding model?",
                $"Switching from {previous.DisplayName} to {model.DisplayName} will discard the existing semantic embeddings and rebuild them for every active project. This can take a while for large projects.\n\n" +
                "Semantic search will be unavailable until the rebuild finishes. Keyword search will remain available throughout. Paused projects will be updated after you resume them.",
                "Switch model");
            if (!confirmed)
            {
                comboBox.SelectedItem = previous;
                return;
            }

            if (!installer.IsModelInstalled(model.Choice))
            {
                var installed = await new ModelSetupWindow(installer, model).ShowDialog<bool>(Owner);
                if (!installed)
                {
                    comboBox.SelectedItem = previous;
                    return;
                }
            }

            await ViewModel.SetEmbeddingModelAsync(model);
        }
        catch (Exception exception)
        {
            comboBox.SelectedItem = ViewModel.SelectedEmbeddingModel;
            var message = installer.IsModelInstalled(model.Choice)
                ? exception.Message
                : $"{exception.Message}\n\nSelect {model.DisplayName} again to verify and repair its local files.";
            await ConfirmWindow.ShowErrorAsync(Owner, message);
        }
        finally
        {
            _changingEmbeddingModel = false;
        }
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
        var model = ViewModel.SelectedEmbeddingModel;
        try
        {
            if (!installer.IsModelInstalled(model.Choice) || ViewModel.IsSemanticSearchUnavailable)
            {
                var installed = await new ModelSetupWindow(installer, model).ShowDialog<bool>(Owner);
                if (!installed) return;
            }

            await ViewModel.SetEmbeddingModelAsync(model);
        }
        catch (Exception exception)
        {
            var message = installer.IsModelInstalled(model.Choice)
                ? exception.Message
                : $"{exception.Message}\n\nUse Download selected model again to verify and repair its local files.";
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
