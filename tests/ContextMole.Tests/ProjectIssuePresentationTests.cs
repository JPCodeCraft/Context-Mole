using ContextMole.App.UI.ViewModels;
using ContextMole.Core;
using ContextMole.Indexing;

namespace ContextMole.Tests;

public sealed class ProjectIssuePresentationTests
{
    [Fact]
    public void ManyAttachmentIssuesRemainOneRootWithBoundedIdentifiableDetails()
    {
        var project = new ProjectItemViewModel(Summary(1000));
        var details = Enumerable.Range(0, 50).Select(index => Detail(index, $"mail/attachment[{index}]/same.pdf")).ToArray();
        var group = Group(details, issueCount: 1000) with { DetailsCursor = "next-details" };
        project.UpdateIssueGroups(Page(project.Id, [group], totalIssues: 1000));
        Assert.Single(project.IssueGroups);
        Assert.Equal(50, project.IssueGroups[0].Details.Count);
        Assert.Equal(50, project.IssueGroups[0].Details.Select(item => item.ComponentKey).Distinct().Count());
        Assert.True(project.IssueGroups[0].CanNextDetails);
        Assert.Contains("1000", project.IssueGroups[0].DetailsPageDisplay.Replace(",", ""));
        Assert.Contains("issue components", project.GroupedIssuesSummary);
        Assert.Equal(group.SourcePath, project.IssueGroups[0].SourcePath);
        Assert.Equal("Some content missing", project.IssueGroups[0].ImpactLabel);
    }

    [Theory]
    [InlineData(ProjectIssueRetryState.Queued, "Retry queued")]
    [InlineData(ProjectIssueRetryState.Running, "Retry running")]
    [InlineData(ProjectIssueRetryState.Scheduled, "Retry scheduled")]
    [InlineData(ProjectIssueRetryState.Exhausted, "Automatic retries exhausted")]
    [InlineData(ProjectIssueRetryState.Manual, "Manual action needed")]
    [InlineData(ProjectIssueRetryState.Paused, "Indexing paused")]
    public void ActualRetryStateIsLegible(ProjectIssueRetryState state, string expected)
    {
        var group = new ProjectIssueGroupViewModel(Group([Detail(1, "root")]) with
            { RetryState = state, NextRetryUtc = DateTimeOffset.UtcNow.AddMinutes(1) });
        Assert.Equal(expected, group.RetryLabel);
        if (state == ProjectIssueRetryState.Exhausted) Assert.Contains("new attempt budget", group.RetryMessage);
        if (state == ProjectIssueRetryState.Running) Assert.Contains("does not mean", group.RetryMessage);
        if (state == ProjectIssueRetryState.Manual) Assert.Contains("do not automatically", group.RetryMessage);
    }

    [Fact]
    public void AllHiddenRemainsAttentionAndOffersShowRestoreAndExactUndo()
    {
        var project = new ProjectItemViewModel(Summary(3));
        project.UpdateIssueGroups(new(project.Id, 1, 0, 1, 3, 3, [], null) { FilteredFileCount = 0 });
        Assert.True(project.HasIssueCard);
        Assert.False(project.IsReady);
        Assert.True(project.HasHiddenIssues);
        Assert.Contains("All matching current issues are hidden", project.NoMatchingIssuesMessage);
        Assert.Contains("3 components / 1 file", project.HiddenIssuesDisplay);
        var acknowledgement = Guid.NewGuid();
        project.RecordIssueHide(new([acknowledgement], 3));
        Assert.Equal(acknowledgement, Assert.Single(project.UndoAcknowledgements));
        Assert.True(project.HasUndoIssueHide);
        Assert.Contains("unchanged", project.IssueActionMessage);
        project.ClearIssueUndo();
        Assert.False(project.HasUndoIssueHide);
        project.ShowHiddenIssues = true;
        Assert.Equal(ProjectIssueVisibility.All, project.CreateIssueRequest().Visibility);
    }

    [Fact]
    public void CursorPagingIsBoundedAndFiltersInvalidateObsoleteRequests()
    {
        var project = new ProjectItemViewModel(Summary(1000));
        var groups = Enumerable.Range(0, 25).Select(index => Group([Detail(index, "root")]) with
            { SourcePath = $"/docs/file-{index:D4}.pdf", FileName = $"file-{index:D4}.pdf" }).ToArray();
        project.UpdateIssueGroups(Page(project.Id, groups, 1000, "next-path"));
        var version = project.IssueQueryVersion;
        Assert.Equal(25, project.IssueGroups.Count);
        Assert.True(project.CanNextIssuePage);
        project.MoveIssuePage(1);
        Assert.Equal("next-path", project.CreateIssueRequest().Cursor);
        Assert.True(project.CanPreviousIssuePage);
        Assert.True(project.IssueQueryVersion > version);
        project.IssueQuery = "mail";
        project.IssueCodeFilter = " access_denied ";
        project.IssueImpactFilterIndex = 1;
        Assert.Null(project.CreateIssueRequest().Cursor);
        Assert.Equal("mail", project.CreateIssueRequest().Query);
        Assert.Equal("access_denied", project.CreateIssueRequest().Code);
        Assert.Equal(ProjectIssueImpact.Unsearchable, project.CreateIssueRequest().Impact);
        Assert.False(project.CanPreviousIssuePage);
        Assert.Empty(project.IssueGroups);
    }

