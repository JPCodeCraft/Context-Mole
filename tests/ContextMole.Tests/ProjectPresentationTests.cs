using ContextMole.App.UI.ViewModels;
using ContextMole.Core;
using ContextMole.Indexing;

namespace ContextMole.Tests;

public sealed class ProjectPresentationTests
{
    [Fact]
    public void SemanticCoverageDistinguishesLoadingCompleteEmptyUnavailableAndFailure()
    {
        var project = new ProjectItemViewModel(Summary(2, ready: 2));
        Assert.Equal("CHECKING", project.SemanticIndexStatusLabel);
        project.UpdateSemanticIndex(new(1, null, 2, TotalDocumentCount: 2, CompatibleDocumentCount: 2), true);
        Assert.Equal("COMPLETE", project.SemanticIndexStatusLabel);
        Assert.Contains("2 of 2", project.SemanticIndexStatusMessage);
        project.BeginSemanticIndexRefresh();
        Assert.Equal("COMPLETE", project.SemanticIndexStatusLabel);
        project.FailSemanticIndexRefresh("Storage is busy.");
        Assert.Equal("UNABLE TO CHECK", project.SemanticIndexStatusLabel);
        Assert.Contains("Last verified: 2 of 2", project.SemanticIndexStatusMessage);
        project.UpdateSemanticIndex(new(1, null, 0), true);
        Assert.Equal("NO INDEXED FILES", project.SemanticIndexStatusLabel);
        project.UpdateSemanticIndex(null, false);
        Assert.Equal("KEYWORD ONLY", project.SemanticIndexStatusLabel);
        Assert.Contains("Keyword search remains", project.SemanticIndexStatusMessage);
    }

    [Fact]
    public void PublishedGenerationRetainsHistoricalCoverageWithoutClaimingCurrentComplete()
    {
        var summary = Summary(2, ready: 1, attention: 1);
        var project = new ProjectItemViewModel(summary);
        project.UpdateSemanticIndex(new(1, null, 2, TotalDocumentCount: 2, CompatibleDocumentCount: 2), true);
        project.UpdateErrors(Errors(summary.Id, firstId: 1, count: 1));
        Assert.True(project.IsSemanticCoverageComplete);
        Assert.True(project.HasRecentErrors);
        project.UpdateFrom(summary with { SearchGeneration = 2 });
        Assert.Equal("REFRESHING COVERAGE", project.SemanticIndexStatusLabel);
        Assert.Contains("Last verified", project.SemanticIndexStatusMessage);
        Assert.Contains("refreshing", project.SemanticIndexStatusMessage);
        Assert.False(project.IsSemanticCoverageComplete);
        Assert.False(project.HasRecentErrors);
    }

    [Fact]
    public void ProjectActionsDisableTogetherAndResumeWithTheirEligibilityRules()
    {
        var project = new ProjectItemViewModel(Summary(3, ready: 2, attention: 1));
        Assert.True(project.CanReindex);
        Assert.True(project.CanRetryFailedFiles);
        project.SetActionsBusy(true);
        Assert.False(project.AreActionsEnabled);
        Assert.False(project.CanReindex);
        Assert.False(project.CanRetryFailedFiles);
        project.SetActionsBusy(false);
        Assert.True(project.CanReindex);
        project.UpdateFrom(project.ToSummary() with { State = ProjectState.Paused });
        Assert.False(project.CanReindex);
        Assert.False(project.CanRetryFailedFiles);
        Assert.True(project.AreActionsEnabled);
    }

