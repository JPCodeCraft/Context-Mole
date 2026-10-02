using Avalonia.Threading;

using ContextMole.Core;

namespace ContextMole.App.UI.ViewModels;

internal partial class MainViewModel
{
    public async Task RefreshIssueFiltersAsync()
    {
        if (SelectedProject is not { } selected) return;
        await RefreshErrorsSafeAsync(selected.Id);
    }

    public async Task RefreshExcludedFilesAsync(Guid projectId)
    {
        var files = await _store.ListExcludedFilesAsync(projectId).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (SelectedProject?.Id == projectId) SelectedProject.UpdateExcludedFiles(files);
        });
    }

    public async Task RetryIssueFileAsync(Guid projectId, Guid documentId)
    {
        var result = await _writer.RetryFileAsync(projectId, documentId);
        Notify("retry_action", $"{result.QueuedCount} file queued; {result.AlreadyPendingCount} already pending. Current issues remain until work succeeds.", projectId);
        await RefreshAsync();
    }

    public async Task RetryVisibleIssueFilesAsync(Guid projectId, IReadOnlyList<Guid> documentIds)
    {
        var queued = 0;
        var pending = 0;
        foreach (var documentId in documentIds.Distinct())
        {
            var result = await _writer.RetryFileAsync(projectId, documentId);
            queued += result.QueuedCount; pending += result.AlreadyPendingCount;
        }
        Notify("retry_action", $"{queued} files queued; {pending} already pending. Only the selected visible page was retried.", projectId);
        await RefreshAsync();
    }

    public async Task HideIssueFileAsync(ProjectItemViewModel project, string sourcePath)
    {
        var result = await _writer.HideProjectIssuesAsync(project.Id, sourcePath);
        project.RecordIssueHide(result);
        project.ResetIssuePages();
        await RefreshErrorsSafeAsync(project.Id);
    }

    public async Task RestoreIssueFileAsync(ProjectItemViewModel project, string? sourcePath = null)
    {
        await _writer.RestoreProjectIssuesAsync(project.Id, sourcePath);
        project.SetIssueActionMessage(sourcePath is null ? "All current hidden issues restored." : "Current issues for this root file restored.");
        project.ClearIssueUndo(); project.ResetIssuePages();
        await RefreshErrorsSafeAsync(project.Id);
    }

    public async Task UndoIssueHideAsync(ProjectItemViewModel project)
    {
        var ids = project.UndoAcknowledgements.ToArray();
        if (ids.Length == 0) return;
        await _writer.RestoreProjectIssueAcknowledgementsAsync(project.Id, ids);
        project.ClearIssueUndo(); project.SetIssueActionMessage("The last hide was undone."); project.ResetIssuePages();
        await RefreshErrorsSafeAsync(project.Id);
    }

    public async Task ExcludeIssueFileAsync(ProjectItemViewModel project, string sourcePath)
    {
        var result = await _projectIndexingControl.ExcludeFileAsync(project.Id, sourcePath);
        project.ClearIssueUndo();
        project.SetIssueActionMessage(result.Changed
            ? "Root file excluded from indexing and search. Original file is unchanged. Include it below to queue a fresh index."
            : "That exact root path is already excluded.");
        project.ResetIssuePages();
        await RefreshAsync();
        await RefreshExcludedFilesAsync(project.Id);
    }

    public async Task IncludeIssueFileAsync(ProjectItemViewModel project, string sourcePath)
    {
        var result = await _projectIndexingControl.IncludeFileAsync(project.Id, sourcePath);
        project.ClearIssueUndo();
        project.SetIssueActionMessage(result.Queued ? "Path included again and queued for a fresh index." :
            "Exclusion rule removed, but no fresh index was queued. Check that this exact path still exists inside a watched folder, restore access if needed, then reindex the project. Removed cached evidence is not restored.");
        await RefreshAsync();
        await RefreshExcludedFilesAsync(project.Id);
    }

    public async Task MoveIssueDetailsAsync(ProjectItemViewModel project, ProjectIssueGroupViewModel group, int direction)
    {
        if (group.IsDetailsLoading) return;
        group.MoveDetails(direction);
        var cursor = group.DetailsCursor;
        var queryVersion = project.IssueQueryVersion;
        group.IsDetailsLoading = true;
        try
        {
            ProjectIssueDetailsResponse details;
            try
            {
                details = await _store.ListProjectIssueDetailsAsync(project.Id, group.SourcePath,
                    project.IssueVisibility, 50, cursor);
            }
            catch (ContextMoleException exception) when (exception.Code is "issues_changed" or "invalid_cursor")
            {
                if (!ReferenceEquals(SelectedProject, project) || project.IssueQueryVersion != queryVersion || !project.IssueGroups.Contains(group)) return;
                group.ResetDetails(); cursor = null;
                project.SetIssueActionMessage("Component issues changed; details were refreshed from the first page.");
                details = await _store.ListProjectIssueDetailsAsync(project.Id, group.SourcePath, project.IssueVisibility, 50);
            }
            if (ReferenceEquals(SelectedProject, project) && project.IssueQueryVersion == queryVersion &&
                project.IssueGroups.Contains(group) && group.DetailsCursor == cursor) group.UpdateDetails(details);
        }
        finally { group.IsDetailsLoading = false; }
    }
}
