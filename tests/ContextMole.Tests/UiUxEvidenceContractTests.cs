using ContextMole.Core;
using ContextMole.Search;

namespace ContextMole.Tests;

/// <summary>Cross-surface contracts: acknowledgement is presentation-only; exclusion removes evidence.</summary>
[Collection(nameof(SqliteIntegrationCollection))]
public sealed class UiUxEvidenceContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ProjectAccessibleContentUsesItsNameInsteadOfATypeName()
    {
        var project = new ContextMole.App.UI.ViewModels.ProjectItemViewModel(new(Guid.NewGuid(),
            "Quarterly customer evidence", ProjectState.Active, [], 0, 0, 0, 0, 0, null));
        Assert.Equal("Quarterly customer evidence", project.ToString());
    }

    [Fact]
    public void OlderUnlocatedPartialIssueDoesNotPretendToIdentifyTheRootComponent()
    {
        var source = new ProjectIssueDetail(1, "signature", null, "root", null,
            "malformed_document", "report.pdf could not be read", false, 0, DateTimeOffset.UtcNow, false)
            { OccurrenceCount = 3 };
        var detail = new ContextMole.App.UI.ViewModels.ProjectIssueDetailViewModel(source);
        Assert.True(detail.HasUnrecordedComponentLocation);
        Assert.Contains("not recorded", detail.ComponentName);
        Assert.Contains("Retry the root file", detail.ComponentKey);
        Assert.Contains("Observed 3 times", detail.CreatedDisplay);
    }

    [Fact]
    public void OnlyExpectedLinuxTrayCancellationDuringQuitIsHandled()
    {
        var tray = new TrayCancellation();
        Assert.True(ContextMole.App.UI.DesktopShutdownExceptionPolicy.IsExpectedTrayCancellation(true, true, tray));
        Assert.False(ContextMole.App.UI.DesktopShutdownExceptionPolicy.IsExpectedTrayCancellation(false, true, tray));
        Assert.False(ContextMole.App.UI.DesktopShutdownExceptionPolicy.IsExpectedTrayCancellation(true, false, tray));
        Assert.False(ContextMole.App.UI.DesktopShutdownExceptionPolicy.IsExpectedTrayCancellation(true, true,
            new OperationCanceledException()));
        Assert.False(ContextMole.App.UI.DesktopShutdownExceptionPolicy.IsExpectedTrayCancellation(true, true,
            new InvalidOperationException("A genuine runtime failure")));
    }

    private sealed class TrayCancellation : OperationCanceledException
    {
        public override string StackTrace => "at Avalonia.FreeDesktop.DBusTrayIconImpl.WatchAsync()";
    }

    [Theory]
    [InlineData("ocr_setup_failed", "Settings")]
    [InlineData("ocr_platform_unsupported", "supported platform")]
    [InlineData("ocr_timeout", "source opens")]
    [InlineData("malformed_document", "verify that the source opens")]
    [InlineData("attachment_count_limit", "split the source")]
    public void CommonIssueCausesHaveActionableAndAccurateRecovery(string code, string guidance)
    {
        var detail = new ProjectIssueDetail(1, "signature", Guid.NewGuid(), "root", "document", code,
            "Authored test failure", true, 1, DateTimeOffset.UtcNow, false);
        var group = new ContextMole.App.UI.ViewModels.ProjectIssueGroupViewModel(new(Guid.NewGuid(),
            "/fixture/document.pdf", "document.pdf", ProjectIssueImpact.Unsearchable, 1, 0,
            ProjectIssueRetryState.Manual, null, true, [detail], null));
        Assert.Contains(guidance, group.RecoveryGuidance);
    }

    [Fact]
    public void PartialImpactDoesNotInferWholeRootCoverageFromOneVisibleDetailPage()
    {
        var semantic = new ProjectIssueDetail(1, "semantic", Guid.NewGuid(), "root", "document",
            "embedding_failed", "Embedding repair failed", true, 0, DateTimeOffset.UtcNow, false);
        var group = new ContextMole.App.UI.ViewModels.ProjectIssueGroupViewModel(new(Guid.NewGuid(),
            "/fixture/document.eml", "document.eml", ProjectIssueImpact.Partial, 2, 1,
            ProjectIssueRetryState.Manual, null, true, [semantic], null));
        Assert.Contains("missing or incomplete", group.ImpactMessage);
        Assert.DoesNotContain("Meaning-based search may be incomplete", group.ImpactMessage);
    }

    [Fact]
    public void ResolvedOrResurfacedAcknowledgementsCannotOfferAStaleHideUndo()
    {
        var project = new ContextMole.App.UI.ViewModels.ProjectItemViewModel(new(Guid.NewGuid(),
            "Reviewed project", ProjectState.Active, [], 0, 1, 0, 1, 1, null));
        project.RecordIssueHide(new([Guid.NewGuid()], 1));
        Assert.True(project.HasUndoIssueHide);
        project.UpdateIssueGroups(new(project.Id, 1, 1, 0, 1, 0, [], null));
        Assert.False(project.HasUndoIssueHide);
    }

    [Fact]
    public async Task HiddenPartialFailureKeepsKeywordLiteralAndCoverageEvidenceAndUndoRestoresIt()
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await database.CreateProjectAsync("Acknowledged partial evidence", Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "message.eml");
        await File.WriteAllTextAsync(path, "authored root email", Token);
        var pending = await database.ObserveAndLeaseAsync(project, folder, path, false, Token);
        var committed = await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length,
            new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero), "orchid lantern evidence", false,
            errors: [new ExtractionError("malformed_document", "Attachment report.pdf could not be read.", false,
                "report.pdf") { ComponentKey = "root/attachment:1:report.pdf" }], cancellationToken: Token);
        var generation = await database.Store.GetSearchGenerationAsync(project, Token);
        var before = (await database.Store.ListProjectsAsync(Token)).Single(p => p.Id == project);
        var query = StructuredSearchQuery.BuildFtsQuery([new SearchClause("term", "orchid")], 1);
        Assert.Single((await database.Store.KeywordSearchAsync(project, query, 10, null, Token)).Candidates);

        var acknowledgement = await database.Writer.HideProjectIssuesAsync(project, path, Token);
        Assert.NotEmpty(acknowledgement.AcknowledgementIds);
        var visible = await database.Store.ListProjectIssuesAsync(new(project), Token);
        Assert.Empty(visible.Groups);
        Assert.Equal(1, visible.HiddenIssueCount);
        Assert.Single((await database.Store.ListProjectIssuesAsync(new(project,
            Visibility: ProjectIssueVisibility.Hidden), Token)).Groups);
        var after = (await database.Store.ListProjectsAsync(Token)).Single(p => p.Id == project);
        Assert.Equal(before.ErrorCount, after.ErrorCount);
        Assert.Equal(before.AttentionCount, after.AttentionCount);
        Assert.Equal(before.SearchableCount, after.SearchableCount);
        Assert.Equal(generation, await database.Store.GetSearchGenerationAsync(project, Token));
        Assert.Single((await database.Store.KeywordSearchAsync(project, query, 10, null, Token)).Candidates);
        Assert.Single(await database.Store.ReadPassagesAsync(project, [committed.PassageId], 0, 0,
            generation, Token));

        await database.Writer.RestoreProjectIssueAcknowledgementsAsync(project,
            acknowledgement.AcknowledgementIds, Token);
        Assert.Single((await database.Store.ListProjectIssuesAsync(new(project), Token)).Groups);
        Assert.Equal(0, (await database.Store.ListProjectIssuesAsync(new(project), Token)).HiddenIssueCount);
    }

    [Fact]
    public async Task ExactExclusionInvalidatesOldReadsAndDoesNotExcludeSameBasenameOrAnotherProject()
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await database.CreateProjectAsync("Exact exclusion scope", Token);
        var (otherProject, otherFolder) = await database.CreateProjectAsync("Other project scope", Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "report.txt");
        var nested = Path.Combine(database.Paths.SourceDirectory, "nested", "report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        await File.WriteAllTextAsync(path, "retained evidence", Token);
        await File.WriteAllTextAsync(nested, "different evidence", Token);
        var first = await database.ObserveAndLeaseAsync(project, folder, path, false, Token);
        var committed = await database.CommitAsync(first.Job, first.Sha256, first.File.Length,
            new DateTimeOffset(first.File.LastWriteTimeUtc, TimeSpan.Zero), "iris retained evidence", false,
            cancellationToken: Token);
        var second = await database.ObserveAndLeaseAsync(project, folder, nested, false, Token);
        await database.CommitAsync(second.Job, second.Sha256, second.File.Length,
            new DateTimeOffset(second.File.LastWriteTimeUtc, TimeSpan.Zero), "iris other basename evidence", false,
            cancellationToken: Token);
        var third = await database.ObserveAndLeaseAsync(otherProject, otherFolder, path, false, Token);
        await database.CommitAsync(third.Job, third.Sha256, third.File.Length,
            new DateTimeOffset(third.File.LastWriteTimeUtc, TimeSpan.Zero), "iris other project evidence", false,
            cancellationToken: Token);
        var generation = await database.Store.GetSearchGenerationAsync(project, Token);
        var sourceHash = await StorageTestDatabase.HashAsync(path, Token);

        Assert.True((await database.Writer.ExcludeFileAsync(project, path, Token)).Changed);
        Assert.True(await database.Store.IsFileExcludedAsync(project, path, Token));
        Assert.False(await database.Store.IsFileExcludedAsync(project, nested, Token));
        Assert.False(await database.Store.IsFileExcludedAsync(otherProject, path, Token));
        var query = StructuredSearchQuery.BuildFtsQuery([new SearchClause("term", "iris")], 1);
        var remaining = Assert.Single((await database.Store.KeywordSearchAsync(project, query, 10, null,
            Token)).Candidates);
        Assert.Equal(nested, remaining.SourcePath);
        Assert.Single((await database.Store.KeywordSearchAsync(otherProject, query, 10, null, Token)).Candidates);
        var stale = await Assert.ThrowsAsync<ContextMoleException>(() => database.Store.ReadPassagesAsync(project,
            [committed.PassageId], 0, 0, generation, Token));
        Assert.Equal("index_changed", stale.Code);
        var unavailable = Assert.Single(await database.Store.ReadPassagesAsync(project,
            [committed.PassageId], 0, 0, Token));
        Assert.Equal("stale_passage", unavailable.ErrorCode);
        Assert.True(string.IsNullOrEmpty(unavailable.Text));
        Assert.True(string.IsNullOrEmpty(unavailable.SourcePath));
        Assert.Equal(sourceHash, await StorageTestDatabase.HashAsync(path, Token));

        // A replacement at the same path remains excluded until explicitly included.
        await File.WriteAllTextAsync(path, "replacement evidence", Token);
        var replaced = new FileInfo(path);
        await database.Writer.ObserveFileAsync(new(project, folder, path, replaced.Length,
            new DateTimeOffset(replaced.LastWriteTimeUtc, TimeSpan.Zero), Force: true), Token);
        await database.Writer.RequestReindexAsync(project, Token);
        while (await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token) is { } job)
        {
            Assert.NotEqual(path, job.SourcePath);
            await database.Writer.FailJobAsync(job, "test_stop", "Unrelated reindex test work stopped.", false, Token);
        }
        Assert.True((await database.Writer.IncludeFileAsync(project, path, Token)).Queued);
        Assert.False(await database.Store.IsFileExcludedAsync(project, path, Token));
        var included = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token);
        Assert.NotNull(included);
        Assert.Equal(path, included.SourcePath);
    }
}
