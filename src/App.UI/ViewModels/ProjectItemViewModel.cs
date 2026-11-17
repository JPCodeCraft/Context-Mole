using System.Collections.ObjectModel;

using ContextMole.Core;
using ContextMole.Indexing;

namespace ContextMole.App.UI.ViewModels;

/// <summary>A watched folder with the folder-level problem that currently affects it, if any.</summary>
public sealed record ProjectFolderRow(string Path, string? Issue)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar,
        System.IO.Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : Path;
    public bool HasIssue => Issue is not null;
    public AppStatusTone Tone => HasIssue ? AppStatusTone.Error : AppStatusTone.Ready;
}

public sealed class ProjectItemViewModel : ViewModelBase
{
    private string _name = string.Empty;
    private ProjectState _state;
    private IReadOnlyList<ProjectFolderInfo> _folders = [];
    private IReadOnlyList<ProjectFolderIssue> _folderIssues = [];
    private IReadOnlyList<ProjectFolderRow> _folderRows = [];
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
    private bool _isErrorsLoading;
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

    // Every computed value the views read. State changes raise PropertyChanged only for values
    // that actually changed, so one-second polling never re-renders text that stayed the same.
    private static readonly (string Name, Func<ProjectItemViewModel, object?> Read)[] DerivedProperties =
    [
        (nameof(Phase), project => project.Phase),
        (nameof(PhaseDetails), project => project.PhaseDetails),
        (nameof(StatusTone), project => project.StatusTone),
        (nameof(StatusHeadline), project => project.StatusHeadline),
        (nameof(StatusSummary), project => project.StatusSummary),
        (nameof(SidebarStatus), project => project.SidebarStatus),
        (nameof(IdleActivityDisplay), project => project.IdleActivityDisplay),
        (nameof(IsPaused), project => project.IsPaused),
        (nameof(IsReady), project => project.IsReady),
        (nameof(IsRetrying), project => project.IsRetrying),
        (nameof(IsRetryScheduled), project => project.IsRetryScheduled),
        (nameof(IsRetryStatus), project => project.IsRetryStatus),
        (nameof(IsWaitingForResources), project => project.IsWaitingForResources),
        (nameof(NeedsAttention), project => project.NeedsAttention),
        (nameof(IsDiscovering), project => project.IsDiscovering),
        (nameof(IsReadinessStale), project => project.IsReadinessStale),
        (nameof(HasErrors), project => project.HasErrors),
        (nameof(ShowSidebarIssueCount), project => project.ShowSidebarIssueCount),
        (nameof(HasAttentionFiles), project => project.HasAttentionFiles),
        (nameof(HasFolderIssues), project => project.HasFolderIssues),
        (nameof(AreActionsEnabled), project => project.AreActionsEnabled),
        (nameof(CanReindex), project => project.CanReindex),
        (nameof(CanRetryFailedFiles), project => project.CanRetryFailedFiles),
        (nameof(ReindexToolTip), project => project.ReindexToolTip),
        (nameof(PauseActionLabel), project => project.PauseActionLabel),
        (nameof(FolderCountDisplay), project => project.FolderCountDisplay),
        (nameof(DocumentCountDisplay), project => project.DocumentCountDisplay),
        (nameof(SidebarErrorCountDisplay), project => project.SidebarErrorCountDisplay),
        (nameof(ErrorCountDisplay), project => project.ErrorCountDisplay),
        (nameof(ProjectDetailsDisplay), project => project.ProjectDetailsDisplay),
        (nameof(ShowProgress), project => project.ShowProgress),
        (nameof(ProgressMaximum), project => project.ProgressMaximum),
        (nameof(ProgressDescription), project => project.ProgressDescription),
        (nameof(ProcessingCount), project => project.ProcessingCount),
        (nameof(QueuedCount), project => project.QueuedCount),
        (nameof(RetryScheduledCount), project => project.RetryScheduledCount),
        (nameof(RunningRetryCount), project => project.RunningRetryCount),
        (nameof(NextRetryUtc), project => project.NextRetryUtc),
        (nameof(WaitingForCpuCount), project => project.WaitingForCpuCount),
        (nameof(QueuedBreakdownDisplay), project => project.QueuedBreakdownDisplay),
        (nameof(HasProcessingFiles), project => project.HasProcessingFiles),
        (nameof(HasQueuedFiles), project => project.HasQueuedFiles),
        (nameof(ReadyLegend), project => project.ReadyLegend),
        (nameof(ProcessingLegend), project => project.ProcessingLegend),
        (nameof(QueuedLegend), project => project.QueuedLegend),
        (nameof(AttentionLegend), project => project.AttentionLegend),
        (nameof(KeywordSearchTone), project => project.KeywordSearchTone),
        (nameof(KeywordSearchLabel), project => project.KeywordSearchLabel),
        (nameof(KeywordSearchMessage), project => project.KeywordSearchMessage),
        (nameof(HasMixedSemanticIndex), project => project.HasMixedSemanticIndex),
        (nameof(HasSemanticCoverageWarning), project => project.HasSemanticCoverageWarning),
        (nameof(IsSemanticCoverageComplete), project => project.IsSemanticCoverageComplete),
        (nameof(IsSemanticRepairQueued), project => project.IsSemanticRepairQueued),
        (nameof(ShowSemanticRepairButton), project => project.ShowSemanticRepairButton),
        (nameof(CanRepairSemanticIndex), project => project.CanRepairSemanticIndex),
        (nameof(SemanticIndexStatusLabel), project => project.SemanticIndexStatusLabel),
        (nameof(SemanticIndexStatusTone), project => project.SemanticIndexStatusTone),
        (nameof(SemanticIndexStatusMessage), project => project.SemanticIndexStatusMessage),
        (nameof(RepairSemanticIndexToolTip), project => project.RepairSemanticIndexToolTip),
        (nameof(IsSemanticSetupNeeded), project => project.IsSemanticSetupNeeded),
        (nameof(GroupedIssuesSummary), project => project.GroupedIssuesSummary),
        (nameof(ExcludedPathsDisplay), project => project.ExcludedPathsDisplay)
    ];

