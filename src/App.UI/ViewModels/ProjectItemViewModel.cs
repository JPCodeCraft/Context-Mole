using System.Collections.ObjectModel;

using ContextMole.Core;
using ContextMole.Indexing;

namespace ContextMole.App.UI.ViewModels;

public sealed class ProjectItemViewModel : ViewModelBase
{
    private string _name = string.Empty;
    private ProjectState _state;
    private IReadOnlyList<ProjectFolderInfo> _folders = [];
    private IReadOnlyList<ProjectFolderIssue> _folderIssues = [];
    private IReadOnlyList<ProjectFileTypeCount> _fileTypeCounts = [];
    private long _searchGeneration;
    private int _documentCount;
    private int _pendingCount;
    private int _indexedCount;
    private int _searchableCount;
    private int _readyCount;
    private int _attentionCount;
    private int _errorFileCount;
    private int _errorCount;
    private bool _isDiscovering;
    private bool _isErrorsExpanded = true;
    private bool _isErrorsLoading;
    private int _errorPageIndex;
    private DateTimeOffset? _lastCompletedUtc;
    private string? _currentFile;
    private ProjectWorkSummary _work = new(0, 0, 0, 0, null);
    private IndexingTimingSnapshot? _runtimeWork;
    private VectorSnapshotMetadata? _semanticIndex;
    private bool _semanticModelAvailable;

    public ProjectItemViewModel(ProjectSummary project)
    {
        Id = project.Id;
        UpdateFrom(project);
    }

    public Guid Id { get; }
    public const int ErrorPageSize = 25;
    public ObservableCollection<ProjectErrorItemViewModel> RecentErrors { get; } = [];

    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public ProjectState State { get => _state; private set => SetProperty(ref _state, value); }
    public IReadOnlyList<ProjectFolderInfo> Folders { get => _folders; private set => SetProperty(ref _folders, value); }
    public IReadOnlyList<ProjectFolderIssue> FolderIssues { get => _folderIssues; private set => SetProperty(ref _folderIssues, value); }
    public bool HasFolderIssues => FolderIssues.Count > 0;
    public IReadOnlyList<ProjectFileTypeCount> FileTypeCounts { get => _fileTypeCounts; private set => SetProperty(ref _fileTypeCounts, value); }
    public long SearchGeneration { get => _searchGeneration; private set => SetProperty(ref _searchGeneration, value); }
    public int DocumentCount { get => _documentCount; private set => SetProperty(ref _documentCount, value); }
    public int PendingCount { get => _pendingCount; private set => SetProperty(ref _pendingCount, value); }
    public int IndexedCount { get => _indexedCount; private set => SetProperty(ref _indexedCount, value); }
    public int SearchableCount { get => _searchableCount; private set => SetProperty(ref _searchableCount, value); }
    public int ReadyCount { get => _readyCount; private set => SetProperty(ref _readyCount, value); }
    public int AttentionCount { get => _attentionCount; private set => SetProperty(ref _attentionCount, value); }
    public int ErrorFileCount { get => _errorFileCount; private set => SetProperty(ref _errorFileCount, value); }
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
    public int WaitingForCpuCount => Math.Min(ClaimedButNotProcessingCount,
        _runtimeWork?.WaitingForCpuCount ?? 0);
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

    public string Phase => State == ProjectState.Paused
        ? _runtimeWork?.ActiveItems.Count > 0 ? "Pausing" : "Paused"
        : _isDiscovering ? "Finding files"
        : RunningRetryCount > 0 ? "Retrying"
        : ProcessingCount > 0 ? "Indexing"
        : WaitingForCpuCount > 0 ? "Waiting for CPU"
        : EffectiveWorkPhase == ProjectWorkPhase.RetryScheduled ? "Retry scheduled"
        : EffectiveWorkPhase == ProjectWorkPhase.Queued ? "Queued"
        : ErrorCount > 0 || AttentionCount > 0 || HasFolderIssues ? "Needs attention"
        : "Ready";

