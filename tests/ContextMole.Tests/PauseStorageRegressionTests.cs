using ContextMole.Core;

using Microsoft.Data.Sqlite;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class PauseStorageRegressionTests
{
    [Fact]
    public async Task PauseRequeuesRunningJobDeletesStagingAndRejectsLateCommit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Immediate pause", cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "pause.txt");
        await File.WriteAllTextAsync(source, "content being indexed", cancellationToken);
        var pending = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var modified = new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero);
        var staging = await database.Writer.BeginRevisionAsync(pending.Job, pending.Sha256,
            pending.File.Length, modified, cancellationToken);
        Assert.True(staging.ShouldExtract);
        Assert.NotNull(staging.RevisionId);

        var before = await ReadJobAsync(database.Paths.DatabasePath, pending.Job.JobId, cancellationToken);
        Assert.Equal("running", before.State);
        Assert.NotNull(before.LeaseUntilUtc);
        Assert.Equal(1, await CountStagingRevisionsAsync(database.Paths.DatabasePath, projectId,
            cancellationToken));

        await database.Writer.SetProjectPausedAsync(projectId, paused: true, cancellationToken);

        var pausedProject = (await database.Store.ListProjectsAsync(cancellationToken))
            .Single(project => project.Id == projectId);
        Assert.Equal(ProjectState.Paused, pausedProject.State);
        var pausedJob = await ReadJobAsync(database.Paths.DatabasePath, pending.Job.JobId, cancellationToken);
        Assert.Equal("queued", pausedJob.State);
        Assert.Equal(before.Attempt, pausedJob.Attempt);
        Assert.Equal(before.LastError, pausedJob.LastError);
        Assert.Null(pausedJob.LeaseUntilUtc);
        Assert.Equal(0, await CountStagingRevisionsAsync(database.Paths.DatabasePath, projectId,
            cancellationToken));
        Assert.Equal(0, await CountErrorsAsync(database.Paths.DatabasePath, projectId, cancellationToken));
        Assert.Null(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken));

        // A worker can finish unwinding after the pause transaction. Its stale callback must not
        // consume the job that pause returned to the queue.
        var lateCommit = await database.Writer.CommitRevisionAsync(new IndexCommitRequest(
            pending.Job.JobId, projectId, pending.Job.DocumentId, staging.RevisionId!.Value,
            pending.Job.ExpectedObservationEpoch, pending.Sha256, pending.File.Length, modified,
            [], [], null, []), cancellationToken);
        Assert.False(lateCommit);
        var afterLateCommit = await ReadJobAsync(database.Paths.DatabasePath, pending.Job.JobId,
            cancellationToken);
        Assert.Equal(pausedJob, afterLateCommit);

        await database.Writer.FailJobAsync(pending.Job, "cancelled", "The project was paused.",
            retryable: true, cancellationToken: cancellationToken);
        Assert.Equal(pausedJob,
            await ReadJobAsync(database.Paths.DatabasePath, pending.Job.JobId, cancellationToken));
        Assert.Equal(0, await CountErrorsAsync(database.Paths.DatabasePath, projectId, cancellationToken));

        await database.Writer.SetProjectPausedAsync(projectId, paused: false, cancellationToken);
        var resumed = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(resumed);
        Assert.Equal(pending.Job.JobId, resumed.JobId);
        Assert.Equal(pending.Job.Attempt, resumed.Attempt);
        await database.Writer.FailJobAsync(resumed, "cleanup", "Deliberate cleanup", retryable: false,
            cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task PausePreservesQueuedAndScheduledRetryState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Pause queue preservation",
            cancellationToken);

        var retrySource = Path.Combine(database.Paths.SourceDirectory, "retry.txt");
        await File.WriteAllTextAsync(retrySource, "temporary failure", cancellationToken);
        var retry = await database.ObserveAndLeaseAsync(projectId, folderId, retrySource, false,
            cancellationToken);
        await database.Writer.FailJobAsync(retry.Job, "temporary", "Try again later.", retryable: true,
            cancellationToken: cancellationToken);
        var scheduledBefore = await ReadJobAsync(database.Paths.DatabasePath, retry.Job.JobId, cancellationToken);
        Assert.Equal("retry_wait", scheduledBefore.State);
        Assert.Equal(1, scheduledBefore.Attempt);

        var queuedSource = Path.Combine(database.Paths.SourceDirectory, "queued.txt");
        await File.WriteAllTextAsync(queuedSource, "still queued", cancellationToken);
        var queuedFile = new FileInfo(queuedSource);
        var queuedObservation = await database.Writer.ObserveFileAsync(new FileObservation(
            projectId, folderId, queuedSource, queuedFile.Length,
            new DateTimeOffset(queuedFile.LastWriteTimeUtc, TimeSpan.Zero)), cancellationToken);
        var queuedBefore = await ReadOpenJobForDocumentAsync(database.Paths.DatabasePath,
            queuedObservation.DocumentId, cancellationToken);
        Assert.Equal("queued", queuedBefore.State);

        await database.Writer.SetProjectPausedAsync(projectId, paused: true, cancellationToken);

        Assert.Equal(scheduledBefore,
            await ReadJobAsync(database.Paths.DatabasePath, retry.Job.JobId, cancellationToken));
        Assert.Equal(queuedBefore,
            await ReadOpenJobForDocumentAsync(database.Paths.DatabasePath, queuedObservation.DocumentId,
                cancellationToken));
    }

    [Fact]
    public async Task PausedEmbeddingRefreshRejectsLateLoadAndCommitWithoutConsumingQueue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Paused embedding refresh",
            cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "embedding.txt");
        await File.WriteAllTextAsync(source, "persisted passage", cancellationToken);
        var initial = await database.ObserveAndLeaseAsync(projectId, folderId, source, false,
            cancellationToken);
        var modified = new DateTimeOffset(initial.File.LastWriteTimeUtc, TimeSpan.Zero);
        await database.CommitAsync(initial.Job, initial.Sha256, initial.File.Length, modified,
            "persisted passage", cancellationToken: cancellationToken);

        var targetPolicy = StorageTestDatabase.TestEmbeddingPolicy with { ModelId = "pause-target" };
        await database.Writer.RequestEmbeddingRefreshAsync(projectId, targetPolicy, retryFailed: false,
            cancellationToken);
        var refreshJob = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(refreshJob);
        Assert.Equal(IndexJobKind.EmbeddingRefresh, refreshJob.Kind);
        var refreshSource = await database.Writer.LoadEmbeddingRefreshSourceAsync(refreshJob, cancellationToken);
        Assert.NotNull(refreshSource);

        await database.Writer.SetProjectPausedAsync(projectId, paused: true, cancellationToken);
        var queuedAfterPause = await ReadJobAsync(database.Paths.DatabasePath, refreshJob.JobId,
            cancellationToken);
        Assert.Equal("queued", queuedAfterPause.State);

        Assert.Null(await database.Writer.LoadEmbeddingRefreshSourceAsync(refreshJob, cancellationToken));
        Assert.Equal(queuedAfterPause,
            await ReadJobAsync(database.Paths.DatabasePath, refreshJob.JobId, cancellationToken));

        var lateCommit = await database.Writer.CommitEmbeddingRefreshAsync(new EmbeddingRefreshCommitRequest(
            refreshJob.JobId, projectId, refreshJob.DocumentId, refreshSource.RevisionId,
            refreshJob.ExpectedObservationEpoch,
            refreshSource.Passages.Select(passage =>
                new PassageEmbedding(passage.PassageId, StorageTestDatabase.TestVector())).ToArray(),
            targetPolicy), cancellationToken);
        Assert.False(lateCommit);
        Assert.Equal(queuedAfterPause,
            await ReadJobAsync(database.Paths.DatabasePath, refreshJob.JobId, cancellationToken));

        await database.Writer.SetProjectPausedAsync(projectId, paused: false, cancellationToken);
        var resumed = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(resumed);
        Assert.Equal(refreshJob.JobId, resumed.JobId);
        await database.Writer.FailJobAsync(resumed, "cleanup", "Deliberate cleanup", retryable: false,
            cancellationToken: cancellationToken);
    }

    private static async Task<PersistedJob> ReadJobAsync(string databasePath, Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id,state,attempt,not_before_utc,lease_until_utc,last_error,updated_utc FROM index_jobs WHERE id=$id;";
        command.Parameters.AddWithValue("$id", jobId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return ReadJob(reader);
    }

    private static async Task<PersistedJob> ReadOpenJobForDocumentAsync(string databasePath, Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,state,attempt,not_before_utc,lease_until_utc,last_error,updated_utc
            FROM index_jobs
            WHERE document_id=$document AND state IN ('queued','retry_wait','running');
            """;
        command.Parameters.AddWithValue("$document", documentId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return ReadJob(reader);
    }

    private static PersistedJob ReadJob(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6));

    private static async Task<int> CountStagingRevisionsAsync(string databasePath, Guid projectId,
        CancellationToken cancellationToken) =>
        await CountAsync(databasePath,
            """
            SELECT COUNT(*) FROM document_revisions r
            JOIN documents d ON d.id=r.document_id
            WHERE d.project_id=$project AND r.status='staging';
            """, projectId, cancellationToken);

    private static async Task<int> CountErrorsAsync(string databasePath, Guid projectId,
        CancellationToken cancellationToken) =>
        await CountAsync(databasePath, "SELECT COUNT(*) FROM project_errors WHERE project_id=$project;",
            projectId, cancellationToken);

    private static async Task<int> CountAsync(string databasePath, string sql, Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$project", projectId.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static SqliteConnection CreateConnection(string databasePath) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());

    private sealed record PersistedJob(
        Guid Id,
        string State,
        int Attempt,
        string NotBeforeUtc,
        string? LeaseUntilUtc,
        string? LastError,
        string UpdatedUtc);
}
