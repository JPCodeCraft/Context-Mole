using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ContextMole.App.UI.Views;

public sealed class ConfirmWindow : Window
{
    private ConfirmWindow(string title, string message, bool singleAction, string acceptLabel, bool destructive,
        bool isError = false)
    {
        Title = title;
        Width = 480;
        MinHeight = 220;
        MaxHeight = 560;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = new WindowIcon("avares://ContextMole.App.UI/Assets/context-mole.ico");

        var accept = new Button
        {
            Content = acceptLabel,
            MinWidth = 96,
            IsDefault = true,
        };
        accept.Classes.Add(destructive ? "destructive" : "primary");
        accept.Click += (_, _) => Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        if (!singleAction)
        {
            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 90,
                IsCancel = true,
            };
            cancel.Classes.Add("ghost");
            cancel.Click += (_, _) => Close(false);
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(accept);

        var showDangerMarker = destructive || isError;
        var marker = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.Parse(showDangerMarker ? "#2A1820" : "#182A47")),
            Child = new TextBlock
            {
                Text = showDangerMarker ? "!" : "i",
                FontSize = 17,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse(showDangerMarker ? "#FF8996" : "#6E9FFF")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 12,
            Children =
            {
                marker,
                new TextBlock
                {
                    Text = title,
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    [Grid.ColumnProperty] = 1,
                },
            },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(26),
            Spacing = 18,
            Children =
            {
                heading,
                new ScrollViewer
                {
                    MaxHeight = 340,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = new SelectableTextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#A2AEC0")),
                        LineHeight = 20,
                    },
                },
                buttons,
            },
        };
    }

    public static Task<bool> AskAsync(Window owner, string title, string message,
        string acceptLabel = "Continue", bool destructive = false) =>
        new ConfirmWindow(title, message, false, acceptLabel, destructive).ShowDialog<bool>(owner);

    public static Task<bool> ShowErrorAsync(Window owner, string message) =>
        new ConfirmWindow("Operation failed", message, true, "OK", false, isError: true).ShowDialog<bool>(owner);

    public static Task<bool> ShowMessageAsync(Window owner, string title, string message) =>
        new ConfirmWindow(title, message, true, "OK", false).ShowDialog<bool>(owner);
}