    public string PhaseDetails => Phase switch
    {
        "Pausing" => "Stopping active work. Interrupted files will be queued for resume.",
        "Paused" => PendingCount > 0
            ? $"{PendingCount:N0} {(PendingCount == 1 ? "file will" : "files will")} continue when indexing resumes. Searchable files remain available."
            : "Indexing is paused. Folder changes will be checked when indexing resumes.",
        "Finding files" => "Checking folders for new, changed, and removed files. Counts update as files are found.",
        "Retrying" => RetryingSourcePath is null
            ? "A failed file is being retried now."
            : $"Retrying {Path.GetFileName(RetryingSourcePath)} now.",
        "Indexing" => ProcessingSourcePath is null
            ? "Indexing is active."
            : $"Indexing {Path.GetFileName(ProcessingSourcePath)} now.",
        "Waiting for CPU" => WaitingForCpuCount == 1
            ? "1 file is waiting for processor capacity."
            : $"{WaitingForCpuCount} files are waiting for processor capacity.",
        "Retry scheduled" when NextRetryUtc is not null =>
            $"Next retry is scheduled for {NextRetryUtc.Value.ToLocalTime():g}.",
        "Retry scheduled" => "A retry is scheduled for later.",
        "Queued" => QueuedCount == 1 ? "1 file is waiting to be processed."
            : $"{QueuedCount} files are waiting to be processed.",
        _ when HasFolderIssues => "Some folders cannot be checked for changes. Previously indexed files remain searchable.",
        _ when ErrorCount > 0 => "Review the current issues below. Files with a previous successful index remain searchable.",
        _ when AttentionCount > 0 => "Some files have no completed index. Reindex this project to queue them again.",
        _ when DocumentCount == 0 => "No supported files have been found in the watched folders.",
        _ => "All discovered files are up to date. Folders are watched for changes."
    };

    public bool IsPaused => State == ProjectState.Paused;
    public bool IsReady => Phase == "Ready";
    public bool IsRetrying => Phase == "Retrying";
    public bool IsRetryScheduled => Phase == "Retry scheduled";
    public bool IsRetryStatus => IsRetrying || IsRetryScheduled;
    public bool IsWaitingForResources => Phase == "Waiting for CPU";
    public bool NeedsAttention => Phase == "Needs attention";
    public bool HasErrors => ErrorCount > 0;
    public bool HasAttentionFiles => AttentionCount > 0;
    public bool CanReindex => State == ProjectState.Active;
    public bool CanRetryFailedFiles => State == ProjectState.Active && ErrorFileCount > 0;
    public bool HasMixedSemanticIndex => _semanticIndex?.HasPartialCoverage == true;
    public bool IsSemanticRepairQueued => _semanticIndex?.IsRepairQueued == true;
    public bool ShowSemanticRepairButton => HasMixedSemanticIndex && !IsSemanticRepairQueued;
    public bool CanRepairSemanticIndex => ShowSemanticRepairButton && State == ProjectState.Active &&
                                          _semanticModelAvailable;
    public string SemanticIndexStatusLabel => IsSemanticRepairQueued ? "REPAIR QUEUED" : "PARTIAL COVERAGE";
    public string SemanticIndexStatusMessage
    {
        get
        {
            if (_semanticIndex is not { HasPartialCoverage: true } metadata) return string.Empty;
            var excluded = metadata.ExcludedDocumentCount;
            var coverage = $"Meaning-based search currently covers {metadata.CompatibleDocumentCount} of " +
                           $"{metadata.TotalDocumentCount} indexed files.";
            if (metadata.IsRepairQueued)
                return $"{coverage} The remaining {excluded} {(excluded == 1 ? "file is" : "files are")} queued for background repair.";
            if (metadata.RepairQueuedDocumentCount > 0)
            {
                var remaining = excluded - metadata.RepairQueuedDocumentCount;
                return $"{coverage} Repair is queued for {metadata.RepairQueuedDocumentCount}; " +
                       $"{remaining} still {(remaining == 1 ? "needs" : "need")} repair.";
            }
            return $"{coverage} {excluded} {(excluded == 1 ? "file needs" : "files need")} compatible embeddings.";
        }
    }
    public string RepairSemanticIndexToolTip => State == ProjectState.Paused
        ? "Resume indexing before repairing semantic coverage."
        : !_semanticModelAvailable
            ? "The selected semantic model must be available before repair can be queued."
            : "Queue only files with missing, incomplete, or outdated embeddings.";
    public string ReindexToolTip => IsPaused
        ? "Resume indexing before rebuilding this project."
        : "Rebuild the local index from the watched folders.";
    public string RetryFailedFilesToolTip => IsPaused
        ? "Resume indexing before retrying failed files."
        : ErrorFileCount == 0 ? "These issues apply to the project or its folders. Review the issue details to restore access."
        : "Queue failed files that do not already have active indexing work.";
    public bool HasFileTypeCounts => FileTypeCounts.Count > 0;
    public bool HasNoFileTypeCounts => !HasFileTypeCounts;
    public bool HasQueueBreakdown => QueuedBreakdownDisplay.Length > 0;
    public string QueuedBreakdownDisplay
    {
        get
        {
            var parts = new List<string>(2);
            if (RetryScheduledCount > 0)
                parts.Add(RetryScheduledCount == 1 ? "1 scheduled retry" : $"{RetryScheduledCount} scheduled retries");
            if (WaitingForCpuCount > 0)
                parts.Add($"{WaitingForCpuCount} CPU wait");
            return string.Join(" · ", parts);
        }
    }
    public bool HasRecentErrors => RecentErrors.Count > 0;
    public bool IsErrorsExpanded
    {
        get => _isErrorsExpanded;
        set
        {
            if (SetProperty(ref _isErrorsExpanded, value)) OnPropertyChanged(nameof(IsErrorsCollapsed));
        }
    }
    public bool IsErrorsCollapsed => !IsErrorsExpanded;
    public bool IsErrorsLoading
    {
        get => _isErrorsLoading;
        set
        {
            if (!SetProperty(ref _isErrorsLoading, value)) return;
            NotifyErrorPagingChanged();
        }
    }
    public int ErrorPageIndex => _errorPageIndex;
    public int ErrorPageOffset => ErrorPageIndex * ErrorPageSize;
    public bool HasErrorPages => ErrorCount > ErrorPageSize;
    public bool CanGoToPreviousErrorPage => !IsErrorsLoading && ErrorPageIndex > 0;
    public bool CanGoToNextErrorPage => !IsErrorsLoading && ErrorPageOffset + ErrorPageSize < ErrorCount;
    public string PauseActionLabel => State == ProjectState.Paused ? "Resume indexing" : "Pause indexing";
    public string FolderCountDisplay => Folders.Count == 1 ? "1 folder" : $"{Folders.Count} folders";
    public string DocumentCountDisplay => DocumentCount == 1 ? "1 file" : $"{DocumentCount:N0} files";
    public string SidebarErrorCountDisplay => ErrorCount == 1 ? "1 issue" : $"{ErrorCount:N0} issues";
    public string ErrorCountDisplay => ErrorCount == 1 ? "1 current issue" : $"{ErrorCount:N0} current issues";
    public string RecentErrorsSummary => ErrorFileCount > 0
        ? $"{ErrorCountDisplay} affecting {ErrorFileCount:N0} {(ErrorFileCount == 1 ? "file" : "files")}. Resolved issues disappear automatically."
        : $"{ErrorCountDisplay}. Resolved issues disappear automatically.";
    public string ErrorPageDisplay => IsErrorsLoading && RecentErrors.Count == 0 ? "Loading current issues…"
        : RecentErrors.Count == 0 ? "Refreshing current issues…"
        : $"{ErrorPageOffset + 1:N0}–{ErrorPageOffset + RecentErrors.Count:N0} of {Math.Max(ErrorCount, ErrorPageOffset + RecentErrors.Count):N0} issues";
    public string SearchableSummary => $"{SearchableCount:N0} {(SearchableCount == 1 ? "file is" : "files are")} searchable now. Previous indexed versions stay available while updates are processed.";
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
        var previousCanRepairSemanticIndex = CanRepairSemanticIndex;
        var previousRepairSemanticIndexToolTip = RepairSemanticIndexToolTip;
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
        var previousWaitingForCpuCount = WaitingForCpuCount;
        var previousHasQueueBreakdown = HasQueueBreakdown;
        var previousQueuedBreakdownDisplay = QueuedBreakdownDisplay;
        var previousSearchableSummary = SearchableSummary;
        var previousHasAttentionFiles = HasAttentionFiles;

