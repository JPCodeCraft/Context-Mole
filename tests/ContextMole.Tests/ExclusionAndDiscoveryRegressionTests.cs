using System.Text;
using System.Reflection;

using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Indexing;
using ContextMole.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class ExclusionAndDiscoveryRegressionTests
{
    [Fact]
    public void InitialDiscoveryCannotBeReadyBeforeEveryCurrentFolderSettles()
    {
        var projectId = Guid.NewGuid();
        var first = new ProjectFolderInfo(Guid.NewGuid(), "/source/first");
        var second = new ProjectFolderInfo(Guid.NewGuid(), "/source/second");
        var summary = new ProjectSummary(projectId, "Discovery", ProjectState.Active, [first], 0, 0, 0, 0, 0, null);
        var tracker = new IndexingActivityTracker();
        Assert.False(tracker.IsInitialScanComplete(projectId));
        tracker.RetainProjects([summary]);
        Assert.False(tracker.IsInitialScanComplete(projectId));
        tracker.SetDiscovering(projectId, true);
        tracker.SetDiscovering(projectId, false);
        Assert.False(tracker.IsInitialScanComplete(projectId), "A cancelled scan must not settle first discovery.");
        tracker.CompleteInitialScan(projectId, first.Id);
        Assert.True(tracker.IsInitialScanComplete(projectId));
        Assert.False(tracker.IsInitialScanComplete(projectId, [first, second]),
            "A newly saved folder must be unverified even before the coordinator refresh tick.");
        tracker.RetainProjects([summary with { Folders = [first, second] }]);
        Assert.False(tracker.IsInitialScanComplete(projectId));
        tracker.SetFolderIssue(projectId, second.Id, second.Path, "Folder unavailable; retained evidence is unverified.");
        tracker.CompleteInitialScan(projectId, second.Id);
        Assert.True(tracker.IsInitialScanComplete(projectId));
        Assert.Single(tracker.GetFolderIssues(projectId));
        tracker.RetainProjects([]);
        Assert.False(tracker.IsInitialScanComplete(projectId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcludingAnActiveExactPathCancelsItAndIncludeIndexesFreshSource(bool ignoresCancellation)
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(token);
        var selected = Path.Combine(database.Paths.SourceDirectory, "selected.txt");
        var similar = Path.Combine(database.Paths.SourceDirectory, "selected-copy.txt");
        await File.WriteAllTextAsync(selected, "beforeexclude", token);
        await File.WriteAllTextAsync(similar, "similarfileevidence", token);
        var (projectId, _) = await database.CreateProjectAsync("Exact exclusion", token);
        var extractor = new BlockingTextExtractor(selected, ignoresCancellation);
        await using var embeddings = new StorageUnavailableEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        var activities = new IndexingActivityTracker();
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths, extractor,
            embeddings, activities, new EmbeddingPolicyRefreshTracker(), budget,
            NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(token);
        try
        {
            await extractor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            var result = await coordinator.ExcludeFileAsync(projectId, selected, token);
            Assert.True(result.Changed);
            await extractor.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.True(await database.Store.IsFileExcludedAsync(projectId, selected, token));
            Assert.False(await database.Store.IsFileExcludedAsync(projectId, similar, token));
            Assert.Equal("beforeexclude", await File.ReadAllTextAsync(selected, token));
            Assert.Empty((await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("beforeexclude"), 10, null, token)).Candidates);
            Assert.Empty(await database.Store.ListProjectErrorsAsync(projectId, 100, token));

            await File.WriteAllTextAsync(selected, "freshincludeevidence", token);
            await database.Writer.RequestReindexAsync(projectId, token);
            await database.Writer.RetryFailedFilesAsync(projectId, token);
            await WaitUntilAsync(async () => (await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("similarfileevidence"), 10, null, token)).Candidates.Count == 1, token);
            Assert.Equal(1, extractor.SelectedCalls);
            Assert.Empty((await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("freshincludeevidence"), 10, null, token)).Candidates);

            extractor.BlockSelected = false;
            var include = coordinator.IncludeFileAsync(projectId, selected, token);
            if (ignoresCancellation)
            {
                await Task.Delay(100, token);
                Assert.False(include.IsCompleted, "Include must not reopen processing before the cancelled parser drains.");
            }
            extractor.Release.TrySetResult();
            var included = await include.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.True(included.Changed);
            Assert.True(included.Queued);
            await WaitUntilAsync(async () => (await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("freshincludeevidence"), 10, null, token)).Candidates.Count == 1, token);
            Assert.Equal(2, extractor.SelectedCalls);
            Assert.False(await database.Store.IsFileExcludedAsync(projectId, selected, token));
            Assert.Empty(await database.Store.ListProjectErrorsAsync(projectId, 100, token));
            Assert.Equal("freshincludeevidence", await File.ReadAllTextAsync(selected, token));
        }
        finally
        {
            extractor.Release.TrySetResult();
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExcludingDuringEmbeddingCancelsSemanticWorkWithoutPublishingOrAddingAnIssue()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(token);
        var source = Path.Combine(database.Paths.SourceDirectory, "semantic.txt");
        await File.WriteAllTextAsync(source, "semanticexclusiveevidence", token);
        var (projectId, _) = await database.CreateProjectAsync("Embedding exclusion", token);
        var extractor = new BlockingTextExtractor(source, false) { BlockSelected = false };
        await using var embeddings = new BlockingEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        var activities = new IndexingActivityTracker();
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths, extractor,
            embeddings, activities, new EmbeddingPolicyRefreshTracker(), budget,
            NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(token);
        try
        {
            await embeddings.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            await coordinator.ExcludeFileAsync(projectId, source, token);
            await embeddings.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            await WaitUntilAsync(() => Task.FromResult(activities.GetSnapshot(projectId).ActiveItems.Count == 0), token);
            await database.Writer.RequestEmbeddingRefreshAsync(projectId, embeddings.Policy, true, token);
            Assert.Empty((await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("semanticexclusiveevidence"), 10, null, token)).Candidates);
            Assert.Empty(await database.Store.ListProjectErrorsAsync(projectId, 100, token));
            Assert.Equal(0, (await database.Store.ListProjectsAsync(token)).Single().PendingCount);
            Assert.Equal("semanticexclusiveevidence", await File.ReadAllTextAsync(source, token));
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ExclusionCancellationIsScopedToTheProjectEvenForTheSameSourcePath()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(token);
        var source = Path.Combine(database.Paths.SourceDirectory, "shared.txt");
        await File.WriteAllTextAsync(source, "sharedsourceevidence", token);
        var (firstProject, _) = await database.CreateProjectAsync("First scope", token);
        var (secondProject, _) = await database.CreateProjectAsync("Second scope", token);
        var extractor = new TwoOperationExtractor();
        await using var embeddings = new StorageUnavailableEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        var activities = new IndexingActivityTracker();
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths, extractor,
            embeddings, activities, new EmbeddingPolicyRefreshTracker(), budget,
            NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(token);
        try
        {
            await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref extractor.Calls) == 2), token);
            await coordinator.ExcludeFileAsync(firstProject, source, token);
            await WaitUntilAsync(() => Task.FromResult(activities.GetSnapshot(firstProject).ActiveItems.Count == 0), token);
            Assert.Equal(1, Volatile.Read(ref extractor.Cancellations));
            Assert.Single(activities.GetSnapshot(secondProject).ActiveItems);
            Assert.True(await database.Store.IsFileExcludedAsync(firstProject, source, token));
            Assert.False(await database.Store.IsFileExcludedAsync(secondProject, source, token));
            Assert.Equal("sharedsourceevidence", await File.ReadAllTextAsync(source, token));
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task EmptyFolderCompletesDiscoveryOnlyAfterItsFirstReconciliation()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(token);
        var (projectId, _) = await database.CreateProjectAsync("Empty first scan", token);
        var activities = new IndexingActivityTracker();
        Assert.False(activities.IsInitialScanComplete(projectId));
        await using var embeddings = new StorageUnavailableEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        var extractor = new BlockingTextExtractor(Path.Combine(database.Paths.SourceDirectory, "unused.txt"), false);
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths, extractor,
            embeddings, activities, new EmbeddingPolicyRefreshTracker(), budget,
            NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(token);
        try
        {
            await WaitUntilAsync(() => Task.FromResult(activities.IsInitialScanComplete(projectId)), token);
            Assert.False(activities.IsDiscovering(projectId));
            Assert.Empty(activities.GetFolderIssues(projectId));
            Assert.Equal(0, (await database.Store.ListProjectsAsync(token)).Single().DocumentCount);
            Assert.Equal(0, extractor.SelectedCalls);
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task SameNamedBrokenAttachmentsRetainDistinctStructuralIdentity()
    {
        using var paths = new StorageTestPaths();
        var source = Path.Combine(paths.SourceDirectory, "duplicate-names.eml");
        await File.WriteAllTextAsync(source, MultipartEmail([
            ("same.json", "application/json", "{ invalid-one"),
            ("same.json", "application/json", "{ invalid-two")]), TestContext.Current.CancellationToken);
        var extraction = await new DocumentExtractionRegistry(new StorageNoOcr())
            .ExtractAsync(new ExtractionRequest(source), TestContext.Current.CancellationToken);
        Assert.Equal(2, extraction.Errors.Count);
        Assert.Equal(2, extraction.Errors.Select(error => error.ComponentKey).Distinct().Count());
        Assert.All(extraction.Errors, error => Assert.StartsWith("root/email-attachment", error.ComponentKey!,
            StringComparison.OrdinalIgnoreCase));
        var bound = IndexingCoordinator.BindExtractionErrors(extraction.Root, Guid.NewGuid(), extraction.Errors);
        Assert.All(bound, error => Assert.NotNull(error.ContentId));
        Assert.Equal(2, bound.Select(error => error.ContentId).Distinct().Count());
        Assert.Contains(bound, error => error.ItemName!.Contains("same.json (item 1)", StringComparison.Ordinal));
        Assert.Contains(bound, error => error.ItemName!.Contains("same.json (item 2)", StringComparison.Ordinal));
    }

    [Fact]
    public void PageAndFrameFailuresHaveExactLocationWithoutChangingTheirContentOwner()
    {
        var root = ExtractedNode.Empty("scanned.pdf");
        var bound = IndexingCoordinator.BindExtractionErrors(root, Guid.NewGuid(), [
            new ExtractionError("ocr_timeout", "Page timed out", true, root.Name) { ComponentKey = "root/page:2" },
            new ExtractionError("ocr_timeout", "Frame timed out", true, root.Name) { ComponentKey = "root/frame:3" }]);
        Assert.NotNull(bound[0].ContentId);
        Assert.Equal(bound[0].ContentId, bound[1].ContentId);
        Assert.Equal("root/page:2", bound[0].ComponentKey);
        Assert.Contains("page 2", bound[0].ItemName);
        Assert.Contains("frame 3", bound[1].ItemName);
    }

    [Fact]
    public async Task AnArchiveEntryStreamReadFailureKeepsItsAllocatedComponentIdentity()
    {
        // Inject a synthetic entry stream into the existing private container pipeline. No native
        // parser, ZIP corruption guess, real user file, or external service is needed.
        var registry = new DocumentExtractionRegistry(new StorageNoOcr());
        var contextType = typeof(DocumentExtractionRegistry).GetNestedType("ExpansionContext", BindingFlags.NonPublic)!;
        var constructor = contextType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var context = constructor.Invoke([new ExtractionRequest("synthetic.zip")]);
        using var rootScope = (IDisposable)contextType.GetMethod("EnterComponent")!
            .Invoke(context, ["synthetic.zip", "root"])!;
        using var stream = new FailingReadStream();
        var method = typeof(DocumentExtractionRegistry).GetMethod("ExtractStreamAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var child = await (Task<ExtractedNode>)method.Invoke(registry,
            [stream, "broken.json", "application/json", "archive-entry", 1, context, TestContext.Current.CancellationToken])!;
        var errors = (IReadOnlyList<ExtractionError>)contextType.GetProperty("Errors")!.GetValue(context)!;
        var error = Assert.Single(errors);
        Assert.Equal("io_error", error.Code);
        Assert.EndsWith("\u001f0", error.ComponentKey);
        var root = new ExtractedNode("synthetic.zip", "application/zip", "root", [], [child]);
        var bound = Assert.Single(IndexingCoordinator.BindExtractionErrors(root, Guid.NewGuid(), errors));
        Assert.NotNull(bound.ContentId);
        Assert.Equal("io_error", child.Status);
        Assert.Contains("broken.json (item 1)", bound.ItemName);
        Assert.DoesNotContain("item 2", bound.ItemName);
    }

    [Fact]
    public async Task UnsupportedAttachmentsAreIntentionalCoverageSkipsWithoutFailureFlood()
    {
        using var paths = new StorageTestPaths();
        var source = Path.Combine(paths.SourceDirectory, "unsupported.eml");
        await File.WriteAllTextAsync(source, MultipartEmail(Enumerable.Range(1, 120)
            .Select(index => ($"binary-{index}.bin", "application/octet-stream", $"binary data {index}"))
            .ToArray()), TestContext.Current.CancellationToken);
        var extraction = await new DocumentExtractionRegistry(new StorageNoOcr())
            .ExtractAsync(new ExtractionRequest(source), TestContext.Current.CancellationToken);
        Assert.Empty(extraction.Errors);
        Assert.Equal(120, extraction.Root.Attachments.Count);
        Assert.All(extraction.Root.Attachments, attachment => Assert.Equal("unsupported_format", attachment.Status));
        Assert.Contains(extraction.Root.Sections, section => section.Text.Contains("Searchable email body", StringComparison.Ordinal));
    }

    private static string MultipartEmail(IReadOnlyList<(string Name, string Mime, string Text)> attachments)
    {
        var result = new StringBuilder("From: sender@example.test\r\nSubject: Attachment identity\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"contextmole-test\"\r\n\r\n--contextmole-test\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nSearchable email body\r\n");
        foreach (var attachment in attachments)
            result.Append($"--contextmole-test\r\nContent-Type: {attachment.Mime}; name=\"{attachment.Name}\"\r\nContent-Disposition: attachment; filename=\"{attachment.Name}\"\r\nContent-Transfer-Encoding: base64\r\n\r\n{Convert.ToBase64String(Encoding.UTF8.GetBytes(attachment.Text))}\r\n");
        return result.Append("--contextmole-test--\r\n").ToString();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The isolated indexing condition did not settle in time.");
            await Task.Delay(50, token);
        }
    }

    private sealed class BlockingTextExtractor(string selectedPath, bool ignoresCancellation) : IDocumentExtractor
    {
        public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool BlockSelected = true;
        public int SelectedCalls;

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken token)
        {
            if (ProjectValidation.FolderKey(request.SourcePath) == ProjectValidation.FolderKey(selectedPath))
            {
                Interlocked.Increment(ref SelectedCalls);
                if (BlockSelected)
                {
                    Entered.TrySetResult();
                    using var cancellation = token.Register(() => Canceled.TrySetResult());
                    if (ignoresCancellation) await Release.Task;
                    else
                    {
                        try { await Release.Task.WaitAsync(token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            Canceled.TrySetResult();
                            throw;
                        }
                    }
                }
            }
            var text = await File.ReadAllTextAsync(request.SourcePath,
                ignoresCancellation ? CancellationToken.None : token);
            return new ExtractionResult(new ExtractedNode(Path.GetFileName(request.SourcePath), "text/plain", "root",
                [new ExtractedSection(text, new SourceLocation(LocationKind.Document), ExtractionMethod.NativeText)], []), []);
        }
    }

    private sealed class BlockingEmbeddings : IEmbeddingGenerator
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public EmbeddingPolicy Policy => StorageTestDatabase.TestEmbeddingPolicy;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        public async Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken token)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { Canceled.TrySetResult(); throw; }
            return new EmbeddingBatch([], Policy);
        }
        public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No model query is needed by this isolated test.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TwoOperationExtractor : IDocumentExtractor
    {
        public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;
        public int Calls;
        public int Cancellations;
        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref Cancellations); throw; }
            throw new InvalidOperationException("This extractor only tests cancellation isolation.");
        }
    }

    private sealed class FailingReadStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Synthetic archive entry read failed."));
    }
}
