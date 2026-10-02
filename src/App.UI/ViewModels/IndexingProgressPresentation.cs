using ContextMole.Core;
using ContextMole.Indexing;

namespace ContextMole.App.UI.ViewModels;

/// <summary>Runtime job progress is separate from durable root-file inventory and coverage.</summary>
public sealed record IndexingProgressPresentation(string Summary, string StatusLabel, string LastCompletion,
    string IdleMessage)
{
    public static IndexingProgressPresentation Create(ProjectItemViewModel? project, IndexingTimingSnapshot runtime)
    {
        var completed = $"{runtime.CompletedSampleCount:N0} completed jobs this app session";
        var summary = $"{completed} · {runtime.ActiveItems.Count:N0} in flight · {project?.QueuedCount ?? 0:N0} queued root files";
        var last = runtime.LastCompletedUtc is { } at
            ? $"Last successful job completed {at.ToLocalTime():G}. Session totals reset when the app restarts; they are not unique-file totals."
            : "No successful job completion recorded in this app session. Session totals reset when the app restarts.";
        var status = project is null || project.IsReadinessStale ? "STATUS UNAVAILABLE"
            : project.IsPaused ? runtime.ActiveItems.Count > 0 ? "PAUSING" : "PAUSED"
            : runtime.RetryingCount > 0 ? "RETRYING"
            : runtime.ProcessingCount > 0 ? "ACTIVE"
            : project.IsDiscovering ? "FINDING FILES"
            : runtime.WaitingForCpuCount > 0 ? "WAITING FOR CPU"
            : runtime.ActiveItems.Count > 0 ? "CHECKING FILES"
            : project.IsRetryScheduled ? "RETRY SCHEDULED"
            : project.PendingCount > 0 ? "WAITING"
            : project.NeedsAttention ? "NEEDS ATTENTION" : "IDLE";
        var idle = project is null ? "Select a project to see indexing activity."
            : project.IsReadinessStale ? "Current queue status could not be verified. Last known counts are retained."
            : project.IsPaused ? project.PhaseDetails
            : project.IsDiscovering ? "Checking watched folders. Processing activity will appear here without moving the page."
            : project.IsRetryScheduled ? project.PhaseDetails
            : project.PendingCount > 0 && project.HasFolderIssues ? "No file is active right now. Folder access needs attention; review the folder issues above."
            : project.PendingCount > 0 ? "Work remains queued; no file is active right now. This is not a completed-work count. Check Settings and current issues if the queue stays unchanged."
            : project.NeedsAttention ? project.PhaseDetails
            : "No files are active. Watched-folder changes will appear here; inventory totals stay the same unless files are added or removed.";
        return new(summary, status, last, idle);
    }
}
