using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Indexing;
using ContextMole.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class EmbeddingLifecycleRegressionTests
{
    [Fact]
    public async Task KeywordOnlyRevisionIsAutomaticallyBackfilledWhenEmbeddingsBecomeAvailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "late-model.txt");
        await File.WriteAllTextAsync(source, "Keyword content indexed before the embedding model exists.",
            cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Late embedding model", cancellationToken);

        await using var embeddings = MutableEmbeddingGenerator.Unavailable();
        var extractor = new CountingExtractor(new DocumentExtractionRegistry(new StorageNoOcr()));
        var tracker = new EmbeddingPolicyRefreshTracker();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths, extractor, embeddings,
            new IndexingActivityTracker(), tracker, budget, NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(async () =>
            {
                var project = (await database.Store.ListProjectsAsync(cancellationToken))
                    .Single(item => item.Id == projectId);
                var metadata = await database.Store.LoadVectorSnapshotMetadataAsync(projectId, cancellationToken);
                return project is { IndexedCount: 1, PendingCount: 0 } && metadata.EntryCount == 0;
            }, cancellationToken);

            var keywordResult = await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("embedding model exists"), 10, null, cancellationToken);
            Assert.Single(keywordResult.Candidates);
            Assert.Equal(1, extractor.CallCount);

            var installedPolicy = StorageTestDatabase.TestEmbeddingPolicy with
            {
                ModelId = "installed-later",
                Revision = "2"
            };
            embeddings.MakeAvailable(installedPolicy);

            await WaitForCompletePolicyAsync(database, projectId, installedPolicy, cancellationToken);

            Assert.Equal(1, extractor.CallCount);
            Assert.True(embeddings.PassageEmbeddingCallCount > 0);
            Assert.Empty(await database.Store.ListProjectErrorsAsync(projectId, 10, cancellationToken));
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PendingPolicyTrackerStillConvergesAfterAnOpenIndexJobInitiallyBlocksRefresh()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "pending-refresh.txt");
        await File.WriteAllTextAsync(source, "Persisted text can be embedded without extraction.", cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Pending policy refresh", cancellationToken);
        var initial = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var initialModified = new DateTimeOffset(initial.File.LastWriteTimeUtc, TimeSpan.Zero);
        await database.CommitAsync(initial.Job, initial.Sha256, initial.File.Length, initialModified,
            "Persisted text can be embedded without extraction.", cancellationToken: cancellationToken);

        File.SetLastWriteTimeUtc(source, initial.File.LastWriteTimeUtc.AddMinutes(1));
        var touched = new FileInfo(source);
        var observation = await database.Writer.ObserveFileAsync(new FileObservation(projectId, folderId, source,
            touched.Length, new DateTimeOffset(touched.LastWriteTimeUtc, TimeSpan.Zero)), cancellationToken);
        Assert.True(observation.Queued);

        var targetPolicy = StorageTestDatabase.TestEmbeddingPolicy with
        {
            ModelId = "pending-target",
            Revision = "2"
        };
        await using var embeddings = MutableEmbeddingGenerator.Available(targetPolicy);
        var tracker = new EmbeddingPolicyRefreshTracker();
        Assert.True(tracker.TryBeginRefresh(projectId, targetPolicy.Key));
        var extractor = new RejectingExtractor();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths, extractor, embeddings,
            new IndexingActivityTracker(), tracker, budget, NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitForCompletePolicyAsync(database, projectId, targetPolicy, cancellationToken);

            Assert.Equal(0, extractor.CallCount);
            Assert.True(embeddings.PassageEmbeddingCallCount > 0);
            var keywordResult = await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("persisted text"), 10, null, cancellationToken);
            Assert.Single(keywordResult.Candidates);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EmbeddingFailureStillCommitsKeywordContentAndBackfillsAfterRepair()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "failed-embedding.txt");
        await File.WriteAllTextAsync(source, "Keyword search survives a temporary embedding failure.",
            cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Embedding failure recovery", cancellationToken);

        var policy = StorageTestDatabase.TestEmbeddingPolicy with
        {
            ModelId = "repairable-model",
            Revision = "2"
        };
        await using var embeddings = MutableEmbeddingGenerator.Available(policy, failPassageEmbeddings: true);
        var extractor = new CountingExtractor(new DocumentExtractionRegistry(new StorageNoOcr()));
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths, extractor, embeddings,
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
                return project is { IndexedCount: 1, PendingCount: 0 } && metadata.EntryCount == 0 &&
                       embeddings.FailedPassageEmbeddingCallCount > 0;
            }, cancellationToken);

            var keywordResult = await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("temporary embedding failure"), 10, null, cancellationToken);
            Assert.Single(keywordResult.Candidates);
            Assert.Equal(1, extractor.CallCount);
            Assert.Contains(await database.Store.ListProjectErrorsAsync(projectId, 10, cancellationToken),
                error => error.Code == "embedding_refresh_failed");

            embeddings.Repair();
            await WaitForCompletePolicyAsync(database, projectId, policy, cancellationToken);

            Assert.Equal(1, extractor.CallCount);
            Assert.True(embeddings.PassageEmbeddingCallCount > 0);
            Assert.Empty(await database.Store.ListProjectErrorsAsync(projectId, 10, cancellationToken));
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    private static Task WaitForCompletePolicyAsync(
        StorageTestDatabase database,
        Guid projectId,
        EmbeddingPolicy policy,
        CancellationToken cancellationToken) =>
        WaitUntilAsync(async () =>
        {
            var project = (await database.Store.ListProjectsAsync(cancellationToken))
                .Single(item => item.Id == projectId);
            var metadata = await database.Store.LoadVectorSnapshotMetadataAsync(projectId, cancellationToken);
            return project.PendingCount == 0 && metadata is { IsComplete: true, EntryCount: > 0 } &&
                   string.Equals(metadata.Policy?.Key, policy.Key, StringComparison.Ordinal);
        }, cancellationToken);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (!await condition())
            await Task.Delay(50, timeout.Token);
    }

    private sealed class CountingExtractor(IDocumentExtractor inner) : IDocumentExtractor
    {
        private int _callCount;

        public IReadOnlyCollection<string> Extensions => inner.Extensions;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return inner.ExtractAsync(request, cancellationToken);
        }
    }

    private sealed class RejectingExtractor : IDocumentExtractor
    {
        private int _callCount;

        public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            throw new InvalidOperationException("Embedding refresh must reuse persisted passages.");
        }
    }

    private sealed class MutableEmbeddingGenerator : IEmbeddingGenerator
    {
        private readonly object _gate = new();
        private EmbeddingPolicy? _policy;
        private bool _available;
        private bool _failPassageEmbeddings;
        private int _passageEmbeddingCallCount;
        private int _failedPassageEmbeddingCallCount;

        private MutableEmbeddingGenerator(
            EmbeddingPolicy? policy,
            bool available,
            bool failPassageEmbeddings)
        {
            _policy = policy;
            _available = available;
            _failPassageEmbeddings = failPassageEmbeddings;
        }

        public static MutableEmbeddingGenerator Unavailable() => new(null, false, false);

        public static MutableEmbeddingGenerator Available(
            EmbeddingPolicy policy,
            bool failPassageEmbeddings = false) => new(policy, true, failPassageEmbeddings);

        public bool IsAvailable
        {
            get { lock (_gate) return _available; }
        }

        public string? UnavailableReason => IsAvailable ? null : "The test embedding model is not installed.";

        public EmbeddingPolicy? Policy
        {
            get { lock (_gate) return _policy; }
        }

        public int PassageEmbeddingCallCount => Volatile.Read(ref _passageEmbeddingCallCount);
        public int FailedPassageEmbeddingCallCount => Volatile.Read(ref _failedPassageEmbeddingCallCount);

        public void MakeAvailable(EmbeddingPolicy policy)
        {
            lock (_gate)
            {
                _policy = policy;
                _available = true;
                _failPassageEmbeddings = false;
            }
        }

        public void Repair()
        {
            lock (_gate)
            {
                _available = true;
                _failPassageEmbeddings = false;
            }
        }

        public Task ReloadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public int CountTokens(string text) =>
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        public Task<EmbeddingBatch> EmbedPassagesAsync(
            IReadOnlyList<string> passages,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EmbeddingPolicy policy;
            lock (_gate)
            {
                if (!_available || _policy is null)
                    throw new ContextMoleException("model_unavailable", UnavailableReason!, true);
                if (_failPassageEmbeddings)
                {
                    Interlocked.Increment(ref _failedPassageEmbeddingCallCount);
                    throw new ContextMoleException("model_output_invalid",
                        "The test embedding model failed during inference.");
                }
                policy = _policy;
            }

            Interlocked.Increment(ref _passageEmbeddingCallCount);
            var vectors = passages.Select((_, index) => StorageTestDatabase.TestVector(index % 384)).ToArray();
            return Task.FromResult(new EmbeddingBatch(vectors, policy));
        }

        public async Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken)
        {
            var batch = await EmbedPassagesAsync([query], cancellationToken);
            return new QueryEmbedding(batch.Vectors[0], batch.Policy);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
