using ContextMole.Core;
using Microsoft.Data.Sqlite;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class PreparationUpgradeRegressionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "layout-v2/spans-v2/body-context-v2")]
    [InlineData(true, "layout-v2/spans-v2/body-context-v2")]
    [InlineData(false, "layout-v4/spans-v3/body-context-v4")]
    [InlineData(true, "layout-v4/spans-v3/body-context-v4")]
    public async Task UnchangedSourcesQueueOnePreparationUpgradeAndKeepExistingEvidence(bool failUpgrade, string oldPreparation)
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "stable.txt");
        await File.WriteAllTextAsync(path, "Current source contains the recovery checksum.", Token);
        var (project, folder) = await database.CreateProjectAsync("Preparation upgrade", Token);
        var first = await database.ObserveAndLeaseAsync(project, folder, path, false, Token);
        var modified = new DateTimeOffset(first.File.LastWriteTimeUtc, TimeSpan.Zero);
        var committed = await database.CommitAsync(first.Job, first.Sha256, first.File.Length, modified,
            "Older extracted evidence remains readable during upgrade.", cancellationToken: Token);
        await using (var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath}"))
        {
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE document_revisions SET preparation_version=$old WHERE id=$id;";
            command.Parameters.AddWithValue("$old", oldPreparation);
            command.Parameters.AddWithValue("$id", committed.RevisionId.ToString());
            await command.ExecuteNonQueryAsync(Token);
        }
        var observation = new FileObservation(project, folder, path, first.File.Length, modified);
        var upgrade = await database.Writer.ObserveFileAsync(observation, Token);
        Assert.True(upgrade.Queued);
        Assert.Equal(first.Observation.ObservationEpoch + 1, upgrade.ObservationEpoch);
        var repeated = await database.Writer.ObserveFileAsync(observation, Token);
        Assert.False(repeated.Queued);
        Assert.Equal(upgrade.ObservationEpoch, repeated.ObservationEpoch);
        var job = Assert.IsType<IndexJobLease>(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
        Assert.Equal(IndexJobKind.Reindex, job.Kind);
        var running = await database.Writer.ObserveFileAsync(observation, Token);
        Assert.False(running.Queued);
        Assert.Equal(upgrade.ObservationEpoch, running.ObservationEpoch);
        var oldEvidence = await database.Store.KeywordSearchAsync(project,
            TextNormalization.QuoteFtsTerms("older extracted evidence"), 10, null, Token);
        Assert.Single(oldEvidence.Candidates);
        if (failUpgrade)
        {
            await database.Writer.FailJobAsync(job, "test_upgrade_failure", "Synthetic failed upgrade", false, Token);
            var failed = await database.Writer.ObserveFileAsync(observation, Token);
            Assert.False(failed.Queued);
            Assert.Null(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
        }
        else
        {
            await database.CommitAsync(job, first.Sha256, first.File.Length, modified,
                "Current source contains the recovery checksum.", cancellationToken: Token);
            var complete = await database.Writer.ObserveFileAsync(observation, Token);
            Assert.False(complete.Queued);
            Assert.Null(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
            var updated = await database.Store.KeywordSearchAsync(project,
                TextNormalization.QuoteFtsTerms("recovery checksum"), 10, null, Token);
            Assert.Single(updated.Candidates);
        }
        Assert.Equal(first.Sha256, await StorageTestDatabase.HashAsync(path, Token));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("preparation:layout-v1/spans-v1/body-context-v1")]
    public async Task AnOlderFailedPreparationDoesNotBlockANewUpgrade(string? olderTarget)
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "failed-old-policy.txt");
        await File.WriteAllTextAsync(path, "The source remains readable after the old policy failed.", Token);
        var (project, folder) = await database.CreateProjectAsync("Upgrade old failure", Token);
        var first = await database.ObserveAndLeaseAsync(project, folder, path, false, Token);
        var modified = new DateTimeOffset(first.File.LastWriteTimeUtc, TimeSpan.Zero);
        var committed = await database.CommitAsync(first.Job, first.Sha256, first.File.Length, modified,
            "Older searchable evidence.", cancellationToken: Token);
        await using (var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath}"))
        {
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE document_revisions SET preparation_version='layout-v2/spans-v2/body-context-v2' WHERE id=$revision;
                UPDATE index_jobs SET state='failed',target_policy_key=$target WHERE id=$job;
                """;
            command.Parameters.AddWithValue("$revision", committed.RevisionId.ToString());
            command.Parameters.AddWithValue("$job", first.Job.JobId.ToString());
            command.Parameters.AddWithValue("$target", (object?)olderTarget ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(Token);
        }
        var observed = await database.Writer.ObserveFileAsync(new FileObservation(project, folder, path,
            first.File.Length, modified), Token);
        Assert.True(observed.Queued);
        var upgrade = Assert.IsType<IndexJobLease>(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
        Assert.Equal(IndexJobKind.Reindex, upgrade.Kind);
        Assert.Equal(first.Observation.ObservationEpoch + 1, upgrade.ExpectedObservationEpoch);
    }

}
