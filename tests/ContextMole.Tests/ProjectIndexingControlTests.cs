using System.Collections.Concurrent;

using ContextMole.Core;
using ContextMole.Indexing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class ProjectIndexingControlTests
{
    [Theory]
    [InlineData(BlockStage.Memory)]
    [InlineData(BlockStage.Cpu)]
    [InlineData(BlockStage.Extraction)]
    [InlineData(BlockStage.Embeddings)]
    public async Task PauseCancellationFlowsThroughTheWholeActivePipelineWithoutRecordingFailure(
        BlockStage stage)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, $"pause-{stage}.txt");
        await File.WriteAllTextAsync(source, "project pause cancellation reaches active indexing work",
            cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync($"Pause {stage}", cancellationToken);
        var probe = new CancellationProbe();
        var writer = new ObservingIndexWriter(database.Writer);
        var memory = new StageMemoryAdmissionController(stage, probe);
        var cpu = new StageCpuBudget(stage, probe);
        var extractor = new StageExtractor(stage, probe);
        var embeddings = new StageEmbeddings(stage, probe);
        var activities = new IndexingActivityTracker();
        using var coordinator = new IndexingCoordinator(writer, database.Store, database.Paths, extractor, embeddings,
            activities, new EmbeddingPolicyRefreshTracker(), cpu, NullLogger<IndexingCoordinator>.Instance, memory);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            coordinator.BeginPause(projectId);
            await writer.SetProjectPausedAsync(projectId, true, cancellationToken);
            await coordinator.DrainPausedAsync(projectId, cancellationToken);

            await probe.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Empty(writer.Failures);
            Assert.Empty(activities.GetSnapshot(projectId).ActiveItems);
            var summary = (await database.Store.ListProjectsAsync(cancellationToken))
                .Single(project => project.Id == projectId);
            Assert.Equal(ProjectState.Paused, summary.State);
            Assert.Equal(0, summary.Work.ProcessingCount);
            Assert.True(summary.Work.QueuedCount > 0);
            Assert.Equal(0, summary.ErrorCount);
            Assert.Equal(stage == BlockStage.Memory ? 0 : 1, memory.Disposals);
            Assert.Equal(stage is BlockStage.Extraction or BlockStage.Embeddings ? 1 : 0, cpu.Disposals);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task DrainWaitsForLeaseClaimThatReturnedBeforeDurablePauseButHasNotRegistered()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "late-lease.txt");
        await File.WriteAllTextAsync(source, "late lease must not escape a project pause", cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Late lease", cancellationToken);
        var writer = new ObservingIndexWriter(database.Writer, holdFirstLease: true);
        var extractor = new CountingExtractor();
        var embeddings = new StorageUnavailableEmbeddings();
        using var coordinator = new IndexingCoordinator(writer, database.Store, database.Paths, extractor, embeddings,
            new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(), new StageCpuBudget(),
            NullLogger<IndexingCoordinator>.Instance, new StageMemoryAdmissionController());

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await writer.LeaseCaptured.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            coordinator.BeginPause(projectId);
            await writer.SetProjectPausedAsync(projectId, true, cancellationToken);

            var drain = coordinator.DrainPausedAsync(projectId, cancellationToken);
            await Task.Delay(100, cancellationToken);
            Assert.False(drain.IsCompleted,
                "Drain acknowledged before the already-issued lease claim reached project registration.");

            writer.ReleaseLease();
            await drain.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal(0, extractor.Calls);
            Assert.Empty(writer.Failures);
        }
        finally
        {
            writer.ReleaseLease();
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task ResumeRemovesPersistentAdmissionMarkerBeforeDurableResume()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "resume.txt");
        await File.WriteAllTextAsync(source, "resumed work", cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Resume", cancellationToken);
        var writer = new ObservingIndexWriter(database.Writer, holdFirstLease: true);
        var extractor = new CountingExtractor();
        var embeddings = new StorageUnavailableEmbeddings();
        using var coordinator = new IndexingCoordinator(writer, database.Store, database.Paths, extractor, embeddings,
            new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(), new StageCpuBudget(),
            NullLogger<IndexingCoordinator>.Instance, new StageMemoryAdmissionController());

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await writer.LeaseCaptured.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            coordinator.BeginPause(projectId);
            await writer.SetProjectPausedAsync(projectId, true, cancellationToken);
            var drain = coordinator.DrainPausedAsync(projectId, cancellationToken);
            writer.ReleaseLease();
            await drain.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            coordinator.Resume(projectId);
            await writer.SetProjectPausedAsync(projectId, false, cancellationToken);
            await WaitUntilAsync(() => Volatile.Read(ref extractor.Calls) == 1, cancellationToken);
            Assert.Empty(writer.Failures);
        }
        finally
        {
            writer.ReleaseLease();
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task DurablePauseReturnsWhileNonCooperativeExtractionDrainsAndResumeWaitsForIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "non-cooperative-pause.txt");
        await File.WriteAllTextAsync(source, "pause must not wait for a native parser", cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Background pause drain", cancellationToken);
        var writer = new ObservingIndexWriter(database.Writer);
        var extractor = new NonCooperativeExtractor();
        var embeddings = new StorageUnavailableEmbeddings();
        using var coordinator = new IndexingCoordinator(writer, database.Store, database.Paths, extractor, embeddings,
            new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(), new StageCpuBudget(),
            NullLogger<IndexingCoordinator>.Instance, new StageMemoryAdmissionController());

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await extractor.Entered.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            coordinator.BeginPause(projectId);
            await writer.SetProjectPausedAsync(projectId, true, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            var drain = coordinator.DrainPausedAsync(projectId, cancellationToken);

            await Task.Delay(100, cancellationToken);
            Assert.False(drain.IsCompleted,
                "The deliberately non-cooperative extraction should still be unwinding in the background.");
            var paused = (await database.Store.ListProjectsAsync(cancellationToken))
                .Single(project => project.Id == projectId);
            Assert.Equal(ProjectState.Paused, paused.State);
            Assert.Equal(0, paused.Work.ProcessingCount);
            Assert.True(paused.Work.QueuedCount > 0);

            var resume = ResumeAfterDrainAsync();
            await Task.Delay(100, cancellationToken);
            Assert.False(resume.IsCompleted, "Resume must not reopen admission before the old operation drains.");
            Assert.Equal(1, extractor.Calls);
            Assert.Equal(ProjectState.Paused,
                (await database.Store.ListProjectsAsync(cancellationToken)).Single(project => project.Id == projectId).State);

            extractor.Release();
            await resume.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal(ProjectState.Active,
                (await database.Store.ListProjectsAsync(cancellationToken)).Single(project => project.Id == projectId).State);
            Assert.Empty(writer.Failures);

            async Task ResumeAfterDrainAsync()
            {
                await drain;
                coordinator.Resume(projectId);
                await writer.SetProjectPausedAsync(projectId, false, cancellationToken);
            }
        }
        finally
        {
            extractor.Release();
            coordinator.Resume(projectId);
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task FailedDurablePauseRollbackProcessesDeferredLeasesWithoutDrainingQueueIntoRunningState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        const int documentCount = 6;
        for (var index = 0; index < documentCount; index++)
            await File.WriteAllTextAsync(Path.Combine(database.Paths.SourceDirectory, $"rollback-{index}.txt"),
                $"rollback document {index}", cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Pause rollback", cancellationToken);
        var writer = new ObservingIndexWriter(database.Writer, failFirstPause: true);
        var extractor = new CountingExtractor();
        var embeddings = new StorageUnavailableEmbeddings();
        using var coordinator = new IndexingCoordinator(writer, database.Store, database.Paths, extractor, embeddings,
            new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(),
            new StageCpuBudget(maximumWorkerCount: 2), NullLogger<IndexingCoordinator>.Instance,
            new StageMemoryAdmissionController());

        coordinator.BeginPause(projectId);
        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(() => writer.LeasedJobs == 2, cancellationToken);
            await Task.Delay(150, cancellationToken);
            Assert.Equal(2, writer.LeasedJobs);

            await Assert.ThrowsAsync<IOException>(() =>
                writer.SetProjectPausedAsync(projectId, true, cancellationToken));
            coordinator.Resume(projectId);

            await WaitUntilAsync(async () =>
            {
                var project = (await database.Store.ListProjectsAsync(cancellationToken))
                    .Single(item => item.Id == projectId);
                return project is { IndexedCount: documentCount, PendingCount: 0, ErrorCount: 0 } &&
                       project.Work.ProcessingCount == 0;
            }, cancellationToken);
            Assert.Equal(documentCount, extractor.Calls);
            Assert.Empty(writer.Failures);
        }
        finally
        {
            coordinator.Resume(projectId);
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task FailedDurablePauseRollbackRetriesTheCanceledActiveLeaseWithoutRecordingFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "active-rollback.txt");
        await File.WriteAllTextAsync(source, "active pause rollback", cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Active pause rollback", cancellationToken);
        var writer = new ObservingIndexWriter(database.Writer, failFirstPause: true);
        var extractor = new CancelFirstExtractor();
        var embeddings = new StorageUnavailableEmbeddings();
        using var coordinator = new IndexingCoordinator(writer, database.Store, database.Paths, extractor, embeddings,
            new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(), new StageCpuBudget(),
            NullLogger<IndexingCoordinator>.Instance, new StageMemoryAdmissionController());

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await extractor.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            coordinator.BeginPause(projectId);
            await Assert.ThrowsAsync<IOException>(() =>
                writer.SetProjectPausedAsync(projectId, true, cancellationToken));
            coordinator.Resume(projectId);

            await WaitUntilAsync(async () =>
            {
                var project = (await database.Store.ListProjectsAsync(cancellationToken))
                    .Single(item => item.Id == projectId);
                return project is { IndexedCount: 1, PendingCount: 0, ErrorCount: 0 } &&
                       project.Work.ProcessingCount == 0;
            }, cancellationToken);
            Assert.Equal(2, extractor.Calls);
            Assert.Equal(1, extractor.Cancellations);
            Assert.Empty(writer.Failures);
        }
        finally
        {
            coordinator.Resume(projectId);
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task PausingOneProjectDoesNotCancelAnotherProjectsActiveOperation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var firstDirectory = Path.Combine(database.Paths.SourceDirectory, "isolation-first");
        var secondDirectory = Path.Combine(database.Paths.SourceDirectory, "isolation-second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var firstSource = Path.Combine(firstDirectory, "first.txt");
        var secondSource = Path.Combine(secondDirectory, "second.txt");
        await File.WriteAllTextAsync(firstSource, "first project", cancellationToken);
        await File.WriteAllTextAsync(secondSource, "second project", cancellationToken);
        var firstProject = await database.Writer.CreateProjectAsync(
            new CreateProjectRequest("Isolation first", [firstDirectory]), cancellationToken);
        var secondProject = await database.Writer.CreateProjectAsync(
            new CreateProjectRequest("Isolation second", [secondDirectory]), cancellationToken);
        var firstProbe = new CancellationProbe();
        var secondProbe = new CancellationProbe();
        var extractor = new ProjectIsolationExtractor(firstSource, firstProbe, secondSource, secondProbe);
        var writer = new ObservingIndexWriter(database.Writer);
        var embeddings = new StorageUnavailableEmbeddings();
        var activities = new IndexingActivityTracker();
        using var coordinator = new IndexingCoordinator(writer, database.Store, database.Paths, extractor, embeddings,
            activities, new EmbeddingPolicyRefreshTracker(), new StageCpuBudget(maximumWorkerCount: 2),
            NullLogger<IndexingCoordinator>.Instance, new StageMemoryAdmissionController());

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await Task.WhenAll(firstProbe.Entered.Task, secondProbe.Entered.Task)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            coordinator.BeginPause(firstProject);
            await writer.SetProjectPausedAsync(firstProject, true, cancellationToken);
            await coordinator.DrainPausedAsync(firstProject, cancellationToken);

            Assert.True(firstProbe.Canceled.Task.IsCompletedSuccessfully);
            Assert.False(secondProbe.Canceled.Task.IsCompleted);
            Assert.Single(activities.GetSnapshot(secondProject).ActiveItems);
            Assert.DoesNotContain(writer.Failures, failure => failure.ProjectId == firstProject);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task IndexingRegistrationResolvesControlAndHostedServiceToSameSingleton()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var embeddings = new StorageUnavailableEmbeddings();
        var services = new ServiceCollection();
        services.AddSingleton<IIndexWriter>(database.Writer);
        services.AddSingleton<ISearchStore>(database.Store);
        services.AddSingleton<IAppPaths>(database.Paths);
        services.AddSingleton<IDocumentExtractor>(new CountingExtractor());
        services.AddSingleton<IEmbeddingGenerator>(embeddings);
        services.AddSingleton<IGlobalCpuBudget>(new StageCpuBudget());
        services.AddSingleton<IMemoryAdmissionController>(new StageMemoryAdmissionController());
        services.AddLogging();
        services.AddContextMoleIndexing();
        await using var provider = services.BuildServiceProvider();

        var coordinator = provider.GetRequiredService<IndexingCoordinator>();
        Assert.Same(coordinator, provider.GetRequiredService<IProjectIndexingControl>());
        Assert.Same(coordinator, provider.GetServices<IHostedService>().OfType<IndexingCoordinator>().Single());
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken) =>
        await WaitUntilAsync(() => Task.FromResult(condition()), cancellationToken);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50, cancellationToken);
        }
        Assert.True(await condition(), "The expected indexing state was not reached before the timeout.");
    }

    public enum BlockStage { Memory, Cpu, Extraction, Embeddings }

    private sealed class CancellationProbe
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task BlockAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Canceled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class StageMemoryAdmissionController : IMemoryAdmissionController
    {
        private readonly BlockStage? _stage;
        private readonly CancellationProbe? _probe;
        private int _disposals;

        public StageMemoryAdmissionController(BlockStage? stage = null, CancellationProbe? probe = null)
        {
            _stage = stage;
            _probe = probe;
        }

        public int Disposals => Volatile.Read(ref _disposals);

        public async ValueTask<IMemoryLease> AcquireAsync(MemoryWorkEstimate estimate,
            CancellationToken cancellationToken = default)
        {
            if (_stage == BlockStage.Memory)
                await _probe!.BlockAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new Lease(estimate.EstimatedBytes, () => Interlocked.Increment(ref _disposals));
        }

        private sealed class Lease(long reservedBytes, Action onDispose) : IMemoryLease
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

    private sealed class StageCpuBudget : IGlobalCpuBudget
    {
        private readonly BlockStage? _stage;
        private readonly CancellationProbe? _probe;
        private readonly int _maximumWorkerCount;
        private int _disposals;

        public StageCpuBudget(BlockStage? stage = null, CancellationProbe? probe = null,
            int maximumWorkerCount = 1)
        {
            _stage = stage;
            _probe = probe;
            _maximumWorkerCount = maximumWorkerCount;
        }

        public int MaximumWorkerCount => _maximumWorkerCount;
        public int Disposals => Volatile.Read(ref _disposals);

        public async ValueTask<ICpuWorkerLease> AcquireWorkerAsync(CancellationToken cancellationToken)
        {
            if (_stage == BlockStage.Cpu)
                await _probe!.BlockAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new WorkerLease(() => Interlocked.Increment(ref _disposals));
        }

        public ValueTask<ICpuFullCapacityLease> AcquireFullCapacityAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<ICpuFullCapacityLease>(new FullCapacityLease());

        private sealed class WorkerLease(Action onDispose) : ICpuWorkerLease
        {
            private int _disposed;
            public IDisposable Activate() => NoopDisposable.Instance;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose();
            }
        }

        private sealed class FullCapacityLease : ICpuFullCapacityLease
        {
            public int ThreadCount => 1;
            public void Dispose() { }
        }
    }

    private sealed class StageExtractor(BlockStage? stage = null, CancellationProbe? probe = null)
        : IDocumentExtractor
    {
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request,
            CancellationToken cancellationToken)
        {
            if (stage == BlockStage.Extraction)
                await probe!.BlockAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(request.SourcePath, cancellationToken);
            return TextExtraction(request.SourcePath, text);
        }
    }

    private sealed class CountingExtractor : IDocumentExtractor
    {
        public int Calls;
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var text = await File.ReadAllTextAsync(request.SourcePath, cancellationToken);
            return TextExtraction(request.SourcePath, text);
        }
    }

    private sealed class CancelFirstExtractor : IDocumentExtractor
    {
        private int _calls;
        private int _cancellations;
        public TaskCompletionSource FirstCallEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => Volatile.Read(ref _calls);
        public int Cancellations => Volatile.Read(ref _cancellations);
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstCallEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _cancellations);
                    throw;
                }
            }

            var text = await File.ReadAllTextAsync(request.SourcePath, cancellationToken);
            return TextExtraction(request.SourcePath, text);
        }
    }

    private sealed class NonCooperativeExtractor : IDocumentExtractor
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];
        public Task Entered => _entered.Task;
        public int Calls => Volatile.Read(ref _calls);
        public void Release() => _release.TrySetResult();

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _entered.TrySetResult();
                // Models a synchronous/native parser call: cancellation is noticed only after the
                // uninterruptible phase returns.
                await _release.Task;
                cancellationToken.ThrowIfCancellationRequested();
            }

            var text = await File.ReadAllTextAsync(request.SourcePath, cancellationToken);
            return TextExtraction(request.SourcePath, text);
        }
    }

    private sealed class ProjectIsolationExtractor(
        string firstPath,
        CancellationProbe firstProbe,
        string secondPath,
        CancellationProbe secondProbe) : IDocumentExtractor
    {
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request,
            CancellationToken cancellationToken)
        {
            if (Path.GetFullPath(request.SourcePath) == Path.GetFullPath(firstPath))
                await firstProbe.BlockAsync(cancellationToken);
            else if (Path.GetFullPath(request.SourcePath) == Path.GetFullPath(secondPath))
                await secondProbe.BlockAsync(cancellationToken);
            else
                throw new InvalidOperationException($"Unexpected isolation source: {request.SourcePath}");
            throw new InvalidOperationException("A blocked isolation extraction unexpectedly completed.");
        }
    }

    private sealed class StageEmbeddings(BlockStage stage, CancellationProbe probe) : IEmbeddingGenerator
    {
        private static readonly EmbeddingPolicy TestPolicy =
            new("pause-test", "1", "model", "tokenizer", "fp32", 384, 384, "mean", "l2");

        public bool IsAvailable => stage == BlockStage.Embeddings;
        public string? UnavailableReason => IsAvailable ? null : "Disabled for this pipeline stage test.";
        public EmbeddingPolicy? Policy => IsAvailable ? TestPolicy : null;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public int CountTokens(string text) =>
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        public async Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages,
            CancellationToken cancellationToken)
        {
            await probe.BlockAsync(cancellationToken);
            throw new InvalidOperationException("A blocked embedding call unexpectedly completed.");
        }

        public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ObservingIndexWriter(
        IIndexWriter inner,
        bool holdFirstLease = false,
        bool failFirstPause = false) : IIndexWriter
    {
        private readonly TaskCompletionSource _leaseCaptured =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseLease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _heldLease;
        private int _leasedJobs;
        private int _pauseFailures;

        public Task Ready => inner.Ready;
        public Task LeaseCaptured => _leaseCaptured.Task;
        public int LeasedJobs => Volatile.Read(ref _leasedJobs);
        public ConcurrentQueue<(Guid ProjectId, string Code)> Failures { get; } = new();
        public void ReleaseLease() => _releaseLease.TrySetResult();

        public Task<Guid> CreateProjectAsync(CreateProjectRequest request,
            CancellationToken cancellationToken = default) => inner.CreateProjectAsync(request, cancellationToken);
        public Task UpdateProjectAsync(UpdateProjectRequest request,
            CancellationToken cancellationToken = default) => inner.UpdateProjectAsync(request, cancellationToken);
        public Task SetProjectPausedAsync(Guid projectId, bool paused,
            CancellationToken cancellationToken = default)
        {
            if (paused && failFirstPause && Interlocked.CompareExchange(ref _pauseFailures, 1, 0) == 0)
                return Task.FromException(new IOException("The durable pause write failed transiently."));
            return inner.SetProjectPausedAsync(projectId, paused, cancellationToken);
        }
        public Task RequestReindexAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            inner.RequestReindexAsync(projectId, cancellationToken);
        public Task RequestEmbeddingRefreshAsync(Guid projectId, EmbeddingPolicy targetPolicy, bool retryFailed,
            CancellationToken cancellationToken = default) =>
            inner.RequestEmbeddingRefreshAsync(projectId, targetPolicy, retryFailed, cancellationToken);
        public Task<int> RetryFailedFilesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            inner.RetryFailedFilesAsync(projectId, cancellationToken);
        public Task RemoveProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            inner.RemoveProjectAsync(projectId, cancellationToken);
        public Task<ObservationResult> ObserveFileAsync(FileObservation observation,
            CancellationToken cancellationToken = default) => inner.ObserveFileAsync(observation, cancellationToken);
        public Task HandleRenamedAsync(Guid projectId, Guid folderId, string oldPath, string newPath,
            CancellationToken cancellationToken = default) =>
            inner.HandleRenamedAsync(projectId, folderId, oldPath, newPath, cancellationToken);
        public Task HandleDeletedAsync(Guid projectId, Guid folderId, string path,
            CancellationToken cancellationToken = default) =>
            inner.HandleDeletedAsync(projectId, folderId, path, cancellationToken);
        public Task CompleteReconciliationAsync(Guid projectId, Guid folderId, string token,
            CancellationToken cancellationToken = default) =>
            inner.CompleteReconciliationAsync(projectId, folderId, token, cancellationToken);

        public async Task<IndexJobLease?> LeaseNextJobAsync(TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            var job = await inner.LeaseNextJobAsync(leaseDuration, cancellationToken);
            if (job is not null)
            {
                Interlocked.Increment(ref _leasedJobs);
                _leaseCaptured.TrySetResult();
                if (holdFirstLease && Interlocked.CompareExchange(ref _heldLease, 1, 0) == 0)
                    await _releaseLease.Task.WaitAsync(cancellationToken);
            }
            return job;
        }

        public Task<BeginRevisionResult> BeginRevisionAsync(IndexJobLease job, string sha256, long size,
            DateTimeOffset modifiedUtc, CancellationToken cancellationToken = default) =>
            inner.BeginRevisionAsync(job, sha256, size, modifiedUtc, cancellationToken);
        public Task<bool> CommitRevisionAsync(IndexCommitRequest request,
            CancellationToken cancellationToken = default) => inner.CommitRevisionAsync(request, cancellationToken);
        public Task<EmbeddingRefreshSource?> LoadEmbeddingRefreshSourceAsync(IndexJobLease job,
            CancellationToken cancellationToken = default) =>
            inner.LoadEmbeddingRefreshSourceAsync(job, cancellationToken);
        public Task<bool> CommitEmbeddingRefreshAsync(EmbeddingRefreshCommitRequest request,
            CancellationToken cancellationToken = default) =>
            inner.CommitEmbeddingRefreshAsync(request, cancellationToken);

        public async Task FailJobAsync(IndexJobLease job, string code, string message, bool retryable,
            CancellationToken cancellationToken = default)
        {
            Failures.Enqueue((job.ProjectId, code));
            await inner.FailJobAsync(job, code, message, retryable, cancellationToken);
        }
    }

    private static ExtractionResult TextExtraction(string path, string text)
    {
        var section = new ExtractedSection(text, new SourceLocation(LocationKind.Document),
            ExtractionMethod.NativeText);
        return new ExtractionResult(new ExtractedNode(Path.GetFileName(path), "text/plain", "root", [section], []),
            []);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
