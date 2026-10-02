using ContextMole.App.UI.ViewModels;
using ContextMole.Core;
using ContextMole.Indexing;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class IndexingProgressRepairIntegrationTests
{
    [Fact]
    public async Task HundredsOfAttachmentRepairsAdvanceActualCoverageQueueAndRuntimeCompletion()
    {
        var token = TestContext.Current.CancellationToken;
        const int total = 240;
        await using var database = await StorageTestDatabase.CreateAsync(token);
        var (projectId, folderId) = await database.CreateProjectAsync("Repair progress with attachments", token);
        for (var index = 0; index < total; index++)
        {
            var path = Path.Combine(database.Paths.SourceDirectory, $"authored-{index:D4}.eml");
            await File.WriteAllTextAsync(path, $"Authored message and supported attachment fixture {index}", token);
            var observed = await database.ObserveAndLeaseAsync(projectId, folderId, path, false, token);
            var root = Guid.NewGuid();
            var child = Guid.NewGuid();
            ContentNodeDraft[] nodes = [new(root, null, 0, Path.GetFileName(path), "message/rfc822", "root", 0),
                new(child, root, 1, "authored-attachment.txt", "text/plain", "root/attachment:1:authored-attachment.txt", 1)];
            PassageDraft[] passages = [Passage(root, "Authored message body."), Passage(child, "Authored attachment evidence.")];
            await database.CommitAsync(observed.Job, observed.Sha256, observed.File.Length,
                new DateTimeOffset(observed.File.LastWriteTimeUtc, TimeSpan.Zero), "unused", includeVector: false,
                nodes: nodes, passages: passages, cancellationToken: token);
        }
        var policy = StorageTestDatabase.TestEmbeddingPolicy;
        await database.Writer.RequestEmbeddingRefreshAsync(projectId, policy, false, token);
        var project = new ProjectItemViewModel((await database.Store.ListProjectsAsync(token)).Single(p => p.Id == projectId));
        project.UpdateFileTypeCounts(await database.Store.ListProjectFileTypeCountsAsync(projectId, token));
        var inventory = project.FileTypeCounts;
        var initial = await database.Store.LoadVectorSnapshotMetadataAsync(projectId, policy, token);
        Assert.Equal(total, initial.RepairQueuedDocumentCount);
        Assert.Equal(0, initial.CompatibleDocumentCount);
        project.UpdateSemanticIndex(initial, true);
        var tracker = new IndexingActivityTracker();
        for (var completed = 1; completed <= total; completed++)
        {
            var job = Assert.IsType<IndexJobLease>(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), token));
            Assert.Equal(IndexJobKind.EmbeddingRefresh, job.Kind);
            using var activity = tracker.Start(job);
            activity.StartProcessing(IndexingPipelineStage.GeneratingEmbeddings);
            var source = Assert.IsType<EmbeddingRefreshSource>(await database.Writer.LoadEmbeddingRefreshSourceAsync(job, token));
            Assert.Equal(2, source.Passages.Count);
            activity.SetStage(IndexingPipelineStage.WritingIndex);
            Assert.True(await database.Writer.CommitEmbeddingRefreshAsync(new(job.JobId, projectId, job.DocumentId,
                source.RevisionId, job.ExpectedObservationEpoch,
                source.Passages.Select(p => new PassageEmbedding(p.PassageId, StorageTestDatabase.TestVector())).ToArray(), policy), token));
            activity.Complete(true);
            // Actual writer generations and metadata feed the same view model used by live polling.
            project.UpdateFrom((await database.Store.ListProjectsAsync(token)).Single(p => p.Id == projectId));
            project.UpdateRuntime(tracker.GetSnapshot(projectId));
            project.BeginSemanticIndexRefresh();
            Assert.Same(inventory, project.FileTypeCounts);
            Assert.NotEqual("CHECKING", project.SemanticIndexStatusLabel);
            var coverage = await database.Store.LoadVectorSnapshotMetadataAsync(projectId, policy, token);
            project.UpdateSemanticIndex(coverage, true);
            Assert.Equal(completed, coverage.CompatibleDocumentCount);
            Assert.Equal(total - completed, project.PendingCount);
            Assert.Equal(completed, tracker.GetSnapshot(projectId).CompletedSampleCount);
            Assert.NotNull(tracker.GetSnapshot(projectId).LastCompletedUtc);
            var progress = IndexingProgressPresentation.Create(project, tracker.GetSnapshot(projectId));
            Assert.Contains($"{completed} completed jobs", progress.Summary);
        }
        Assert.Equal(total, project.ReadyCount);
        Assert.Equal(0, project.QueuedCount);
        Assert.True(project.IsSemanticCoverageComplete);
        Assert.Equal("IDLE", IndexingProgressPresentation.Create(project, tracker.GetSnapshot(projectId)).StatusLabel);
        Assert.Equal(total, Assert.Single(project.FileTypeCounts).Count);
    }

    private static PassageDraft Passage(Guid contentId, string text) => new(Guid.NewGuid(), contentId, 0, text,
        TextNormalization.ForSearch(text), new SourceLocation(LocationKind.Document), ExtractionMethod.NativeText,
        null, null, LexicalText.Canonicalize(text));
}
