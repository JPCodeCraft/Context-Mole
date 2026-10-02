using System.Text.Json;
using ContextMole.Core;
using Microsoft.Data.Sqlite;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class PreparationRefreshSafetyTests
{
    private const string OldPreparation = "layout-v2/spans-v2/body-context-v2";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FailedPreparationUpgradeCannotBeRelabeledByEmbeddingRefresh()
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "unavailable-source.txt");
        await File.WriteAllTextAsync(path, "Retained canonical recovery evidence.", Token);
        var (project, folder) = await database.CreateProjectAsync("Preparation provenance", Token);
        var first = await database.ObserveAndLeaseAsync(project, folder, path, false, Token);
        var modified = new DateTimeOffset(first.File.LastWriteTimeUtc, TimeSpan.Zero);
        var committed = await database.CommitAsync(first.Job, first.Sha256, first.File.Length, modified,
            "Retained canonical recovery evidence.", cancellationToken: Token);
        var legacyPolicy = StorageTestDatabase.TestEmbeddingPolicy with { PreparationVersion = OldPreparation };
        await using (var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath}"))
        {
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE document_revisions SET preparation_version=$preparation,embedding_policy_json=$policy WHERE id=$revision;
                UPDATE embeddings SET policy_key=$key WHERE revision_id=$revision;
                """;
            command.Parameters.AddWithValue("$preparation", OldPreparation);
            command.Parameters.AddWithValue("$policy", JsonSerializer.Serialize(legacyPolicy));
            command.Parameters.AddWithValue("$key", legacyPolicy.Key);
            command.Parameters.AddWithValue("$revision", committed.RevisionId.ToString());
            await command.ExecuteNonQueryAsync(Token);
        }
        Assert.True((await database.Writer.ObserveFileAsync(new FileObservation(project, folder, path,
            first.File.Length, modified), Token)).Queued);
        var upgrade = Assert.IsType<IndexJobLease>(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
        Assert.Equal(IndexJobKind.Reindex, upgrade.Kind);
        await database.Writer.FailJobAsync(upgrade, "source_unavailable", "Synthetic unavailable source", false, Token);

        foreach (var retry in new[] { false, true })
        {
            await database.Writer.RequestEmbeddingRefreshAsync(project, StorageTestDatabase.TestEmbeddingPolicy, retry, Token);
            var admitted = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token);
            if (!retry) Assert.Null(admitted);
            else
            {
                Assert.NotNull(admitted);
                Assert.Equal(IndexJobKind.Reindex, admitted.Kind);
                await database.Writer.FailJobAsync(admitted, "source_unavailable", "Synthetic unavailable source", false, Token);
            }
        }
        var current = await database.Store.LoadVectorSnapshotMetadataAsync(project, StorageTestDatabase.TestEmbeddingPolicy, Token);
        Assert.False(current.IsComplete);
        Assert.Equal(0, current.EntryCount);
        Assert.Equal(1, current.ExcludedDocumentCount);
        Assert.Single((await database.Store.LoadVectorSnapshotAsync(project, legacyPolicy, Token)).Entries);
        var evidence = Assert.Single(await database.Store.ReadPassagesAsync(project, [committed.PassageId], 0, 0, Token));
        Assert.Equal("Retained canonical recovery evidence.", evidence.Text);
        Assert.Contains(await database.Store.ListProjectErrorsAsync(project, 10, Token), error => error.Code == "source_unavailable");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshRechecksPreparationBeforeLoadingAndCommitting(bool changedAfterLoading)
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "stable-evidence.txt");
        await File.WriteAllTextAsync(path, "Literal evidence must survive a rejected refresh.", Token);
        var (project, folder) = await database.CreateProjectAsync("Refresh guard", Token);
        var first = await database.ObserveAndLeaseAsync(project, folder, path, false, Token);
        var committed = await database.CommitAsync(first.Job, first.Sha256, first.File.Length,
            new DateTimeOffset(first.File.LastWriteTimeUtc, TimeSpan.Zero),
            "Literal evidence must survive a rejected refresh.", cancellationToken: Token);
        var target = StorageTestDatabase.TestEmbeddingPolicy with { Revision = "new-model-revision" };
        await database.Writer.RequestEmbeddingRefreshAsync(project, target, false, Token);
        var job = Assert.IsType<IndexJobLease>(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
        if (changedAfterLoading) Assert.NotNull(await database.Writer.LoadEmbeddingRefreshSourceAsync(job, Token));
        var generation = await database.Store.GetSearchGenerationAsync(project, Token);
        await using (var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath}"))
        {
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE document_revisions SET preparation_version=$old WHERE id=$revision;";
            command.Parameters.AddWithValue("$old", OldPreparation);
            command.Parameters.AddWithValue("$revision", committed.RevisionId.ToString());
            await command.ExecuteNonQueryAsync(Token);
        }
        if (changedAfterLoading)
        {
            Assert.False(await database.Writer.CommitEmbeddingRefreshAsync(new EmbeddingRefreshCommitRequest(
                job.JobId, project, job.DocumentId, committed.RevisionId, job.ExpectedObservationEpoch,
                [new PassageEmbedding(committed.PassageId, StorageTestDatabase.TestVector())], target), Token));
        }
        else Assert.Null(await database.Writer.LoadEmbeddingRefreshSourceAsync(job, Token));

        Assert.Equal(generation, await database.Store.GetSearchGenerationAsync(project, Token));
        // Simulate an old buggy database: JSON/vector keys claim the current preparation,
        // but the independently stored extraction preparation says otherwise.
        var originalPolicy = StorageTestDatabase.TestEmbeddingPolicy;
        var metadata = await database.Store.LoadVectorSnapshotMetadataAsync(project, originalPolicy, Token);
        Assert.False(metadata.IsComplete);
        Assert.Equal(0, metadata.EntryCount);
        Assert.Empty((await database.Store.LoadVectorSnapshotAsync(project, originalPolicy, Token)).Entries);
        await foreach (var _ in database.Store.StreamVectorEntriesAsync(project, generation, originalPolicy, null, Token))
            Assert.Fail("Mislabeled old-preparation vectors must not enter the streaming search path.");
        var unqualified = await database.Store.LoadVectorSnapshotMetadataAsync(project, Token);
        Assert.False(unqualified.IsComplete);
        Assert.Null(unqualified.Policy);
        var evidence = Assert.Single(await database.Store.ReadPassagesAsync(project, [committed.PassageId], 0, 0, Token));
        Assert.Equal("Literal evidence must survive a rejected refresh.", evidence.Text);
        await using var retained = new SqliteConnection($"Data Source={database.Paths.DatabasePath}");
        await retained.OpenAsync(Token);
        await using var count = retained.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM embeddings WHERE revision_id=$revision;";
        count.Parameters.AddWithValue("$revision", committed.RevisionId.ToString());
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync(Token))!);
    }

    [Fact]
    public async Task ZeroVectorRevisionCannotAdvertiseFalsePreparationCompleteness()
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "boilerplate.txt");
        await File.WriteAllTextAsync(path, "Retained boilerplate evidence.", Token);
        var (project, folder) = await database.CreateProjectAsync("Zero-vector provenance", Token);
        var first = await database.ObserveAndLeaseAsync(project, folder, path, false, Token);
        var committed = await database.CommitAsync(first.Job, first.Sha256, first.File.Length,
            new DateTimeOffset(first.File.LastWriteTimeUtc, TimeSpan.Zero), "Retained boilerplate evidence.", cancellationToken: Token);
        await using var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath}");
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE document_revisions SET preparation_version=$old WHERE id=$revision;
            UPDATE passages SET semantic_eligible=0 WHERE revision_id=$revision;
            DELETE FROM embeddings WHERE revision_id=$revision;
            """;
        command.Parameters.AddWithValue("$old", OldPreparation);
        command.Parameters.AddWithValue("$revision", committed.RevisionId.ToString());
        await command.ExecuteNonQueryAsync(Token);
        var unqualified = await database.Store.LoadVectorSnapshotMetadataAsync(project, Token);
        Assert.False(unqualified.IsComplete);
        Assert.Null(unqualified.Policy);
        var targeted = await database.Store.LoadVectorSnapshotMetadataAsync(project, StorageTestDatabase.TestEmbeddingPolicy, Token);
        Assert.False(targeted.IsComplete);
        Assert.Equal(1, targeted.ExcludedDocumentCount);
        Assert.Equal("Retained boilerplate evidence.",
            Assert.Single(await database.Store.ReadPassagesAsync(project, [committed.PassageId], 0, 0, Token)).Text);
    }
}
