using ContextMole.Core;
using ContextMole.Indexing;

using Microsoft.Extensions.Logging.Abstractions;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class IndexingMemoryOrderingTests
{
    [Fact]
    public void ActivityTrackerClassifiesAdmissionWaitsSeparatelyFromRunningRetries()
    {
        var statuses = new MemoryAdmissionStatusStore();
        var tracker = new IndexingActivityTracker(statuses);
        var projectId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var job = new IndexJobLease(jobId, projectId, Guid.NewGuid(), Guid.NewGuid(), "retry.txt", ".txt",
            1, IndexJobKind.Index, 2);
        using var activity = tracker.Start(job);
        activity.SetStage(IndexingPipelineStage.QueuedForAdmission);
        var waitingSince = DateTimeOffset.UtcNow.AddSeconds(-2);
        statuses.Publish(new MemoryAdmissionWaitSnapshot(jobId, MemoryAdmissionWaitReason.SystemMemory, 1,
            1L << 30, 2L << 30, 3L << 30, 2L << 30, 2L << 30, 256L << 20,
            512L << 20, 4L << 30, waitingSince, DateTimeOffset.UtcNow));

        var waiting = tracker.GetSnapshot(projectId);
        var waitingItem = Assert.Single(waiting.ActiveItems);
        Assert.Equal(IndexingPipelineStage.WaitingForMemory, waitingItem.Stage);
        Assert.True(waitingItem.IsWaitingForMemory);
        Assert.Equal(0, waiting.ProcessingCount);
        Assert.Equal(0, waiting.RetryingCount);
        Assert.True(waitingItem.StageElapsed >= TimeSpan.FromSeconds(1));

        statuses.Clear(jobId);
        activity.SetStage(IndexingPipelineStage.Hashing);
        var processing = tracker.GetSnapshot(projectId);
        Assert.Equal(1, processing.ProcessingCount);
        Assert.Equal(1, processing.RetryingCount);
        Assert.True(Assert.Single(processing.ActiveItems).IsRetrying);
    }

    [Fact]
    public async Task WorkerDoesNotHoldCpuCapacityWhileWaitingForMemory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "memory-order.txt");
        await File.WriteAllTextAsync(source, "memory admission precedes cpu capacity", cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Memory ordering", cancellationToken);
        var memory = new BlockingMemoryAdmissionController();
        var cpu = new RecordingCpuBudget();
        var embeddings = new StorageUnavailableEmbeddings();
        var activities = new IndexingActivityTracker();
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new TextExtractor(), embeddings, activities, new EmbeddingPolicyRefreshTracker(),
            cpu, NullLogger<IndexingCoordinator>.Instance, memory);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await memory.Waiting.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal(0, cpu.WorkerAcquisitions);
            var waiting = activities.GetSnapshot(projectId);
            Assert.Equal(1, waiting.QueuedCount);
            Assert.Equal(0, waiting.ProcessingCount);
            Assert.Equal(IndexingPipelineStage.QueuedForAdmission, Assert.Single(waiting.ActiveItems).Stage);

            memory.Allow();
            await WaitUntilAsync(async () =>
                (await database.Store.ListProjectsAsync(cancellationToken)).Single(item => item.Id == projectId)
                is { IndexedCount: 1, PendingCount: 0 }, cancellationToken);
            Assert.True(cpu.WorkerAcquisitions > 0);
            await WaitUntilAsync(() => Task.FromResult(memory.Disposals == 1), cancellationToken);
        }
        finally
        {
            memory.Allow();
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task MemoryLeaseIsDisposedWhenProcessingFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "memory-failure.txt");
        await File.WriteAllTextAsync(source, "failure", cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Memory failure", cancellationToken);
        var memory = new BlockingMemoryAdmissionController(startBlocked: false);
        var embeddings = new StorageUnavailableEmbeddings();
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new ThrowingExtractor(), embeddings, new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(),
            new RecordingCpuBudget(), NullLogger<IndexingCoordinator>.Instance, memory);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(async () =>
                (await database.Store.ListProjectsAsync(cancellationToken)).Single(item => item.Id == projectId)
                is { ErrorCount: 1, PendingCount: 0 }, cancellationToken);
            await WaitUntilAsync(() => Task.FromResult(memory.Disposals == 1), cancellationToken);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task MemoryLeaseIsDisposedWhenCpuAdmissionIsCancelled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "memory-cancel.txt");
        await File.WriteAllTextAsync(source, "cancel", cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Memory cancellation", cancellationToken);
        var memory = new BlockingMemoryAdmissionController(startBlocked: false);
        var cpu = new BlockingCpuBudget();
        var embeddings = new StorageUnavailableEmbeddings();
        var activities = new IndexingActivityTracker();
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new TextExtractor(), embeddings, activities, new EmbeddingPolicyRefreshTracker(),
            cpu, NullLogger<IndexingCoordinator>.Instance, memory);
        var stopped = false;

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await cpu.Waiting.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal(1, memory.Acquisitions);
            Assert.Equal(0, memory.Disposals);
            var waiting = activities.GetSnapshot(projectId);
            Assert.Equal(1, waiting.WaitingForCpuCount);
            Assert.Equal(0, waiting.ProcessingCount);
            Assert.Equal(IndexingPipelineStage.WaitingForCpu, Assert.Single(waiting.ActiveItems).Stage);

            await coordinator.StopAsync(CancellationToken.None);
            stopped = true;
            Assert.Equal(1, memory.Disposals);
        }
        finally
        {
            if (!stopped) await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50, cancellationToken);
        }
        Assert.True(await condition(), "The indexing worker did not complete before the timeout.");
    }

    private sealed class TextExtractor : IDocumentExtractor
    {
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request,
            CancellationToken cancellationToken)
        {
            var text = await File.ReadAllTextAsync(request.SourcePath, cancellationToken);
            return new ExtractionResult(new ExtractedNode(Path.GetFileName(request.SourcePath), "text/plain",
                "root", [new ExtractedSection(text, new SourceLocation(LocationKind.Document),
                    ExtractionMethod.NativeText)], []), []);
        }
    }

    private sealed class ThrowingExtractor : IDocumentExtractor
    {
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];
        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken) =>
            Task.FromException<ExtractionResult>(new ContextMoleException(
                "intentional_extraction_failure", "Intentional extraction failure for lease testing.", false));
    }

    private sealed class BlockingMemoryAdmissionController : IMemoryAdmissionController
    {
        private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _acquisitions;
        private int _disposals;

        public BlockingMemoryAdmissionController(bool startBlocked = true)
        {
            if (!startBlocked) _allowed.TrySetResult();
        }

        public Task Waiting => _waiting.Task;
        public int Acquisitions => Volatile.Read(ref _acquisitions);
        public int Disposals => Volatile.Read(ref _disposals);

        public async ValueTask<IMemoryLease> AcquireAsync(MemoryWorkEstimate estimate,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _acquisitions);
            _waiting.TrySetResult();
            await _allowed.Task.WaitAsync(cancellationToken);
            return new MemoryLease(estimate.EstimatedBytes, () => Interlocked.Increment(ref _disposals));
        }

        public void Allow() => _allowed.TrySetResult();

        private sealed class MemoryLease(long reservedBytes, Action onDispose) : IMemoryLease
        {
            private int _disposed;
            public long ReservedBytes { get; } = reservedBytes;
            public bool IsExclusive => false;
            public SystemMemorySnapshot AdmissionSnapshot => default;
            public long ProcessSoftLimitBytes => long.MaxValue;
            public long SystemReserveBytes => 0;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose();
            }
        }
    }

    private sealed class RecordingCpuBudget : IGlobalCpuBudget
    {
        private int _workerAcquisitions;
        public int MaximumWorkerCount => 1;
        public int WorkerAcquisitions => Volatile.Read(ref _workerAcquisitions);

        public ValueTask<ICpuWorkerLease> AcquireWorkerAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _workerAcquisitions);
            return ValueTask.FromResult<ICpuWorkerLease>(new WorkerLease());
        }

        public ValueTask<ICpuFullCapacityLease> AcquireFullCapacityAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ICpuFullCapacityLease>(new FullCapacityLease());
        }

        private sealed class WorkerLease : ICpuWorkerLease
        {
            public IDisposable Activate() => NoopDisposable.Instance;
            public void Dispose()
            {
            }
        }

        private sealed class FullCapacityLease : ICpuFullCapacityLease
        {
            public int ThreadCount => 1;
            public void Dispose()
            {
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();
            public void Dispose()
            {
            }
        }
    }

    private sealed class BlockingCpuBudget : IGlobalCpuBudget
    {
        private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaximumWorkerCount => 1;
        public Task Waiting => _waiting.Task;

        public async ValueTask<ICpuWorkerLease> AcquireWorkerAsync(CancellationToken cancellationToken)
        {
            _waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("An infinite CPU wait unexpectedly completed.");
        }

        public ValueTask<ICpuFullCapacityLease> AcquireFullCapacityAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
