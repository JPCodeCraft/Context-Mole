using Avalonia.Controls;
using Avalonia.Interactivity;

using ContextMole.App.UI.ViewModels;

namespace ContextMole.App.UI.Views;

public partial class ProjectsView : UserControl
{
    private bool _projectActionBusy;

    public ProjectsView()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private async void AddProject(object? sender, RoutedEventArgs args)
    {
        if (Owner is MainWindow mainWindow)
            await mainWindow.AddProjectAsync(sender as Control);
    }

    private async void EditProject(object? sender, RoutedEventArgs args)
    {
        if (_projectActionBusy || ViewModel.SelectedProject is not { } project) return;
        var result = await new ProjectEditorWindow(project.ToSummary()).ShowDialog<ProjectEditorResult?>(Owner);
        if (result is null) return;
        await RunUiActionAsync(sender as Control,
            () => ViewModel.UpdateAsync(project.Id, result.Name, result.Folders));
    }

    private async void TogglePause(object? sender, RoutedEventArgs args)
    {
        if (_projectActionBusy) return;
        await RunUiActionAsync(sender as Control, ViewModel.TogglePauseAsync);
    }

    private async void RetryFailedFiles(object? sender, RoutedEventArgs args)
    {
        if (_projectActionBusy || ViewModel.SelectedProject is not { CanRetryFailedFiles: true }) return;
        if (!await ConfirmWindow.AskAsync(Owner, "Retry failed files?",
                "Only documents currently marked with errors will be queued again. Successfully indexed files will not be touched.")) return;
        await RunUiActionAsync(sender as Control, ViewModel.RetryFailedFilesAsync);
    }

    private async void RepairSemanticIndex(object? sender, RoutedEventArgs args)
    {
        if (_projectActionBusy || ViewModel.SelectedProject is not { CanRepairSemanticIndex: true }) return;
        if (!await ConfirmWindow.AskAsync(Owner, "Repair semantic index?",
                "Only files with missing, incomplete, or outdated embeddings will be queued. " +
                "Keyword search and original files will not be changed.", "Repair")) return;
        await RunUiActionAsync(sender as Control, ViewModel.RepairSemanticIndexAsync);
    }

    private async void ReindexProject(object? sender, RoutedEventArgs args)
    {
        if (_projectActionBusy || ViewModel.SelectedProject is null) return;
        if (!await ConfirmWindow.AskAsync(Owner, "Reindex project?",
                "A fresh index will be built. Original files remain untouched.", "Reindex")) return;
        await RunUiActionAsync(sender as Control, ViewModel.ReindexAsync);
    }

    private async void RemoveProject(object? sender, RoutedEventArgs args)
    {
        if (_projectActionBusy || ViewModel.SelectedProject is not { } project) return;
        if (!await ConfirmWindow.AskAsync(Owner, "Remove project?",
                $"Remove the local index for “{project.Name}”? Original files remain untouched.",
                "Remove project", destructive: true)) return;
        await RunUiActionAsync(sender as Control, ViewModel.RemoveAsync);
    }

    private async Task RunUiActionAsync(Control? source, Func<Task> action)
    {
        if (_projectActionBusy) return;
        _projectActionBusy = true;
        if (source is not null)
        {
            source.IsHitTestVisible = false;
            source.Opacity = 0.65;
        }
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            await ConfirmWindow.ShowErrorAsync(Owner, exception.Message);
        }
        finally
        {
            if (source is not null)
            {
                source.IsHitTestVisible = true;
                source.Opacity = 1;
            }
            _projectActionBusy = false;
        }
    }
}