        Name = project.Name;
        State = project.State;
        if (!Folders.SequenceEqual(project.Folders)) Folders = project.Folders.ToArray();
        SearchGeneration = project.SearchGeneration;
        DocumentCount = project.DocumentCount;
        PendingCount = project.PendingCount;
        IndexedCount = project.IndexedCount;
        SearchableCount = project.SearchableCount;
        ReadyCount = project.ReadyCount;
        AttentionCount = project.AttentionCount;
        ErrorFileCount = project.ErrorFileCount;
        ErrorCount = project.ErrorCount;
        LastCompletedUtc = project.LastCompletedUtc;
        CurrentFile = project.CurrentFile;
        Work = project.Work;
        // A resolved page must not linger until the next details query finishes.
        var lastPage = Math.Max(0, (ErrorCount - 1) / ErrorPageSize);
        if (_errorPageIndex > lastPage)
        {
            _errorPageIndex = lastPage;
            RecentErrors.Clear();
            OnPropertyChanged(nameof(HasRecentErrors));
        }
        if (ErrorCount == 0 && RecentErrors.Count > 0)
        {
            RecentErrors.Clear();
            OnPropertyChanged(nameof(HasRecentErrors));
        }
        NotifyErrorPagingChanged();

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
        if (previousHasAttentionFiles != HasAttentionFiles) OnPropertyChanged(nameof(HasAttentionFiles));
        if (previousSearchableSummary != SearchableSummary) OnPropertyChanged(nameof(SearchableSummary));
        if (previousCanReindex != CanReindex) OnPropertyChanged(nameof(CanReindex));
        if (previousCanRetryFailedFiles != CanRetryFailedFiles) OnPropertyChanged(nameof(CanRetryFailedFiles));
        if (previousCanRepairSemanticIndex != CanRepairSemanticIndex)
            OnPropertyChanged(nameof(CanRepairSemanticIndex));
        if (!string.Equals(previousRepairSemanticIndexToolTip, RepairSemanticIndexToolTip, StringComparison.Ordinal))
            OnPropertyChanged(nameof(RepairSemanticIndexToolTip));
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
        if (previousWaitingForCpuCount != WaitingForCpuCount) OnPropertyChanged(nameof(WaitingForCpuCount));
        if (previousHasQueueBreakdown != HasQueueBreakdown) OnPropertyChanged(nameof(HasQueueBreakdown));
        if (!string.Equals(previousQueuedBreakdownDisplay, QueuedBreakdownDisplay, StringComparison.Ordinal))
            OnPropertyChanged(nameof(QueuedBreakdownDisplay));
    }

    public void UpdateRuntime(IndexingTimingSnapshot runtime, bool isDiscovering = false)
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
        var previousWaitingForCpuCount = WaitingForCpuCount;
        var previousHasQueueBreakdown = HasQueueBreakdown;
        var previousQueuedBreakdownDisplay = QueuedBreakdownDisplay;

        _runtimeWork = runtime;
        _isDiscovering = isDiscovering;

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
        if (previousWaitingForCpuCount != WaitingForCpuCount) OnPropertyChanged(nameof(WaitingForCpuCount));
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
        OnPropertyChanged(nameof(ErrorPageDisplay));
    }

    public void MoveErrorPage(int direction)
    {
        var nextPage = Math.Clamp(ErrorPageIndex + direction, 0, Math.Max(0, (ErrorCount - 1) / ErrorPageSize));
        if (nextPage == ErrorPageIndex) return;
        _errorPageIndex = nextPage;
        RecentErrors.Clear();
        OnPropertyChanged(nameof(HasRecentErrors));
        NotifyErrorPagingChanged();
    }

    private void NotifyErrorPagingChanged()
    {
        OnPropertyChanged(nameof(ErrorPageIndex));
        OnPropertyChanged(nameof(ErrorPageOffset));
        OnPropertyChanged(nameof(HasErrorPages));
        OnPropertyChanged(nameof(CanGoToPreviousErrorPage));
        OnPropertyChanged(nameof(CanGoToNextErrorPage));
        OnPropertyChanged(nameof(ErrorPageDisplay));
    }

    public void UpdateSemanticIndex(VectorSnapshotMetadata? metadata, bool modelAvailable)
    {
        if (_semanticIndex == metadata && _semanticModelAvailable == modelAvailable) return;
        _semanticIndex = metadata;
        _semanticModelAvailable = modelAvailable;
        OnPropertyChanged(nameof(HasMixedSemanticIndex));
        OnPropertyChanged(nameof(IsSemanticRepairQueued));
        OnPropertyChanged(nameof(ShowSemanticRepairButton));
        OnPropertyChanged(nameof(CanRepairSemanticIndex));
        OnPropertyChanged(nameof(SemanticIndexStatusLabel));
        OnPropertyChanged(nameof(SemanticIndexStatusMessage));
        OnPropertyChanged(nameof(RepairSemanticIndexToolTip));
    }

    public void UpdateFileTypeCounts(IReadOnlyList<ProjectFileTypeCount> counts)
    {
        if (FileTypeCounts.SequenceEqual(counts)) return;
        FileTypeCounts = counts.ToArray();
        OnPropertyChanged(nameof(HasFileTypeCounts));
        OnPropertyChanged(nameof(HasNoFileTypeCounts));
    }

    public void UpdateFolderIssues(IReadOnlyList<ProjectFolderIssue> issues)
    {
        var current = issues.Where(issue => Folders.Any(folder => folder.Id == issue.FolderId)).ToArray();
        if (FolderIssues.SequenceEqual(current)) return;
        FolderIssues = current;
        OnPropertyChanged(nameof(HasFolderIssues));
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(PhaseDetails));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(NeedsAttention));
    }

    public ProjectSummary ToSummary() => new(Id, Name, State, Folders, SearchGeneration, DocumentCount,
        PendingCount, IndexedCount, ErrorCount, LastCompletedUtc, CurrentFile)
    {
        Work = Work,
        SearchableCount = SearchableCount,
        ReadyCount = ReadyCount,
        AttentionCount = AttentionCount,
        ErrorFileCount = ErrorFileCount
    };

    private int IndexOfError(long id, int startIndex)
    {
        for (var index = startIndex; index < RecentErrors.Count; index++)
        {
            if (RecentErrors[index].Id == id) return index;
        }

        return -1;
    }
}
