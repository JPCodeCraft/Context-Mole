using ContextMole.App.UI.ViewModels;
using ContextMole.Core;
using ContextMole.Indexing;

namespace ContextMole.Tests;

public sealed class IndexingProgressRegressionTests
{
    [Fact]
    public void HundredsOfRepairGenerationsRetainInventoryAndWarningDuringRefresh()
    {
        var summary = Summary(600);
        var project = new ProjectItemViewModel(summary);
        ProjectFileTypeCount[] inventory = [new(".eml", 300), new(".pdf", 300)];
        project.UpdateFileTypeCounts(inventory);
        project.UpdateSemanticIndex(Coverage(1, 0), true);
        for (var completed = 0; completed < 600; completed++)
        {
            var generation = completed + 2;
            project.UpdateFrom(summary with { SearchGeneration = generation, PendingCount = 600 - completed,
                Work = new(600 - completed, 0, 0, 0, null), ReadyCount = completed });
            project.BeginSemanticIndexRefresh();
            Assert.True(project.HasFileTypeCounts);
            Assert.Equal(inventory, project.FileTypeCounts);
            Assert.True(project.HasSemanticCoverageWarning);
            Assert.NotEqual("CHECKING", project.SemanticIndexStatusLabel);
            Assert.False(project.IsSemanticCoverageComplete);
            Assert.Contains("Last verified", project.SemanticIndexStatusMessage);
            project.UpdateSemanticIndex(Coverage(generation, completed + 1), true);
        }
        Assert.Equal("COMPLETE", project.SemanticIndexStatusLabel);
        Assert.True(project.IsSemanticCoverageComplete);
        Assert.Equal(inventory, project.FileTypeCounts);
    }

    [Fact]
    public void RefreshFailureRetainsVerifiedCountsButCannotOfferAStaleRepair()
    {
        var project = new ProjectItemViewModel(Summary(600));
        project.UpdateSemanticIndex(Coverage(1, 20) with { RepairQueuedDocumentCount = 0 }, true);
        Assert.True(project.CanRepairSemanticIndex);
        project.SetFreshness(true, DateTimeOffset.UtcNow);
        Assert.False(project.CanRepairSemanticIndex);
        project.SetFreshness(false, DateTimeOffset.UtcNow);
        project.BeginSemanticIndexRefresh();
        Assert.True(project.CanRepairSemanticIndex);
        project.FailSemanticIndexRefresh("Storage is busy.");
        Assert.Equal("UNABLE TO CHECK", project.SemanticIndexStatusLabel);
        Assert.Contains("Last verified: 20 of 600", project.SemanticIndexStatusMessage);
        Assert.False(project.CanRepairSemanticIndex);
        project.BeginSemanticIndexRefresh(invalidate: true);
        Assert.Equal("CHECKING", project.SemanticIndexStatusLabel);
        Assert.DoesNotContain("20 of 600", project.SemanticIndexStatusMessage);
    }

    [Fact]
    public void EachProjectsRetainedCoverageIsInvalidatedForItsOwnModelPolicy()
    {
        var first = new ProjectItemViewModel(Summary(600));
        var second = new ProjectItemViewModel(Summary(600));
        var policyA = StorageTestDatabase.TestEmbeddingPolicy;
        var policyB = policyA with { ModelSha256 = "different-model" };
        first.UpdateSemanticIndex(Coverage(1, 600) with { Policy = policyA }, true);
        second.UpdateSemanticIndex(Coverage(1, 600) with { Policy = policyA }, true);
        first.BeginSemanticIndexRefreshForPolicy(policyB.Key, true);
        Assert.Equal("CHECKING", first.SemanticIndexStatusLabel);
        first.UpdateSemanticIndex(Coverage(1, 600) with { Policy = policyB }, true);
        // Selecting another project cannot use the first project's last-success policy cache.
        second.BeginSemanticIndexRefreshForPolicy(policyB.Key, true);
        Assert.Equal("CHECKING", second.SemanticIndexStatusLabel);
        Assert.False(second.IsSemanticCoverageComplete);
        Assert.DoesNotContain("600 of 600", second.SemanticIndexStatusMessage);
        first.BeginSemanticIndexRefreshForPolicy(policyB.Key, true);
        Assert.True(first.IsSemanticCoverageComplete);
        first.BeginSemanticIndexRefreshForPolicy(null, false);
        Assert.Equal("CHECKING", first.SemanticIndexStatusLabel);
        first.UpdateSemanticIndex(null, false);
        Assert.Equal("KEYWORD ONLY", first.SemanticIndexStatusLabel);
        for (var poll = 0; poll < 10; poll++)
        {
            first.BeginSemanticIndexRefreshForPolicy(policyB.Key, false);
            Assert.Equal("KEYWORD ONLY", first.SemanticIndexStatusLabel);
        }
        first.BeginSemanticIndexRefreshForPolicy(policyB.Key, true);
        Assert.Equal("CHECKING", first.SemanticIndexStatusLabel);
    }

