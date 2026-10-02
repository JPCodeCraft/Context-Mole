using System.Collections.ObjectModel;

using ContextMole.Core;

namespace ContextMole.App.UI.ViewModels;

/// <summary>One root file, with bounded, identifiable component detail pages.</summary>
public sealed class ProjectIssueGroupViewModel : ViewModelBase
{
    private ProjectIssueGroup _source;
    private bool _isExpanded;
    private bool _isDetailsLoading;
    private readonly List<string?> _detailCursors = [null];
    private int _detailPageIndex;
    private string? _nextDetailsCursor;
    private bool _detailsWereRefreshed;
    private bool _initialized;

    public ProjectIssueGroupViewModel(ProjectIssueGroup source) { _source = source; UpdateFrom(source); }
    public ProjectIssueGroup Source => _source;
    public string SourcePath => _source.SourcePath;
    public string FileName => string.IsNullOrWhiteSpace(_source.FileName) ? "Project operation" : _source.FileName;
    public Guid? DocumentId => _source.DocumentId;
    public bool IsRootFile => DocumentId is not null && !string.IsNullOrWhiteSpace(SourcePath);
    public bool CanRetry => IsRootFile && _source.CanRetry;
    public bool IsHidden => _source.IssueCount > 0 && _source.HiddenIssueCount == _source.IssueCount;
    public bool HasHiddenIssues => _source.HiddenIssueCount > 0;
    public bool CanHide => IsRootFile && _source.HiddenIssueCount < _source.IssueCount;
    public string IssueCountDisplay => $"{_source.IssueCount:N0} issue {(_source.IssueCount == 1 ? "component" : "components")}" +
        (HasHiddenIssues ? $" · {_source.HiddenIssueCount:N0} hidden" : string.Empty);
    public string ImpactLabel => !IsRootFile ? "Project operation needs attention" : _source.Impact switch
    {
        ProjectIssueImpact.Unsearchable => "No searchable content",
        ProjectIssueImpact.Partial => "Some content missing",
        ProjectIssueImpact.RetainedSearchable => "Previous indexed content retained",
        _ => "Project operation needs attention"
    };
    public string ImpactMessage => !IsRootFile ? "This issue applies to a project operation. Review its cause below; it does not identify a particular unsearchable source file." : _source.Impact switch
    {
        ProjectIssueImpact.Unsearchable => "This file has no searchable passages. Fix the cause, then retry this root file.",
        ProjectIssueImpact.Partial when _source.Details.Count > 0 && _source.Details.Count == _source.IssueCount &&
            _source.HiddenIssueCount == 0 && _source.DetailsCursor is null &&
            _source.Details.All(detail => detail.Code.Contains("embedding", StringComparison.OrdinalIgnoreCase)) =>
            "Keyword evidence remains available. Meaning-based search may be incomplete until semantic coverage is repaired.",
        ProjectIssueImpact.Partial => "Available content remains searchable. Some content or processing coverage is missing or incomplete; check component details and hidden issues.",
        ProjectIssueImpact.RetainedSearchable => "The last successfully indexed evidence remains searchable. It may not reflect the latest source changes.",
        _ => "Review the cause below. No root-file retry is available for this operation."
    };
    public string RecoveryGuidance => _source.Details.Any(detail => detail.Code == "ocr_platform_unsupported")
        ? "OCR is unavailable on this platform. Use a supported platform or a source copy with readable text. Retrying unchanged images will not add OCR."
        : _source.Details.Any(detail => detail.Code is "ocr_unavailable" or "ocr_not_available" or "ocr_setup_failed" or
            "ocr_cached_assets_unavailable" or "ocr_initialization_failed" or "model_unavailable")
        ? "Setup blocked: check OCR or the selected semantic model in Settings before retrying."
        : _source.Details.Any(detail => detail.Code is "ocr_timeout" or "ocr_failed")
            ? "OCR processing failed. Check Document OCR in Settings and verify that the source opens, then retry this file."
        : _source.Details.Any(detail => detail.Code.Contains("limit", StringComparison.OrdinalIgnoreCase))
            ? "A processing limit was reached. Use smaller files or split the source container; repeating the same input does not expand those limits."
        : _source.Details.Any(detail => detail.Code.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
            ? "Encrypted content: provide a supported readable copy of the source, then retry."
            : _source.Details.Any(detail => detail.Code is "access_denied" or "folder_unavailable")
                ? "Access blocked: restore folder availability or read access, then retry."
                : _source.Details.Any(detail => detail.Code is "unsupported_format" or "extraction_failed" or
                    "invalid_document" or "malformed_document" or "ocr_image_invalid")
                    ? "File review needed: verify that the source opens and is a supported, readable document."
                    : "Review the component cause before retrying; a repeat of unchanged input can fail again.";
    public string RetryLabel => _source.RetryState switch
    {
        ProjectIssueRetryState.Queued => "Retry queued",
        ProjectIssueRetryState.Running => "Retry running",
        ProjectIssueRetryState.Scheduled => "Retry scheduled",
        ProjectIssueRetryState.Exhausted => "Automatic retries exhausted",
        ProjectIssueRetryState.Paused => "Indexing paused",
        _ => "Manual action needed"
    };
    public string RetryMessage => _source.RetryState switch
    {
        ProjectIssueRetryState.Queued => "This root file is queued. Its current failure remains visible until replacement work succeeds.",
        ProjectIssueRetryState.Running => "This root file is being processed. Retry admission does not mean the issue is resolved.",
        ProjectIssueRetryState.Scheduled when _source.NextRetryUtc is { } next => $"Next attempt: {next.ToLocalTime():g}.",
        ProjectIssueRetryState.Scheduled => "An automatic retry is scheduled for later.",
        ProjectIssueRetryState.Exhausted => "The automatic retry budget has stopped. Fix the cause, then Retry file to start a new attempt budget.",
        ProjectIssueRetryState.Paused => "Resume this project before retrying. Discovery and removal checks continue while paused.",
        _ when !_source.Details.Any(detail => detail.Retryable) => "Review or fix the original file before retrying; repeating unchanged input may produce the same issue.",
        _ => "Partial-content issues do not automatically schedule another extraction. Retry file when the cause is fixed."
    };
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public bool IsDetailsLoading { get => _isDetailsLoading; set { if (SetProperty(ref _isDetailsLoading, value)) NotifyDetails(); } }
    public ObservableCollection<ProjectIssueDetailViewModel> Details { get; } = [];
    public string? DetailsCursor => _detailCursors[_detailPageIndex];
    public bool CanPreviousDetails => !IsDetailsLoading && _detailPageIndex > 0;
    public bool CanNextDetails => !IsDetailsLoading && _nextDetailsCursor is not null;
    public bool DetailsWereRefreshed => _detailsWereRefreshed;
    public string DetailsPageDisplay => IsDetailsLoading ? "Loading component details…" :
        $"Component detail page {_detailPageIndex + 1} · {Details.Count:N0} shown of {_source.IssueCount:N0} total";

    public void UpdateFrom(ProjectIssueGroup source)
    {
        if (_initialized && (_source with { Details = source.Details }) == source && _source.Details.SequenceEqual(source.Details)) return;
        var detailsChanged = _source.IssueCount != source.IssueCount || _source.HiddenIssueCount != source.HiddenIssueCount ||
            _source.DetailsCursor != source.DetailsCursor || !_source.Details.SequenceEqual(source.Details);
        _source = source;
        if (detailsChanged)
        {
            _detailsWereRefreshed = _detailPageIndex > 0;
            _detailCursors.Clear(); _detailCursors.Add(null); _detailPageIndex = 0;
            OnPropertyChanged(nameof(DetailsWereRefreshed));
        }
        if (_detailPageIndex == 0) UpdateDetails(new(source.Details, source.DetailsCursor));
        _initialized = true;
        foreach (var name in new[] { nameof(SourcePath), nameof(FileName), nameof(DocumentId), nameof(IsRootFile),
                     nameof(CanRetry), nameof(IsHidden), nameof(HasHiddenIssues), nameof(CanHide), nameof(IssueCountDisplay),
                     nameof(ImpactLabel), nameof(ImpactMessage), nameof(RecoveryGuidance), nameof(RetryLabel), nameof(RetryMessage) }) OnPropertyChanged(name);
    }
    public void UpdateDetails(ProjectIssueDetailsResponse response)
    {
        Details.Clear();
        foreach (var detail in response.Details) Details.Add(new(detail));
        _nextDetailsCursor = response.NextCursor;
        NotifyDetails();
    }
    public void ResetDetails()
    {
        _detailsWereRefreshed = true; OnPropertyChanged(nameof(DetailsWereRefreshed));
        _detailCursors.Clear(); _detailCursors.Add(null); _detailPageIndex = 0; _nextDetailsCursor = null;
        NotifyDetails();
    }
    public void MoveDetails(int direction)
    {
        if (direction < 0 && CanPreviousDetails) _detailPageIndex--;
        else if (direction > 0 && CanNextDetails)
        {
            if (_detailCursors.Count > _detailPageIndex + 1) _detailCursors.RemoveRange(_detailPageIndex + 1, _detailCursors.Count - _detailPageIndex - 1);
            _detailCursors.Add(_nextDetailsCursor); _detailPageIndex++;
        }
        NotifyDetails();
    }
    private void NotifyDetails()
    {
        OnPropertyChanged(nameof(CanPreviousDetails)); OnPropertyChanged(nameof(CanNextDetails));
        OnPropertyChanged(nameof(DetailsPageDisplay));
    }
}

public sealed class ProjectIssueDetailViewModel(ProjectIssueDetail source)
{
    public ProjectIssueDetail Source { get; } = source;
    public bool HasUnrecordedComponentLocation => Source.Attempt == 0 && Source.ContentId is null &&
        string.IsNullOrWhiteSpace(Source.ComponentName) && Source.ComponentKey == "root";
    public string ComponentName => HasUnrecordedComponentLocation ? "Component location not recorded" :
        string.IsNullOrWhiteSpace(Source.ComponentName) ? "Root document" : Source.ComponentName;
    public string ComponentKey => HasUnrecordedComponentLocation
        ? "This older issue has no exact attachment or page locator. Retry the root file to record its location."
        : Source.ComponentKey;
    public string Message => Source.Message;
    public string Code => Source.Code;
    public string CreatedDisplay => $"First seen {Source.FirstSeenUtc.ToLocalTime():g} · Last seen {Source.LastSeenUtc.ToLocalTime():g} · " +
        $"Observed {Source.OccurrenceCount:N0} {(Source.OccurrenceCount == 1 ? "time" : "times")}";
    public string AttemptDisplay => Source.Attempt == 0 ? "Partial extraction · manual retry" : $"{Source.Attempt} failed {(Source.Attempt == 1 ? "attempt" : "attempts")}";
    public bool IsHidden => Source.IsHidden;
}

public sealed class ExcludedFileItemViewModel(ExcludedFileInfo source, bool isWithinWatchedFolder = true)
{
    public string SourcePath => source.SourcePath;
    public string FileName => Path.GetFileName(source.SourcePath);
    public string ScopeDisplay => isWithinWatchedFolder
        ? "Within a watched folder. Include rebuilds from the currently available source."
        : "Outside current watched folders. Include removes the rule, but add its folder to this project before it can be indexed.";
    public string ExcludedDisplay => $"Excluded {source.ExcludedUtc.ToLocalTime():g}";
}