    [Fact]
    public void SameCountMaterialReplacementResetsObsoleteDetailsButPreservesExpansion()
    {
        var first = Group([Detail(1, "attachment[0]")], issueCount: 100) with { DetailsCursor = "version-one" };
        var group = new ProjectIssueGroupViewModel(first) { IsExpanded = true };
        group.MoveDetails(1);
        group.UpdateDetails(new([Detail(51, "attachment[50]")], null));
        Assert.True(group.CanPreviousDetails);
        var changed = first with { Details = [Detail(2, "attachment[1]")], DetailsCursor = "version-two" };
        group.UpdateFrom(changed);
        Assert.True(group.IsExpanded);
        Assert.False(group.CanPreviousDetails);
        Assert.True(group.DetailsWereRefreshed);
        Assert.Equal("attachment[1]", Assert.Single(group.Details).ComponentKey);
    }

    [Fact]
    public void StaleReadinessAndCoverageCannotLookCompleteAndRepeatedFreshnessDoesNotChatter()
    {
        var summary = Summary(0) with { ErrorCount = 0, ErrorFileCount = 0, AttentionCount = 0, ReadyCount = 1 };
        var project = new ProjectItemViewModel(summary);
        project.UpdateSemanticIndex(new(1, null, 1, TotalDocumentCount: 1, CompatibleDocumentCount: 1), true);
        Assert.True(project.IsReady);
        Assert.True(project.IsSemanticCoverageComplete);
        var checkedAt = DateTimeOffset.UtcNow;
        project.SetFreshness(true, checkedAt);
        Assert.False(project.IsReady);
        Assert.False(project.IsSemanticCoverageComplete);
        Assert.Equal("Status unavailable", project.Phase);
        Assert.Contains("STALE", project.SemanticIndexStatusLabel);
        var notifications = 0;
        project.PropertyChanged += (_, _) => notifications++;
        project.SetFreshness(true, checkedAt);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void InitialScanAndSettledEmptyAreHonestStates()
    {
        var project = new ProjectItemViewModel(Summary(0) with
            { DocumentCount = 0, IndexedCount = 0, SearchableCount = 0, ErrorCount = 0, ErrorFileCount = 0, AttentionCount = 0 });
        Assert.Equal("Awaiting first scan", project.Phase);
        Assert.False(project.IsReady);
        project.UpdateRuntime(new([], null, 0), false, true);
        Assert.Equal("No supported files", project.Phase);
        Assert.False(project.IsReady);
        Assert.Contains("scan completed", project.PhaseDetails);
        project.UpdateFrom(project.ToSummary() with { State = ProjectState.Paused });
        Assert.Contains("removed sources leave search", project.PhaseDetails);
    }

    [Fact]
    public void ExcludedManagementRendersOnlyOneBoundedPageAndFiltersExactPaths()
    {
        var project = new ProjectItemViewModel(Summary(0));
        project.UpdateExcludedFiles(Enumerable.Range(0, 1000)
            .Select(index => new ExcludedFileInfo($"/docs/file-{index:D4}.pdf", DateTimeOffset.UtcNow)).ToArray());
        Assert.Equal(1000, project.ExcludedFileCount);
        Assert.Equal(25, project.ExcludedFiles.Count);
        Assert.True(project.CanNextExcludedPage);
        project.MoveExcludedPage(1);
        Assert.True(project.CanPreviousExcludedPage);
        project.ExcludedQuery = "file-0999";
        Assert.Equal(1, project.MatchingExcludedFileCount);
        Assert.Equal("/docs/file-0999.pdf", Assert.Single(project.ExcludedFiles).SourcePath);
        Assert.False(project.CanPreviousExcludedPage);
        project.ExcludedQuery = "no-match";
        Assert.True(project.HasNoMatchingExcludedFiles);
        Assert.Empty(project.ExcludedFiles);
        Assert.Equal(1000, project.ExcludedFileCount);
    }

    [Fact]
    public void UnchangedPollRetainsRootAndDetailObjectsForKeyboardFocus()
    {
        var project = new ProjectItemViewModel(Summary(1));
        var source = Group([Detail(1, "attachment[0]")]);
        var page = Page(project.Id, [source], 1);
        project.UpdateIssueGroups(page);
        var root = Assert.Single(project.IssueGroups);
        var detail = Assert.Single(root.Details);
        var mutations = 0;
        project.IssueGroups.CollectionChanged += (_, _) => mutations++;
        root.Details.CollectionChanged += (_, _) => mutations++;
        project.UpdateIssueGroups(page with { Groups = [source with { Details = source.Details.ToArray() }] });
        Assert.Same(root, Assert.Single(project.IssueGroups));
        Assert.Same(detail, Assert.Single(root.Details));
        Assert.Equal(0, mutations);
    }

    [Fact]
    public void SustainedUnrelatedPollingPreservesFileAndExpandedComponentPages()
    {
        var summary = Summary(100);
        var project = new ProjectItemViewModel(summary);
        var source = Group([Detail(1, "attachment[0]")], issueCount: 100) with { DetailsCursor = "component-page-two" };
        project.UpdateIssueGroups(Page(project.Id, [source], 100, "stable-file-page-two"));
        project.MoveIssuePage(1);
        project.UpdateIssueGroups(Page(project.Id, [source], 100, "stable-file-page-three"));
        var root = Assert.Single(project.IssueGroups);
        root.IsExpanded = true;
        root.MoveDetails(1);
        root.UpdateDetails(new([Detail(51, "attachment[50]")], "component-page-three"));
        var component = Assert.Single(root.Details);
        for (var poll = 0; poll < 100; poll++)
        {
            project.UpdateFrom(summary with { SearchGeneration = poll + 2 });
            project.UpdateIssueGroups(Page(project.Id, [source with { Details = source.Details.ToArray() }], 100, "stable-file-page-three")
                with { IssuesChangedDuringPaging = true });
            Assert.Same(root, Assert.Single(project.IssueGroups));
            Assert.Same(component, Assert.Single(root.Details));
            Assert.True(root.IsExpanded);
            Assert.True(root.CanPreviousDetails);
            Assert.Equal("component-page-two", root.DetailsCursor);
            Assert.Equal("stable-file-page-two", project.CreateIssueRequest().Cursor);
            Assert.StartsWith("File page 2", project.IssuePageDisplay);
        }
        Assert.True(project.IssuesChangedDuringPaging);
        project.UpdateIssueGroups(Page(project.Id, [source], 100));
        Assert.True(project.IssuesChangedDuringPaging); // Keep the reminder until the user refreshes from the start.
        project.ResetIssuePages();
        Assert.False(project.IssuesChangedDuringPaging);
        Assert.Null(project.CreateIssueRequest().Cursor);
    }

    [Fact]
    public void RepairSeparatesLegacySourcePreparationFromEmbeddingOnlyWork()
    {
        var project = new ProjectItemViewModel(Summary(0));
        project.UpdateSemanticIndex(new(1, null, 1, TotalDocumentCount: 20, CompatibleDocumentCount: 1)
        { ReextractionRequiredDocumentCount = 12, EmbeddingRepairEligibleDocumentCount = 7 }, true);
        Assert.Contains("12 legacy files need source re-extraction", project.SemanticIndexStatusMessage);
        Assert.Contains("7 can reuse extracted text", project.SemanticIndexStatusMessage);
    }

    private static ProjectSummary Summary(int issues) => new(Guid.NewGuid(), "Project", ProjectState.Active,
        [], 1, 1, 0, 1, issues, null)
        { ReadyCount = issues == 0 ? 1 : 0, AttentionCount = issues == 0 ? 0 : 1, ErrorFileCount = issues == 0 ? 0 : 1 };
    private static ProjectIssueDetail Detail(long id, string component) => new(id, $"signature-{id}", null,
        component, "same.pdf", "extraction_failed", "This component could not be read.", true, 0, DateTimeOffset.UnixEpoch, false);
    private static ProjectIssueGroup Group(IReadOnlyList<ProjectIssueDetail> details, int issueCount = 1) => new(Guid.NewGuid(),
        "/docs/mail.eml", "mail.eml", ProjectIssueImpact.Partial, issueCount, 0, ProjectIssueRetryState.Manual,
        null, true, details, null);
    private static ProjectIssueListResponse Page(Guid projectId, IReadOnlyList<ProjectIssueGroup> groups, int totalIssues,
        string? nextCursor = null) => new(projectId, groups.Count, groups.Count, 0, totalIssues, 0, groups, nextCursor)
        { FilteredFileCount = groups.Count, FilteredIssueCount = totalIssues };
}
