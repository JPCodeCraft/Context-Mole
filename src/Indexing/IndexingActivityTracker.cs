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
    WaitingForMemory = 9,
    QueuedForAdmission = 10,
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
    public MemoryAdmissionWaitSnapshot? MemoryWait { get; init; }
    public bool IsQueuedForAdmission => Stage == IndexingPipelineStage.QueuedForAdmission;
    public bool IsWaitingForMemory => Stage == IndexingPipelineStage.WaitingForMemory;
    public bool IsWaitingForCpu => Stage == IndexingPipelineStage.WaitingForCpu;
    public bool IsWaitingForResources => IsQueuedForAdmission || IsWaitingForMemory || IsWaitingForCpu;
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
    public int QueuedCount => ActiveItems.Count(item => item.IsQueuedForAdmission);
    public int WaitingForMemoryCount => ActiveItems.Count(item => item.IsWaitingForMemory);
    public int WaitingForCpuCount => ActiveItems.Count(item => item.IsWaitingForCpu);
}

public sealed class IndexingActivityTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ActiveActivity> _active = [];
    private readonly Dictionary<Guid, CompletedTiming> _completedByProject = [];
    private readonly IMemoryAdmissionStatusStore _memoryStatuses;

    public IndexingActivityTracker() : this(new MemoryAdmissionStatusStore())
    {
    }

    public IndexingActivityTracker(IMemoryAdmissionStatusStore memoryStatuses)
    {
        _memoryStatuses = memoryStatuses ?? throw new ArgumentNullException(nameof(memoryStatuses));
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
            var nowUtc = DateTimeOffset.UtcNow;
            var items = _active.Values
                .Where(item => item.ProjectId == projectId.Value)
                .OrderBy(item => item.StartedTimestamp)
                .Select(item => CreateSnapshot(item, now, nowUtc))
                .ToArray();
            if (!_completedByProject.TryGetValue(projectId.Value, out var completed) || completed.Count == 0)
                return new(items, null, 0);
            return new(items, TimeSpan.FromTicks(completed.TotalTicks / completed.Count), completed.Count);
        }
    }

    private IndexingActivitySnapshot CreateSnapshot(ActiveActivity item, long now, DateTimeOffset nowUtc)
    {
        MemoryAdmissionWaitSnapshot? memoryWait = null;
        if (_memoryStatuses.TryGet(item.JobId, out var observed)) memoryWait = observed;

        var stage = item.Stage;
        if (memoryWait is not null)
        {
            stage = memoryWait.Reason is MemoryAdmissionWaitReason.SystemMemory or
                MemoryAdmissionWaitReason.ProcessSoftLimit
                ? IndexingPipelineStage.WaitingForMemory
                : IndexingPipelineStage.QueuedForAdmission;
        }

        var stageElapsed = Stopwatch.GetElapsedTime(item.StageStartedTimestamp, now);
        if (memoryWait is not null)
        {
            var waitingSince = memoryWait.WaitingSinceUtc;
            stageElapsed = waitingSince >= nowUtc ? TimeSpan.Zero : nowUtc - waitingSince;
        }

        return new IndexingActivitySnapshot(item.JobId, item.ProjectId, item.DocumentId,
            item.SourcePath, stage, Stopwatch.GetElapsedTime(item.StartedTimestamp, now),
            stageElapsed, item.StartedUtc)
        {
            Attempt = item.Attempt,
            MemoryWait = memoryWait
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
