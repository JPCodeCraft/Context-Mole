using ContextMole.App.UI.ViewModels;
using ContextMole.Core;
using ContextMole.Indexing;
using ContextMole.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class FolderAvailabilityTests
{
    [Fact]
    public async Task AnOfflineFolderIsVisibleWithoutPendingJobsAndClearsAfterReconnection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Folder availability", cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "available.txt");
        await File.WriteAllTextAsync(source, "Searchable while the drive is disconnected.", cancellationToken);
        var observed = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        await database.CommitAsync(observed.Job, observed.Sha256, observed.File.Length,
            new DateTimeOffset(observed.File.LastWriteTimeUtc, TimeSpan.Zero), "Retained searchable evidence.",
            includeVector: false, cancellationToken: cancellationToken);

        var offlinePath = Path.Combine(Path.GetDirectoryName(database.Paths.SourceDirectory)!, "offline-folder");
        Directory.Move(database.Paths.SourceDirectory, offlinePath);
        var activities = new IndexingActivityTracker();
        await using var embeddings = new StorageUnavailableEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new UnexpectedExtractor(), embeddings, activities, new EmbeddingPolicyRefreshTracker(), budget,
            NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(() => activities.GetFolderIssues(projectId).Count == 1, cancellationToken);
            var summary = (await database.Store.ListProjectsAsync(cancellationToken)).Single();
            Assert.Equal(1, summary.IndexedCount);
            Assert.Equal(0, summary.PendingCount);
            Assert.Equal(0, summary.ErrorCount);
            var project = new ProjectItemViewModel(summary);
            project.UpdateFolderIssues(activities.GetFolderIssues(projectId));
            Assert.False(project.IsReady);
            Assert.Equal("Needs attention", project.Phase);

            Directory.Move(offlinePath, database.Paths.SourceDirectory);
            await WaitUntilAsync(() => activities.GetFolderIssues(projectId).Count == 0, cancellationToken);
            project.UpdateFolderIssues(activities.GetFolderIssues(projectId));
            Assert.True(project.IsReady);
            Assert.Empty(await database.Store.ListProjectErrorsAsync(projectId, 10, cancellationToken));
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void ARemovedFolderCannotKeepAnObsoleteIssueOnTheProject()
    {
        var folderId = Guid.NewGuid();
        var summary = new ProjectSummary(Guid.NewGuid(), "Removed folder", ProjectState.Active,
            [new ProjectFolderInfo(folderId, "offline-folder")], 0, 0, 0, 0, 0, null);
        var issue = new ProjectFolderIssue(folderId, "offline-folder", "Folder unavailable.");
        var project = new ProjectItemViewModel(summary);
        project.UpdateFolderIssues([issue]);
        Assert.True(project.HasFolderIssues);
        project.UpdateFrom(summary with { Folders = [] });
        project.UpdateFolderIssues([issue]);
        Assert.False(project.HasFolderIssues);
        Assert.True(project.IsReady);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "Folder availability did not refresh in time.");
            await Task.Delay(50, cancellationToken);
        }
    }

    private sealed class UnexpectedExtractor : IDocumentExtractor
    {
        public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;
        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unchanged files should not need extraction after reconnecting.");
    }
}
