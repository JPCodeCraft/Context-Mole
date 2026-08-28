using System.Collections.ObjectModel;

using ContextMole.Core;

namespace ContextMole.App.UI.ViewModels;

public sealed class ProjectItemViewModel : ViewModelBase
{
    private string _name = string.Empty;
    private ProjectState _state;
    private IReadOnlyList<ProjectFolderInfo> _folders = [];
    private IReadOnlyList<ProjectFileTypeCount> _fileTypeCounts = [];
    private long _searchGeneration;
    private int _documentCount;
    private int _pendingCount;
    private int _indexedCount;
    private int _errorCount;
    private DateTimeOffset? _lastCompletedUtc;
    private string? _currentFile;

    public ProjectItemViewModel(ProjectSummary project)
    {
        Id = project.Id;
        UpdateFrom(project);
    }

    public Guid Id { get; }
    public ObservableCollection<ProjectErrorItemViewModel> RecentErrors { get; } = [];

    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public ProjectState State { get => _state; private set => SetProperty(ref _state, value); }
    public IReadOnlyList<ProjectFolderInfo> Folders { get => _folders; private set => SetProperty(ref _folders, value); }
    public IReadOnlyList<ProjectFileTypeCount> FileTypeCounts { get => _fileTypeCounts; private set => SetProperty(ref _fileTypeCounts, value); }
    public long SearchGeneration { get => _searchGeneration; private set => SetProperty(ref _searchGeneration, value); }
    public int DocumentCount { get => _documentCount; private set => SetProperty(ref _documentCount, value); }
    public int PendingCount { get => _pendingCount; private set => SetProperty(ref _pendingCount, value); }
    public int IndexedCount { get => _indexedCount; private set => SetProperty(ref _indexedCount, value); }
    public int ErrorCount { get => _errorCount; private set => SetProperty(ref _errorCount, value); }
    public DateTimeOffset? LastCompletedUtc { get => _lastCompletedUtc; private set => SetProperty(ref _lastCompletedUtc, value); }
    public string? CurrentFile { get => _currentFile; private set => SetProperty(ref _currentFile, value); }

    public string Phase => State == ProjectState.Paused ? "Paused"
        : CurrentFile is not null ? "Indexing"
        : PendingCount > 0 && ErrorCount > 0 ? "Retrying"
        : PendingCount > 0 ? "Queued"
        : ErrorCount > 0 ? "Needs attention" : "Ready";

    public bool IsPaused => State == ProjectState.Paused;
    public bool IsReady => Phase == "Ready";
    public bool IsRetrying => Phase == "Retrying";
    public bool NeedsAttention => Phase == "Needs attention";
    public bool HasErrors => ErrorCount > 0;
    public bool CanReindex => State == ProjectState.Active;
    public bool CanRetryFailedFiles => State == ProjectState.Active && ErrorCount > 0 &&
        PendingCount == 0 && CurrentFile is null;
    public string ReindexToolTip => IsPaused
        ? "Resume indexing before rebuilding this project."
        : "Rebuild the local index from the watched folders.";
    public string RetryFailedFilesToolTip => IsPaused
        ? "Resume indexing before retrying failed files."
        : PendingCount > 0 || CurrentFile is not null
            ? "Wait for the current indexing work to finish."
            : "Queue only the files that currently have errors.";
    public bool HasFileTypeCounts => FileTypeCounts.Count > 0;
    public bool HasNoFileTypeCounts => !HasFileTypeCounts;
    public bool HasRecentErrors => RecentErrors.Count > 0;
    public string PauseActionLabel => State == ProjectState.Paused ? "Resume indexing" : "Pause indexing";
    public string FolderCountDisplay => Folders.Count == 1 ? "1 folder" : $"{Folders.Count} folders";
    public string DocumentCountDisplay => DocumentCount == 1 ? "1 file" : $"{DocumentCount} files";
    public string SidebarErrorCountDisplay => ErrorCount == 1 ? "1 error" : $"{ErrorCount} errors";
    public string ErrorCountDisplay => ErrorCount == 1 ? "1 unresolved error" : $"{ErrorCount} unresolved errors";
    public string RecentErrorsSummary => ErrorCount <= RecentErrors.Count
        ? ErrorCountDisplay
        : $"{RecentErrors.Count} of {ErrorCount} unresolved errors shown";
    public string LastCompletedDisplay => LastCompletedUtc?.ToLocalTime().ToString("g") ?? "Not yet completed";
    public string ProjectDetailsDisplay => LastCompletedUtc is null
        ? $"{FolderCountDisplay} · Not indexed yet"
        : $"{FolderCountDisplay} · Last completed {LastCompletedDisplay}";