    [Fact]
    public void RuntimeSummaryExposesSuccessfulJobsEvenWhenDurableFileCountsLag()
    {
        var project = new ProjectItemViewModel(Summary(600));
        var started = DateTimeOffset.UtcNow;
        var active = new IndexingActivitySnapshot(Guid.NewGuid(), project.Id, Guid.NewGuid(),
            "message-with-attachments.eml", IndexingPipelineStage.ExtractingContent,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(28), started);
        var runtime = new IndexingTimingSnapshot([active], TimeSpan.FromSeconds(2), 12)
            { LastCompletedUtc = started };
        project.UpdateRuntime(runtime);
        // A durable snapshot can lag a lease. Runtime progress must not claim no activity.
        Assert.Equal(0, project.ProcessingCount);
        var progress = IndexingProgressPresentation.Create(project, runtime);
        Assert.Equal("ACTIVE", progress.StatusLabel);
        Assert.Contains("12 completed jobs this app session", progress.Summary);
        Assert.Contains("1 in flight", progress.Summary);
        Assert.Contains("600 queued root files", progress.Summary);
        Assert.Contains("Last successful job completed", progress.LastCompletion);
        runtime = runtime with { ActiveItems = [], CompletedSampleCount = 13 };
        progress = IndexingProgressPresentation.Create(project, runtime);
        Assert.Equal("WAITING", progress.StatusLabel);
        Assert.Contains("13 completed jobs", progress.Summary);
        Assert.Contains("no file is active right now", progress.IdleMessage);
        Assert.DoesNotContain("stalled", progress.IdleMessage);
    }

    [Fact]
    public void ClaimedCpuWaitRunningRetryAndPauseDrainExposeTheirActualPhase()
    {
        var project = new ProjectItemViewModel(Summary(10));
        var item = new IndexingActivitySnapshot(Guid.NewGuid(), project.Id, Guid.NewGuid(), "queued.eml",
            IndexingPipelineStage.WaitingForCpu, TimeSpan.Zero, TimeSpan.Zero, null);
        var runtime = new IndexingTimingSnapshot([item], null, 0);
        project.UpdateRuntime(runtime);
        Assert.Equal("WAITING FOR CPU", IndexingProgressPresentation.Create(project, runtime).StatusLabel);
        Assert.False(new IndexingActivityItemViewModel(item).ShowTimers);
        runtime = runtime with { ActiveItems = [item with { Stage = IndexingPipelineStage.ExtractingContent,
            StartedUtc = DateTimeOffset.UtcNow, Attempt = 1 }] };
        Assert.Equal("RETRYING", IndexingProgressPresentation.Create(project, runtime).StatusLabel);
        project.UpdateFrom(project.ToSummary() with { State = ProjectState.Paused });
        Assert.Equal("PAUSING", IndexingProgressPresentation.Create(project, runtime).StatusLabel);
        Assert.Equal("PAUSED", IndexingProgressPresentation.Create(project, runtime with { ActiveItems = [] }).StatusLabel);
    }

    [Fact]
    public void QueueIdlePauseRetryAndStaleReasonsAreNotPresentedAsLiveProcessing()
    {
        var project = new ProjectItemViewModel(Summary(10));
        var runtime = new IndexingTimingSnapshot([], null, 0);
        Assert.Equal("WAITING", IndexingProgressPresentation.Create(project, runtime).StatusLabel);
        Assert.Contains("No successful job completion", IndexingProgressPresentation.Create(project, runtime).LastCompletion);
        project.UpdateFrom(project.ToSummary() with { State = ProjectState.Paused });
        Assert.Equal("PAUSED", IndexingProgressPresentation.Create(project, runtime).StatusLabel);
        project.UpdateFrom(project.ToSummary() with { State = ProjectState.Active, Work = new(10, 10, 0, 0, DateTimeOffset.UtcNow.AddMinutes(5)) });
        Assert.Equal("RETRY SCHEDULED", IndexingProgressPresentation.Create(project, runtime).StatusLabel);
        project.SetFreshness(true, DateTimeOffset.UtcNow);
        Assert.Equal("STATUS UNAVAILABLE", IndexingProgressPresentation.Create(project, runtime).StatusLabel);
        project.SetFreshness(false, DateTimeOffset.UtcNow);
        project.UpdateFrom(project.ToSummary() with { PendingCount = 0, ReadyCount = 10, Work = new(0, 0, 0, 0, null) });
        Assert.Equal("IDLE", IndexingProgressPresentation.Create(project, runtime).StatusLabel);
    }

    private static ProjectSummary Summary(int total) => new(Guid.NewGuid(), "Repair fixture", ProjectState.Active,
        [], 1, total, total, total, 0, DateTimeOffset.UtcNow.AddDays(-1))
        { Work = new(total, 0, 0, 0, null), SearchableCount = total };
    private static VectorSnapshotMetadata Coverage(long generation, int covered) => new(generation, null, covered,
        TotalDocumentCount: 600, CompatibleDocumentCount: covered, RepairQueuedDocumentCount: 600 - covered);
}
