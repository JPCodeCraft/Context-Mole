using ContextMole.App.UI.ViewModels;
using ContextMole.Core;
using ContextMole.Indexing;

namespace ContextMole.Tests;

public sealed class IndexingTimingRegressionTests
{
    [Theory]
    [InlineData(IndexJobKind.Index)]
    [InlineData(IndexJobKind.EmbeddingRefresh)]
    public void QueuedTimeIsExcludedFromTimersAndSuccessfulAverage(IndexJobKind kind)
    {
        var time = new ManualTimeProvider();
        var tracker = new IndexingActivityTracker(time);
        var job = Job(kind);
        using var activity = tracker.Start(job);
        time.Advance(TimeSpan.FromMinutes(3));
        var preflight = Assert.Single(tracker.GetSnapshot(job.ProjectId).ActiveItems);
        Assert.False(preflight.IsProcessing);
        Assert.Null(preflight.StartedUtc);
        Assert.Equal(TimeSpan.Zero, preflight.Elapsed);
        Assert.Equal(TimeSpan.Zero, preflight.StageElapsed);
        var viewModel = new IndexingActivityItemViewModel(preflight);
        Assert.False(viewModel.ShowTimers);

        activity.SetStage(IndexingPipelineStage.WaitingForCpu);
        time.Advance(TimeSpan.FromHours(2));
        var queued = Assert.Single(tracker.GetSnapshot(job.ProjectId).ActiveItems);
        Assert.True(queued.IsWaitingForCpu);
        Assert.Equal(TimeSpan.Zero, queued.Elapsed);
        Assert.Equal(TimeSpan.Zero, queued.StageElapsed);
        Assert.Null(tracker.GetSnapshot(job.ProjectId).AverageCompletedDuration);
        var admittedUtc = time.GetUtcNow();
        activity.StartProcessing(kind == IndexJobKind.Index
            ? IndexingPipelineStage.Hashing : IndexingPipelineStage.PreparingRevision);
        time.Advance(TimeSpan.FromSeconds(4));
        var processing = Assert.Single(tracker.GetSnapshot(job.ProjectId).ActiveItems);
        Assert.True(processing.IsProcessing);
        Assert.Equal(admittedUtc, processing.StartedUtc);
        Assert.Equal(TimeSpan.FromSeconds(4), processing.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(4), processing.StageElapsed);
        viewModel.UpdateFrom(processing);
        Assert.True(viewModel.ShowTimers);

        activity.SetStage(IndexingPipelineStage.ExtractingContent);
        time.Advance(TimeSpan.FromSeconds(6));
        var extracting = Assert.Single(tracker.GetSnapshot(job.ProjectId).ActiveItems);
        Assert.Equal(TimeSpan.FromSeconds(10), extracting.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(6), extracting.StageElapsed);
        activity.Complete(true);
        var completed = tracker.GetSnapshot(job.ProjectId);
        Assert.Equal(1, completed.CompletedSampleCount);
        Assert.Equal(TimeSpan.FromSeconds(10), completed.AverageCompletedDuration);
    }

    [Fact]
    public void LaterCpuWaitFreezesTimeAndResumesWithoutResettingTotal()
    {
        var time = new ManualTimeProvider();
        var tracker = new IndexingActivityTracker(time);
        var job = Job(IndexJobKind.Index);
        using var activity = tracker.Start(job);
        activity.StartProcessing(IndexingPipelineStage.Hashing);
        time.Advance(TimeSpan.FromSeconds(2));
        activity.SetStage(IndexingPipelineStage.WaitingForCpu);
        time.Advance(TimeSpan.FromMinutes(10));
        var waiting = Assert.Single(tracker.GetSnapshot(job.ProjectId).ActiveItems);
        Assert.Equal(TimeSpan.FromSeconds(2), waiting.Elapsed);
        Assert.Equal(TimeSpan.Zero, waiting.StageElapsed);
        Assert.False(new IndexingActivityItemViewModel(waiting).ShowTimers);
        activity.SetStage(IndexingPipelineStage.GeneratingEmbeddings);
        time.Advance(TimeSpan.FromSeconds(3));
        activity.Complete(true);
        Assert.Equal(TimeSpan.FromSeconds(5), tracker.GetSnapshot(job.ProjectId).AverageCompletedDuration);
    }

    [Fact]
    public void CancellationUnstartedCompletionAndFailedAttemptDoNotAffectAverage()
    {
        var time = new ManualTimeProvider();
        var tracker = new IndexingActivityTracker(time);
        var job = Job(IndexJobKind.Index);
        using (var waiting = tracker.Start(job))
        {
            waiting.SetStage(IndexingPipelineStage.WaitingForCpu);
            time.Advance(TimeSpan.FromHours(1));
        }
        using (var neverStarted = tracker.Start(job)) neverStarted.Complete(true);
        using (var failed = tracker.Start(job))
        {
            failed.StartProcessing(IndexingPipelineStage.Hashing);
            time.Advance(TimeSpan.FromSeconds(20));
            failed.Complete(false);
        }
        Assert.Equal(0, tracker.GetSnapshot(job.ProjectId).CompletedSampleCount);
        Assert.Null(tracker.GetSnapshot(job.ProjectId).AverageCompletedDuration);

        using (var retry = tracker.Start(job with { Attempt = 2 }))
        {
            time.Advance(TimeSpan.FromMinutes(5));
            retry.StartProcessing(IndexingPipelineStage.ExtractingContent);
            time.Advance(TimeSpan.FromSeconds(8));
            Assert.True(Assert.Single(tracker.GetSnapshot(job.ProjectId).ActiveItems).IsRetrying);
            retry.Complete(true);
        }
        using (var second = tracker.Start(job with { JobId = Guid.NewGuid() }))
        {
            second.StartProcessing(IndexingPipelineStage.Hashing);
            time.Advance(TimeSpan.FromSeconds(4));
            second.Complete(true);
        }
        var completed = tracker.GetSnapshot(job.ProjectId);
        Assert.Equal(2, completed.CompletedSampleCount);
        Assert.Equal(TimeSpan.FromSeconds(6), completed.AverageCompletedDuration);
        Assert.Empty(completed.ActiveItems);
    }

    private static IndexJobLease Job(IndexJobKind kind) => new(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), "timing.txt", ".txt", 1, kind, 0);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
}
