using System.Collections.Concurrent;

using ContextMole.Core;
using ContextMole.Indexing;
using ContextMole.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class SourceMutationRegressionTests
{
    [Fact]
    public async Task SameSizeSameTimestampMutationDuringExtractionRequeuesAndIndexesCurrentContent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "changing.txt");
        const string originalText = "obsolete zebra";
        const string replacementText = "current quartz";
        Assert.Equal(originalText.Length, replacementText.Length);

        await File.WriteAllTextAsync(source, originalText, cancellationToken);
        var originalModifiedUtc = File.GetLastWriteTimeUtc(source);
        var originalHash = await StorageTestDatabase.HashAsync(source, cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Source mutation", cancellationToken);

        var extractor = new BlockingTextExtractor();
        var embeddings = new StorageUnavailableEmbeddings();
        var recordingWriter = new RecordingIndexWriter(database.Writer);
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(recordingWriter, database.Store, extractor, embeddings,
            new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(), budget,
            NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await extractor.FirstExtractionStarted.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            await File.WriteAllTextAsync(source, replacementText, cancellationToken);
            File.SetLastWriteTimeUtc(source, originalModifiedUtc);
            Assert.Equal(originalText.Length, new FileInfo(source).Length);
            Assert.Equal(originalModifiedUtc, File.GetLastWriteTimeUtc(source));
            var replacementHash = await StorageTestDatabase.HashAsync(source, cancellationToken);
            Assert.NotEqual(originalHash, replacementHash);

            extractor.AllowFirstExtractionToContinue();

            await WaitUntilAsync(async () =>
            {
                var project = (await database.Store.ListProjectsAsync(cancellationToken))
                    .Single(item => item.Id == projectId);
                if (project is not { IndexedCount: 1, PendingCount: 0 } || extractor.CallCount < 2)
                    return false;

                var documents = await database.Store.ListDocumentsAsync(
                    new DocumentListRequest(projectId, Limit: 10), cancellationToken);
                return documents.Documents.Count == 1 &&
                       string.Equals(documents.Documents[0].IndexedFingerprint, replacementHash,
                           StringComparison.OrdinalIgnoreCase);
            }, cancellationToken);

            var current = await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("current quartz"), 10, null, cancellationToken);
            Assert.Single(current.Candidates);
            var obsolete = await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("obsolete zebra"), 10, null, cancellationToken);
            Assert.Empty(obsolete.Candidates);
            Assert.DoesNotContain(recordingWriter.CommitAttempts, attempt =>
                attempt.Committed && string.Equals(attempt.Request.Sha256, originalHash,
                    StringComparison.OrdinalIgnoreCase));
            Assert.Contains(recordingWriter.CommitAttempts, attempt =>
                attempt.Committed && string.Equals(attempt.Request.Sha256, replacementHash,
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            extractor.AllowFirstExtractionToContinue();
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task SupportedRenameDuringExtractionSettlesOldLeaseAndIndexesNewPath()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "before-rename.txt");
        const string indexedText = "live rename recovery evidence";
        await File.WriteAllTextAsync(source, indexedText, cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Live supported rename", cancellationToken);

        var extractor = new SnapshotBlockingExtractor();
        var embeddings = new StorageUnavailableEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, extractor, embeddings,
            new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(), budget,
            NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await extractor.FirstExtractionStarted.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var renamed = Path.Combine(database.Paths.SourceDirectory, "after-rename.txt");
            File.Move(source, renamed);

            await WaitUntilAsync(async () =>
            {
                var documents = await database.Store.ListDocumentsAsync(
                    new DocumentListRequest(projectId, Limit: 10), cancellationToken);
                return documents.Documents.Count == 1 &&
                       string.Equals(documents.Documents[0].SourcePath, Path.GetFullPath(renamed),
                           StringComparison.OrdinalIgnoreCase);
            }, cancellationToken);

            extractor.AllowFirstExtractionToContinue();
            await WaitUntilAsync(async () =>
            {
                var project = (await database.Store.ListProjectsAsync(cancellationToken))
                    .Single(item => item.Id == projectId);
                return project is { IndexedCount: 1, PendingCount: 0 } && extractor.CallCount >= 2;
            }, cancellationToken);

            var result = await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("live rename recovery"), 10, null, cancellationToken);
            Assert.Equal(Path.GetFullPath(renamed), Assert.Single(result.Candidates).SourcePath);
        }
        finally
        {
            extractor.AllowFirstExtractionToContinue();
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task RenameFromSupportedToUnsupportedExtensionPromptlyRemovesIndexedDocument()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "indexed.txt");
        const string indexedText = "watcher rename evidence";
        await File.WriteAllTextAsync(source, indexedText, cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Unsupported rename", cancellationToken);

        var embeddings = new StorageUnavailableEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store,
            new ImmediateTextExtractor(), embeddings, new IndexingActivityTracker(),
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

            var unsupported = Path.ChangeExtension(source, ".bin");
            File.Move(source, unsupported);

            await WaitUntilAsync(async () =>
            {
                var project = (await database.Store.ListProjectsAsync(cancellationToken))
                    .Single(item => item.Id == projectId);
                return project is { DocumentCount: 0, IndexedCount: 0, PendingCount: 0 };
            }, cancellationToken);

            var stale = await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("watcher rename evidence"), 10, null, cancellationToken);
            Assert.Empty(stale.Candidates);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await condition())
            await Task.Delay(50, timeout.Token);
    }

    private sealed class BlockingTextExtractor : IDocumentExtractor
    {
        private readonly TaskCompletionSource _firstExtractionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _continueFirstExtraction =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;
        public Task FirstExtractionStarted => _firstExtractionStarted.Task;
        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                _firstExtractionStarted.TrySetResult();
                await _continueFirstExtraction.Task.WaitAsync(cancellationToken);
            }

            return await ExtractTextAsync(request, cancellationToken);
        }

        public void AllowFirstExtractionToContinue() => _continueFirstExtraction.TrySetResult();
    }

    private sealed class ImmediateTextExtractor : IDocumentExtractor
    {
        public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;

        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request,
            CancellationToken cancellationToken) => ExtractTextAsync(request, cancellationToken);
    }

    private sealed class SnapshotBlockingExtractor : IDocumentExtractor
    {
        private readonly TaskCompletionSource _firstExtractionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _continueFirstExtraction =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;
        public Task FirstExtractionStarted => _firstExtractionStarted.Task;
        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request,
            CancellationToken cancellationToken)
        {
            var text = await File.ReadAllTextAsync(request.SourcePath, cancellationToken);
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                _firstExtractionStarted.TrySetResult();
                await _continueFirstExtraction.Task.WaitAsync(cancellationToken);
            }

            var section = new ExtractedSection(text, new SourceLocation(LocationKind.Document),
                ExtractionMethod.NativeText);
            var root = new ExtractedNode(Path.GetFileName(request.SourcePath), "text/plain", "root", [section], []);
            return new ExtractionResult(root, []);
        }

        public void AllowFirstExtractionToContinue() => _continueFirstExtraction.TrySetResult();
    }

    private sealed class RecordingIndexWriter(IIndexWriter inner) : IIndexWriter
    {
        private readonly IIndexWriter _inner = inner;
        private readonly ConcurrentQueue<CommitAttempt> _commitAttempts = new();

        public Task Ready => _inner.Ready;
        public IReadOnlyCollection<CommitAttempt> CommitAttempts => _commitAttempts.ToArray();

        public Task<Guid> CreateProjectAsync(CreateProjectRequest request,
            CancellationToken cancellationToken = default) =>
            _inner.CreateProjectAsync(request, cancellationToken);

        public Task UpdateProjectAsync(UpdateProjectRequest request,
            CancellationToken cancellationToken = default) =>
            _inner.UpdateProjectAsync(request, cancellationToken);

        public Task SetProjectPausedAsync(Guid projectId, bool paused,
            CancellationToken cancellationToken = default) =>
            _inner.SetProjectPausedAsync(projectId, paused, cancellationToken);

        public Task RequestReindexAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            _inner.RequestReindexAsync(projectId, cancellationToken);

        public Task RequestEmbeddingRefreshAsync(Guid projectId, EmbeddingPolicy targetPolicy, bool retryFailed,
            CancellationToken cancellationToken = default) =>
            _inner.RequestEmbeddingRefreshAsync(projectId, targetPolicy, retryFailed, cancellationToken);

        public Task<int> RetryFailedFilesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            _inner.RetryFailedFilesAsync(projectId, cancellationToken);

        public Task RemoveProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            _inner.RemoveProjectAsync(projectId, cancellationToken);

        public Task<ObservationResult> ObserveFileAsync(FileObservation observation,
            CancellationToken cancellationToken = default) =>
            _inner.ObserveFileAsync(observation, cancellationToken);

        public Task HandleRenamedAsync(Guid projectId, Guid folderId, string oldPath, string newPath,
            CancellationToken cancellationToken = default) =>
            _inner.HandleRenamedAsync(projectId, folderId, oldPath, newPath, cancellationToken);

        public Task HandleDeletedAsync(Guid projectId, Guid folderId, string path,
            CancellationToken cancellationToken = default) =>
            _inner.HandleDeletedAsync(projectId, folderId, path, cancellationToken);

        public Task CompleteReconciliationAsync(Guid projectId, Guid folderId, string token,
            CancellationToken cancellationToken = default) =>
            _inner.CompleteReconciliationAsync(projectId, folderId, token, cancellationToken);

        public Task<IndexJobLease?> LeaseNextJobAsync(TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            _inner.LeaseNextJobAsync(leaseDuration, cancellationToken);

        public Task<BeginRevisionResult> BeginRevisionAsync(IndexJobLease job, string sha256, long size,
            DateTimeOffset modifiedUtc, CancellationToken cancellationToken = default) =>
            _inner.BeginRevisionAsync(job, sha256, size, modifiedUtc, cancellationToken);

        public async Task<bool> CommitRevisionAsync(IndexCommitRequest request,
            CancellationToken cancellationToken = default)
        {
            var committed = await _inner.CommitRevisionAsync(request, cancellationToken);
            _commitAttempts.Enqueue(new CommitAttempt(request, committed));
            return committed;
        }

        public Task<EmbeddingRefreshSource?> LoadEmbeddingRefreshSourceAsync(IndexJobLease job,
            CancellationToken cancellationToken = default) =>
            _inner.LoadEmbeddingRefreshSourceAsync(job, cancellationToken);

        public Task<bool> CommitEmbeddingRefreshAsync(EmbeddingRefreshCommitRequest request,
            CancellationToken cancellationToken = default) =>
            _inner.CommitEmbeddingRefreshAsync(request, cancellationToken);

        public Task FailJobAsync(IndexJobLease job, string code, string message, bool retryable,
            CancellationToken cancellationToken = default) =>
            _inner.FailJobAsync(job, code, message, retryable, cancellationToken);
    }

    private sealed record CommitAttempt(IndexCommitRequest Request, bool Committed);

    private static async Task<ExtractionResult> ExtractTextAsync(ExtractionRequest request,
        CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(request.SourcePath, cancellationToken);
        var section = new ExtractedSection(text, new SourceLocation(LocationKind.Document),
            ExtractionMethod.NativeText);
        var root = new ExtractedNode(Path.GetFileName(request.SourcePath), "text/plain", "root", [section], []);
        return new ExtractionResult(root, []);
    }
}
