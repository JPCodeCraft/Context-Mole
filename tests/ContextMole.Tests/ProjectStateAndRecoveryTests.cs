using ContextMole.Core;
using ContextMole.Indexing;
using ContextMole.Infrastructure;
using ContextMole.Storage;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class ProjectStateAndRecoveryTests
{
    [Fact]
    public async Task EmptyFilesAreUpToDateButNotCountedAsSearchableText()
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = await StorageTestDatabase.CreateAsync(token);
        var (project, folder) = await db.CreateProjectAsync("Empty files", token);
        var path = Path.Combine(db.Paths.SourceDirectory, "empty.txt");
        await File.WriteAllTextAsync(path, string.Empty, token);
        var source = await db.ObserveAndLeaseAsync(project, folder, path, false, token);
        var modified = new DateTimeOffset(source.File.LastWriteTimeUtc, TimeSpan.Zero);
        var begin = await db.Writer.BeginRevisionAsync(source.Job, source.Sha256, 0, modified, token);
        Assert.True(await db.Writer.CommitRevisionAsync(new(source.Job.JobId, project, source.Job.DocumentId,
            begin.RevisionId!.Value, source.Job.ExpectedObservationEpoch, source.Sha256, 0, modified,
            [new ContentNodeDraft(Guid.NewGuid(), null, 0, "empty.txt", "text/plain", "root", 0)], [], null, []), token));
        var summary = Assert.Single(await db.Store.ListProjectsAsync(token));
        Assert.Equal(1, summary.ReadyCount);
        Assert.Equal(1, summary.IndexedCount);
        Assert.Equal(0, summary.SearchableCount);
        Assert.Equal(0, summary.AttentionCount);
        Assert.False((await db.Store.GetDocumentInfoAsync(project, source.Job.DocumentId, null, token))!.Searchable);
    }

    [Fact]
    public async Task MovingAPopulatedFolderIntoWatchedRootPromptlyDiscoversEveryFile()
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = await StorageTestDatabase.CreateAsync(token);
        var (project, _) = await db.CreateProjectAsync("Moved folder", token);
        var incoming = Path.Combine(Path.GetDirectoryName(db.Paths.SourceDirectory)!, "incoming");
        Directory.CreateDirectory(Path.Combine(incoming, "nested"));
        await File.WriteAllTextAsync(Path.Combine(incoming, "first.txt"), "First file.", token);
        await File.WriteAllTextAsync(Path.Combine(incoming, "nested", "second.txt"), "Second file.", token);
        using var cpu = new GlobalCpuBudget(new StorageFixedCpuSettings());
        await using var embeddings = new StorageUnavailableEmbeddings();
        using var coordinator = new IndexingCoordinator(db.Writer, db.Store, db.Paths,
            new SimpleTextExtractor(), embeddings, new IndexingActivityTracker(),
            new EmbeddingPolicyRefreshTracker(), cpu, NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(token);
        try
        {
            // Wait until startup has installed the watcher; only the later directory event can
            // discover the move after initial reconciliation finishes.
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            var moved = Path.Combine(db.Paths.SourceDirectory, "moved.pdf");
            Directory.Move(incoming, moved);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (true)
            {
                var summary = Assert.Single(await db.Store.ListProjectsAsync(deadline.Token));
                if (summary is { ReadyCount: 2, PendingCount: 0 }) break;
                await Task.Delay(100, deadline.Token);
            }
            var inventory = await db.Store.ListDocumentsAsync(new(project), token);
            Assert.Equal(2, inventory.Documents.Count);
            Assert.All(inventory.Documents, item => Assert.Equal(DocumentInventoryStatus.Indexed, item.Status));

            var renamed = Path.Combine(db.Paths.SourceDirectory, "renamed.pdf");
            Directory.Move(moved, renamed);
            while (true)
            {
                inventory = await db.Store.ListDocumentsAsync(new(project), deadline.Token);
                if (inventory.Documents.Count == 2 && inventory.Documents.All(item =>
                        item.SourcePath.StartsWith(renamed, StringComparison.OrdinalIgnoreCase) &&
                        item.Status == DocumentInventoryStatus.Indexed)) break;
                await Task.Delay(100, deadline.Token);
            }
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task FileBucketsAreDisjointWhileSearchableRevisionsOverlapPendingWork()
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = await StorageTestDatabase.CreateAsync(token);
        var (project, folder) = await db.CreateProjectAsync("Current file state", token);
        var clean = await IndexAsync(db, project, folder, "clean.txt", [], token);
        await IndexAsync(db, project, folder, "partial.txt",
            [new("page_failed", "Page 1 failed.", true), new("page_failed", "Page 2 failed.", true)], token);
        var queued = Path.Combine(db.Paths.SourceDirectory, "queued.txt");
        await File.WriteAllTextAsync(queued, "Waiting to be indexed.", token);
        await db.Writer.ObserveFileAsync(new(project, folder, queued, new FileInfo(queued).Length,
            new DateTimeOffset(File.GetLastWriteTimeUtc(queued), TimeSpan.Zero)), token);

        var summary = Assert.Single(await db.Store.ListProjectsAsync(token));
        Assert.Equal(3, summary.DocumentCount);
        Assert.Equal(1, summary.ReadyCount);
        Assert.Equal(1, summary.PendingCount);
        Assert.Equal(1, summary.AttentionCount);
        Assert.Equal(2, summary.SearchableCount);
        Assert.Equal(2, summary.ErrorCount);
        Assert.Equal(1, summary.ErrorFileCount);
        AssertBuckets(summary);

        await db.Writer.RequestReindexAsync(project, token);
        summary = Assert.Single(await db.Store.ListProjectsAsync(token));
        Assert.Equal(3, summary.PendingCount);
        Assert.Equal(0, summary.ReadyCount);
        Assert.Equal(0, summary.AttentionCount);
        Assert.Equal(2, summary.SearchableCount);
        AssertBuckets(summary);
        Assert.Equal(db.Paths.SourceDirectory, await db.Store.GetProjectFolderPathAsync(project, folder, token));
        Assert.Null(await db.Store.GetProjectFolderPathAsync(Guid.NewGuid(), folder, token));
    }

    [Fact]
    public async Task MoreThanOneThousandCurrentFileErrorsAreRetainedAndCanBePaged()
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = await StorageTestDatabase.CreateAsync(token);
        var (project, folder) = await db.CreateProjectAsync("Many issues", token);
        await using (var connection = new SqliteConnection($"Data Source={db.Paths.DatabasePath}"))
        {
            await connection.OpenAsync(token);
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<1001)
                INSERT INTO documents(id,project_id,folder_id,path,path_key,file_name,extension,size,modified_utc,created_utc,updated_utc)
                SELECT printf('00000000-0000-0000-0000-%012d',n),$project,$folder,
                  'file-'||n,'FILE-'||n,'file-'||n,'.txt',1,$now,$now,$now FROM numbers;
                INSERT INTO project_errors(project_id,document_id,code,message,retryable,attempt,source_path,created_utc)
                SELECT project_id,id,'unreadable','Needs attention.',0,1,path,$now FROM documents;
                """;
            seed.Parameters.AddWithValue("$project", project.ToString());
            seed.Parameters.AddWithValue("$folder", folder.ToString());
            seed.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await seed.ExecuteNonQueryAsync(token);
        }
        var path = Path.Combine(db.Paths.SourceDirectory, "latest.txt");
        await File.WriteAllTextAsync(path, "latest failed file", token);
        var latest = await db.ObserveAndLeaseAsync(project, folder, path, false, token);
        await db.Writer.FailJobAsync(latest.Job, "unreadable", "Latest issue.", false, token);

        var summary = Assert.Single(await db.Store.ListProjectsAsync(token));
        Assert.Equal(1002, summary.ErrorCount);
        Assert.Equal(1002, summary.ErrorFileCount);
        Assert.Equal(1002, summary.AttentionCount);
        AssertBuckets(summary);
        var first = await db.Store.ListProjectErrorsAsync(project, 25, token);
        var second = await db.Store.ListProjectErrorsAsync(project, 25, token, offset: 25);
        var last = await db.Store.ListProjectErrorsAsync(project, 25, token, offset: 1000);
        Assert.Equal(25, first.Count);
        Assert.Equal(25, second.Count);
        Assert.Equal(2, last.Count);
        Assert.Empty(first.Select(error => error.Id).Intersect(second.Select(error => error.Id)));
        Assert.Equal(latest.Job.DocumentId, first[0].DocumentId);
    }

    [Fact]
    public async Task ANewAttemptClearsItsOldFailureAndFingerprintVerificationSkipsExtraction()
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = await StorageTestDatabase.CreateAsync(token);
        var (project, folder) = await db.CreateProjectAsync("Verify changes", token);
        var indexed = await IndexAsync(db, project, folder, "verify.txt", [], token);
        var path = Path.Combine(db.Paths.SourceDirectory, "verify.txt");
        var file = new FileInfo(path);
        var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        var observation = new FileObservation(project, folder, path, file.Length, modified, VerifyContent: true);
        Assert.True((await db.Writer.ObserveFileAsync(observation, token)).Queued);
        var check = Assert.IsType<IndexJobLease>(await db.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), token));
        Assert.Equal(IndexJobKind.Index, check.Kind);
        await db.Writer.FailJobAsync(check, "file_locked", "File is locked.", false, token);
        Assert.Single(await db.Store.ListProjectErrorsAsync(project, 25, token));
        await db.Writer.ObserveFileAsync(observation, token);
        check = Assert.IsType<IndexJobLease>(await db.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), token));
        Assert.Empty(await db.Store.ListProjectErrorsAsync(project, 25, token));
        var info = await db.Store.GetDocumentInfoAsync(project, indexed.DocumentId, null, token);
        var begin = await db.Writer.BeginRevisionAsync(check, info!.Sha256!, file.Length, modified, token);
        Assert.False(begin.ShouldExtract);
        Assert.False(begin.IsStale);
        var summary = Assert.Single(await db.Store.ListProjectsAsync(token));
        Assert.Equal(1, summary.ReadyCount);
        Assert.Equal(0, summary.PendingCount);
        Assert.Equal(0, summary.ErrorCount);

        await db.Writer.RequestReindexAsync(project, token);
        var forced = Assert.IsType<IndexJobLease>(await db.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), token));
        Assert.True((await db.Writer.BeginRevisionAsync(forced, info.Sha256!, file.Length, modified, token)).ShouldExtract);
    }

    [Fact]
    public async Task ShutdownRequeuesRunningWorkAndRestartRetainsPausedWorkAndLastGoodRevision()
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = await StorageTestDatabase.CreateAsync(token);
        var (project, folder) = await db.CreateProjectAsync("Shutdown recovery", token);
        var indexed = await IndexAsync(db, project, folder, "resume.txt", [], token);
        await db.Writer.RequestReindexAsync(project, token);
        var running = Assert.IsType<IndexJobLease>(await db.Writer.LeaseNextJobAsync(TimeSpan.FromHours(1), token));
        var file = new FileInfo(running.SourcePath);
        Assert.True((await db.Writer.BeginRevisionAsync(running, "replacement", file.Length,
            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), token)).ShouldExtract);

        await db.Writer.StopAsync(token);
        var stopped = Assert.Single(await db.Store.ListProjectsAsync(token));
        Assert.Equal(1, stopped.Work.QueuedCount);
        Assert.Equal(0, stopped.Work.ProcessingCount);
        Assert.Equal(1, stopped.SearchableCount);

        using var restarted = new DatabaseWriterService(db.Paths);
        await restarted.StartAsync(token);
        await restarted.Ready.WaitAsync(TimeSpan.FromSeconds(10), token);
        try
        {
            await restarted.SetProjectPausedAsync(project, true, token);
            Assert.Null(await restarted.LeaseNextJobAsync(TimeSpan.FromMinutes(1), token));
            var retained = await db.Store.GetDocumentInfoAsync(project, indexed.DocumentId, null, token);
            Assert.Equal(indexed.RevisionId, retained!.ActiveRevisionId);
            await restarted.SetProjectPausedAsync(project, false, token);
            var resumed = Assert.IsType<IndexJobLease>(await restarted.LeaseNextJobAsync(TimeSpan.FromMinutes(1), token));
            Assert.Equal(running.JobId, resumed.JobId);
            Assert.Equal(running.Attempt, resumed.Attempt);
            Assert.Empty(await db.Store.ListProjectErrorsAsync(project, 25, token));
        }
        finally
        {
            await restarted.StopAsync(CancellationToken.None);
        }
    }

    private static void AssertBuckets(ProjectSummary summary) =>
        Assert.Equal(summary.DocumentCount, summary.ReadyCount + summary.PendingCount + summary.AttentionCount);

    private sealed class SimpleTextExtractor : IDocumentExtractor
    {
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken)
        {
            var text = await File.ReadAllTextAsync(request.SourcePath, cancellationToken);
            return new(new ExtractedNode(Path.GetFileName(request.SourcePath), "text/plain", "root",
                [new ExtractedSection(text, new(LocationKind.Document), ExtractionMethod.NativeText)], []), []);
        }
    }

    private static async Task<CommittedTestDocument> IndexAsync(StorageTestDatabase db, Guid project, Guid folder,
        string name, IReadOnlyList<ExtractionError> errors, CancellationToken token)
    {
        var path = Path.Combine(db.Paths.SourceDirectory, name);
        await File.WriteAllTextAsync(path, "Searchable content.", token);
        var lease = await db.ObserveAndLeaseAsync(project, folder, path, false, token);
        return await db.CommitAsync(lease.Job, lease.Sha256, lease.File.Length,
            new DateTimeOffset(lease.File.LastWriteTimeUtc, TimeSpan.Zero), "Searchable content.",
            includeVector: false, errors: errors, cancellationToken: token);
    }
}
