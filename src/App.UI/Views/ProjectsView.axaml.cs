using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using ContextMole.App.UI.ViewModels;

namespace ContextMole.App.UI.Views;

public partial class ProjectsView : UserControl
{
    private CancellationTokenSource? _issueFilterDelay;
    private bool _confirmingAction;
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
        if (ProjectActionBusy || _confirmingAction || ViewModel.SelectedProject is not { CanRetryFailedFiles: true } project) return;
        if (!await AskProjectActionAsync("Retry all files with current issues?",
                $"Queue up to {project.ErrorFileCount:N0} root files with current issues in “{project.Name}”, including hidden issues, other pages and filters. " +
                "Partially indexed searchable files may also be retried. Files without current issues are not queued. Existing evidence remains searchable while replacements are processed; already-pending work is skipped.", "Retry all issue files")) return;
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
        if (!await AskProjectActionAsync("Complete meaning-based search coverage?",
                "Files missing compatible embeddings will be queued. Older indexes that need current text preparation are re-read and re-extracted from their original sources; other files can reuse saved extracted text. " +
                "Existing keyword evidence stays available while replacements are processed. Unavailable sources may prevent repair. Original files are untouched.", "Queue coverage work")) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.RepairSemanticIndexAsync(project.Id), project.Id, "Queuing coverage repair…", "repair_action");
    }

    private async void ReindexProject(object? sender, RoutedEventArgs args)
    {
        if (ProjectActionBusy || ViewModel.SelectedProject is not { } project) return;
        if (!await AskProjectActionAsync("Reindex project?",
                "A fresh index will be built. Original files remain untouched.", "Reindex")) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.ReindexAsync(project.Id), project.Id, $"Queuing reindex for {project.Name}…", "reindex_action");
    }

    private async void RemoveProject(object? sender, RoutedEventArgs args)
    {
        if (ProjectActionBusy || ViewModel.SelectedProject is not { } project) return;
        if (!await AskProjectActionAsync("Remove project?",
                $"Remove the local index for “{project.Name}”? Original files remain untouched.",
                "Remove project", destructive: true)) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.RemoveAsync(project.Id), project.Id, $"Removing {project.Name}…", "remove_action");
    }

    private async Task<bool> AskProjectActionAsync(string title, string message, string acceptLabel, bool destructive = false)
    {
        if (_confirmingAction) return false;
        _confirmingAction = true;
        try { return await ConfirmWindow.AskAsync(Owner, title, message, acceptLabel, destructive); }
        finally { _confirmingAction = false; }
    }

    private async void IssueFiltersChanged(object? sender, TextChangedEventArgs args) => await ScheduleIssueFiltersAsync(sender as Control);
    private async void IssueImpactChanged(object? sender, SelectionChangedEventArgs args) => await ScheduleIssueFiltersAsync(sender as Control);
    private async void ShowHiddenIssues(object? sender, RoutedEventArgs args) => await ScheduleIssueFiltersAsync(sender as Control);
    private async void IssueSectionToggled(object? sender, RoutedEventArgs args) => await ScheduleIssueFiltersAsync(sender as Control);
    private async Task ScheduleIssueFiltersAsync(Control? source)
    {
        if (source?.DataContext is not ProjectItemViewModel project || DataContext is not MainViewModel viewModel) return;
        _issueFilterDelay?.Cancel();
        using var delay = new CancellationTokenSource();
        _issueFilterDelay = delay;
        try
        {
            await Task.Delay(250, delay.Token);
            if (ReferenceEquals(viewModel.SelectedProject, project)) await viewModel.RefreshIssueFiltersAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { project.FailIssueRefresh(exception.Message); }
        finally { if (ReferenceEquals(_issueFilterDelay, delay)) _issueFilterDelay = null; }
    }
    private async void RefreshIssues(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedProject is not { } project) return;
        try { await ViewModel.RefreshIssueFiltersAsync(); }
        catch (Exception exception) { project.FailIssueRefresh(exception.Message); }
    }
    private async void RefreshIssuesFromStart(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedProject is not { } project) return;
        project.ResetIssuePages();
        try { await ViewModel.RefreshIssueFiltersAsync(); }
        catch (Exception exception) { project.FailIssueRefresh(exception.Message); }
    }
    private async void RetryVisibleFiles(object? sender, RoutedEventArgs args)
    {
        if (ProjectActionBusy || ViewModel.SelectedProject is not { CanRetryVisibleIssueFiles: true } project) return;
        var ids = project.VisibleRetryDocumentIds.ToArray();
        if (!await AskProjectActionAsync("Retry this visible page?",
                $"Queue {ids.Length:N0} eligible root files currently shown on this page. Hidden files, other pages and nonmatching filters are not included. " +
                "Existing searchable evidence stays available while replacements are processed.", "Retry visible page")) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.RetryVisibleIssueFilesAsync(project.Id, ids), project.Id,
            "Queuing visible issue files…", "retry_action");
    }
    private async void RetryIssueFile(object? sender, RoutedEventArgs args)
    {
        if (ProjectActionBusy || ViewModel.SelectedProject is not { CanReindex: true } project ||
            (sender as Control)?.DataContext is not ProjectIssueGroupViewModel { CanRetry: true, DocumentId: { } id } group) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.RetryIssueFileAsync(project.Id, id), project.Id,
            $"Queuing {group.FileName}…", "retry_action");
    }
    private async void HideIssueFile(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedProject is not { } project || (sender as Control)?.DataContext is not ProjectIssueGroupViewModel group) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.HideIssueFileAsync(project, group.SourcePath), project.Id, "Acknowledging current issues…", "hide_issues_action");
    }
    private async void RestoreIssueFile(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedProject is not { } project || (sender as Control)?.DataContext is not ProjectIssueGroupViewModel group) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.RestoreIssueFileAsync(project, group.SourcePath), project.Id, "Restoring issues…", "restore_issues_action");
    }
    private async void RestoreAllIssues(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedProject is not { } project) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.RestoreIssueFileAsync(project), project.Id, "Restoring all hidden issues…", "restore_issues_action");
    }
    private async void UndoIssueHide(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedProject is not { } project) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.UndoIssueHideAsync(project), project.Id, "Undoing last hide…", "restore_issues_action");
    }
    private async void ExcludeIssueFile(object? sender, RoutedEventArgs args)
    {
        if (ProjectActionBusy || ViewModel.SelectedProject is not { } project ||
            (sender as Control)?.DataContext is not ProjectIssueGroupViewModel { IsRootFile: true } group) return;
        if (!await AskProjectActionAsync("Exclude this exact root file?",
                $"Project: {project.Name}\nExact root path: {group.SourcePath}\n\n" +
                "This stops indexing and retries for this path and removes its retained indexed evidence, including all indexed attachments and any older revision, from search. " +
                "The original file and attachments remain untouched. A replacement at the same path stays excluded; a renamed path is eligible again. Other same-name files are not affected. " +
                "Include and reindex rebuilds from the source available then; it does not restore removed cached evidence. Removing and re-adding a watched folder does not undo this path rule.",
                "Exclude root file", destructive: true)) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.ExcludeIssueFileAsync(project, group.SourcePath), project.Id, "Excluding root file…", "exclude_action");
    }
    private async void IncludeIssueFile(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedProject is not { } project || (sender as Control)?.DataContext is not ExcludedFileItemViewModel file) return;
        await RunUiActionAsync(sender as Control, () => ViewModel.IncludeIssueFileAsync(project, file.SourcePath), project.Id, "Including path and queuing fresh index…", "include_action");
    }
    private async void PreviousIssueDetails(object? sender, RoutedEventArgs args) => await MoveIssueDetailsAsync(sender as Control, -1);
    private async void NextIssueDetails(object? sender, RoutedEventArgs args) => await MoveIssueDetailsAsync(sender as Control, 1);
    private async Task MoveIssueDetailsAsync(Control? source, int direction)
    {
        if (ViewModel.SelectedProject is not { } project || source?.DataContext is not ProjectIssueGroupViewModel group) return;
        try { await ViewModel.MoveIssueDetailsAsync(project, group, direction); }
        catch (Exception exception) { project.FailIssueRefresh($"Component details could not be loaded: {exception.Message}"); }
    }
    private void PreviousExcludedPage(object? sender, RoutedEventArgs args) => ViewModel.SelectedProject?.MoveExcludedPage(-1);
    private void NextExcludedPage(object? sender, RoutedEventArgs args) => ViewModel.SelectedProject?.MoveExcludedPage(1);

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
