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
    private int _excludedPathCount;
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
    private bool _isSemanticCoverageLoading = true;
    private bool _hasSemanticCoverageSnapshot;
    private string? _semanticCoverageError;
    private bool _actionsBusy;
    private bool _isReadinessStale;
    private DateTimeOffset? _lastChecked;
    private bool _isInitialScanComplete;
    private string _issueQuery = string.Empty;
    private string _issueCodeFilter = string.Empty;
    private int _issueImpactFilterIndex;
    private bool _showHiddenIssues;
    private ProjectIssueListResponse? _issuePage;
    private string? _nextIssueCursor;
    private bool _issuesChangedDuringPaging;
    private readonly List<string?> _issueCursors = [null];
    private int _issuePageIndex;
    private long _issueQueryVersion;
    private string? _issueLoadError;
    private IReadOnlyList<Guid> _undoAcknowledgements = [];
    private string _issueActionMessage = string.Empty;
    private IReadOnlyList<ExcludedFileInfo> _allExcludedFiles = [];
    private int _excludedPageIndex;
    private string _excludedQuery = string.Empty;

    public ProjectItemViewModel(ProjectSummary project)
    {
        Id = project.Id;
        _isInitialScanComplete = project.DocumentCount > 0 || project.LastCompletedUtc is not null;
        UpdateFrom(project);
    }

    public Guid Id { get; }
    public override string ToString() => Name;
    public const int ErrorPageSize = 25;
    public ObservableCollection<ProjectErrorItemViewModel> RecentErrors { get; } = [];

    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public ProjectState State { get => _state; private set => SetProperty(ref _state, value); }
    public IReadOnlyList<ProjectFolderInfo> Folders { get => _folders; private set => SetProperty(ref _folders, value); }
    public IReadOnlyList<ProjectFolderIssue> FolderIssues { get => _folderIssues; private set => SetProperty(ref _folderIssues, value); }
    public bool HasFolderIssues => FolderIssues.Count > 0;
    public bool IsDiscovering => _isDiscovering;
    public int ProgressMaximum => Math.Max(1, DocumentCount);
    public string ProgressDescription => $"{ReadyCount:N0} of {DocumentCount:N0} files up to date. {PendingCount:N0} pending. {AttentionCount:N0} need attention.";
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
    public int ExcludedPathCount { get => _excludedPathCount; private set => SetProperty(ref _excludedPathCount, value); }
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

    public string Phase => _isReadinessStale ? "Status unavailable"
        : State == ProjectState.Paused
        ? _runtimeWork?.ActiveItems.Count > 0 ? "Pausing" : "Paused"
        : _isDiscovering ? "Finding files"
        : RunningRetryCount > 0 ? "Retrying"
        : ProcessingCount > 0 ? "Indexing"
        : WaitingForCpuCount > 0 ? "Waiting for CPU"
        : EffectiveWorkPhase == ProjectWorkPhase.RetryScheduled ? "Retry scheduled"
        : EffectiveWorkPhase == ProjectWorkPhase.Queued ? "Queued"
        : ErrorCount > 0 || AttentionCount > 0 || HasFolderIssues ? "Needs attention"
        : !_isInitialScanComplete ? "Awaiting first scan"
        : DocumentCount == 0 ? "No supported files" : "Ready";

    public string PhaseDetails => Phase switch
    {
        "Status unavailable" => "Current readiness could not be verified. Counts and search coverage below are from the last successful check.",
        "Awaiting first scan" => "Waiting for the first folder scan. No supported-file or search-coverage result has been verified yet.",
        "No supported files" => "The folder scan completed, but no supported files were found. Choose a different folder or add supported files.",
        "Pausing" => "Stopping active extraction. Folder discovery and removal checks continue; interrupted files are queued for resume.",
        "Paused" => PendingCount > 0
            ? $"{PendingCount:N0} {(PendingCount == 1 ? "file will" : "files will")} continue when indexing resumes. Changes are still detected and queued; saved evidence stays searchable unless its source is removed."
            : "Extraction is paused. Folder changes are still detected and queued, and removed sources leave search. Other saved evidence stays searchable.",
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
    public bool IsReady => !_isReadinessStale && Phase == "Ready";
    public bool IsRetrying => Phase == "Retrying";
    public bool IsRetryScheduled => Phase == "Retry scheduled";
    public bool IsRetryStatus => IsRetrying || IsRetryScheduled;
    public bool IsWaitingForResources => Phase == "Waiting for CPU";
    public bool NeedsAttention => Phase == "Needs attention";
    public bool HasErrors => ErrorCount > 0;
    public bool HasAttentionFiles => AttentionCount > 0;
    public bool AreActionsEnabled => !_actionsBusy;
    public bool CanReindex => AreActionsEnabled && State == ProjectState.Active;
    public bool CanRetryFailedFiles => AreActionsEnabled && State == ProjectState.Active && ErrorFileCount > 0;
    public bool HasMixedSemanticIndex => !_isSemanticCoverageLoading && _semanticCoverageError is null && _semanticIndex?.HasPartialCoverage == true;
    public bool HasSemanticCoverageWarning => HasMixedSemanticIndex || _semanticCoverageError is not null;
    public bool IsSemanticRepairQueued => _semanticIndex?.IsRepairQueued == true;
    public bool ShowSemanticRepairButton => HasMixedSemanticIndex && !IsSemanticRepairQueued;
    public bool CanRepairSemanticIndex => AreActionsEnabled && ShowSemanticRepairButton && State == ProjectState.Active &&
                                          _semanticModelAvailable && !_isReadinessStale && _semanticCoverageError is null && IsSemanticSnapshotCurrent;
    private bool IsSemanticSnapshotCurrent => _semanticIndex is null || _semanticIndex.SearchGeneration == SearchGeneration;
    public string SemanticIndexStatusLabel => _isReadinessStale ? "LAST CHECK · STALE"
        : _isSemanticCoverageLoading ? "CHECKING"
        : _semanticCoverageError is not null ? "UNABLE TO CHECK"
        : !_semanticModelAvailable ? "KEYWORD ONLY"
        : _semanticIndex is null || _semanticIndex.TotalDocumentCount == 0 ? "NO INDEXED FILES"
        : IsSemanticRepairQueued ? "REPAIR QUEUED"
        : HasMixedSemanticIndex ? "PARTIAL COVERAGE" : !IsSemanticSnapshotCurrent ? "REFRESHING COVERAGE" : "COMPLETE";
    public string SemanticIndexStatusMessage
    {
        get
        {
            if (_isReadinessStale) return "Current meaning-based coverage could not be verified. The last known counts may have changed.";
            if (_isSemanticCoverageLoading) return "Checking meaning-based coverage for the current index and selected model…";
            if (_semanticCoverageError is not null)
                return $"Current coverage could not be checked. {_semanticCoverageError}" +
                       (_semanticIndex is { } previous ? $" Last verified: {previous.CompatibleDocumentCount:N0} of {previous.TotalDocumentCount:N0} indexed files covered." : string.Empty);
            if (!_semanticModelAvailable) return "Keyword search remains available. Set up a semantic model in Settings to add meaning-based search.";
            if (_semanticIndex is not { } metadata || metadata.TotalDocumentCount == 0)
                return "No indexed files are available for meaning-based search yet.";
            var excluded = metadata.ExcludedDocumentCount;
            var coverage = $"Last verified meaning-based coverage: {metadata.CompatibleDocumentCount:N0} of " +
                           $"{metadata.TotalDocumentCount:N0} indexed files.";
            if (!IsSemanticSnapshotCurrent)
                return $"{coverage} Coverage is refreshing as the index changes. Background work and completed jobs are shown below.";
            if (!metadata.HasPartialCoverage) return coverage;
            if (metadata.IsRepairQueued)
                return $"{coverage} The remaining {excluded} {(excluded == 1 ? "file is" : "files are")} queued for background repair.";
            if (metadata.RepairQueuedDocumentCount > 0)
            {
                var remaining = excluded - metadata.RepairQueuedDocumentCount;
                return $"{coverage} Repair is queued for {metadata.RepairQueuedDocumentCount}; " +
                       $"{remaining} still {(remaining == 1 ? "needs" : "need")} repair.";
            }
            var legacy = metadata.ReextractionRequiredDocumentCount;
            if (legacy > 0)
                return $"{coverage} {legacy} {(legacy == 1 ? "legacy file needs" : "legacy files need")} source re-extraction; " +
                       $"{metadata.EmbeddingRepairEligibleDocumentCount} can reuse extracted text for embedding repair. Complete search coverage queues the appropriate work.";
            return $"{coverage} {excluded} {(excluded == 1 ? "file needs" : "files need")} compatible embeddings.";
        }
    }
    public string RepairSemanticIndexToolTip => State == ProjectState.Paused
        ? "Resume indexing before repairing semantic coverage."
        : _isReadinessStale ? "Current coverage could not be verified. Wait for a successful status check before queuing repair."
        : _isSemanticCoverageLoading ? "Checking meaning-based coverage before repair can be queued."
        : _semanticCoverageError is not null ? "Coverage could not be checked. Wait for a successful refresh before queuing repair."
        : !_semanticModelAvailable ? "The selected semantic model must be available before repair can be queued."
        : !IsSemanticSnapshotCurrent ? "Coverage is refreshing for the current index. Follow indexing activity below."
        : IsSemanticRepairQueued ? "Coverage repair is already queued. Follow indexing activity below."
        : IsSemanticCoverageComplete ? "Current meaning-based coverage is complete."
        : "Queue compatible embedding repair, including source re-extraction where legacy text preparation needs upgrading.";
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
            NotifyIssuePresentation();
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
        ? $"{ErrorCountDisplay} affecting {ErrorFileCount:N0} {(ErrorFileCount == 1 ? "file" : "files")}. Hidden issues still affect coverage. Issues clear only when their cause is resolved."
        : $"{ErrorCountDisplay}. Hidden issues still affect coverage. Issues clear only when their cause is resolved.";
    public string ErrorPageDisplay => IsErrorsLoading && RecentErrors.Count == 0 ? "Loading current issues…"
        : RecentErrors.Count == 0 ? "Refreshing current issues…"
        : $"{ErrorPageOffset + 1:N0}–{ErrorPageOffset + RecentErrors.Count:N0} of {Math.Max(ErrorCount, ErrorPageOffset + RecentErrors.Count):N0} issues";
    public string SearchableSummary => _isReadinessStale
        ? $"Last verified: {SearchableCount:N0} files with searchable content. Current coverage is unavailable."
        : $"{SearchableCount:N0} {(SearchableCount == 1 ? "file is" : "files are")} searchable now. " +
          (IndexedCount > SearchableCount ? $"{IndexedCount - SearchableCount:N0} completed files contain no searchable passages. " : string.Empty) +
          "Searchable content can be partial or from a previous indexed version while updates are processed.";
    public string LastCompletedDisplay => LastCompletedUtc?.ToLocalTime().ToString("g") ?? "Not yet completed";
    public string ProjectDetailsDisplay => LastCompletedUtc is null
        ? $"{FolderCountDisplay} · Not indexed yet{ExcludedPathsDisplay}"
        : $"{FolderCountDisplay} · Last completed {LastCompletedDisplay}{ExcludedPathsDisplay}";
    private string ExcludedPathsDisplay => ExcludedPathCount > 0 ? $" · {ExcludedPathCount:N0} exact paths excluded" : string.Empty;

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
        if (!Folders.SequenceEqual(project.Folders))
        {
            Folders = project.Folders.ToArray();
            if (_allExcludedFiles.Count > 0) UpdateExcludedPage();
        }
        var generationChanged = SearchGeneration != project.SearchGeneration;
        SearchGeneration = project.SearchGeneration;
        DocumentCount = project.DocumentCount;
        PendingCount = project.PendingCount;
        IndexedCount = project.IndexedCount;
        SearchableCount = project.SearchableCount;
        ReadyCount = project.ReadyCount;
        AttentionCount = project.AttentionCount;
        ErrorFileCount = project.ErrorFileCount;
        ErrorCount = project.ErrorCount;
        ExcludedPathCount = project.ExcludedPathCount;
        LastCompletedUtc = project.LastCompletedUtc;
        CurrentFile = project.CurrentFile;
        Work = project.Work;
        if (generationChanged)
        {
            // Keep the current file keyset and root objects while refreshed details are read.
            // An unrelated published revision must not interrupt ongoing issue triage.
            // Detail rows belong to the published generation that produced them.
            // Each committed file advances the generation. Keep the last verified coverage and
            // inventory on screen while polling; clearing them made every job flash the page.
            // Coverage is explicitly historical until the matching generation is read.
            BeginSemanticIndexRefresh();
            if (RecentErrors.Count > 0)
            {
                RecentErrors.Clear();
                OnPropertyChanged(nameof(HasRecentErrors));
            }
        }
        // A resolved page must not linger until the next details query finishes.
        var lastPage = Math.Max(0, (ErrorCount - 1) / ErrorPageSize);
        if (_errorPageIndex > lastPage)
        {
            _errorPageIndex = lastPage;
            RecentErrors.Clear();
            OnPropertyChanged(nameof(HasRecentErrors));
        }
        if (ErrorCount == 0 && IssueGroups.Count > 0)
        {
            ResetIssuePages();
        }
        if (ErrorCount == 0 && RecentErrors.Count > 0)
        {
            RecentErrors.Clear();
            OnPropertyChanged(nameof(HasRecentErrors));
        }
        NotifyErrorPagingChanged();
        NotifyIssuePresentation();

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
        OnPropertyChanged(nameof(ProgressMaximum));
        OnPropertyChanged(nameof(ProgressDescription));
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

    public void UpdateRuntime(IndexingTimingSnapshot runtime, bool isDiscovering = false, bool isInitialScanComplete = true)
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
        _isInitialScanComplete = isInitialScanComplete;
        OnPropertyChanged(nameof(IsDiscovering));

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
        if (_semanticIndex == metadata && _semanticModelAvailable == modelAvailable && !_isSemanticCoverageLoading && _semanticCoverageError is null) return;
        _semanticIndex = metadata;
        _semanticModelAvailable = modelAvailable;
        _hasSemanticCoverageSnapshot = true;
        _isSemanticCoverageLoading = false;
        _semanticCoverageError = null;
        NotifySemanticCoverageChanged();
    }

    public bool IsSemanticCoverageComplete => SemanticIndexStatusLabel == "COMPLETE";

    public void SetActionsBusy(bool busy)
    {
        if (_actionsBusy == busy) return;
        _actionsBusy = busy;
        OnPropertyChanged(nameof(AreActionsEnabled));
        OnPropertyChanged(nameof(CanReindex));
        OnPropertyChanged(nameof(CanRetryFailedFiles));
        OnPropertyChanged(nameof(CanRepairSemanticIndex));
        OnPropertyChanged(nameof(CanRetryVisibleIssueFiles));
    }

    public void BeginSemanticIndexRefreshForPolicy(string? policyKey, bool modelAvailable) =>
        BeginSemanticIndexRefresh(invalidate: _hasSemanticCoverageSnapshot &&
            (_semanticIndex is { } previous && !string.Equals(previous.Policy?.Key, policyKey, StringComparison.Ordinal) ||
             _semanticModelAvailable != modelAvailable));

    public void BeginSemanticIndexRefresh(bool invalidate = false)
    {
        if (invalidate)
        {
            _semanticIndex = null;
            _hasSemanticCoverageSnapshot = false;
            _semanticCoverageError = null;
        }
        _isSemanticCoverageLoading = !_hasSemanticCoverageSnapshot;
        NotifySemanticCoverageChanged();
    }

    public void FailSemanticIndexRefresh(string message)
    {
        _isSemanticCoverageLoading = false;
        _semanticCoverageError = message;
        NotifySemanticCoverageChanged();
    }

    private void NotifySemanticCoverageChanged()
    {
        OnPropertyChanged(nameof(HasMixedSemanticIndex));
        OnPropertyChanged(nameof(HasSemanticCoverageWarning));
        OnPropertyChanged(nameof(IsSemanticCoverageComplete));
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

    public ObservableCollection<ProjectIssueGroupViewModel> IssueGroups { get; } = [];
    public ObservableCollection<ExcludedFileItemViewModel> ExcludedFiles { get; } = [];
    public string IssueQuery { get => _issueQuery; set { if (SetProperty(ref _issueQuery, value)) ResetIssuePages(); } }
    public string IssueCodeFilter { get => _issueCodeFilter; set { if (SetProperty(ref _issueCodeFilter, value)) ResetIssuePages(); } }
    public int IssueImpactFilterIndex { get => _issueImpactFilterIndex; set { if (SetProperty(ref _issueImpactFilterIndex, value)) ResetIssuePages(); } }
    public bool ShowHiddenIssues { get => _showHiddenIssues; set { if (SetProperty(ref _showHiddenIssues, value)) ResetIssuePages(); } }
    public ProjectIssueImpact IssueImpactFilter => (ProjectIssueImpact)Math.Clamp(IssueImpactFilterIndex, 0, 3);
    public ProjectIssueVisibility IssueVisibility => ShowHiddenIssues ? ProjectIssueVisibility.All : ProjectIssueVisibility.Visible;
    public string? IssueCursor => _issueCursors[_issuePageIndex];
    public long IssueQueryVersion => _issueQueryVersion;
    public int HiddenIssueCount => _issuePage?.HiddenIssueCount ?? 0;
    public int HiddenIssueFileCount => _issuePage?.HiddenFileCount ?? 0;
    public bool HasHiddenIssues => HiddenIssueCount > 0;
    public string HiddenIssuesDisplay => $"Show hidden ({HiddenIssueCount:N0} {(HiddenIssueCount == 1 ? "component" : "components")} / {HiddenIssueFileCount:N0} {(HiddenIssueFileCount == 1 ? "file" : "files")})";
    public bool HasIssueCard => HasErrors || HasIssueLoadError;
    public bool HasIssueGroups => IssueGroups.Count > 0;
    public bool HasNoMatchingIssueGroups => !IsErrorsLoading && IssueLoadError is null && !HasIssueGroups;
    public bool HasIssueLoadError => IssueLoadError is not null;
    public string? IssueLoadError { get => _issueLoadError; private set { SetProperty(ref _issueLoadError, value); NotifyIssuePresentation(); } }
    public string NoMatchingIssuesMessage => _issuePageIndex > 0
        ? "No remaining matching files on this page. Use Previous or Refresh issues to review the latest list."
        : HasHiddenIssues && string.IsNullOrWhiteSpace(IssueQuery) && string.IsNullOrWhiteSpace(IssueCodeFilter) && IssueImpactFilter == ProjectIssueImpact.All && !ShowHiddenIssues
        ? "All matching current issues are hidden. They still affect readiness and coverage. Show hidden to review or restore them."
        : "No files match these issue filters. Try a different path, cause, or impact, or show hidden issues.";
    public string GroupedIssuesSummary => _issuePage is null ? RecentErrorsSummary :
        $"{_issuePage.TotalIssueCount:N0} current issue components affecting {_issuePage.TotalFileCount:N0} file / operation groups · " +
        $"{_issuePage.HiddenIssueCount:N0} components hidden. Hiding acknowledges an issue; it does not resolve it or change search.";
    public string IssuePageDisplay => IsErrorsLoading ? "Loading current issues…" :
        $"File page {_issuePageIndex + 1} · {IssueGroups.Count:N0} shown of {_issuePage?.FilteredFileCount ?? 0:N0} matching files · path order";
    public bool IssuesChangedDuringPaging => _issuesChangedDuringPaging;
    public bool CanPreviousIssuePage => !IsErrorsLoading && _issuePageIndex > 0;
    public bool CanNextIssuePage => !IsErrorsLoading && _nextIssueCursor is not null;
    public bool CanRetryVisibleIssueFiles => CanReindex && IssueGroups.Any(group => group.CanRetry && !group.IsHidden);
    public IReadOnlyList<Guid> VisibleRetryDocumentIds => IssueGroups.Where(group => group.CanRetry && !group.IsHidden)
        .Select(group => group.DocumentId!.Value).Distinct().ToArray();
    public bool HasUndoIssueHide => _undoAcknowledgements.Count > 0;
    public IReadOnlyList<Guid> UndoAcknowledgements => _undoAcknowledgements;
    public string IssueActionMessage { get => _issueActionMessage; private set => SetProperty(ref _issueActionMessage, value); }
    public bool HasIssueActionMessage => IssueActionMessage.Length > 0;
    public string ExcludedQuery { get => _excludedQuery; set { if (SetProperty(ref _excludedQuery, value)) { _excludedPageIndex = 0; UpdateExcludedPage(); } } }
    private IReadOnlyList<ExcludedFileInfo> FilteredExcludedFiles => string.IsNullOrWhiteSpace(ExcludedQuery) ? _allExcludedFiles :
        _allExcludedFiles.Where(file => file.SourcePath.Contains(ExcludedQuery, StringComparison.OrdinalIgnoreCase)).ToArray();
    public int MatchingExcludedFileCount => FilteredExcludedFiles.Count;
    public bool HasNoMatchingExcludedFiles => HasExcludedFiles && MatchingExcludedFileCount == 0;
    public int ExcludedFileCount => _allExcludedFiles.Count;
    public bool HasExcludedFiles => ExcludedFileCount > 0;
    public string ExcludedFilesSummary => $"{ExcludedFileCount:N0} exact root paths excluded from indexing and search. Renamed paths are eligible again.";
    public bool CanPreviousExcludedPage => _excludedPageIndex > 0;
    public bool CanNextExcludedPage => (_excludedPageIndex + 1) * ErrorPageSize < MatchingExcludedFileCount;
    public string ExcludedPageDisplay => $"Excluded path page {_excludedPageIndex + 1} · {ExcludedFiles.Count:N0} shown of {MatchingExcludedFileCount:N0} matching paths";
    public bool IsReadinessStale => _isReadinessStale;
    public string LastCheckedDisplay => _lastChecked is { } checkedAt ? $"Last verified {checkedAt.ToLocalTime():g}" : "Not verified yet";

    public ProjectIssueListRequest CreateIssueRequest() => new(Id, IssueQuery, Code: string.IsNullOrWhiteSpace(IssueCodeFilter) ? null : IssueCodeFilter.Trim(), Impact: IssueImpactFilter,
        Visibility: IssueVisibility, Limit: ErrorPageSize, Cursor: IssueCursor);

    public void UpdateIssueGroups(ProjectIssueListResponse page)
    {
        if (page.ProjectId != Id) throw new ArgumentException("Issue page must belong to this project.", nameof(page));
        _issuePage = page;
        if (page.HiddenIssueCount == 0 && HasUndoIssueHide) ClearIssueUndo();
        _issuesChangedDuringPaging |= page.IssuesChangedDuringPaging;
        _nextIssueCursor = page.NextCursor;
        for (var targetIndex = 0; targetIndex < page.Groups.Count; targetIndex++)
        {
            var source = page.Groups[targetIndex];
            if (targetIndex < IssueGroups.Count && IssueGroups[targetIndex].SourcePath == source.SourcePath)
            {
                IssueGroups[targetIndex].UpdateFrom(source);
                continue;
            }
            var existingIndex = -1;
            for (var index = targetIndex + 1; index < IssueGroups.Count; index++)
                if (IssueGroups[index].SourcePath == source.SourcePath) { existingIndex = index; break; }
            if (existingIndex >= 0)
            {
                IssueGroups.Move(existingIndex, targetIndex);
                IssueGroups[targetIndex].UpdateFrom(source);
            }
            else IssueGroups.Insert(targetIndex, new(source));
        }
        while (IssueGroups.Count > page.Groups.Count) IssueGroups.RemoveAt(IssueGroups.Count - 1);
        IssueLoadError = null;
        NotifyIssuePresentation();
    }

    public void MoveIssuePage(int direction)
    {
        if (direction < 0 && CanPreviousIssuePage) _issuePageIndex--;
        else if (direction > 0 && CanNextIssuePage)
        {
            if (_issueCursors.Count > _issuePageIndex + 1) _issueCursors.RemoveRange(_issuePageIndex + 1, _issueCursors.Count - _issuePageIndex - 1);
            _issueCursors.Add(_nextIssueCursor); _issuePageIndex++;
        }
        _issueQueryVersion++;
        NotifyIssuePresentation();
    }

    public void ResetIssuePages()
    {
        _issueCursors.Clear(); _issueCursors.Add(null); _issuePageIndex = 0; _nextIssueCursor = null;
        _issuesChangedDuringPaging = false;
        _issueQueryVersion++; IssueGroups.Clear(); NotifyIssuePresentation();
    }
    public void FailIssueRefresh(string message) { IssueLoadError = message; NotifyIssuePresentation(); }
    public void RecordIssueHide(HideProjectIssuesResult result)
    {
        _undoAcknowledgements = result.AcknowledgementIds;
        SetIssueActionMessage($"Hidden {result.HiddenIssueCount:N0} current components. Discovery, retry and search are unchanged; new material failures appear again.");
        OnPropertyChanged(nameof(HasUndoIssueHide));
    }
    public void ClearIssueUndo() { _undoAcknowledgements = []; OnPropertyChanged(nameof(HasUndoIssueHide)); }
    public void SetIssueActionMessage(string message) { IssueActionMessage = message; OnPropertyChanged(nameof(HasIssueActionMessage)); }
    public void UpdateExcludedFiles(IReadOnlyList<ExcludedFileInfo> files)
    {
        if (_allExcludedFiles.SequenceEqual(files)) return;
        _allExcludedFiles = files;
        _excludedPageIndex = Math.Min(_excludedPageIndex, Math.Max(0, (MatchingExcludedFileCount - 1) / ErrorPageSize));
        UpdateExcludedPage();
    }
    public void MoveExcludedPage(int direction)
    {
        _excludedPageIndex = Math.Clamp(_excludedPageIndex + direction, 0, Math.Max(0, (MatchingExcludedFileCount - 1) / ErrorPageSize));
        UpdateExcludedPage();
    }
    private void UpdateExcludedPage()
    {
        ExcludedFiles.Clear();
        foreach (var file in FilteredExcludedFiles.Skip(_excludedPageIndex * ErrorPageSize).Take(ErrorPageSize))
            ExcludedFiles.Add(new(file, Folders.Any(folder => IsWithinFolder(file.SourcePath, folder.Path))));
        foreach (var name in new[] { nameof(ExcludedFileCount), nameof(HasExcludedFiles), nameof(ExcludedFilesSummary),
                     nameof(CanPreviousExcludedPage), nameof(CanNextExcludedPage), nameof(ExcludedPageDisplay),
                     nameof(MatchingExcludedFileCount), nameof(HasNoMatchingExcludedFiles) }) OnPropertyChanged(name);
    }
    private static bool IsWithinFolder(string path, string folder)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }
    public void SetFreshness(bool isStale, DateTimeOffset? lastChecked)
    {
        if (_isReadinessStale == isStale && _lastChecked == lastChecked) return;
        _isReadinessStale = isStale; _lastChecked = lastChecked;
        foreach (var name in new[] { nameof(IsReadinessStale), nameof(LastCheckedDisplay), nameof(Phase), nameof(PhaseDetails),
                     nameof(IsReady), nameof(SearchableSummary) }) OnPropertyChanged(name);
        NotifySemanticCoverageChanged();
    }
    private void NotifyIssuePresentation()
    {
        foreach (var name in new[] { nameof(HiddenIssueCount), nameof(HiddenIssueFileCount), nameof(HasHiddenIssues),
                     nameof(HiddenIssuesDisplay), nameof(HasIssueGroups), nameof(HasNoMatchingIssueGroups), nameof(HasIssueLoadError),
                     nameof(HasIssueCard), nameof(NoMatchingIssuesMessage), nameof(GroupedIssuesSummary), nameof(IssuePageDisplay),
                     nameof(CanPreviousIssuePage), nameof(CanNextIssuePage), nameof(CanRetryVisibleIssueFiles), nameof(IssuesChangedDuringPaging) }) OnPropertyChanged(name);
    }

    public ProjectSummary ToSummary() => new(Id, Name, State, Folders, SearchGeneration, DocumentCount,
        PendingCount, IndexedCount, ErrorCount, LastCompletedUtc, CurrentFile)
    {
        Work = Work,
        SearchableCount = SearchableCount,
        ReadyCount = ReadyCount,
        AttentionCount = AttentionCount,
        ErrorFileCount = ErrorFileCount,
        ExcludedPathCount = ExcludedPathCount
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
