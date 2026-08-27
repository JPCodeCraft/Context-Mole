using MCPIndexSearch.Core;
using MCPIndexSearch.Documents;
using MCPIndexSearch.Indexing;
using MCPIndexSearch.Infrastructure;
using MCPIndexSearch.Mcp;
using MCPIndexSearch.Search;
using MCPIndexSearch.Storage;

using Microsoft.Extensions.Logging.Abstractions;

namespace MCPIndexSearch.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class IndexingPipelineTests
{
    [Fact]
    public async Task CoordinatorDiscoversExtractsAndIndexesAFileWithoutChangingIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "pipeline.txt");
        const string evidence = "Pipeline evidence with café and contrato.";
        await File.WriteAllTextAsync(source, evidence, cancellationToken);
        var originalHash = await StorageTestDatabase.HashAsync(source, cancellationToken);
        var originalModified = File.GetLastWriteTimeUtc(source);
        var (projectId, _) = await database.CreateProjectAsync("Pipeline", cancellationToken);

        var embeddings = new StorageUnavailableEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        var activities = new IndexingActivityTracker();
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store,
            new DocumentExtractionRegistry(new StorageNoOcr()), embeddings, activities,
            new EmbeddingPolicyRefreshTracker(), budget, NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(async () =>
            {
                var project = (await database.Store.ListProjectsAsync(cancellationToken))
                    .Single(item => item.Id == projectId);
                return project is { IndexedCount: 1, PendingCount: 0 };
            }, cancellationToken);

            var result = await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("café contrato"), 10, null, cancellationToken);
            Assert.Equal(Path.GetFullPath(source), Assert.Single(result.Candidates).SourcePath);
            Assert.Equal(originalHash, await StorageTestDatabase.HashAsync(source, cancellationToken));
            Assert.Equal(originalModified, File.GetLastWriteTimeUtc(source));
            var timing = activities.GetSnapshot(projectId);
            Assert.Empty(timing.ActiveItems);
            Assert.Equal(1, timing.CompletedSampleCount);
            Assert.NotNull(timing.AverageCompletedDuration);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task CoordinatorRefreshesEmbeddingsFromStorageWithoutRunningExtraction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Stored embedding refresh", cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "stored.txt");
        await File.WriteAllTextAsync(source, "Persisted passage text.", cancellationToken);
        var pending = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var modified = new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero);
        var committed = await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length, modified,
            "Persisted passage text.", cancellationToken: cancellationToken);

        var targetPolicy = StorageTestDatabase.TestEmbeddingPolicy with { ModelId = "coordinator-target" };
        await database.Writer.RequestEmbeddingRefreshAsync(projectId, targetPolicy, retryFailed: false,
            cancellationToken);
        var extractor = new RejectingExtractor();
        var embeddings = new FixedEmbeddingGenerator(targetPolicy);
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, extractor, embeddings,
            new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(), budget,
            NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(async () =>
            {
                var project = (await database.Store.ListProjectsAsync(cancellationToken))
                    .Single(item => item.Id == projectId);
                var metadata = await database.Store.LoadVectorSnapshotMetadataAsync(projectId, cancellationToken);
                return project.PendingCount == 0 && metadata.IsComplete && metadata.Policy?.Key == targetPolicy.Key;
            }, cancellationToken);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }

        Assert.Equal(0, extractor.CallCount);
        var document = await database.Store.GetDocumentInfoAsync(projectId, committed.DocumentId, null,
            cancellationToken);
        Assert.Equal(committed.RevisionId, document?.ActiveRevisionId);
        var passage = Assert.Single(await database.Store.ReadPassagesAsync(projectId, [committed.PassageId], 0, 0,
            cancellationToken));
        Assert.Equal("Persisted passage text.", passage.Text);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await condition())
            await Task.Delay(50, timeout.Token);
    }

    private sealed class RejectingExtractor : IDocumentExtractor
    {
        public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;
        public int CallCount { get; private set; }

        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Embedding refresh must not invoke document extraction.");
        }
    }

    private sealed class FixedEmbeddingGenerator(EmbeddingPolicy policy) : IEmbeddingGenerator
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public EmbeddingPolicy Policy { get; } = policy;
        EmbeddingPolicy? IEmbeddingGenerator.Policy => Policy;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public int CountTokens(string text) => text.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries).Length;

        public Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages,
            CancellationToken cancellationToken) => Task.FromResult(new EmbeddingBatch(
            passages.Select((_, index) => StorageTestDatabase.TestVector(index % 384)).ToArray(), Policy));

        public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult(new QueryEmbedding(StorageTestDatabase.TestVector(), Policy));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class IndexingStateTests
{
    [Fact]
    public void ActivityTrackerReportsStagesAndAveragesOnlySuccessfulCompletions()
    {
        var tracker = new IndexingActivityTracker();
        var projectId = Guid.CreateVersion7();
        var first = NewJob(projectId, "first.txt");
        using (var activity = tracker.Start(first))
        {
            activity.SetStage(IndexingPipelineStage.GeneratingEmbeddings);
            var active = Assert.Single(tracker.GetSnapshot(projectId).ActiveItems);
            Assert.Equal(IndexingPipelineStage.GeneratingEmbeddings, active.Stage);
            activity.Complete(includeInAverage: true);
        }

        using (tracker.Start(NewJob(projectId, "failed.txt")))
        {
        }

        var completed = tracker.GetSnapshot(projectId);
        Assert.Empty(completed.ActiveItems);
        Assert.Equal(1, completed.CompletedSampleCount);
        Assert.NotNull(completed.AverageCompletedDuration);
        Assert.False(tracker.HasActiveItems);
    }

    [Fact]
    public async Task EmbeddingPolicyRefreshIsPerPolicyRetryableAndSerialized()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tracker = new EmbeddingPolicyRefreshTracker();
        var projectId = Guid.CreateVersion7();
        Assert.True(tracker.TryBeginRefresh(projectId, "policy-a"));
        Assert.False(tracker.TryBeginRefresh(projectId, "policy-a"));
        Assert.True(tracker.TryBeginRefresh(projectId, "policy-b"));
        tracker.CancelRefresh(projectId, "policy-b");
        Assert.True(tracker.TryBeginRefresh(projectId, "policy-b"));
        Assert.True(tracker.IsRefreshPending(projectId, "policy-b"));
        tracker.Clear();
        Assert.False(tracker.IsRefreshPending(projectId, "policy-b"));
        Assert.True(tracker.TryBeginRefresh(projectId, "policy-b"));

        var active = 0;
        var maximum = 0;
        var maximumGate = new object();
        async Task Work()
        {
            await tracker.RunExclusiveAsync(async () =>
            {
                var current = Interlocked.Increment(ref active);
                lock (maximumGate)
                    maximum = Math.Max(maximum, current);
                try
                {
                    await Task.Delay(40, cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }, cancellationToken);
        }

        await Task.WhenAll(Work(), Work(), Work());
        Assert.Equal(1, maximum);
    }

    private static IndexJobLease NewJob(Guid projectId, string path) =>
        new(Guid.CreateVersion7(), projectId, Guid.CreateVersion7(), Guid.CreateVersion7(), path, ".txt", 1,
            IndexJobKind.Index, 0);
}

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class McpBoundaryTests
{
    [Fact]
    public async Task UninitializedIndexReturnsStructuredPublicError()
    {
        using var paths = new StorageTestPaths();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        var tools = CreateTools(new SqliteSearchStore(paths), paths, budget);

        var result = await tools.ListProjects(CancellationToken.None);
        var envelope = Assert.IsType<ErrorEnvelope>(result);
        Assert.Equal("not_initialized", envelope.Error.Code);
        Assert.False(envelope.Error.Retryable);
    }

    [Fact]
    public async Task InvalidSearchInputIsRejectedBeforeSearchExecution()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("MCP boundary", cancellationToken);
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        var tools = CreateTools(database.Store, database.Paths, budget);

        var empty = Assert.IsType<ErrorEnvelope>(await tools.SearchProject(projectId, "   ",
            cancellationToken: cancellationToken));
        Assert.Equal("invalid_request", empty.Error.Code);
        var invalidRange = Assert.IsType<ErrorEnvelope>(await tools.SearchProject(projectId, "query", filters:
            new McpSearchFilters(ModifiedFromUtc: DateTimeOffset.UtcNow,
                ModifiedToUtc: DateTimeOffset.UtcNow.AddDays(-1)), cancellationToken: cancellationToken));
        Assert.Equal("invalid_filter", invalidRange.Error.Code);
    }

    private static McpTools CreateTools(ISearchStore store, IAppPaths paths, IGlobalCpuBudget budget)
    {
        var embeddings = new StorageUnavailableEmbeddings();
        var search = new HybridSearchService(store, embeddings, new FlatVectorIndexFactory(),
            new VectorIndexCache(), budget);
        return new McpTools(store, search, new UnusedMaterializer(), paths, NullLogger<McpTools>.Instance);
    }

    private sealed class UnusedMaterializer : IContentMaterializer
    {
        public Task<MaterializedContent> MaterializeAsync(Guid projectId, Guid contentId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Materialization should not run in an input validation test.");
    }
}