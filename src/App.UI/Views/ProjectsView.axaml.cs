using Avalonia.Controls;
using Avalonia.Interactivity;

using ContextMole.App.UI.ViewModels;

namespace ContextMole.App.UI.Views;

public partial class ProjectsView : UserControl
{
    public ProjectsView()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private async void EditProject(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedProject is not { } project) return;
        var result = await new ProjectEditorWindow(project.ToSummary()).ShowDialog<ProjectEditorResult?>(Owner);
        if (result is null) return;
        await RunUiActionAsync(() => ViewModel.UpdateAsync(project.Id, result.Name, result.Folders));
    }

    private async void TogglePause(object? sender, RoutedEventArgs args) =>
        await RunUiActionAsync(ViewModel.TogglePauseAsync);

    private async void RetryFailedFiles(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedProject is not { CanRetryFailedFiles: true }) return;
        if (!await ConfirmWindow.AskAsync(Owner, "Retry failed files?",
                "Only documents currently marked with errors will be queued again. Successfully indexed files will not be touched.")) return;
        await RunUiActionAsync(ViewModel.RetryFailedFilesAsync);
    }

    private async void ReindexProject(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedProject is null) return;
        if (!await ConfirmWindow.AskAsync(Owner, "Reindex project?",
                "A fresh index will be built. Original files remain untouched.", "Reindex")) return;
        await RunUiActionAsync(ViewModel.ReindexAsync);
    }

    private async void RemoveProject(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedProject is not { } project) return;
        if (!await ConfirmWindow.AskAsync(Owner, "Remove project?",
                $"Remove the local index for “{project.Name}”? Original files remain untouched.",
                "Remove project", destructive: true)) return;
        await RunUiActionAsync(ViewModel.RemoveAsync);
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