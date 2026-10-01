using ContextMole.Core;

namespace ContextMole.App.UI.ViewModels;

internal enum AppStatusTone { Neutral, Busy, Ready, Warning, Error }

internal sealed record AppStatus(string Message, string Details, AppStatusTone Tone);

internal sealed record OperationNotification(string Source, Guid? ProjectId, string Message, bool IsError,
    DateTimeOffset? ExpiresUtc, long Sequence);

/// <summary>Current index health and operation feedback have independent lifetimes.</summary>
internal sealed class AppPresentationState(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<(string, Guid?), OperationNotification> _notifications = [];
    private long _sequence;

    public bool HasLoadedProjects { get; private set; }
    public string? RefreshError { get; private set; }
    public void ProjectsLoaded() { HasLoadedProjects = true; RefreshError = null; }
    public void ProjectsFailed(string message) => RefreshError = message;

    public void Notify(string source, Guid? projectId, string message, bool isError = false)
    {
        if (isError && _notifications.TryGetValue((source, projectId), out var previous) && previous.IsError && previous.Message == message)
            return;
        _notifications[(source, projectId)] = new(source, projectId, message, isError,
            isError ? null : _time.GetUtcNow().AddSeconds(5), ++_sequence);
    }

    public void Clear(string source, Guid? projectId = null) => _notifications.Remove((source, projectId));
    public void RemoveProject(Guid projectId)
    {
        foreach (var key in _notifications.Keys.Where(key => key.Item2 == projectId).ToArray())
            _notifications.Remove(key);
    }
    public void Dismiss(OperationNotification? notification)
    {
        if (notification is not null && _notifications.TryGetValue((notification.Source, notification.ProjectId), out var current) &&
            current.Sequence == notification.Sequence)
            _notifications.Remove((notification.Source, notification.ProjectId));
    }

    public OperationNotification? VisibleNotification(Guid? selectedProjectId)
    {
        var now = _time.GetUtcNow();
        foreach (var key in _notifications.Where(item => item.Value.ExpiresUtc <= now).Select(item => item.Key).ToArray())
            _notifications.Remove(key);
        return _notifications.Values.Where(item => item.ProjectId is null || item.ProjectId == selectedProjectId)
            .OrderByDescending(item => item.IsError).ThenByDescending(item => item.Sequence).FirstOrDefault();
    }

    public AppStatus CurrentStatus(IReadOnlyList<ProjectItemViewModel> projects)
    {
        if (RefreshError is { } error)
            return new("Unable to refresh index status", $"The displayed counts may be outdated. {error}", AppStatusTone.Error);
        if (!HasLoadedProjects)
            return new("Loading local indexes…", "Reading your projects and their current indexing state.", AppStatusTone.Busy);
        if (projects.Count == 0)
            return new("Create a project to begin", "Choose folders in Add project to build a local search index.", AppStatusTone.Neutral);

        var active = projects.Where(project => !project.IsPaused).ToArray();
        var processing = active.Sum(project => project.ProcessingCount);
        var waiting = active.Sum(project => project.QueuedCount);
        var discovering = active.Count(project => project.IsDiscovering);
        var pausing = projects.Count(project => project.Phase == "Pausing");
        var attention = projects.Count(project => project.ErrorCount > 0 || project.AttentionCount > 0 || project.HasFolderIssues);
        var searchable = projects.Sum(project => project.SearchableCount);
        var parts = new List<string>();
        if (processing > 0) parts.Add($"{processing:N0} {(processing == 1 ? "file" : "files")} processing");
        if (discovering > 0) parts.Add("Finding files");
        if (waiting > 0) parts.Add($"{waiting:N0} queued");
        if (pausing > 0) parts.Add("Finishing pause cleanup");
        if (attention > 0) parts.Add($"{attention} {(attention == 1 ? "project needs" : "projects need")} attention");
        var details = $"{searchable:N0} searchable files across {projects.Count} {(projects.Count == 1 ? "project" : "projects")}. " +
                      $"{projects.Count - active.Length} paused. " +
                      (parts.Count > 0 ? string.Join(" · ", parts) + ". " : string.Empty) +
                      "Open a project for counts, coverage, and current issues.";
        if (processing > 0)
            return new(parts[0], details, AppStatusTone.Busy);
        if (discovering > 0)
            return new("Finding files", details, AppStatusTone.Busy);
        if (waiting > 0)
            return new($"{waiting:N0} {(waiting == 1 ? "file" : "files")} queued", details, AppStatusTone.Busy);
        if (pausing > 0)
            return new("Finishing pause cleanup", details, AppStatusTone.Busy);
        if (active.Length == 0)
            return new("All projects paused", details + " Existing indexed files remain searchable.", AppStatusTone.Neutral);
        if (attention > 0)
            return new($"{attention} {(attention == 1 ? "project needs" : "projects need")} attention", details, AppStatusTone.Warning);
        if (projects.Sum(project => project.DocumentCount) == 0)
            return new("No supported files found", details + " Active folders are watched for changes.", AppStatusTone.Neutral);
        return new("Local indexes up to date", details + " Active folders are watched for changes.", AppStatusTone.Ready);
    }
}

internal sealed record UiRefreshStamp(Guid ProjectId, long Generation, string? PolicyKey, bool ModelAvailable, long Sequence)
{
    public bool IsCurrent(Guid? selectedId, long generation, string? policyKey, bool modelAvailable, long latestSequence) =>
        selectedId == ProjectId && generation == Generation &&
        string.Equals(policyKey, PolicyKey, StringComparison.Ordinal) && modelAvailable == ModelAvailable && latestSequence == Sequence;
}
