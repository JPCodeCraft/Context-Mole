using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MCPIndexSearch.Core;
using MCPIndexSearch.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace MCPIndexSearch.App.UI.Views;

public partial class ModelSetupWindow : Window
{
    private readonly GraniteModelInstaller _installer;
    private readonly IEmbeddingGenerator _embeddingGenerator;
    private CancellationTokenSource? _installation;
    private bool _installing;

    public ModelSetupWindow() : this(
        Program.Services.GetRequiredService<GraniteModelInstaller>(),
        Program.Services.GetRequiredService<IEmbeddingGenerator>())
    {
    }

    public ModelSetupWindow(GraniteModelInstaller installer, IEmbeddingGenerator embeddingGenerator)
    {
        _installer = installer;
        _embeddingGenerator = embeddingGenerator;
        InitializeComponent();
        if (_installer.HasRecordedTermsAcceptance)
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

    private void OpenTerms(object? sender, RoutedEventArgs args)
    {
        Process.Start(new ProcessStartInfo(GraniteModelInstaller.GemmaTermsUrl) { UseShellExecute = true });
    }

    private void AcceptanceChanged(object? sender, RoutedEventArgs args) =>
        InstallButton.IsEnabled = !_installing && AcceptTermsCheckBox.IsChecked == true;

    private async void Install(object? sender, RoutedEventArgs args)
    {
        if (_installing || AcceptTermsCheckBox.IsChecked != true) return;
        _installing = true;
        _installation = new CancellationTokenSource();
        AcceptTermsCheckBox.IsEnabled = false;
        InstallButton.IsEnabled = false;
        CancelButton.Content = "Cancel download";
        var progress = new Progress<ModelInstallProgress>(value => Dispatcher.UIThread.Post(() => ShowProgress(value)));
        try
        {
            await _installer.InstallAsync(true, progress, _installation.Token);
            StatusBlock.Text = "Loading the model…";
            DownloadProgress.IsIndeterminate = true;
            await _embeddingGenerator.ReloadAsync(_installation.Token);
            if (!_embeddingGenerator.IsAvailable)
                throw new InvalidOperationException(_embeddingGenerator.UnavailableReason ?? "The model could not be loaded.");
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
            BytesBlock.Text = "The verified part of the download is retained so setup can resume.";
            DownloadProgress.IsIndeterminate = false;
            AcceptTermsCheckBox.IsEnabled = true;
            InstallButton.IsEnabled = AcceptTermsCheckBox.IsChecked == true;
            CancelButton.Content = "Close";
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
        StatusBlock.Text = progress.Stage switch
        {
            "downloading" => $"Downloading {progress.AssetName}…",
            "verifying" => $"Verifying {progress.AssetName}…",
            "verified" => $"Verified {progress.AssetName}.",
            "validating" => "Validating the optimized model on this computer…",
            "complete" => "Download complete.",
            _ => progress.AssetName
        };
        DownloadProgress.IsIndeterminate = progress.Stage is "verifying" or "validating" || progress.Fraction is null;
        if (progress.Fraction is { } fraction) DownloadProgress.Value = fraction;
        BytesBlock.Text = progress.TotalBytes is { } total
            ? $"{FormatBytes(progress.BytesReceived)} of {FormatBytes(total)}"
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