    [Fact]
    public void ProgressExplainsUpToDateFilesAndDoesNotEstimateStagePercentages()
    {
        var project = new ProjectItemViewModel(Summary(3, pending: 1, ready: 1, attention: 1) with { IndexedCount = 3 });
        Assert.Equal(3, project.ProgressMaximum);
        Assert.StartsWith("1 of 3 files up to date", project.ProgressDescription);
        var activity = new IndexingActivityItemViewModel(Activity(project.Id, IndexingPipelineStage.ExtractingContent));
        Assert.True(activity.IsProgressIndeterminate);
        activity.UpdateFrom(Activity(project.Id, IndexingPipelineStage.WritingIndex) with { JobId = activity.JobId });
        Assert.True(activity.IsProgressIndeterminate);
    }
    [Fact]
    public void ProcessorWaitIsQueuedAndDisplayedBucketsStillSumToAllFiles()
    {
        var summary = Summary(total: 117, pending: 10, ready: 100, attention: 7) with
        {
            IndexedCount = 105,
            ErrorCount = 12,
            ErrorFileCount = 9,
            Work = new ProjectWorkSummary(8, 3, 2, 0, DateTimeOffset.UtcNow.AddMinutes(1))
        };
        var project = new ProjectItemViewModel(summary);
        project.UpdateRuntime(new IndexingTimingSnapshot(
            [Activity(summary.Id, IndexingPipelineStage.ExtractingContent),
             Activity(summary.Id, IndexingPipelineStage.WaitingForCpu)], null, 0));

        Assert.Equal(1, project.ProcessingCount);
        Assert.Equal(9, project.QueuedCount);
        Assert.Equal(1, project.WaitingForCpuCount);
        Assert.Equal(3, project.RetryScheduledCount);
        Assert.Equal(project.DocumentCount,
            project.ReadyCount + project.ProcessingCount + project.QueuedCount + project.AttentionCount);
        Assert.Equal(105, project.IndexedCount);
        Assert.Contains("affecting 9 files", project.RecentErrorsSummary);
    }

    [Fact]
    public void RuntimeAheadOfDurableSnapshotDoesNotInflatePendingFiles()
    {
        var summary = Summary(total: 1, pending: 1);
        var project = new ProjectItemViewModel(summary);
        project.UpdateRuntime(new IndexingTimingSnapshot(
            [Activity(summary.Id, IndexingPipelineStage.ExtractingContent)], null, 0));

        Assert.Equal(0, project.ProcessingCount);
        Assert.Equal(1, project.QueuedCount);
        Assert.Equal(project.PendingCount, project.ProcessingCount + project.QueuedCount);
    }

    [Fact]
    public void EmptyIndexedFilesCanBeUpToDateWithoutClaimingSearchableContent()
    {
        var project = new ProjectItemViewModel(Summary(total: 1, ready: 1) with { SearchableCount = 0 });
        Assert.Equal(1, project.ReadyCount);
        Assert.Equal(1, project.IndexedCount);
        Assert.Equal(0, project.SearchableCount);
        Assert.True(project.IsReady);
        Assert.StartsWith("0 files are searchable now.", project.SearchableSummary);
        Assert.Equal(0, project.ToSummary().SearchableCount);
    }

    [Fact]
    public void ProjectIssuesDoNotOfferAFileRetryAndUnindexedFilesDoNotClaimReady()
    {
        var summary = Summary(total: 1, attention: 1) with { ErrorCount = 0, ErrorFileCount = 0 };
        var project = new ProjectItemViewModel(summary);
        Assert.Equal("Needs attention", project.Phase);
        Assert.False(project.IsReady);
        project.UpdateFrom(summary with { ErrorCount = 1 });
        Assert.True(project.HasErrors);
        Assert.False(project.CanRetryFailedFiles);
    }

    [Fact]
    public void DiscoveryAndPauseCleanupAreVisibleBeforeSettledState()
    {
        var summary = Summary(total: 1, pending: 1);
        var project = new ProjectItemViewModel(summary);
        project.UpdateRuntime(new IndexingTimingSnapshot([], null, 0), isDiscovering: true);
        Assert.Equal("Finding files", project.Phase);
        Assert.False(project.IsReady);

        var active = new IndexingTimingSnapshot([Activity(summary.Id, IndexingPipelineStage.ExtractingContent)], null, 0);
        project.UpdateRuntime(active);
        project.UpdateFrom(summary with { State = ProjectState.Paused });
        Assert.Equal("Pausing", project.Phase);
        Assert.True(project.IsPaused);
        Assert.False(project.CanRetryFailedFiles);

        project.UpdateRuntime(new IndexingTimingSnapshot([], null, 0));
        Assert.Equal("Paused", project.Phase);
        Assert.Contains("1 file will continue", project.PhaseDetails);
        Assert.Equal(1, project.QueuedCount);
    }

