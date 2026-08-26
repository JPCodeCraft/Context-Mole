using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MCPIndexSearch.App.UI.Views;

public sealed class ConfirmWindow : Window
{
    private ConfirmWindow(string title, string message, bool error)
    {
        Title = title;
        Width = 460;
        Height = 210;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var accept = new Button { Content = error ? "OK" : "Continue", MinWidth = 90 };
        accept.Click += (_, _) => Close(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        if (!error)
        {
            var cancel = new Button { Content = "Cancel", MinWidth = 90 };
            cancel.Click += (_, _) => Close(false);
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(accept);
        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                buttons
            }
        };
    }

    public static Task<bool> AskAsync(Window owner, string title, string message) => new ConfirmWindow(title, message, false).ShowDialog<bool>(owner);
    public static Task<bool> ShowErrorAsync(Window owner, string message) => new ConfirmWindow("Operation failed", message, true).ShowDialog<bool>(owner);
    public static Task<bool> ShowMessageAsync(Window owner, string title, string message) => new ConfirmWindow(title, message, true).ShowDialog<bool>(owner);
}
