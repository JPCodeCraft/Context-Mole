using System.Collections.ObjectModel;
using MCPIndexSearch.Core;

namespace MCPIndexSearch.App.UI.ViewModels;

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
        : PendingCount > 0 ? "Queued" : "Ready";

    public int SkippedCount => Math.Max(0, DocumentCount - IndexedCount - PendingCount);
    public bool CanRetryFailedFiles => State == ProjectState.Active && ErrorCount > 0;
    public bool HasFileTypeCounts => FileTypeCounts.Count > 0;
    public bool HasNoFileTypeCounts => !HasFileTypeCounts;
    public bool HasNoRecentErrors => RecentErrors.Count == 0;
    public string PauseActionLabel => State == ProjectState.Paused ? "Resume indexing" : "Pause indexing";
    public string FolderCountDisplay => Folders.Count == 1 ? "1 indexed folder" : $"{Folders.Count} indexed folders";
    public string ErrorCountDisplay => ErrorCount == 1 ? "1 unresolved error" : $"{ErrorCount} unresolved errors";
    public string CurrentFileDisplay => string.IsNullOrWhiteSpace(CurrentFile) ? "—" : CurrentFile;
    public string LastCompletedDisplay => LastCompletedUtc?.ToLocalTime().ToString("g") ?? "Not yet completed";

    public void UpdateFrom(ProjectSummary project)
    {
        if (project.Id != Id)
        {
            throw new ArgumentException("A project view model cannot change identity.", nameof(project));
        }

        var previousPhase = Phase;
        var previousSkipped = SkippedCount;
        var previousCanRetryFailedFiles = CanRetryFailedFiles;
        var previousPauseActionLabel = PauseActionLabel;
        var previousFolderCountDisplay = FolderCountDisplay;
        var previousCurrentFileDisplay = CurrentFileDisplay;
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
        if (previousSkipped != SkippedCount) OnPropertyChanged(nameof(SkippedCount));
        if (previousCanRetryFailedFiles != CanRetryFailedFiles) OnPropertyChanged(nameof(CanRetryFailedFiles));
        if (!string.Equals(previousPauseActionLabel, PauseActionLabel, StringComparison.Ordinal)) OnPropertyChanged(nameof(PauseActionLabel));
        if (!string.Equals(previousFolderCountDisplay, FolderCountDisplay, StringComparison.Ordinal)) OnPropertyChanged(nameof(FolderCountDisplay));
        OnPropertyChanged(nameof(ErrorCountDisplay));
        if (!string.Equals(previousCurrentFileDisplay, CurrentFileDisplay, StringComparison.Ordinal)) OnPropertyChanged(nameof(CurrentFileDisplay));
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
        OnPropertyChanged(nameof(HasNoRecentErrors));
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
