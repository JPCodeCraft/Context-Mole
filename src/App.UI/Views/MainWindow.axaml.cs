using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using ContextMole.App.UI.ViewModels;

namespace ContextMole.App.UI.Views;

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

    private void ShowProjects(object? sender, RoutedEventArgs args) => ViewModel.ShowProjects();

    private void ShowSettings(object? sender, RoutedEventArgs args) => ViewModel.ShowSettings();

    private void ProjectSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (ViewModel.SelectedProject is not null) ViewModel.ShowProjects();
    }

    private void ProjectCardPressed(object? sender, PointerPressedEventArgs args) => ViewModel.ShowProjects();

    private async void AddProject(object? sender, RoutedEventArgs args)
    {
        var result = await new ProjectEditorWindow().ShowDialog<ProjectEditorResult?>(this);
        if (result is null) return;
        await RunUiActionAsync(() => ViewModel.CreateAsync(result.Name, result.Folders));
    }

    private async void QuitApplication(object? sender, RoutedEventArgs args)
    {
        if (Application.Current is App app) await app.QuitAsync();
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            await ConfirmWindow.ShowErrorAsync(this, exception.Message);
        }
    }
}