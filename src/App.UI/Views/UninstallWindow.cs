using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ContextMole.App.UI.Views;

internal enum UninstallChoice
{
    KeepData,
    DeleteData,
}

internal sealed class UninstallWindow : Window
{
    private readonly RadioButton _keepData;
    private readonly RadioButton _deleteData;
    private readonly Border _irreversibleWarning;
    private readonly Button _uninstallButton;

    public UninstallWindow(WindowsUninstallAvailability availability)
    {
        Title = "Uninstall Context Mole";
        Width = 620;
        MinHeight = 490;
        MaxHeight = 720;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = new WindowIcon("avares://ContextMole.App.UI/Assets/context-mole.ico");

        _keepData = new RadioButton
        {
            GroupName = "uninstall-data-choice",
            IsChecked = true,
            Content = BuildOption(
                "Keep data",
                "Keep indexes, downloaded models, settings, temporary materializations, and logs for a future reinstall."),
        };

        _deleteData = new RadioButton
        {
            GroupName = "uninstall-data-choice",
            IsEnabled = availability.CanDeleteData,
            Content = BuildOption(
                "Permanently delete local application data",
                $"Delete indexes, downloaded models, settings, temporary materializations, and logs from:\n{availability.DataDirectory}"),
        };

        _irreversibleWarning = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Color.Parse("#2A1820")),
            BorderBrush = new SolidColorBrush(Color.Parse("#6D3340")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(14),
            Child = new TextBlock
            {
                Text = "This deletion is irreversible. Context Mole cannot restore these indexes, models, settings, or logs.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#FF8996")),
                FontWeight = FontWeight.SemiBold,
            },
        };

        _uninstallButton = new Button
        {
            Content = "Uninstall",
            MinWidth = 112,
            IsDefault = false,
        };
        _uninstallButton.Classes.Add("primary");
        _uninstallButton.Click += (_, _) => Close(
            _deleteData.IsChecked == true ? UninstallChoice.DeleteData : UninstallChoice.KeepData);

        _keepData.Click += (_, _) => RefreshSelectedChoice();
        _deleteData.Click += (_, _) => RefreshSelectedChoice();

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            IsCancel = true,
        };
        cancel.Classes.Add("ghost");
        cancel.Click += (_, _) => Close(null);

        var choicePanel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                BuildOptionCard(_keepData),
                BuildOptionCard(_deleteData),
            },
        };

        if (!availability.CanDeleteData)
        {
            choicePanel.Children.Add(new TextBlock
            {
                Text = $"Local data deletion is unavailable because Context Mole is using the custom directory '{availability.DataDirectory}'. The directory will be kept and must be removed manually.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#F2C66D")),
                FontSize = 12,
            });
        }

        Content = new StackPanel
        {
            Margin = new Thickness(26),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = "Uninstall Context Mole?",
                    FontSize = 22,
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock
                {
                    Text = "The Windows uninstaller will remove the application after Context Mole safely stops its background services.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#A2AEC0")),
                },
                choicePanel,
                _irreversibleWarning,
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#182A47")),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(14),
                    Child = new TextBlock
                    {
                        Text = "Your indexed source files are never deleted. Only Context Mole's own local application data can be removed.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#83ADFF")),
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, _uninstallButton },
                },
            },
        };
    }

    private static Border BuildOptionCard(Control option) => new()
    {
        Background = new SolidColorBrush(Color.Parse("#151E2D")),
        BorderBrush = new SolidColorBrush(Color.Parse("#34445E")),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(14),
        Child = option,
    };

    private static StackPanel BuildOption(string title, string description) => new()
    {
        Margin = new Thickness(7, 1, 0, 1),
        Spacing = 4,
        Children =
        {
            new TextBlock
            {
                Text = title,
                FontWeight = FontWeight.SemiBold,
            },
            new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#A2AEC0")),
                FontSize = 12,
            },
        },
    };

    private void RefreshSelectedChoice()
    {
        var deletesData = _deleteData.IsChecked == true;
        _irreversibleWarning.IsVisible = deletesData;
        _uninstallButton.Content = deletesData ? "Uninstall and delete data" : "Uninstall";
        _uninstallButton.Classes.Clear();
        _uninstallButton.Classes.Add(deletesData ? "destructive" : "primary");
        _uninstallButton.IsDefault = false;
    }
}