    public void UpdateFrom(ProjectSummary project)
    {
        if (project.Id != Id)
        {
            throw new ArgumentException("A project view model cannot change identity.", nameof(project));
        }

        var previousPhase = Phase;
        var previousIsPaused = IsPaused;
        var previousIsReady = IsReady;
        var previousIsRetrying = IsRetrying;
        var previousNeedsAttention = NeedsAttention;
        var previousHasErrors = HasErrors;
        var previousCanReindex = CanReindex;
        var previousCanRetryFailedFiles = CanRetryFailedFiles;
        var previousReindexToolTip = ReindexToolTip;
        var previousRetryFailedFilesToolTip = RetryFailedFilesToolTip;
        var previousPauseActionLabel = PauseActionLabel;
        var previousFolderCountDisplay = FolderCountDisplay;
        var previousDocumentCountDisplay = DocumentCountDisplay;
        var previousSidebarErrorCountDisplay = SidebarErrorCountDisplay;
        var previousRecentErrorsSummary = RecentErrorsSummary;
        var previousProjectDetailsDisplay = ProjectDetailsDisplay;
        var previousLastCompletedDisplay = LastCompletedDisplay;

        Name = project.Name;
        State = project.State;
        if (!Folders.SequenceEqual(project.Folders)) Folders = project.Folders.ToArray();
        SearchGeneration = project.SearchGeneration;
        DocumentCount = project.DocumentCount;
        PendingCount = project.PendingCount;
        IndexedCount = project.IndexedCount;
        ErrorCount = project.ErrorCount;
        LastCompletedUtc = project.LastCompletedUtc;
        CurrentFile = project.CurrentFile;

        if (!string.Equals(previousPhase, Phase, StringComparison.Ordinal)) OnPropertyChanged(nameof(Phase));
        if (previousIsPaused != IsPaused) OnPropertyChanged(nameof(IsPaused));
        if (previousIsReady != IsReady) OnPropertyChanged(nameof(IsReady));
        if (previousIsRetrying != IsRetrying) OnPropertyChanged(nameof(IsRetrying));
        if (previousNeedsAttention != NeedsAttention) OnPropertyChanged(nameof(NeedsAttention));
        if (previousHasErrors != HasErrors) OnPropertyChanged(nameof(HasErrors));
        if (previousCanReindex != CanReindex) OnPropertyChanged(nameof(CanReindex));
        if (previousCanRetryFailedFiles != CanRetryFailedFiles) OnPropertyChanged(nameof(CanRetryFailedFiles));
        if (!string.Equals(previousReindexToolTip, ReindexToolTip, StringComparison.Ordinal)) OnPropertyChanged(nameof(ReindexToolTip));
        if (!string.Equals(previousRetryFailedFilesToolTip, RetryFailedFilesToolTip, StringComparison.Ordinal)) OnPropertyChanged(nameof(RetryFailedFilesToolTip));
        if (!string.Equals(previousPauseActionLabel, PauseActionLabel, StringComparison.Ordinal)) OnPropertyChanged(nameof(PauseActionLabel));
        if (!string.Equals(previousFolderCountDisplay, FolderCountDisplay, StringComparison.Ordinal)) OnPropertyChanged(nameof(FolderCountDisplay));
        if (!string.Equals(previousDocumentCountDisplay, DocumentCountDisplay, StringComparison.Ordinal)) OnPropertyChanged(nameof(DocumentCountDisplay));
        if (!string.Equals(previousSidebarErrorCountDisplay, SidebarErrorCountDisplay, StringComparison.Ordinal)) OnPropertyChanged(nameof(SidebarErrorCountDisplay));
        OnPropertyChanged(nameof(ErrorCountDisplay));
        if (!string.Equals(previousRecentErrorsSummary, RecentErrorsSummary, StringComparison.Ordinal)) OnPropertyChanged(nameof(RecentErrorsSummary));
        if (!string.Equals(previousProjectDetailsDisplay, ProjectDetailsDisplay, StringComparison.Ordinal)) OnPropertyChanged(nameof(ProjectDetailsDisplay));
        if (!string.Equals(previousLastCompletedDisplay, LastCompletedDisplay, StringComparison.Ordinal)) OnPropertyChanged(nameof(LastCompletedDisplay));
    }

    public void UpdateErrors(IReadOnlyList<ProjectErrorInfo> errors)
    {
        for (var targetIndex = 0; targetIndex < errors.Count; targetIndex++)
        {
            var incoming = errors[targetIndex];
            if (targetIndex < RecentErrors.Count && RecentErrors[targetIndex].Id == incoming.Id)
            {
                if (RecentErrors[targetIndex].Source != incoming) RecentErrors[targetIndex] = new ProjectErrorItemViewModel(incoming);
                continue;
            }

            var existingIndex = IndexOfError(incoming.Id, targetIndex + 1);
            if (existingIndex >= 0)
            {
                RecentErrors.Move(existingIndex, targetIndex);
                if (RecentErrors[targetIndex].Source != incoming) RecentErrors[targetIndex] = new ProjectErrorItemViewModel(incoming);
            }
            else
            {
                RecentErrors.Insert(targetIndex, new ProjectErrorItemViewModel(incoming));
            }
        }

        while (RecentErrors.Count > errors.Count) RecentErrors.RemoveAt(RecentErrors.Count - 1);
        OnPropertyChanged(nameof(HasRecentErrors));
        OnPropertyChanged(nameof(RecentErrorsSummary));
    }

    public void UpdateFileTypeCounts(IReadOnlyList<ProjectFileTypeCount> counts)
    {
        if (FileTypeCounts.SequenceEqual(counts)) return;
        FileTypeCounts = counts.ToArray();
        OnPropertyChanged(nameof(HasFileTypeCounts));
        OnPropertyChanged(nameof(HasNoFileTypeCounts));
    }

    public ProjectSummary ToSummary() => new(Id, Name, State, Folders, SearchGeneration, DocumentCount,
        PendingCount, IndexedCount, ErrorCount, LastCompletedUtc, CurrentFile);

    private int IndexOfError(long id, int startIndex)
    {
        for (var index = startIndex; index < RecentErrors.Count; index++)
        {
            if (RecentErrors[index].Id == id) return index;
        }

        return -1;
    }
}
