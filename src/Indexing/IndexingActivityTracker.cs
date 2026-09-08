using System.Diagnostics;

using ContextMole.Core;

namespace ContextMole.Indexing;

public enum IndexingPipelineStage
{
    InspectingSource = 0,
    Hashing = 1,
    PreparingRevision = 2,
    ExtractingContent = 3,
    ChunkingText = 4,
    GeneratingEmbeddings = 5,
    VerifyingSource = 6,
    WritingIndex = 7,
    RecordingError = 8,
    WaitingForCpu = 11
}

public sealed record IndexingActivitySnapshot(
    Guid JobId,
    Guid ProjectId,
    Guid DocumentId,
    string SourcePath,
    IndexingPipelineStage Stage,
    TimeSpan Elapsed,
    TimeSpan StageElapsed,
    DateTimeOffset StartedUtc)
{
    public int Attempt { get; init; }
    public bool IsWaitingForCpu => Stage == IndexingPipelineStage.WaitingForCpu;
    public bool IsWaitingForResources => IsWaitingForCpu;
    public bool IsProcessing => !IsWaitingForResources;
    public bool IsRetrying => IsProcessing && Attempt > 0;
}

public sealed record IndexingTimingSnapshot(
    IReadOnlyList<IndexingActivitySnapshot> ActiveItems,
    TimeSpan? AverageCompletedDuration,
    long CompletedSampleCount)
{
    public int ProcessingCount => ActiveItems.Count(item => item.IsProcessing);
    public int RetryingCount => ActiveItems.Count(item => item.IsRetrying);
    public int WaitingForCpuCount => ActiveItems.Count(item => item.IsWaitingForCpu);
}

public sealed record ProjectFolderIssue(Guid FolderId, string Path, string Message);

public sealed class IndexingActivityTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ActiveActivity> _active = [];
    private readonly Dictionary<Guid, CompletedTiming> _completedByProject = [];
    private readonly HashSet<Guid> _discovering = [];
    private readonly Dictionary<(Guid ProjectId, Guid FolderId), ProjectFolderIssue> _folderIssues = [];

    public IReadOnlyList<ProjectFolderIssue> GetFolderIssues(Guid projectId)
    {
        lock (_gate)
            return _folderIssues.Where(item => item.Key.ProjectId == projectId)
                .Select(item => item.Value).OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal void SetFolderIssue(Guid projectId, Guid folderId, string path, string message)
    {
        lock (_gate) _folderIssues[(projectId, folderId)] = new ProjectFolderIssue(folderId, path, message);
    }

    internal void ClearFolderIssue(Guid projectId, Guid folderId)
    {
        lock (_gate) _folderIssues.Remove((projectId, folderId));
    }

    internal bool HasFolderIssue(Guid projectId, Guid folderId)
    {
        lock (_gate) return _folderIssues.ContainsKey((projectId, folderId));
    }

    internal void RetainFolderIssues(IReadOnlySet<Guid> folderIds)
    {
        lock (_gate)
            foreach (var key in _folderIssues.Keys.Where(key => !folderIds.Contains(key.FolderId)).ToArray())
                _folderIssues.Remove(key);
    }

    public bool IsDiscovering(Guid projectId)
    {
        lock (_gate) return _discovering.Contains(projectId);
    }

    internal void SetDiscovering(Guid projectId, bool discovering)
    {
        lock (_gate)
        {
            if (discovering) _discovering.Add(projectId);
            else _discovering.Remove(projectId);
        }
    }

    public bool HasActiveItems
    {
        get
        {
            lock (_gate) return _active.Count > 0;
        }
    }

    public IndexingActivityHandle Start(IndexJobLease job)
    {
        var now = Stopwatch.GetTimestamp();
        var activity = new ActiveActivity(job.JobId, job.ProjectId, job.DocumentId, job.SourcePath,
            job.Attempt, IndexingPipelineStage.InspectingSource, now, now, DateTimeOffset.UtcNow);
        lock (_gate) _active[job.JobId] = activity;
        return new IndexingActivityHandle(this, job.JobId);
    }

    public IndexingTimingSnapshot GetSnapshot(Guid? projectId)
    {
        if (projectId is null) return new([], null, 0);
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var items = _active.Values
                .Where(item => item.ProjectId == projectId.Value)
                .OrderBy(item => item.StartedTimestamp)
                .Select(item => CreateSnapshot(item, now))
                .ToArray();
            if (!_completedByProject.TryGetValue(projectId.Value, out var completed) || completed.Count == 0)
                return new(items, null, 0);
            return new(items, TimeSpan.FromTicks(completed.TotalTicks / completed.Count), completed.Count);
        }
    }

    private static IndexingActivitySnapshot CreateSnapshot(ActiveActivity item, long now)
    {
        var stageElapsed = Stopwatch.GetElapsedTime(item.StageStartedTimestamp, now);
        return new IndexingActivitySnapshot(item.JobId, item.ProjectId, item.DocumentId,
            item.SourcePath, item.Stage, Stopwatch.GetElapsedTime(item.StartedTimestamp, now),
            stageElapsed, item.StartedUtc)
        {
            Attempt = item.Attempt
        };
    }

    internal void SetStage(Guid jobId, IndexingPipelineStage stage)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(jobId, out var activity) && activity.Stage != stage)
            {
                activity.Stage = stage;
                activity.StageStartedTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    internal void Finish(Guid jobId, bool includeInAverage)
    {
        lock (_gate)
        {
            if (!_active.Remove(jobId, out var activity) || !includeInAverage) return;
            var elapsed = Stopwatch.GetElapsedTime(activity.StartedTimestamp);
            if (!_completedByProject.TryGetValue(activity.ProjectId, out var completed))
            {
                completed = new CompletedTiming();
                _completedByProject[activity.ProjectId] = completed;
            }
            completed.Count++;
            completed.TotalTicks += elapsed.Ticks;
        }
    }

    private sealed class ActiveActivity(
        Guid jobId,
        Guid projectId,
        Guid documentId,
        string sourcePath,
        int attempt,
        IndexingPipelineStage stage,
        long startedTimestamp,
        long stageStartedTimestamp,
        DateTimeOffset startedUtc)
    {
        public Guid JobId { get; } = jobId;
        public Guid ProjectId { get; } = projectId;
        public Guid DocumentId { get; } = documentId;
        public string SourcePath { get; } = sourcePath;
        public int Attempt { get; } = attempt;
        public IndexingPipelineStage Stage { get; set; } = stage;
        public long StartedTimestamp { get; } = startedTimestamp;
        public long StageStartedTimestamp { get; set; } = stageStartedTimestamp;
        public DateTimeOffset StartedUtc { get; } = startedUtc;
    }

    private sealed class CompletedTiming
    {
        public long Count { get; set; }
        public long TotalTicks { get; set; }
    }
}

public sealed class IndexingActivityHandle : IDisposable
{
    private readonly IndexingActivityTracker _tracker;
    private readonly Guid _jobId;
    private int _finished;

    internal IndexingActivityHandle(IndexingActivityTracker tracker, Guid jobId)
    {
        _tracker = tracker;
        _jobId = jobId;
    }

    public void SetStage(IndexingPipelineStage stage)
    {
        if (Volatile.Read(ref _finished) == 0) _tracker.SetStage(_jobId, stage);
    }

    public void Complete(bool includeInAverage)
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0) _tracker.Finish(_jobId, includeInAverage);
    }

    public void Dispose() => Complete(false);
}
