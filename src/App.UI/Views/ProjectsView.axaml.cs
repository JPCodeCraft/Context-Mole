using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using ContextMole.App.UI.ViewModels;

namespace ContextMole.App.UI.Views;

public partial class ProjectsView : UserControl
{
    private bool ProjectActionBusy => ViewModel.IsProjectActionBusy;

    public ProjectsView()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private async void ReloadProjects(object? sender, RoutedEventArgs args) =>
        await RunUiActionAsync(sender as Control, () => ViewModel.RefreshAsync(), description: "Loading local projects…");

    private async void AddProject(object? sender, RoutedEventArgs args)
    {
        if (Owner is MainWindow mainWindow)
            await mainWindow.AddProjectAsync(sender as Control);
    }

    private async void EditProject(object? sender, RoutedEventArgs args)
    {
        if (ProjectActionBusy || ViewModel.SelectedProject is not { } project) return;
        await new ProjectEditorWindow(project.ToSummary(), result => ViewModel.RunProjectActionAsync(project.Id,
            $"Saving {project.Name}…", () => ViewModel.UpdateAsync(project.Id, result.Name, result.Folders), "save_action"))
            .ShowDialog<ProjectEditorResult?>(Owner);
    }

    private async void TogglePause(object? sender, RoutedEventArgs args)
    {
        if (ProjectActionBusy || ViewModel.SelectedProject is not { } project) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.TogglePauseAsync(project.Id), project.Id, $"Updating {project.Name}…", "pause_action");
    }

    private async void RetryFailedFiles(object? sender, RoutedEventArgs args)
    {
        if (ProjectActionBusy || ViewModel.SelectedProject is not { CanRetryFailedFiles: true } project) return;
        if (!await ConfirmWindow.AskAsync(Owner, "Retry failed files?",
                "Only documents currently marked with errors will be queued again. Successfully indexed files will not be touched.")) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.RetryFailedFilesAsync(project.Id), project.Id, "Queuing failed files…", "retry_action");
    }

    private async void PreviousErrorPage(object? sender, RoutedEventArgs args) =>
        await MoveErrorPageAsync(sender as Control, -1);

    private async void NextErrorPage(object? sender, RoutedEventArgs args) =>
        await MoveErrorPageAsync(sender as Control, 1);

    private async Task MoveErrorPageAsync(Control? source, int direction)
    {
        var section = source?.GetVisualAncestors().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("issuesCard"));
        await RunUiActionAsync(source, () => ViewModel.MoveErrorPageAsync(direction));
        section?.BringIntoView(new Rect(0, 0, section.Bounds.Width, 1));
    }

    private async void RepairSemanticIndex(object? sender, RoutedEventArgs args)
    {
        if (ProjectActionBusy || ViewModel.SelectedProject is not { CanRepairSemanticIndex: true } project) return;
        if (!await ConfirmWindow.AskAsync(Owner, "Repair semantic index?",
                "Only files with missing, incomplete, or outdated embeddings will be queued. " +
                "Keyword search and original files will not be changed.", "Repair")) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.RepairSemanticIndexAsync(project.Id), project.Id, "Queuing coverage repair…", "repair_action");
    }

    private async void ReindexProject(object? sender, RoutedEventArgs args)
    {
        if (ProjectActionBusy || ViewModel.SelectedProject is not { } project) return;
        if (!await ConfirmWindow.AskAsync(Owner, "Reindex project?",
                "A fresh index will be built. Original files remain untouched.", "Reindex")) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.ReindexAsync(project.Id), project.Id, $"Queuing reindex for {project.Name}…", "reindex_action");
    }

    private async void RemoveProject(object? sender, RoutedEventArgs args)
    {
        if (ProjectActionBusy || ViewModel.SelectedProject is not { } project) return;
        if (!await ConfirmWindow.AskAsync(Owner, "Remove project?",
                $"Remove the local index for “{project.Name}”? Original files remain untouched.",
                "Remove project", destructive: true)) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.RemoveAsync(project.Id), project.Id, $"Removing {project.Name}…", "remove_action");
    }

    private async Task RunUiActionAsync(Control? control, Func<Task> action, Guid? projectId = null,
        string description = "Refreshing project…", string source = "project_refresh_action")
    {
        if (ProjectActionBusy) return;
        try
        {
            await ViewModel.RunProjectActionAsync(projectId ?? ViewModel.SelectedProject?.Id, description, action, source);
        }
        catch (Exception exception)
        {
            await ConfirmWindow.ShowErrorAsync(Owner, exception.Message);
        }
    }
}
