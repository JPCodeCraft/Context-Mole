using ContextMole.Core;
using ContextMole.Indexing;
using ContextMole.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class IndexingCpuOrderingTests
{
    [Fact]
    public void ActivityTrackerClassifiesCpuWaitsSeparatelyFromRunningRetries()
    {
        var tracker = new IndexingActivityTracker();
        var projectId = Guid.NewGuid();
        var job = new IndexJobLease(Guid.NewGuid(), projectId, Guid.NewGuid(), Guid.NewGuid(),
            "retry.txt", ".txt", 1, IndexJobKind.Index, 2);
        using var activity = tracker.Start(job);
        activity.SetStage(IndexingPipelineStage.WaitingForCpu);

        var waiting = tracker.GetSnapshot(projectId);
        var waitingItem = Assert.Single(waiting.ActiveItems);
        Assert.Equal(IndexingPipelineStage.WaitingForCpu, waitingItem.Stage);
        Assert.True(waitingItem.IsWaitingForCpu);
        Assert.Equal(1, waiting.WaitingForCpuCount);
        Assert.Equal(0, waiting.ProcessingCount);
        Assert.Equal(0, waiting.RetryingCount);

        activity.SetStage(IndexingPipelineStage.Hashing);
        var processing = tracker.GetSnapshot(projectId);
        Assert.Equal(1, processing.ProcessingCount);
        Assert.Equal(1, processing.RetryingCount);
        Assert.True(Assert.Single(processing.ActiveItems).IsRetrying);
    }

    [Fact]
    public async Task DocumentsEnterExtractionConcurrentlyAtTheCpuProfileLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(database.Paths.SourceDirectory, "first.msg"),
            "first message", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(database.Paths.SourceDirectory, "second.msg"),
            "second message", cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Concurrent containers", cancellationToken);
        var extractor = new ConcurrentExtractor();
        var embeddings = new StorageUnavailableEmbeddings();
        var activities = new IndexingActivityTracker();
        using var cpu = new GlobalCpuBudget(new FixedCpuUsageSettings(logicalProcessorCount: 5));
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            extractor, embeddings, activities, new EmbeddingPolicyRefreshTracker(),
            cpu, NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await extractor.BothEntered.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal(2, activities.GetSnapshot(projectId).ProcessingCount);

            extractor.Release();
            await WaitUntilAsync(async () =>
                (await database.Store.ListProjectsAsync(cancellationToken)).Single(item => item.Id == projectId)
                is { IndexedCount: 2, PendingCount: 0 }, cancellationToken);
        }
        finally
        {
            extractor.Release();
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task CpuWaitIsNotCountedAsProcessingAndStopsCleanly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "cpu-wait.txt");
        await File.WriteAllTextAsync(source, "wait", cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("CPU wait", cancellationToken);
        var cpu = new BlockingCpuBudget();
        var embeddings = new StorageUnavailableEmbeddings();
        var activities = new IndexingActivityTracker();
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new TextExtractor(), embeddings, activities, new EmbeddingPolicyRefreshTracker(),
            cpu, NullLogger<IndexingCoordinator>.Instance);
        var stopped = false;

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await cpu.Waiting.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var waiting = activities.GetSnapshot(projectId);
            Assert.Equal(1, waiting.WaitingForCpuCount);
            Assert.Equal(0, waiting.ProcessingCount);
            Assert.Equal(IndexingPipelineStage.WaitingForCpu, Assert.Single(waiting.ActiveItems).Stage);

            await coordinator.StopAsync(CancellationToken.None);
            stopped = true;
            Assert.Empty(activities.GetSnapshot(projectId).ActiveItems);
        }
        finally
        {
            if (!stopped) await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task CpuLeaseIsDisposedWhenProcessingFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "cpu-failure.txt");
        await File.WriteAllTextAsync(source, "failure", cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("CPU failure", cancellationToken);
        var cpu = new RecordingCpuBudget();
        var embeddings = new StorageUnavailableEmbeddings();
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new ThrowingExtractor(), embeddings, new IndexingActivityTracker(),
            new EmbeddingPolicyRefreshTracker(), cpu, NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(async () =>
                (await database.Store.ListProjectsAsync(cancellationToken)).Single(item => item.Id == projectId)
                is { ErrorCount: 1, PendingCount: 0 }, cancellationToken);
            await WaitUntilAsync(() => Task.FromResult(cpu.Disposals == 1), cancellationToken);
            Assert.Equal(1, cpu.Acquisitions);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
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
            return TextExtraction(request.SourcePath, text);
        }
    }

    private sealed class ThrowingExtractor : IDocumentExtractor
    {
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];
        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken) =>
            Task.FromException<ExtractionResult>(new ContextMoleException(
                "intentional_extraction_failure", "Intentional extraction failure for CPU lease testing.", false));
    }

    private sealed class ConcurrentExtractor : IDocumentExtractor
    {
        private readonly TaskCompletionSource _bothEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;

        public IReadOnlyCollection<string> Extensions { get; } = [".msg"];
        public Task BothEntered => _bothEntered.Task;

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _entered) == 2) _bothEntered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            var text = await File.ReadAllTextAsync(request.SourcePath, cancellationToken);
            return TextExtraction(request.SourcePath, text);
        }

        public void Release() => _release.TrySetResult();
    }

    private static ExtractionResult TextExtraction(string path, string text) => new(
        new ExtractedNode(Path.GetFileName(path), "text/plain", "root",
            [new ExtractedSection(text, new SourceLocation(LocationKind.Document), ExtractionMethod.NativeText)],
            []), []);

    private sealed class FixedCpuUsageSettings(int logicalProcessorCount) : ICpuUsageSettings
    {
        public CpuUsageProfile Profile => CpuUsageProfile.Normal;
        public int LogicalProcessorCount { get; } = logicalProcessorCount;
        public int ThreadLimit => CpuUsageSettings.CalculateThreadLimit(Profile, LogicalProcessorCount);
        public int MaximumThreadLimit =>
            CpuUsageSettings.CalculateThreadLimit(CpuUsageProfile.Heavy, LogicalProcessorCount);
        public void SetProfile(CpuUsageProfile profile) => throw new NotSupportedException();
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class RecordingCpuBudget : IGlobalCpuBudget
    {
        private int _acquisitions;
        private int _disposals;
        public int MaximumWorkerCount => 1;
        public int Acquisitions => Volatile.Read(ref _acquisitions);
        public int Disposals => Volatile.Read(ref _disposals);

        public ValueTask<ICpuWorkerLease> AcquireWorkerAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _acquisitions);
            return ValueTask.FromResult<ICpuWorkerLease>(
                new WorkerLease(() => Interlocked.Increment(ref _disposals)));
        }

        public ValueTask<ICpuFullCapacityLease> AcquireFullCapacityAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private sealed class WorkerLease(Action onDispose) : ICpuWorkerLease
        {
            private int _disposed;
            public IDisposable Activate() => NoopDisposable.Instance;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose();
            }
        }
    }

    private sealed class BlockingCpuBudget : IGlobalCpuBudget
    {
        private readonly TaskCompletionSource _waiting =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose()
        {
        }
    }
}
