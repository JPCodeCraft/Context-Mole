using ContextMole.Core;
using ContextMole.Indexing;

namespace ContextMole.App.UI.ViewModels;

/// <summary>Runtime job progress is separate from durable root-file inventory and coverage.</summary>
public sealed record IndexingProgressPresentation(string Summary, string StatusLabel, string LastCompletion,
    string IdleMessage)
{
    public static IndexingProgressPresentation Create(ProjectItemViewModel? project, IndexingTimingSnapshot runtime)
    {
        var completed = $"{runtime.CompletedSampleCount:N0} {(runtime.CompletedSampleCount == 1 ? "job" : "jobs")} finished since Context Mole started";
        var summary = $"{completed} · {runtime.ActiveItems.Count:N0} running now · {project?.QueuedCount ?? 0:N0} files waiting";
        var last = runtime.LastCompletedUtc is { } at
            ? $"Last job finished {at.ToLocalTime():G}. Job counts restart with the app and can include the same file more than once."
            : "No job has finished since Context Mole started. Job counts restart with the app.";
        var status = project is null || project.IsReadinessStale ? "Status unavailable"
            : project.IsPaused ? runtime.ActiveItems.Count > 0 ? "Pausing" : "Paused"
            : runtime.RetryingCount > 0 ? "Retrying"
            : runtime.ProcessingCount > 0 ? "Active"
            : project.IsDiscovering ? "Finding files"
            : runtime.WaitingForCpuCount > 0 ? "Waiting for CPU"
            : runtime.ActiveItems.Count > 0 ? "Checking files"
            : project.IsRetryScheduled ? "Retry scheduled"
            : project.PendingCount > 0 ? "Waiting"
            : project.NeedsAttention ? "Needs attention" : "Idle";
        var idle = project is null ? "Select a project to see indexing activity."
            : project.IsReadinessStale ? "Queue status could not be checked. The last known counts are shown."
            : project.IsPaused ? project.PhaseDetails
            : project.IsDiscovering ? "Checking watched folders for changes…"
            : project.IsRetryScheduled ? project.PhaseDetails
            : project.PendingCount > 0 && project.HasFolderIssues ? "Files are waiting, but no file is active right now. A watched folder can't be read; see Folders below."
            : project.PendingCount > 0 ? "Files are waiting, but no file is active right now. If this doesn't change, check Settings and the files that need attention."
            : project.NeedsAttention ? project.PhaseDetails
            : "Nothing is being indexed. Changes in watched folders are picked up automatically.";
        return new(summary, status, last, idle);
    }
}
