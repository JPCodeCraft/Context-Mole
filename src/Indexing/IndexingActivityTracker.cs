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
    DateTimeOffset? StartedUtc)
{
    public int Attempt { get; init; }
    public bool HasStartedProcessing => StartedUtc is not null;
    public bool IsWaitingForCpu => Stage == IndexingPipelineStage.WaitingForCpu;
    public bool IsWaitingForResources => !HasStartedProcessing || IsWaitingForCpu;
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
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ActiveActivity> _active = [];
    private readonly Dictionary<Guid, CompletedTiming> _completedByProject = [];
    private readonly HashSet<Guid> _discovering = [];
    private readonly Dictionary<(Guid ProjectId, Guid FolderId), ProjectFolderIssue> _folderIssues = [];

    public IndexingActivityTracker(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

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
        var now = _time.GetTimestamp();
        var activity = new ActiveActivity(job.JobId, job.ProjectId, job.DocumentId, job.SourcePath,
            job.Attempt, now);
        lock (_gate) _active[job.JobId] = activity;
        return new IndexingActivityHandle(this, job.JobId);
    }

    public IndexingTimingSnapshot GetSnapshot(Guid? projectId)
    {
        if (projectId is null) return new([], null, 0);
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var items = _active.Values
                .Where(item => item.ProjectId == projectId.Value)
                .OrderBy(item => item.ClaimedTimestamp)
                .Select(item => CreateSnapshot(item, now))
                .ToArray();
            if (!_completedByProject.TryGetValue(projectId.Value, out var completed) || completed.Count == 0)
                return new(items, null, 0);
            return new(items, TimeSpan.FromTicks(completed.TotalTicks / completed.Count), completed.Count);
        }
    }

    private IndexingActivitySnapshot CreateSnapshot(ActiveActivity item, long now)
    {
        var stageElapsed = item.StageStartedTimestamp is { } stageStarted
            ? _time.GetElapsedTime(stageStarted, now) : TimeSpan.Zero;
        return new IndexingActivitySnapshot(item.JobId, item.ProjectId, item.DocumentId,
            item.SourcePath, item.Stage, ProcessingElapsed(item, now),
            stageElapsed, item.StartedUtc)
        {
            Attempt = item.Attempt
        };
    }

    internal void StartProcessing(Guid jobId, IndexingPipelineStage stage)
    {
        if (stage is IndexingPipelineStage.InspectingSource or IndexingPipelineStage.WaitingForCpu)
            throw new ArgumentException("Processing must start in an executable pipeline stage.", nameof(stage));
        lock (_gate)
        {
            if (!_active.TryGetValue(jobId, out var activity)) return;
            activity.StartedUtc ??= _time.GetUtcNow();
            ChangeStage(activity, stage, _time.GetTimestamp());
        }
    }

    internal void SetStage(Guid jobId, IndexingPipelineStage stage)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(jobId, out var activity) && activity.Stage != stage)
            {
                ChangeStage(activity, stage, _time.GetTimestamp());
            }
        }
    }

    private void ChangeStage(ActiveActivity activity, IndexingPipelineStage stage, long now)
    {
        activity.CompletedProcessingTime = ProcessingElapsed(activity, now);
        activity.Stage = stage;
        var processing = activity.StartedUtc is not null && stage != IndexingPipelineStage.WaitingForCpu;
        activity.ProcessingStartedTimestamp = processing ? now : null;
        activity.StageStartedTimestamp = processing ? now : null;
    }

    private TimeSpan ProcessingElapsed(ActiveActivity activity, long now) =>
        activity.CompletedProcessingTime + (activity.ProcessingStartedTimestamp is { } started
            ? _time.GetElapsedTime(started, now) : TimeSpan.Zero);

    internal void Finish(Guid jobId, bool includeInAverage)
    {
        lock (_gate)
        {
            if (!_active.Remove(jobId, out var activity) || !includeInAverage || activity.StartedUtc is null) return;
            var elapsed = ProcessingElapsed(activity, _time.GetTimestamp());
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
        long claimedTimestamp)
    {
        public Guid JobId { get; } = jobId;
        public Guid ProjectId { get; } = projectId;
        public Guid DocumentId { get; } = documentId;
        public string SourcePath { get; } = sourcePath;
        public int Attempt { get; } = attempt;
        public IndexingPipelineStage Stage { get; set; } = IndexingPipelineStage.InspectingSource;
        public long ClaimedTimestamp { get; } = claimedTimestamp;
        public long? ProcessingStartedTimestamp { get; set; }
        public long? StageStartedTimestamp { get; set; }
        public TimeSpan CompletedProcessingTime { get; set; }
        public DateTimeOffset? StartedUtc { get; set; }
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

    public void StartProcessing(IndexingPipelineStage stage)
    {
        if (Volatile.Read(ref _finished) == 0) _tracker.StartProcessing(_jobId, stage);
    }

    public void Complete(bool includeInAverage)
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0) _tracker.Finish(_jobId, includeInAverage);
    }

    public void Dispose() => Complete(false);
}
