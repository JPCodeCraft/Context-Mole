using System.Collections.ObjectModel;

using ContextMole.Core;
using ContextMole.Indexing;

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
    private ProjectWorkSummary _work = new(0, 0, 0, 0, null);
    private IndexingTimingSnapshot? _runtimeWork;

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
    public ProjectWorkSummary Work { get => _work; private set => SetProperty(ref _work, value); }
    // The database owns the durable total while the tracker tells us which claimed jobs are
    // genuinely executing. Reconciling against both keeps Processing + Queued equal to Pending
    // even if their independently refreshed snapshots cross during a lease/completion transition.
    public int ProcessingCount => _runtimeWork is null
        ? Work.ProcessingCount
        : Math.Min(Work.ProcessingCount, _runtimeWork.ProcessingCount);
    public int QueuedCount => Work.QueuedCount + ClaimedButNotProcessingCount;
    public int RetryScheduledCount => Work.RetryScheduledCount;
    public int RunningRetryCount => _runtimeWork is null
        ? Work.RunningRetryCount
        : Math.Min(ProcessingCount, _runtimeWork.RetryingCount);
    public DateTimeOffset? NextRetryUtc => Work.NextRetryUtc;
    public int WaitingForMemoryCount => Math.Min(ClaimedButNotProcessingCount,
        _runtimeWork?.WaitingForMemoryCount ?? 0);
    public int WaitingForCpuCount => Math.Min(
        Math.Max(0, ClaimedButNotProcessingCount - WaitingForMemoryCount),
        _runtimeWork?.WaitingForCpuCount ?? 0);
    public int AdmissionQueuedCount => Math.Min(
        Math.Max(0, ClaimedButNotProcessingCount - WaitingForMemoryCount - WaitingForCpuCount),
        _runtimeWork?.QueuedCount ?? 0);
    private int ClaimedButNotProcessingCount => _runtimeWork is null
        ? 0
        : Math.Max(0, Work.ProcessingCount - ProcessingCount);
    private ProjectWorkPhase EffectiveWorkPhase => RunningRetryCount > 0 ? ProjectWorkPhase.Retrying
        : ProcessingCount > 0 ? ProjectWorkPhase.Indexing
        : QueuedCount > 0 && QueuedCount == RetryScheduledCount ? ProjectWorkPhase.RetryScheduled
        : QueuedCount > 0 ? ProjectWorkPhase.Queued
        : ProjectWorkPhase.Ready;
    private string? ProcessingSourcePath => _runtimeWork?.ActiveItems
        .FirstOrDefault(item => item.IsProcessing)?.SourcePath ?? CurrentFile;
    private string? RetryingSourcePath => _runtimeWork?.ActiveItems
        .FirstOrDefault(item => item.IsRetrying)?.SourcePath;

    public string Phase => State == ProjectState.Paused ? "Paused"
        : RunningRetryCount > 0 ? "Retrying"
        : ProcessingCount > 0 ? "Indexing"
        : WaitingForMemoryCount > 0 ? "Waiting for memory"
        : WaitingForCpuCount > 0 ? "Waiting for CPU"
        : EffectiveWorkPhase == ProjectWorkPhase.RetryScheduled ? "Retry scheduled"
        : EffectiveWorkPhase == ProjectWorkPhase.Queued ? "Queued"
        : ErrorCount > 0 ? "Needs attention"
        : "Ready";

    public string PhaseDetails => State == ProjectState.Paused
        ? "Indexing is paused."
        : Phase switch
    {
        "Retrying" => RetryingSourcePath is null
            ? "A failed file is being retried now."
            : $"Retrying {Path.GetFileName(RetryingSourcePath)} now.",
        "Indexing" => ProcessingSourcePath is null
            ? "Indexing is active."
            : $"Indexing {Path.GetFileName(ProcessingSourcePath)} now.",
        "Waiting for memory" => WaitingForMemoryCount == 1
            ? "1 file is waiting for safe memory admission."
            : $"{WaitingForMemoryCount} files are waiting for safe memory admission.",
        "Waiting for CPU" => WaitingForCpuCount == 1
            ? "1 file has memory reserved and is waiting for processor capacity."
            : $"{WaitingForCpuCount} files have memory reserved and are waiting for processor capacity.",
        "Retry scheduled" when NextRetryUtc is not null =>
            $"Next retry is scheduled for {NextRetryUtc.Value.ToLocalTime():g}.",
        "Retry scheduled" => "A retry is scheduled for later.",
        "Queued" => QueuedCount == 1 ? "1 file is waiting to be processed."
            : $"{QueuedCount} files are waiting to be processed.",
        _ when ErrorCount > 0 => "Some files need attention before they can be searched.",
        _ => "All available files are up to date."
    };

    public bool IsPaused => State == ProjectState.Paused;
    public bool IsReady => Phase == "Ready";
    public bool IsRetrying => Phase == "Retrying";
    public bool IsRetryScheduled => Phase == "Retry scheduled";
    public bool IsRetryStatus => IsRetrying || IsRetryScheduled;
    public bool IsWaitingForResources => Phase is "Waiting for memory" or "Waiting for CPU";
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
    public bool HasQueueBreakdown => QueuedBreakdownDisplay.Length > 0;
    public string QueuedBreakdownDisplay
    {
        get
        {
            var parts = new List<string>(4);
            if (RetryScheduledCount > 0)
                parts.Add(RetryScheduledCount == 1 ? "1 scheduled retry" : $"{RetryScheduledCount} scheduled retries");
            if (WaitingForMemoryCount > 0)
                parts.Add($"{WaitingForMemoryCount} memory wait");
            if (WaitingForCpuCount > 0)
                parts.Add($"{WaitingForCpuCount} CPU wait");
            if (AdmissionQueuedCount > 0)
                parts.Add(AdmissionQueuedCount == 1 ? "1 awaiting admission" :
                    $"{AdmissionQueuedCount} awaiting admission");
            return string.Join(" · ", parts);
        }
    }
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
        var previousIsRetryScheduled = IsRetryScheduled;
        var previousIsRetryStatus = IsRetryStatus;
        var previousIsWaitingForResources = IsWaitingForResources;
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
        var previousPhaseDetails = PhaseDetails;
        var previousProcessingCount = ProcessingCount;
        var previousQueuedCount = QueuedCount;
        var previousRetryScheduledCount = RetryScheduledCount;
        var previousRunningRetryCount = RunningRetryCount;
        var previousNextRetryUtc = NextRetryUtc;
        var previousHasQueueBreakdown = HasQueueBreakdown;
        var previousQueuedBreakdownDisplay = QueuedBreakdownDisplay;

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
        Work = project.Work;

        if (!string.Equals(previousPhase, Phase, StringComparison.Ordinal)) OnPropertyChanged(nameof(Phase));
        if (!string.Equals(previousPhaseDetails, PhaseDetails, StringComparison.Ordinal)) OnPropertyChanged(nameof(PhaseDetails));
        if (previousIsPaused != IsPaused) OnPropertyChanged(nameof(IsPaused));
        if (previousIsReady != IsReady) OnPropertyChanged(nameof(IsReady));
        if (previousIsRetrying != IsRetrying) OnPropertyChanged(nameof(IsRetrying));
        if (previousIsRetryScheduled != IsRetryScheduled) OnPropertyChanged(nameof(IsRetryScheduled));
        if (previousIsRetryStatus != IsRetryStatus) OnPropertyChanged(nameof(IsRetryStatus));
        if (previousIsWaitingForResources != IsWaitingForResources)
            OnPropertyChanged(nameof(IsWaitingForResources));
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
        if (previousProcessingCount != ProcessingCount) OnPropertyChanged(nameof(ProcessingCount));
        if (previousQueuedCount != QueuedCount) OnPropertyChanged(nameof(QueuedCount));
        if (previousRetryScheduledCount != RetryScheduledCount) OnPropertyChanged(nameof(RetryScheduledCount));
        if (previousRunningRetryCount != RunningRetryCount) OnPropertyChanged(nameof(RunningRetryCount));
        if (previousNextRetryUtc != NextRetryUtc) OnPropertyChanged(nameof(NextRetryUtc));
        if (previousHasQueueBreakdown != HasQueueBreakdown) OnPropertyChanged(nameof(HasQueueBreakdown));
        if (!string.Equals(previousQueuedBreakdownDisplay, QueuedBreakdownDisplay, StringComparison.Ordinal))
            OnPropertyChanged(nameof(QueuedBreakdownDisplay));
    }

    public void UpdateRuntime(IndexingTimingSnapshot runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.ActiveItems.Any(item => item.ProjectId != Id))
            throw new ArgumentException("A runtime summary can only contain this project's work.", nameof(runtime));

        var previousPhase = Phase;
        var previousPhaseDetails = PhaseDetails;
        var previousIsReady = IsReady;
        var previousIsRetrying = IsRetrying;
        var previousIsRetryScheduled = IsRetryScheduled;
        var previousIsRetryStatus = IsRetryStatus;
        var previousIsWaitingForResources = IsWaitingForResources;
        var previousNeedsAttention = NeedsAttention;
        var previousProcessingCount = ProcessingCount;
        var previousQueuedCount = QueuedCount;
        var previousRunningRetryCount = RunningRetryCount;
        var previousWaitingForMemoryCount = WaitingForMemoryCount;
        var previousWaitingForCpuCount = WaitingForCpuCount;
        var previousAdmissionQueuedCount = AdmissionQueuedCount;
        var previousHasQueueBreakdown = HasQueueBreakdown;
        var previousQueuedBreakdownDisplay = QueuedBreakdownDisplay;

        _runtimeWork = runtime;

        if (!string.Equals(previousPhase, Phase, StringComparison.Ordinal)) OnPropertyChanged(nameof(Phase));
        if (!string.Equals(previousPhaseDetails, PhaseDetails, StringComparison.Ordinal))
            OnPropertyChanged(nameof(PhaseDetails));
        if (previousIsReady != IsReady) OnPropertyChanged(nameof(IsReady));
        if (previousIsRetrying != IsRetrying) OnPropertyChanged(nameof(IsRetrying));
        if (previousIsRetryScheduled != IsRetryScheduled) OnPropertyChanged(nameof(IsRetryScheduled));
        if (previousIsRetryStatus != IsRetryStatus) OnPropertyChanged(nameof(IsRetryStatus));
        if (previousIsWaitingForResources != IsWaitingForResources)
            OnPropertyChanged(nameof(IsWaitingForResources));
        if (previousNeedsAttention != NeedsAttention) OnPropertyChanged(nameof(NeedsAttention));
        if (previousProcessingCount != ProcessingCount) OnPropertyChanged(nameof(ProcessingCount));
        if (previousQueuedCount != QueuedCount) OnPropertyChanged(nameof(QueuedCount));
        if (previousRunningRetryCount != RunningRetryCount) OnPropertyChanged(nameof(RunningRetryCount));
        if (previousWaitingForMemoryCount != WaitingForMemoryCount)
            OnPropertyChanged(nameof(WaitingForMemoryCount));
        if (previousWaitingForCpuCount != WaitingForCpuCount) OnPropertyChanged(nameof(WaitingForCpuCount));
        if (previousAdmissionQueuedCount != AdmissionQueuedCount) OnPropertyChanged(nameof(AdmissionQueuedCount));
        if (previousHasQueueBreakdown != HasQueueBreakdown) OnPropertyChanged(nameof(HasQueueBreakdown));
        if (!string.Equals(previousQueuedBreakdownDisplay, QueuedBreakdownDisplay, StringComparison.Ordinal))
            OnPropertyChanged(nameof(QueuedBreakdownDisplay));
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
        PendingCount, IndexedCount, ErrorCount, LastCompletedUtc, CurrentFile) { Work = Work };

    private int IndexOfError(long id, int startIndex)
    {
        for (var index = startIndex; index < RecentErrors.Count; index++)
        {
            if (RecentErrors[index].Id == id) return index;
        }

        return -1;
    }
}