    [Fact]
    public void ResolvedIssuesClearImmediatelyAndClampAnObsoletePage()
    {
        var summary = Summary(total: 100, attention: 100);
        var project = new ProjectItemViewModel(summary);
        project.MoveErrorPage(3);
        project.UpdateErrors(Errors(summary.Id, firstId: 76, count: 25));
        Assert.Equal(75, project.ErrorPageOffset);
        Assert.Equal(25, project.RecentErrors.Count);

        project.UpdateFrom(summary with { ErrorCount = 2, ErrorFileCount = 2, AttentionCount = 2, ReadyCount = 98 });
        Assert.Equal(0, project.ErrorPageOffset);
        Assert.Empty(project.RecentErrors);
        Assert.True(project.HasErrors);
        project.UpdateErrors(Errors(summary.Id, firstId: 99, count: 2));
        Assert.Equal(2, project.RecentErrors.Count);

        project.UpdateFrom(summary with { ErrorCount = 0, ErrorFileCount = 0, AttentionCount = 0, ReadyCount = 100 });
        Assert.False(project.HasErrors);
        Assert.False(project.HasRecentErrors);
        Assert.Empty(project.RecentErrors);
    }

    [Fact]
    public void IssuePagesStayBoundedAndRetainCollapsePreferenceDuringRefresh()
    {
        var summary = Summary(total: 5000, attention: 5000);
        var project = new ProjectItemViewModel(summary) { IsErrorsExpanded = false };
        project.UpdateErrors(Errors(summary.Id, firstId: 1, count: ProjectItemViewModel.ErrorPageSize));
        project.UpdateFrom(summary);
        Assert.True(project.IsErrorsCollapsed);
        Assert.False(project.CanGoToPreviousErrorPage);
        Assert.True(project.CanGoToNextErrorPage);

        project.MoveErrorPage(int.MaxValue);
        Assert.Equal(199, project.ErrorPageIndex);
        Assert.Empty(project.RecentErrors);
        project.UpdateErrors(Errors(summary.Id, firstId: 4976, count: ProjectItemViewModel.ErrorPageSize));
        Assert.Equal(25, project.RecentErrors.Count);
        Assert.True(project.CanGoToPreviousErrorPage);
        Assert.False(project.CanGoToNextErrorPage);
        project.IsErrorsLoading = true;
        Assert.False(project.CanGoToPreviousErrorPage);
    }

    private static ProjectSummary Summary(int total, int pending = 0, int ready = 0, int attention = 0) =>
        new(Guid.NewGuid(), "Project state", ProjectState.Active, [], 1, total, pending, ready,
            attention, null)
        {
            ReadyCount = ready,
            AttentionCount = attention,
            ErrorFileCount = attention,
            Work = new ProjectWorkSummary(pending, 0, 0, 0, null)
        };

    private static IndexingActivitySnapshot Activity(Guid projectId, IndexingPipelineStage stage) =>
        new(Guid.NewGuid(), projectId, Guid.NewGuid(), "example.pdf", stage,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow);

    private static IReadOnlyList<ProjectErrorInfo> Errors(Guid projectId, int firstId, int count) =>
        Enumerable.Range(firstId, count).Select(id => new ProjectErrorInfo(id, projectId, Guid.NewGuid(),
            "extraction_failed", "File could not be read.", true, 1, DateTimeOffset.UtcNow, $"file-{id}.pdf")).ToArray();
}
