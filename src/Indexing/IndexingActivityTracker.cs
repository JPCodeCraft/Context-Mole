using System.Diagnostics;
using MCPIndexSearch.Core;

namespace MCPIndexSearch.Indexing;

public enum IndexingPipelineStage
{
    InspectingSource,
    Hashing,
    PreparingRevision,
    ExtractingContent,
    ChunkingText,
    GeneratingEmbeddings,
    VerifyingSource,
    WritingIndex,
    RecordingError
}

public sealed record IndexingActivitySnapshot(
    Guid JobId,
    Guid ProjectId,
    Guid DocumentId,
    string SourcePath,
    IndexingPipelineStage Stage,
    TimeSpan Elapsed,
    TimeSpan StageElapsed,
    DateTimeOffset StartedUtc);

public sealed record IndexingTimingSnapshot(
    IReadOnlyList<IndexingActivitySnapshot> ActiveItems,
    TimeSpan? AverageCompletedDuration,
    long CompletedSampleCount);

public sealed class IndexingActivityTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ActiveActivity> _active = [];
    private readonly Dictionary<Guid, CompletedTiming> _completedByProject = [];

    public IndexingActivityHandle Start(IndexJobLease job)
    {
        var now = Stopwatch.GetTimestamp();
        var activity = new ActiveActivity(job.JobId, job.ProjectId, job.DocumentId, job.SourcePath,
            IndexingPipelineStage.InspectingSource, now, now, DateTimeOffset.UtcNow);
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
                .Select(item => new IndexingActivitySnapshot(item.JobId, item.ProjectId, item.DocumentId,
                    item.SourcePath, item.Stage, Stopwatch.GetElapsedTime(item.StartedTimestamp, now),
                    Stopwatch.GetElapsedTime(item.StageStartedTimestamp, now), item.StartedUtc))
                .ToArray();
            if (!_completedByProject.TryGetValue(projectId.Value, out var completed) || completed.Count == 0)
                return new(items, null, 0);
            return new(items, TimeSpan.FromTicks(completed.TotalTicks / completed.Count), completed.Count);
        }
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
        IndexingPipelineStage stage,
        long startedTimestamp,
        long stageStartedTimestamp,
        DateTimeOffset startedUtc)
    {
        public Guid JobId { get; } = jobId;
        public Guid ProjectId { get; } = projectId;
        public Guid DocumentId { get; } = documentId;
        public string SourcePath { get; } = sourcePath;
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
