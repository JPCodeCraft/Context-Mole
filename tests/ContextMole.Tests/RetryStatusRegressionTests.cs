using System.Text.Json;

using ContextMole.Core;

using Microsoft.Data.Sqlite;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class RetryStatusRegressionTests
{
    [Fact]
    public async Task ProjectWorkSummaryDistinguishesScheduledDueAndRunningRetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Retry state", cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "retry.txt");
        await File.WriteAllTextAsync(source, "retry state evidence", cancellationToken);

        var observed = await database.ObserveAndLeaseAsync(projectId, folderId, source, false,
            cancellationToken);
        var processing = await LoadProjectAsync(database, projectId, cancellationToken);
        Assert.Equal(1, processing.PendingCount);
        Assert.Equal(0, processing.Work.QueuedCount);
        Assert.Equal(1, processing.Work.ProcessingCount);
        Assert.Equal(0, processing.Work.RunningRetryCount);
        Assert.Equal(processing.PendingCount,
            processing.Work.QueuedCount + processing.Work.ProcessingCount);
        Assert.Equal(ProjectWorkPhase.Indexing, processing.Work.Phase);

        var failedUtc = DateTimeOffset.UtcNow;
        await database.Writer.FailJobAsync(observed.Job, "temporary_failure", "Try again later.", true,
            cancellationToken);

        var scheduled = await LoadProjectAsync(database, projectId, cancellationToken);
        Assert.Equal(1, scheduled.PendingCount);
        Assert.Equal(1, scheduled.Work.QueuedCount);
        Assert.Equal(1, scheduled.Work.RetryScheduledCount);
        Assert.Equal(0, scheduled.Work.ProcessingCount);
        Assert.Equal(0, scheduled.Work.RunningRetryCount);
        Assert.Equal(scheduled.PendingCount,
            scheduled.Work.QueuedCount + scheduled.Work.ProcessingCount);
        Assert.True(scheduled.Work.NextRetryUtc > failedUtc);
        Assert.Equal(ProjectWorkPhase.RetryScheduled, scheduled.Work.Phase);

        await SetRetryDueAsync(database.Paths.DatabasePath, observed.Job.JobId, cancellationToken);
        var due = await LoadProjectAsync(database, projectId, cancellationToken);
        Assert.Equal(1, due.Work.QueuedCount);
        Assert.Equal(0, due.Work.RetryScheduledCount);
        Assert.Null(due.Work.NextRetryUtc);
        Assert.Equal(ProjectWorkPhase.Queued, due.Work.Phase);

        var retry = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(retry);
        Assert.Equal(1, retry.Attempt);
        var retrying = await LoadProjectAsync(database, projectId, cancellationToken);
        Assert.Equal(0, retrying.Work.QueuedCount);
        Assert.Equal(1, retrying.Work.ProcessingCount);
        Assert.Equal(1, retrying.Work.RunningRetryCount);
        Assert.Equal(retrying.PendingCount,
            retrying.Work.QueuedCount + retrying.Work.ProcessingCount);
        Assert.Equal(ProjectWorkPhase.Retrying, retrying.Work.Phase);

        await database.Writer.FailJobAsync(retry, "permanent_failure", "Manual review is required.", false,
            cancellationToken);
        var settled = await LoadProjectAsync(database, projectId, cancellationToken);
        Assert.Equal(0, settled.PendingCount);
        Assert.Equal(0, settled.Work.QueuedCount);
        Assert.Equal(0, settled.Work.ProcessingCount);
        Assert.Equal(ProjectWorkPhase.Ready, settled.Work.Phase);
        Assert.Equal(1, settled.ErrorCount);
    }

    [Fact]
    public void ProjectWorkSummaryDoesNotChangePublicProjectJson()
    {
        var project = new ProjectSummary(Guid.NewGuid(), "Stable schema", ProjectState.Active, [], 1, 5, 3, 2,
            1, null)
        {
            Work = new ProjectWorkSummary(2, 1, 1, 1, DateTimeOffset.UtcNow.AddMinutes(1))
        };

        var json = JsonSerializer.Serialize(project, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"pendingCount\":3", json, StringComparison.Ordinal);
        Assert.DoesNotContain("work", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("retryScheduled", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("processingCount", json, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ProjectSummary> LoadProjectAsync(StorageTestDatabase database, Guid projectId,
        CancellationToken cancellationToken) =>
        (await database.Store.ListProjectsAsync(cancellationToken)).Single(project => project.Id == projectId);

    private static async Task SetRetryDueAsync(string databasePath, Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE index_jobs SET not_before_utc=$due WHERE id=$id;";
        command.Parameters.AddWithValue("$due", DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O"));
        command.Parameters.AddWithValue("$id", jobId.ToString());
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }
}
