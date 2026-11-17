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
        Assert.Equal("Checking", project.SemanticIndexStatusLabel);
        Assert.Equal(AppStatusTone.Busy, project.SemanticIndexStatusTone);
        project.UpdateSemanticIndex(new(1, null, 2, TotalDocumentCount: 2, CompatibleDocumentCount: 2), true);
        Assert.Equal("Complete", project.SemanticIndexStatusLabel);
        Assert.Equal(AppStatusTone.Ready, project.SemanticIndexStatusTone);
        Assert.Contains("2 of 2", project.SemanticIndexStatusMessage);
        project.BeginSemanticIndexRefresh();
        Assert.Equal("Complete", project.SemanticIndexStatusLabel);
        project.FailSemanticIndexRefresh("Storage is busy.");
        Assert.Equal("Couldn't check", project.SemanticIndexStatusLabel);
        Assert.Contains("Last result: 2 of 2", project.SemanticIndexStatusMessage);
        project.UpdateSemanticIndex(new(1, null, 0), true);
        Assert.Equal("No files yet", project.SemanticIndexStatusLabel);
        Assert.False(project.IsSemanticSetupNeeded);
        project.UpdateSemanticIndex(null, false);
        Assert.Equal("Off", project.SemanticIndexStatusLabel);
        Assert.True(project.IsSemanticSetupNeeded);
        Assert.Contains("keyword search works without it", project.SemanticIndexStatusMessage);
    }

    [Fact]
    public void PublishedGenerationRetainsHistoricalCoverageWithoutClaimingCurrentComplete()
    {
        var summary = Summary(2, ready: 1, attention: 1);
        var project = new ProjectItemViewModel(summary);
        project.UpdateSemanticIndex(new(1, null, 2, TotalDocumentCount: 2, CompatibleDocumentCount: 2), true);
        Assert.True(project.IsSemanticCoverageComplete);
        project.UpdateFrom(summary with { SearchGeneration = 2 });
        Assert.Equal("Updating", project.SemanticIndexStatusLabel);
        Assert.Contains("2 of 2", project.SemanticIndexStatusMessage);
        Assert.Contains("Updating", project.SemanticIndexStatusMessage);
        Assert.False(project.IsSemanticCoverageComplete);
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
        Assert.StartsWith("12 issues in 9 files.", project.GroupedIssuesSummary);
        Assert.Equal("Indexing · 10 files left", project.StatusHeadline);
        Assert.Contains("1 waiting for CPU", project.QueuedBreakdownDisplay);
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
        Assert.Equal("Everything is indexed", project.StatusHeadline);
        Assert.StartsWith("0 files are searchable", project.StatusSummary);
        Assert.Contains("1 file had no text to index.", project.StatusSummary);
        Assert.Equal("Not yet", project.KeywordSearchLabel);
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
    public void StatusLinesAnswerHowTheProjectIsDoingInPlainWords()
    {
        var summary = Summary(total: 24, ready: 24) with { IndexedCount = 24, SearchableCount = 24, LastCompletedUtc = DateTimeOffset.UtcNow };
        var project = new ProjectItemViewModel(summary);
        Assert.Equal("Ready", project.Phase);
        Assert.Equal(AppStatusTone.Ready, project.StatusTone);
        Assert.Equal("Ready · 24 files", project.SidebarStatus);
        Assert.Equal("Everything is indexed", project.StatusHeadline);
        Assert.StartsWith("24 files are searchable by your AI assistant.", project.StatusSummary);
        Assert.StartsWith("Last indexed just now", project.IdleActivityDisplay);
        Assert.False(project.ShowProgress);

        project.UpdateFrom(summary with { State = ProjectState.Paused, PendingCount = 4, ReadyCount = 20,
            Work = new ProjectWorkSummary(4, 0, 0, 0, null) });
        Assert.Equal("Paused · 4 waiting", project.SidebarStatus);
        Assert.Equal("Paused · 4 files waiting", project.StatusHeadline);
        Assert.Equal(AppStatusTone.Warning, project.StatusTone);
        Assert.False(project.ShowProgress);

        project.UpdateFrom(summary with { ReadyCount = 21, AttentionCount = 3, ErrorCount = 4, ErrorFileCount = 3 });
        Assert.Equal("Needs attention · 3 files", project.SidebarStatus);
        Assert.Equal("3 files need attention", project.StatusHeadline);
        Assert.Equal(AppStatusTone.Error, project.StatusTone);
        Assert.False(project.ShowSidebarIssueCount);
    }

    [Fact]
    public void RepeatedPollingWithUnchangedStateRaisesNoPropertyChanges()
    {
        var summary = Summary(total: 10, pending: 2, ready: 8);
        var project = new ProjectItemViewModel(summary);
        var runtime = new IndexingTimingSnapshot([], null, 0);
        project.UpdateRuntime(runtime);
        var changed = new List<string?>();
        project.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        project.UpdateRuntime(runtime);
        Assert.Empty(changed);

        project.UpdateFrom(summary with { PendingCount = 1, ReadyCount = 9, Work = new ProjectWorkSummary(1, 0, 0, 0, null) });
        Assert.Contains(nameof(ProjectItemViewModel.StatusHeadline), changed);
        Assert.Contains(nameof(ProjectItemViewModel.SidebarStatus), changed);
        Assert.DoesNotContain(nameof(ProjectItemViewModel.Phase), changed);
    }

    [Fact]
    public void FolderRowsShowTheirOwnAccessProblem()
    {
        var first = new ProjectFolderInfo(Guid.NewGuid(), Path.Combine(Path.GetTempPath(), "Contracts"));
        var second = new ProjectFolderInfo(Guid.NewGuid(), Path.Combine(Path.GetTempPath(), "Notes"));
        var project = new ProjectItemViewModel(Summary(total: 2, ready: 2) with { Folders = [first, second] });
        Assert.Equal(["Contracts", "Notes"], project.FolderRows.Select(row => row.Name));
        Assert.All(project.FolderRows, row => Assert.False(row.HasIssue));

        project.UpdateFolderIssues([new ProjectFolderIssue(second.Id, second.Path, "Access denied.")]);
        Assert.Equal("Access denied.", project.FolderRows[1].Issue);
        Assert.Equal(AppStatusTone.Error, project.FolderRows[1].Tone);
        Assert.Equal("Needs attention", project.Phase);
        Assert.Equal("A folder can't be read", project.StatusHeadline);
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
}
