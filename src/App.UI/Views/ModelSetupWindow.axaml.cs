using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

using ContextMole.Core;
using ContextMole.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

namespace ContextMole.App.UI.Views;

public partial class ModelSetupWindow : Window
{
    private readonly GraniteModelInstaller _installer;
    private readonly GraniteEmbeddingModelDefinition _model;
    private CancellationTokenSource? _installation;
    private bool _installing;

    public ModelSetupWindow() : this(
        Program.Services.GetRequiredService<GraniteModelInstaller>(),
        GraniteEmbeddingModels.Get(Program.Services.GetRequiredService<IEmbeddingModelSettings>().Model))
    {
    }

    public ModelSetupWindow(GraniteModelInstaller installer, GraniteEmbeddingModelDefinition model)
    {
        GraniteEmbeddingModels.EnsureSupported(model);
        _installer = installer;
        _model = model;
        InitializeComponent();
        var isRepair = _installer.HasModelAssets(_model.Choice);
        if (isRepair)
        {
            Title = "Repair semantic search";
            SetupTitleBlock.Text = "Repair semantic search";
            ValidationTitleBlock.Text = "Verification and repair";
            StatusBlock.Text = "Ready to verify the local model files.";
            InstallButton.Content = "Verify and repair";
        }
        ModelNameBlock.Text = _model.DisplayName;
        ModelDescriptionBlock.Text = "Find passages with similar meaning across languages using the local Granite Multilingual 97M model. Keyword search needs no model download.";
        DownloadDescriptionBlock.Text = isRepair
            ? "Existing files will be verified. Only missing or damaged model files will be downloaded again. Your documents are never included."
            : $"{_model.DisplayName} is stored only on this computer. Downloads are checksum-verified, resumable, and never include your documents.";

        if (!_model.RequiresGemmaTerms)
        {
            TermsCard.IsVisible = false;
            InstallButton.IsEnabled = true;
        }
        else if (_installer.HasRecordedTermsAcceptance)
        {
            AcceptTermsCheckBox.IsChecked = true;
            AcceptTermsCheckBox.IsEnabled = false;
            AcceptTermsCheckBox.Content = "Gemma terms were previously accepted on this computer";
            InstallButton.IsEnabled = true;
        }
        Closing += (_, args) =>
        {
            if (!_installing) return;
            args.Cancel = true;
            _installation?.Cancel();
        };
    }

    private async void OpenTerms(object? sender, RoutedEventArgs args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(GraniteModelInstaller.GemmaTermsUrl) { UseShellExecute = true })?
                .Dispose();
        }
        catch (Exception exception)
        {
            await ConfirmWindow.ShowErrorAsync(this, $"Could not open the Gemma terms: {exception.Message}");
        }
    }

    private void AcceptanceChanged(object? sender, RoutedEventArgs args) =>
        InstallButton.IsEnabled = !_installing && (!_model.RequiresGemmaTerms || AcceptTermsCheckBox.IsChecked == true);

    private async void Install(object? sender, RoutedEventArgs args)
    {
        if (_installing || (_model.RequiresGemmaTerms && AcceptTermsCheckBox.IsChecked != true)) return;
        _installing = true;
        _installation = new CancellationTokenSource();
        AcceptTermsCheckBox.IsEnabled = false;
        InstallButton.IsEnabled = false;
        CancelButton.Content = "Cancel setup";
        CancelButton.IsEnabled = true;
        var progress = new Progress<ModelInstallProgress>(value => Dispatcher.UIThread.Post(() => ShowProgress(value)));
        try
        {
            await _installer.InstallAsync(_model.Choice, AcceptTermsCheckBox.IsChecked == true, progress, _installation.Token);
            StatusBlock.Text = "Finalizing setup…";
            DownloadProgress.IsIndeterminate = true;
            _installing = false;
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 1;
            Close(true);
        }
        catch (OperationCanceledException)
        {
            _installing = false;
            Close(false);
        }
        catch (Exception exception)
        {
            _installing = false;
            StatusBlock.Text = $"Setup failed: {exception.Message}";
            BytesBlock.Text = "Any completed files and partial downloads that remain are kept. Retry to resume; files will be verified before use.";
            DownloadProgress.IsIndeterminate = false;
            AcceptTermsCheckBox.IsEnabled = _model.RequiresGemmaTerms && !_installer.HasRecordedTermsAcceptance;
            InstallButton.IsEnabled = !_model.RequiresGemmaTerms || AcceptTermsCheckBox.IsChecked == true;
            CancelButton.Content = "Close";
            CancelButton.IsEnabled = true;
            InstallButton.Content = "Retry setup";
        }
        finally
        {
            _installation?.Dispose();
            _installation = null;
        }
    }

    private void Cancel(object? sender, RoutedEventArgs args)
    {
        if (_installing)
        {
            CancelButton.IsEnabled = false;
            StatusBlock.Text = "Cancelling…";
            _installation?.Cancel();
            return;
        }
        Close(false);
    }

    private void ShowProgress(ModelInstallProgress progress)
    {
        if (!_installing) return;
        StatusBlock.Text = progress.Stage switch
        {
            "downloading" => $"Downloading {progress.AssetName}…",
            "verifying" => $"Verifying {progress.AssetName}…",
            "verified" => $"Verified {progress.AssetName}.",
            "waiting_cpu" => "Waiting for processor capacity before validating the optimized model…",
            "validating" => "Validating the optimized model on this computer…",
            "complete" => "Download complete.",
            _ => progress.AssetName
        };
        DownloadProgress.IsIndeterminate = progress.Stage is "verifying" or "waiting_cpu" or "validating" || progress.Fraction is null;
        if (progress.Fraction is { } fraction) DownloadProgress.Value = fraction;
        BytesBlock.Text = progress.TotalBytes is { } total
            ? $"Current file: {FormatBytes(progress.BytesReceived)} of {FormatBytes(total)}"
            : progress.BytesReceived > 0 ? FormatBytes(progress.BytesReceived) : string.Empty;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
