using ContextMole.App.UI.ViewModels;
using ContextMole.Core;

using Microsoft.Data.Sqlite;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class SemanticRepairNotificationTests
{
    [Theory]
    [InlineData(0, 2, "no repair jobs are queued", "2 files still lack compatible meaning-based coverage")]
    [InlineData(1, 2, "queued for 1 file", "1 file still lacks compatible meaning-based coverage")]
    [InlineData(2, 2, "queued for 2 files", "expand in the background")]
    public void NotificationUsesActualQueuedAndExcludedCounts(int queued, int excluded,
        string queueMessage, string coverageMessage)
    {
        var metadata = new VectorSnapshotMetadata(1, StorageTestDatabase.TestEmbeddingPolicy, 0,
            IsComplete: false, TotalDocumentCount: excluded, RepairQueuedDocumentCount: queued);

        var message = MainViewModel.FormatSemanticRepairNotification("Fixture project", metadata);

        Assert.Contains(queueMessage, message);
        Assert.Contains(coverageMessage, message);
        if (queued < excluded)
        {
            Assert.Contains("Reindex", message);
            Assert.Contains("source folders available", message);
        }
    }

    [Fact]
    public void RepairThatAlreadyCompletedDoesNotClaimWorkIsStillQueued()
    {
        var metadata = new VectorSnapshotMetadata(1, StorageTestDatabase.TestEmbeddingPolicy, 1,
            TotalDocumentCount: 1, CompatibleDocumentCount: 1);

        var message = MainViewModel.FormatSemanticRepairNotification("Fixture project", metadata);

        Assert.Contains("complete meaning-based coverage", message);
        Assert.DoesNotContain("queued", message);
        Assert.DoesNotContain("Reindex", message);
    }

    [Fact]
    public async Task ObsoletePreparationQueuesSourceReextractionAndKeepsRetainedKeywordEvidence()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(token);
        var (project, folder) = await database.CreateProjectAsync("Old preparation", token);
        var path = Path.Combine(database.Paths.SourceDirectory, "old.txt");
        await File.WriteAllTextAsync(path, "Preserved evidence still needs updated preparation.", token);
        var observed = await database.ObserveAndLeaseAsync(project, folder, path, false, token);
        var committed = await database.CommitAsync(observed.Job, observed.Sha256, observed.File.Length,
            new DateTimeOffset(observed.File.LastWriteTimeUtc, TimeSpan.Zero),
            "Preserved evidence still needs updated preparation.", cancellationToken: token);
        await using (var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath}"))
        {
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE document_revisions SET preparation_version=$old WHERE id=$id;";
            command.Parameters.AddWithValue("$old", "layout-v2/spans-v2/body-context-v2");
            command.Parameters.AddWithValue("$id", committed.RevisionId.ToString());
            await command.ExecuteNonQueryAsync(token);
        }
        var target = StorageTestDatabase.TestEmbeddingPolicy;

        await database.Writer.RequestEmbeddingRefreshAsync(project, target, retryFailed: true, token);
        var metadata = await database.Store.LoadVectorSnapshotMetadataAsync(project, target, token);
        var message = MainViewModel.FormatSemanticRepairNotification("Old preparation", metadata);

        Assert.Equal(1, metadata.ExcludedDocumentCount);
        Assert.Equal(1, metadata.RepairQueuedDocumentCount);
        Assert.Equal(1, metadata.ReextractionRequiredDocumentCount);
        var leased = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), token);
        Assert.NotNull(leased);
        Assert.Equal(IndexJobKind.Reindex, leased.Kind);
        Assert.Contains("queued for 1 file", message);
        var evidence = await database.Store.KeywordSearchAsync(project,
            TextNormalization.QuoteFtsTerms("Preserved evidence"), 10, null, token);
        Assert.Single(evidence.Candidates);
    }
}