    public ProjectItemViewModel(ProjectSummary project)
    {
        Id = project.Id;
        _isInitialScanComplete = project.DocumentCount > 0 || project.LastCompletedUtc is not null;
        UpdateFrom(project);
    }

    public Guid Id { get; }
    public override string ToString() => Name;
    public const int ErrorPageSize = 25;

    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public ProjectState State { get => _state; private set => SetProperty(ref _state, value); }
    public IReadOnlyList<ProjectFolderInfo> Folders { get => _folders; private set => SetProperty(ref _folders, value); }
    public IReadOnlyList<ProjectFolderIssue> FolderIssues { get => _folderIssues; private set => SetProperty(ref _folderIssues, value); }
    public IReadOnlyList<ProjectFolderRow> FolderRows { get => _folderRows; private set => SetProperty(ref _folderRows, value); }
    public bool HasFolderIssues => FolderIssues.Count > 0;
    public bool IsDiscovering => _isDiscovering;
    public int ProgressMaximum => Math.Max(1, DocumentCount);
    public bool ShowProgress => !IsPaused && (PendingCount > 0 || IsDiscovering);
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
        "Status unavailable" => "Current status could not be checked. The counts shown are from the last successful check.",
        "Awaiting first scan" => "Waiting for the first folder scan. Nothing has been counted or indexed yet.",
        "No supported files" => "The folder scan completed, but no supported files were found. Edit the project to choose other folders, or add supported files.",
        "Pausing" => "Stopping the files that are being indexed right now. They will continue when indexing resumes.",
        "Paused" => PendingCount > 0
            ? $"{PendingCount:N0} {(PendingCount == 1 ? "file will" : "files will")} continue when indexing resumes. Changes are still detected; indexed files stay searchable unless their source is removed."
            : "Indexing is paused. Changes are still detected, and removed sources leave search. Other indexed files stay searchable.",
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
        "Queued" => QueuedCount == 1 ? "1 file is waiting to be indexed."
            : $"{QueuedCount} files are waiting to be indexed.",
        _ when HasFolderIssues => "A watched folder can't be checked for changes. Files indexed before stay searchable.",
        _ when ErrorCount > 0 => "Some files could not be fully indexed. Files indexed before stay searchable.",
        _ when AttentionCount > 0 => "Some files have no completed index. Reindex this project to queue them again.",
        _ when DocumentCount == 0 => "No supported files have been found in the watched folders.",
        _ => "All files are indexed. Folders are watched for changes."
    };

    /// <summary>One tone for the project's dot and pill, derived from <see cref="Phase"/>.</summary>
    public AppStatusTone StatusTone => Phase switch
    {
        "Ready" => AppStatusTone.Ready,
        "Needs attention" => AppStatusTone.Error,
        "Paused" or "Pausing" or "Status unavailable" or "Retrying" or "Retry scheduled" => AppStatusTone.Warning,
        "Awaiting first scan" or "No supported files" => AppStatusTone.Neutral,
        _ => AppStatusTone.Busy
    };

    private bool IsWorking => Phase is "Indexing" or "Retrying" or "Waiting for CPU" or "Queued";
    public int AttentionFileCount => Math.Max(AttentionCount, ErrorFileCount);

    /// <summary>The one-line answer to "how is my data?".</summary>
    public string StatusHeadline => Phase switch
    {
        "Status unavailable" => "Status could not be checked",
        "Awaiting first scan" => "Getting ready to scan",
        "No supported files" => "No supported files found",
        "Finding files" => "Looking for files",
        "Pausing" => "Pausing…",
        "Paused" => PendingCount > 0 ? $"Paused · {Files(PendingCount)} waiting" : "Paused",
        "Retry scheduled" => NextRetryUtc is { } next ? $"Retry scheduled for {next.ToLocalTime():t}" : "Retry scheduled",
        _ when IsWorking => $"Indexing · {Files(PendingCount)} left",
        "Needs attention" => AttentionFileCount > 0
            ? $"{Files(AttentionFileCount)} {(AttentionFileCount == 1 ? "needs" : "need")} attention"
            : HasFolderIssues ? "A folder can't be read" : "Needs attention",
        _ => "Everything is indexed"
    };

    public string StatusSummary
    {
        get
        {
            var searchable = SearchableCount == 1 ? "1 file is" : $"{SearchableCount:N0} files are";
            var summary = Phase switch
            {
                "Status unavailable" => _lastChecked is { } checkedAt
                    ? $"Current status could not be checked. These counts are from {checkedAt.ToLocalTime():g}."
                    : "Current status could not be checked. These are the last known counts.",
                "Awaiting first scan" => "Context Mole will look through the project's folders shortly.",
                "No supported files" => "The folders were scanned but contain no supported files. Edit the project to choose other folders.",
                "Finding files" => "Looking for new, changed and removed files. Counts update as files are found.",
                "Pausing" => "Stopping the files being indexed right now.",
                "Paused" => $"{searchable} still searchable. Changes are detected and indexed when you resume.",
                "Retry scheduled" => $"{searchable} searchable. Files that failed will be tried again automatically.",
                _ when IsWorking => SearchableCount > 0
                    ? $"{searchable} already searchable. New results appear as each file finishes."
                    : "Each file becomes searchable as soon as it finishes.",
                "Needs attention" => HasFolderIssues && AttentionFileCount == 0
                    ? "A watched folder can't be checked for changes. Files indexed before stay searchable."
                    : $"{searchable} searchable. Some files could not be fully indexed; see the list below.",
                _ => $"{searchable} searchable by your AI assistant. Folders are watched for changes."
            };
            var empty = IndexedCount - SearchableCount;
            return empty > 0 && Phase is not ("Status unavailable" or "Awaiting first scan" or "No supported files")
                ? $"{summary} {Files(empty)} had no text to index."
                : summary;
        }
    }

    public string SidebarStatus
    {
        get
        {
            var detail = Phase switch
            {
                "Ready" => DocumentCountDisplay,
                "Paused" => PendingCount > 0 ? $"{PendingCount:N0} waiting" : DocumentCountDisplay,
                "Needs attention" => AttentionFileCount > 0 ? $"{Files(AttentionFileCount)}" : "folder issue",
                "Finding files" => DocumentCount > 0 ? $"{DocumentCountDisplay} so far" : string.Empty,
                _ when IsWorking || Phase == "Retry scheduled" => $"{PendingCount:N0} left",
                _ => string.Empty
            };
            return detail.Length == 0 ? Phase : $"{Phase} · {detail}";
        }
    }

    public string IdleActivityDisplay => IsPaused ? "Indexing is paused. Changes in watched folders are still detected."
        : IsDiscovering ? "Checking watched folders for new, changed and removed files…"
        : LastCompletedUtc is { } last ? $"Last indexed {FriendlyTime.Ago(last)} · watching {FolderCountDisplay} for changes"
        : $"Watching {FolderCountDisplay} for changes";

    public bool IsPaused => State == ProjectState.Paused;
    public bool IsReady => !_isReadinessStale && Phase == "Ready";
    public bool IsRetrying => Phase == "Retrying";
    public bool IsRetryScheduled => Phase == "Retry scheduled";
    public bool IsRetryStatus => IsRetrying || IsRetryScheduled;
    public bool IsWaitingForResources => Phase == "Waiting for CPU";
    public bool NeedsAttention => Phase == "Needs attention";
    public bool HasErrors => ErrorCount > 0;
    public bool ShowSidebarIssueCount => HasErrors && !NeedsAttention;
    public bool HasAttentionFiles => AttentionCount > 0;
    public bool HasProcessingFiles => ProcessingCount > 0;
    public bool HasQueuedFiles => QueuedCount > 0;
    public string ReadyLegend => $"{ReadyCount:N0} up to date";
    public string ProcessingLegend => $"{ProcessingCount:N0} indexing now";
    public string QueuedLegend => $"{QueuedCount:N0} waiting";
    public string AttentionLegend => $"{AttentionCount:N0} need attention";
    public bool AreActionsEnabled => !_actionsBusy;
    public bool CanReindex => AreActionsEnabled && State == ProjectState.Active;
    public bool CanRetryFailedFiles => AreActionsEnabled && State == ProjectState.Active && ErrorFileCount > 0;

    public AppStatusTone KeywordSearchTone => _isReadinessStale ? AppStatusTone.Warning
        : SearchableCount > 0 ? AppStatusTone.Ready : AppStatusTone.Neutral;
    public string KeywordSearchLabel => _isReadinessStale ? "Not verified" : SearchableCount > 0 ? "Ready" : "Not yet";
    public string KeywordSearchMessage => SearchableCount > 0
        ? $"{Files(SearchableCount)} can be searched by exact words and names."
        : "Files become searchable as soon as they are indexed.";

    public bool HasMixedSemanticIndex => !_isSemanticCoverageLoading && _semanticCoverageError is null && _semanticIndex?.HasPartialCoverage == true;
    public bool HasSemanticCoverageWarning => HasMixedSemanticIndex || _semanticCoverageError is not null;
    public bool IsSemanticRepairQueued => _semanticIndex?.IsRepairQueued == true;
    public bool ShowSemanticRepairButton => HasMixedSemanticIndex && !IsSemanticRepairQueued;
    public bool IsSemanticSetupNeeded => !_isReadinessStale && !_isSemanticCoverageLoading && _semanticCoverageError is null && !_semanticModelAvailable;
    public bool CanRepairSemanticIndex => AreActionsEnabled && ShowSemanticRepairButton && State == ProjectState.Active &&
                                          _semanticModelAvailable && !_isReadinessStale && _semanticCoverageError is null && IsSemanticSnapshotCurrent;
    private bool IsSemanticSnapshotCurrent => _semanticIndex is null || _semanticIndex.SearchGeneration == SearchGeneration;
    public string SemanticIndexStatusLabel => _isReadinessStale ? "Not verified"
        : _isSemanticCoverageLoading ? "Checking"
        : _semanticCoverageError is not null ? "Couldn't check"
        : !_semanticModelAvailable ? "Off"
        : _semanticIndex is null || _semanticIndex.TotalDocumentCount == 0 ? "No files yet"
        : IsSemanticRepairQueued ? "Repair queued"
        : HasMixedSemanticIndex ? "Partial" : !IsSemanticSnapshotCurrent ? "Updating" : "Complete";
    public AppStatusTone SemanticIndexStatusTone => SemanticIndexStatusLabel switch
    {
        "Complete" => AppStatusTone.Ready,
        "Checking" or "Updating" or "Repair queued" => AppStatusTone.Busy,
        "Partial" or "Couldn't check" or "Not verified" => AppStatusTone.Warning,
        _ => AppStatusTone.Neutral
    };
    public string SemanticIndexStatusMessage
    {
        get
        {
            if (_isReadinessStale) return "Coverage could not be verified. The last known result may have changed.";
            if (_isSemanticCoverageLoading) return "Checking which files support meaning-based search…";
            if (_semanticCoverageError is not null)
                return $"Coverage could not be checked. {_semanticCoverageError}" +
                       (_semanticIndex is { } previous ? $" Last result: {previous.CompatibleDocumentCount:N0} of {previous.TotalDocumentCount:N0} files." : string.Empty);
            if (!_semanticModelAvailable) return "Finds passages by meaning, not just exact words. Turn it on in Settings; keyword search works without it.";
            if (_semanticIndex is not { } metadata || metadata.TotalDocumentCount == 0)
                return "No indexed files are available for meaning-based search yet.";
            var excluded = metadata.ExcludedDocumentCount;
            var coverage = $"{metadata.CompatibleDocumentCount:N0} of {metadata.TotalDocumentCount:N0} indexed files.";
            if (!IsSemanticSnapshotCurrent)
                return $"{coverage} Updating as files finish indexing.";
            if (!metadata.HasPartialCoverage) return coverage;
            if (metadata.IsRepairQueued)
                return $"{coverage} The other {Files(excluded)} {(excluded == 1 ? "is" : "are")} queued for repair.";
            if (metadata.RepairQueuedDocumentCount > 0)
            {
                var remaining = excluded - metadata.RepairQueuedDocumentCount;
                return $"{coverage} Repair is queued for {metadata.RepairQueuedDocumentCount}; " +
                       $"{remaining} still {(remaining == 1 ? "needs" : "need")} repair.";
            }
            var legacy = metadata.ReextractionRequiredDocumentCount;
            if (legacy > 0)
                return $"{coverage} {Files(legacy)} indexed by an older version must be re-read from the source; " +
                       $"{Files(metadata.EmbeddingRepairEligibleDocumentCount)} only need their meaning-based data rebuilt. Complete coverage queues both.";
            return $"{coverage} {Files(excluded)} still {(excluded == 1 ? "needs" : "need")} meaning-based search data.";
        }
    }
    public string RepairSemanticIndexToolTip => State == ProjectState.Paused
        ? "Resume indexing before completing meaning-based coverage."
        : _isReadinessStale ? "Status could not be verified. Wait for a successful check before queuing repair."
        : _isSemanticCoverageLoading ? "Checking coverage before repair can be queued."
        : _semanticCoverageError is not null ? "Coverage could not be checked. Wait for a successful refresh before queuing repair."
        : !_semanticModelAvailable ? "Turn on meaning-based search in Settings first."
        : !IsSemanticSnapshotCurrent ? "Coverage is updating for the current index."
        : IsSemanticRepairQueued ? "Coverage repair is already queued."
        : IsSemanticCoverageComplete ? "Meaning-based coverage is complete."
        : "Queue the missing meaning-based search data. Files indexed by an older version are re-read from their sources.";
    public bool IsSemanticCoverageComplete => SemanticIndexStatusLabel == "Complete";

    public string ReindexToolTip => IsPaused
        ? "Resume indexing before rebuilding this project."
        : "Rebuild the local index from the watched folders.";
    public bool HasFileTypeCounts => FileTypeCounts.Count > 0;
    public bool HasNoFileTypeCounts => !HasFileTypeCounts;
    public string QueuedBreakdownDisplay
    {
        get
        {
            var parts = new List<string>(2);
            if (RetryScheduledCount > 0)
                parts.Add(RetryScheduledCount == 1 ? "1 scheduled retry" : $"{RetryScheduledCount} scheduled retries");
            if (WaitingForCpuCount > 0)
                parts.Add($"{WaitingForCpuCount} waiting for CPU");
            return parts.Count == 0 ? "Files waiting to be indexed." : string.Join(" · ", parts);
        }
    }
    public bool IsErrorsLoading
    {
        get => _isErrorsLoading;
        set
        {
            if (!SetProperty(ref _isErrorsLoading, value)) return;
            NotifyIssuePresentation();
        }
    }
    public string PauseActionLabel => State == ProjectState.Paused ? "Resume indexing" : "Pause indexing";
    public string FolderCountDisplay => Folders.Count == 1 ? "1 folder" : $"{Folders.Count} folders";
    public string DocumentCountDisplay => Files(DocumentCount);
    public string SidebarErrorCountDisplay => ErrorCount == 1 ? "1 issue" : $"{ErrorCount:N0} issues";
    public string ErrorCountDisplay => ErrorCount == 1 ? "1 current issue" : $"{ErrorCount:N0} current issues";
    public string ProjectDetailsDisplay => $"{FolderCountDisplay} · {DocumentCountDisplay}{ExcludedPathsDisplay}";
    public string ExcludedPathsDisplay => ExcludedPathCount > 0 ? $" · {ExcludedPathCount:N0} excluded" : string.Empty;

    private static string Files(int count) => count == 1 ? "1 file" : $"{count:N0} files";

    public void UpdateFrom(ProjectSummary project)
    {
        if (project.Id != Id)
        {
            throw new ArgumentException("A project view model cannot change identity.", nameof(project));
        }

        var before = CaptureDerived();
        Name = project.Name;
        State = project.State;
        if (!Folders.SequenceEqual(project.Folders))
        {
            Folders = project.Folders.ToArray();
            UpdateFolderRows();
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
        // Each committed file advances the generation. Keep the last verified coverage and the
        // current issue keyset on screen while refreshed data is read; clearing them made every
        // job flash the page. Coverage is explicitly historical until the matching generation is read.
        if (generationChanged) _isSemanticCoverageLoading = !_hasSemanticCoverageSnapshot;
        if (ErrorCount == 0 && IssueGroups.Count > 0) ResetIssuePages();
        NotifyIssuePresentation();
        NotifyDerivedChanged(before);
    }

    public void UpdateRuntime(IndexingTimingSnapshot runtime, bool isDiscovering = false, bool isInitialScanComplete = true)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.ActiveItems.Any(item => item.ProjectId != Id))
            throw new ArgumentException("A runtime summary can only contain this project's work.", nameof(runtime));

        var before = CaptureDerived();
        _runtimeWork = runtime;
        _isDiscovering = isDiscovering;
        _isInitialScanComplete = isInitialScanComplete;
        NotifyDerivedChanged(before);
    }

    public void UpdateSemanticIndex(VectorSnapshotMetadata? metadata, bool modelAvailable)
    {
        if (_semanticIndex == metadata && _semanticModelAvailable == modelAvailable && !_isSemanticCoverageLoading && _semanticCoverageError is null) return;
        var before = CaptureDerived();
        _semanticIndex = metadata;
        _semanticModelAvailable = modelAvailable;
        _hasSemanticCoverageSnapshot = true;
        _isSemanticCoverageLoading = false;
        _semanticCoverageError = null;
        NotifyDerivedChanged(before);
    }

    public void SetActionsBusy(bool busy)
    {
        if (_actionsBusy == busy) return;
        var before = CaptureDerived();
        _actionsBusy = busy;
        NotifyDerivedChanged(before);
    }

    public void BeginSemanticIndexRefreshForPolicy(string? policyKey, bool modelAvailable) =>
        BeginSemanticIndexRefresh(invalidate: _hasSemanticCoverageSnapshot &&
            (_semanticIndex is { } previous && !string.Equals(previous.Policy?.Key, policyKey, StringComparison.Ordinal) ||
             _semanticModelAvailable != modelAvailable));

    public void BeginSemanticIndexRefresh(bool invalidate = false)
    {
        var before = CaptureDerived();
        if (invalidate)
        {
            _semanticIndex = null;
            _hasSemanticCoverageSnapshot = false;
            _semanticCoverageError = null;
        }
        _isSemanticCoverageLoading = !_hasSemanticCoverageSnapshot;
        NotifyDerivedChanged(before);
    }

    public void FailSemanticIndexRefresh(string message)
    {
        var before = CaptureDerived();
        _isSemanticCoverageLoading = false;
        _semanticCoverageError = message;
        NotifyDerivedChanged(before);
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
        var before = CaptureDerived();
        FolderIssues = current;
        UpdateFolderRows();
        NotifyDerivedChanged(before);
    }

    private void UpdateFolderRows() => FolderRows = Folders.Select(folder => new ProjectFolderRow(folder.Path,
        FolderIssues.FirstOrDefault(issue => issue.FolderId == folder.Id)?.Message)).ToArray();

    public ObservableCollection<ProjectIssueGroupViewModel> IssueGroups { get; } = [];
    public ObservableCollection<ExcludedFileItemViewModel> ExcludedFiles { get; } = [];
    public string IssueQuery { get => _issueQuery; set { if (SetProperty(ref _issueQuery, value)) ResetIssuePages(); } }
    public int IssueImpactFilterIndex { get => _issueImpactFilterIndex; set { if (SetProperty(ref _issueImpactFilterIndex, value)) ResetIssuePages(); } }
    public bool ShowHiddenIssues { get => _showHiddenIssues; set { if (SetProperty(ref _showHiddenIssues, value)) ResetIssuePages(); } }
    public ProjectIssueImpact IssueImpactFilter => (ProjectIssueImpact)Math.Clamp(IssueImpactFilterIndex, 0, 3);
    public ProjectIssueVisibility IssueVisibility => ShowHiddenIssues ? ProjectIssueVisibility.All : ProjectIssueVisibility.Visible;
    public string? IssueCursor => _issueCursors[_issuePageIndex];
    public long IssueQueryVersion => _issueQueryVersion;
    public int HiddenIssueCount => _issuePage?.HiddenIssueCount ?? 0;
    public int HiddenIssueFileCount => _issuePage?.HiddenFileCount ?? 0;
    public bool HasHiddenIssues => HiddenIssueCount > 0;
    public string HiddenIssuesDisplay => $"Show hidden ({Files(HiddenIssueFileCount)})";
    public bool HasIssueCard => HasErrors || HasIssueLoadError;
    public bool HasIssueGroups => IssueGroups.Count > 0;
    public bool HasNoMatchingIssueGroups => !IsErrorsLoading && IssueLoadError is null && !HasIssueGroups;
    public bool HasIssueLoadError => IssueLoadError is not null;
    public string? IssueLoadError { get => _issueLoadError; private set { SetProperty(ref _issueLoadError, value); NotifyIssuePresentation(); } }
    public string NoMatchingIssuesMessage => _issuePageIndex > 0
        ? "No more matching files on this page. Go back to the previous page to see the latest list."
        : HasHiddenIssues && string.IsNullOrWhiteSpace(IssueQuery) && IssueImpactFilter == ProjectIssueImpact.All && !ShowHiddenIssues
        ? "All current issues are hidden. They still count toward this project's status. Show hidden to review or restore them."
        : "No files match this search. Try other words, another filter, or show hidden issues.";
    public string GroupedIssuesSummary
    {
        get
        {
            var issues = _issuePage?.TotalIssueCount ?? ErrorCount;
            var files = _issuePage?.TotalFileCount ?? ErrorFileCount;
            var summary = $"{issues:N0} {(issues == 1 ? "issue" : "issues")} in {Files(files)}.";
            if (HiddenIssueCount > 0) summary += $" {HiddenIssueCount:N0} hidden.";
            return summary + " Files indexed before stay searchable while you fix them.";
        }
    }
    public bool HasIssuePages => _issuePageIndex > 0 || _nextIssueCursor is not null;
    public string IssuePageDisplay => IsErrorsLoading ? "Loading…" :
        $"Page {_issuePageIndex + 1} · {IssueGroups.Count:N0} of {_issuePage?.FilteredFileCount ?? 0:N0} matching files";
    public bool IssuesChangedDuringPaging => _issuesChangedDuringPaging;
    public bool CanPreviousIssuePage => !IsErrorsLoading && _issuePageIndex > 0;
    public bool CanNextIssuePage => !IsErrorsLoading && _nextIssueCursor is not null;
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
    public string ExcludedFilesSummary => $"Excluded files ({ExcludedFileCount:N0})";
    public bool CanPreviousExcludedPage => _excludedPageIndex > 0;
    public bool CanNextExcludedPage => (_excludedPageIndex + 1) * ErrorPageSize < MatchingExcludedFileCount;
    public bool HasExcludedPages => MatchingExcludedFileCount > ErrorPageSize;
    public string ExcludedPageDisplay => $"Page {_excludedPageIndex + 1} · {ExcludedFiles.Count:N0} of {MatchingExcludedFileCount:N0} files";
    public bool IsReadinessStale => _isReadinessStale;

    public ProjectIssueListRequest CreateIssueRequest() => new(Id, IssueQuery, Impact: IssueImpactFilter,
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
        SetIssueActionMessage($"Hid {result.HiddenIssueCount:N0} {(result.HiddenIssueCount == 1 ? "issue" : "issues")}. Indexing, retries and search are unchanged, and new failures still appear.");
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
                     nameof(CanPreviousExcludedPage), nameof(CanNextExcludedPage), nameof(ExcludedPageDisplay), nameof(HasExcludedPages),
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
        var before = CaptureDerived();
        _isReadinessStale = isStale; _lastChecked = lastChecked;
        NotifyDerivedChanged(before);
    }
    private void NotifyIssuePresentation()
    {
        foreach (var name in new[] { nameof(HiddenIssueCount), nameof(HiddenIssueFileCount), nameof(HasHiddenIssues),
                     nameof(HiddenIssuesDisplay), nameof(HasIssueGroups), nameof(HasNoMatchingIssueGroups), nameof(HasIssueLoadError),
                     nameof(HasIssueCard), nameof(NoMatchingIssuesMessage), nameof(GroupedIssuesSummary), nameof(IssuePageDisplay),
                     nameof(HasIssuePages), nameof(CanPreviousIssuePage), nameof(CanNextIssuePage), nameof(IssuesChangedDuringPaging) }) OnPropertyChanged(name);
    }

    private object?[] CaptureDerived()
    {
        var values = new object?[DerivedProperties.Length];
        for (var index = 0; index < values.Length; index++) values[index] = DerivedProperties[index].Read(this);
        return values;
    }

    private void NotifyDerivedChanged(object?[] before)
    {
        for (var index = 0; index < before.Length; index++)
        {
            var (name, read) = DerivedProperties[index];
            if (!Equals(before[index], read(this))) OnPropertyChanged(name);
        }
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
}

/// <summary>Short, human relative times for status lines that refresh every second.</summary>
internal static class FriendlyTime
{
    public static string Ago(DateTimeOffset when, DateTimeOffset? now = null)
    {
        var elapsed = (now ?? DateTimeOffset.UtcNow) - when;
        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes} min ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours} h ago";
        return $"on {when.ToLocalTime():g}";
    }
}
