using ContextMole.Core;
using ContextMole.Indexing;
using ContextMole.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class RecoveryFlowRegressionTests
{
    [Fact]
    public async Task FailedStagedReplacementPreservesLastGoodSearchableRevision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Failed replacement", cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "replacement.txt");
        const string lastGoodText = "Last good searchable lighthouse evidence.";
        await File.WriteAllTextAsync(source, lastGoodText, cancellationToken);

        var initial = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var initialModified = new DateTimeOffset(initial.File.LastWriteTimeUtc, TimeSpan.Zero);
        var committed = await database.CommitAsync(initial.Job, initial.Sha256, initial.File.Length,
            initialModified, lastGoodText, includeVector: false, cancellationToken: cancellationToken);

        const string replacementText = "A replacement revision that fails before activation.";
        await File.WriteAllTextAsync(source, replacementText, cancellationToken);
        var replacement = await database.ObserveAndLeaseAsync(projectId, folderId, source, false,
            cancellationToken);
        var replacementModified = new DateTimeOffset(replacement.File.LastWriteTimeUtc, TimeSpan.Zero);
        var staging = await database.Writer.BeginRevisionAsync(replacement.Job, replacement.Sha256,
            replacement.File.Length, replacementModified, cancellationToken);
        Assert.True(staging.ShouldExtract);
        Assert.NotNull(staging.RevisionId);

        await database.Writer.FailJobAsync(replacement.Job, "replacement_failed",
            "The replacement could not be extracted.", retryable: false, cancellationToken);

        var document = await database.Store.GetDocumentInfoAsync(projectId, committed.DocumentId, null,
            cancellationToken);
        Assert.NotNull(document);
        Assert.Equal(committed.RevisionId, document.ActiveRevisionId);
        Assert.True(document.Searchable);

        var retained = await database.Store.KeywordSearchAsync(projectId,
            TextNormalization.QuoteFtsTerms("lighthouse evidence"), 10, null, cancellationToken);
        Assert.Equal(committed.DocumentId, Assert.Single(retained.Candidates).DocumentId);
        var uncommitted = await database.Store.KeywordSearchAsync(projectId,
            TextNormalization.QuoteFtsTerms("replacement revision"), 10, null, cancellationToken);
        Assert.Empty(uncommitted.Candidates);
    }

    [Fact]
    public async Task MissingProjectRootWithQueuedReindexRetainsLastGoodContent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Offline root", cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "offline.txt");
        const string indexedText = "Offline roots retain durable compass evidence.";
        await File.WriteAllTextAsync(source, indexedText, cancellationToken);

        var initial = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var initialModified = new DateTimeOffset(initial.File.LastWriteTimeUtc, TimeSpan.Zero);
        var committed = await database.CommitAsync(initial.Job, initial.Sha256, initial.File.Length,
            initialModified, indexedText, includeVector: false, cancellationToken: cancellationToken);
        await database.Writer.RequestReindexAsync(projectId, cancellationToken);

        var offlineDirectory = Path.Combine(Path.GetDirectoryName(database.Paths.SourceDirectory)!, "source-offline");
        Directory.Move(database.Paths.SourceDirectory, offlineDirectory);

        var embeddings = new StorageUnavailableEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store,
            new NeverCalledExtractor(), embeddings, new IndexingActivityTracker(),
            new EmbeddingPolicyRefreshTracker(), budget, NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(async () =>
            {
                var errors = await database.Store.ListProjectErrorsAsync(projectId, 10, cancellationToken);
                return errors.Any(error => error.Code == "folder_unavailable");
            }, TimeSpan.FromSeconds(10), cancellationToken);

            var project = (await database.Store.ListProjectsAsync(cancellationToken))
                .Single(item => item.Id == projectId);
            Assert.Equal(1, project.DocumentCount);
            Assert.Equal(1, project.IndexedCount);
            Assert.Equal(1, project.PendingCount);

            var document = await database.Store.GetDocumentInfoAsync(projectId, committed.DocumentId, null,
                cancellationToken);
            Assert.NotNull(document);
            Assert.Equal(committed.RevisionId, document.ActiveRevisionId);

            var retained = await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("durable compass"), 10, null, cancellationToken);
            Assert.Equal(committed.DocumentId, Assert.Single(retained.Candidates).DocumentId);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task RetryableZeroContentExtractionAutomaticallyRetriesAndSucceeds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "transient.txt");
        await File.WriteAllTextAsync(source, "Source bytes for transient extraction.", cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Transient extraction", cancellationToken);

        var extractor = new EventuallySuccessfulExtractor();
        var embeddings = new StorageUnavailableEmbeddings();
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
                return extractor.CallCount >= 2 && project is { IndexedCount: 1, PendingCount: 0, ErrorCount: 0 };
            }, TimeSpan.FromSeconds(15), cancellationToken);

            Assert.Equal(2, extractor.CallCount);
            var result = await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("recovered retry evidence"), 10, null, cancellationToken);
            Assert.Single(result.Candidates);
            Assert.Empty(await database.Store.ListProjectErrorsAsync(projectId, 10, cancellationToken));
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (!await condition())
            await Task.Delay(50, deadline.Token);
    }

    private sealed class NeverCalledExtractor : IDocumentExtractor
    {
        public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;

        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An unavailable project root must not be extracted.");
    }

    private sealed class EventuallySuccessfulExtractor : IDocumentExtractor
    {
        private int _callCount;

        public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                return Task.FromResult(ExtractionResult.Failure(Path.GetFileName(request.SourcePath),
                    "transient_extraction", "The first extraction attempt failed transiently.", retryable: true));
            }

            var root = new ExtractedNode(Path.GetFileName(request.SourcePath), "text/plain", "root",
                [new ExtractedSection("Recovered retry evidence after a transient failure.",
                    new SourceLocation(LocationKind.Document), ExtractionMethod.NativeText)], []);
            return Task.FromResult(new ExtractionResult(root, []));
        }
    }
}
