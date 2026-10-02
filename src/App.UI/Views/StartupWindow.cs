using System.Diagnostics;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;

namespace ContextMole.App.UI.Views;

/// <summary>An honest indeterminate screen while the host safely opens and upgrades local data.</summary>
internal sealed class StartupWindow : Window
{
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _close;
    private readonly WrapPanel _actions;
    private bool _allowClose;
    private bool _failed;

    public event Action? CancelRequested;

    public StartupWindow(bool background)
    {
        Title = "Starting Context Mole";
        Width = 670;
        Height = 370;
        MinWidth = 520;
        MinHeight = 310;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (background)
        {
            ShowActivated = false;
            ShowInTaskbar = false;
            Opacity = 0;
        }
        _status = new TextBlock
        {
            Text = "Opening your local index…",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 14
        };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _progress = new ProgressBar { IsIndeterminate = true, Height = 7 };
        AutomationProperties.SetName(_progress, "Opening and upgrading the local index");
        _close = new Button { Content = "Cancel startup", IsCancel = true, IsDefault = true, Margin = new Thickness(0, 0, 8, 8) };
        _close.Click += (_, _) => RequestCancel();
        _actions = new WrapPanel { Orientation = Orientation.Horizontal };
        _actions.Children.Add(_close);
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(28),
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = "Starting Context Mole", FontSize = 25, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "Existing indexed evidence is being preserved. Large index upgrades may take a few minutes.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    _status,
                    _progress,
                    _actions
                }
            }
        };
        Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            RequestCancel();
        };
    }

    public void Report(string stage) => _status.Text = stage;

    public void ShowFailure(string message)
    {
        _failed = true;
        Title = "Context Mole could not start";
        _status.IsVisible = false;
        _progress.IsVisible = false;
        if (Content is ScrollViewer { Content: StackPanel panel })
        {
            var details = new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            panel.Children.Insert(panel.Children.Count - 1, details);
        }
        _close.Content = "Close";
        _close.IsEnabled = true;
        var copy = new Button { Content = "Copy details", Margin = new Thickness(0, 0, 8, 8) };
        copy.Click += async (_, _) =>
        {
            try { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(message); }
            catch (Exception exception) { _status.IsVisible = true; _status.Text = $"Could not copy details: {exception.Message}"; }
        };
        _actions.Children.Insert(0, copy);
        var guide = new Button { Content = "Read recovery guide", Margin = new Thickness(0, 0, 8, 8) };
        guide.Click += async (_, _) =>
        {
            string text;
            try { text = await File.ReadAllTextAsync(Program.StartupRecoveryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                text = $"The bundled guide could not be read: {exception.Message}\n\n" +
                    "Keep the database. Close Context Mole before making a backup, and keep any WAL/SHM companion files. " +
                    "Do not delete or recreate the index to recover from a startup failure. Review and resolve the logged cause, then restart.";
            }
            var close = new Button { Content = "Close", IsDefault = true, IsCancel = true };
            var window = new Window
            {
                Title = "Local index recovery guide", Width = 760, Height = 620,
                MinWidth = 500, MinHeight = 350, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new DockPanel
                {
                    Margin = new Thickness(24),
                    Children =
                    {
                        close,
                        new ScrollViewer { Content = new SelectableTextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap } }
                    }
                }
            };
            DockPanel.SetDock(close, Dock.Bottom);
            close.Click += (_, _) => window.Close();
            await window.ShowDialog(this);
        };
        _actions.Children.Insert(1, guide);
        if (Program.StartupLogsDirectory is { } logsDirectory)
        {
            var logs = new Button { Content = "Open logs", Margin = new Thickness(0, 0, 8, 8) };
            logs.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(logsDirectory) { UseShellExecute = true })?.Dispose(); }
                catch (Exception exception)
                {
                    _status.IsVisible = true;
                    _status.Text = $"Could not open the log folder: {exception.Message}. Logs: {logsDirectory}";
                }
            };
            _actions.Children.Insert(2, logs);
        }
        Opacity = 1;
        ShowInTaskbar = true;
        ShowActivated = true;
        Activate();
    }

    public void CompleteAndClose()
    {
        _allowClose = true;
        Close();
    }

    private void RequestCancel()
    {
        if (!_close.IsEnabled) return;
        _close.IsEnabled = false;
        if (!_failed) _status.Text = "Canceling startup and closing local services safely…";
        CancelRequested?.Invoke();
    }
}
