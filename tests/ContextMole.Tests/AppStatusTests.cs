using ContextMole.App.UI.ViewModels;
using ContextMole.Core;
using ContextMole.Indexing;
using ContextMole.Infrastructure;

namespace ContextMole.Tests;

public sealed class AppStatusTests
{
    [Fact]
    public void InitialLoadingFailureAndEmptyIndexAreDifferentAndRecoveryClearsFailure()
    {
        var state = new AppPresentationState();
        Assert.Equal(AppStatusTone.Busy, state.CurrentStatus([]).Tone);
        Assert.False(state.HasLoadedProjects);
        state.ProjectsFailed("Database temporarily unavailable.");
        Assert.Equal(AppStatusTone.Error, state.CurrentStatus([]).Tone);
        Assert.Contains("outdated", state.CurrentStatus([]).Details);
        state.ProjectsLoaded();
        Assert.Null(state.RefreshError);
        Assert.Equal("Create a project to begin", state.CurrentStatus([]).Message);
    }

    [Fact]
    public void StatusRecoversWithoutChangingProjectCountAndDoesNotHideAnotherIssue()
    {
        var project = Project(2, ready: 2);
        var state = new AppPresentationState();
        state.ProjectsLoaded();
        state.Notify("ocr", null, "OCR needs repair.", isError: true);
        Assert.Equal(AppStatusTone.Ready, state.CurrentStatus([project]).Tone);
        state.ProjectsFailed("Temporary read failure.");
        Assert.Equal(AppStatusTone.Error, state.CurrentStatus([project]).Tone);
        state.ProjectsLoaded();
        Assert.Equal(AppStatusTone.Ready, state.CurrentStatus([project]).Tone);
        Assert.Equal("OCR needs repair.", state.VisibleNotification(project.Id)?.Message);
    }

    [Fact]
    public void FooterIncludesOtherProjectsWorkAndIssuesAlongsidePausedProjects()
    {
        var paused = Project(2, pending: 2, paused: true);
        var working = Project(3, pending: 2, ready: 1, processing: 1);
        var attention = Project(1, attention: 1);
        var state = new AppPresentationState();
        state.ProjectsLoaded();
        var status = state.CurrentStatus([paused, working, attention]);
        Assert.Contains("1 file processing", status.Message);
        Assert.DoesNotContain("queued", status.Message);
        Assert.Contains("1 queued", status.Details);
        Assert.Contains("1 project needs attention", status.Details);
        Assert.Contains("1 paused", status.Details);
        Assert.Equal(AppStatusTone.Busy, status.Tone);
    }

    [Fact]
    public void PauseCleanupAndQueuedWorkSettleToCurrentState()
    {
        var project = Project(1, pending: 1, paused: true);
        project.UpdateRuntime(new([Activity(project.Id)], null, 0));
        var state = new AppPresentationState();
        state.ProjectsLoaded();
        Assert.Contains("pause cleanup", state.CurrentStatus([project]).Message);
        project.UpdateRuntime(new([], null, 0));
        Assert.Equal("All projects paused", state.CurrentStatus([project]).Message);
        Assert.DoesNotContain("queued", state.CurrentStatus([project]).Message);
        project.UpdateFrom(project.ToSummary() with { State = ProjectState.Active });
        Assert.Equal("1 file queued", state.CurrentStatus([project]).Message);
        project.UpdateFrom(project.ToSummary() with { PendingCount = 0, ReadyCount = 1, Work = new(0, 0, 0, 0, null) });
        Assert.Equal(AppStatusTone.Ready, state.CurrentStatus([project]).Tone);
    }

    [Fact]
    public void DiscoveryDoesNotClaimReadyWhenNoFilesAreKnownYet()
    {
        var project = Project(0);
        var state = new AppPresentationState();
        state.ProjectsLoaded();
        Assert.Equal("No supported files found", state.CurrentStatus([project]).Message);
        project.UpdateRuntime(new([], null, 0), isDiscovering: true);
        Assert.Contains("Finding files", state.CurrentStatus([project]).Message);
        Assert.Equal(AppStatusTone.Busy, state.CurrentStatus([project]).Tone);
    }

    [Fact]
    public void FooterChoosesOnePrimaryStateAndKeepsSimultaneousIssuesInDetails()
    {
        var state = new AppPresentationState();
        state.ProjectsLoaded();
        var discovering = Project(0);
        discovering.UpdateRuntime(new([], null, 0), isDiscovering: true);
        var queued = Project(2, pending: 2);
        var pausing = Project(1, pending: 1, paused: true);
        pausing.UpdateRuntime(new([Activity(pausing.Id)], null, 0));
        var attention = Project(1, attention: 1);
        Assert.Equal("Finding files", state.CurrentStatus([discovering, queued, pausing, attention]).Message);
        Assert.Equal("2 files queued", state.CurrentStatus([queued, pausing, attention]).Message);
        Assert.Equal("Finishing pause cleanup", state.CurrentStatus([pausing, attention]).Message);
        Assert.Equal(AppStatusTone.Busy, state.CurrentStatus([pausing, attention]).Tone);
        Assert.Contains("project needs attention", state.CurrentStatus([pausing, attention]).Details);
        Assert.Equal(AppStatusTone.Warning, state.CurrentStatus([attention]).Tone);
        attention.UpdateFrom(attention.ToSummary() with { State = ProjectState.Paused });
        Assert.Equal("All projects paused", state.CurrentStatus([attention]).Message);
        Assert.Contains("project needs attention", state.CurrentStatus([attention]).Details);
    }

    [Fact]
    public void SuccessfulAcknowledgmentExpiresAtFiveSecondsAndIsProjectScoped()
    {
        var time = new ManualTime();
        var state = new AppPresentationState(time);
        var id = Guid.NewGuid();
        state.Notify("retry", id, "Queued retry.");
        Assert.Null(state.VisibleNotification(Guid.NewGuid()));
        Assert.NotNull(state.VisibleNotification(id));
        time.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.NotNull(state.VisibleNotification(id));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null(state.VisibleNotification(id));
    }

    [Fact]
    public void UnrelatedSuccessCannotHideOrClearAnUnresolvedFailure()
    {
        var time = new ManualTime();
        var state = new AppPresentationState(time);
        var id = Guid.NewGuid();
        state.Notify("coverage", id, "Coverage query failed.", isError: true);
        state.Notify("cpu", null, "CPU profile changed.");
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("Coverage query failed.", state.VisibleNotification(id)?.Message);
        state.Clear("cpu");
        Assert.NotNull(state.VisibleNotification(id));
        state.Clear("coverage", id);
        Assert.Null(state.VisibleNotification(id));
    }

    [Fact]
    public void DismissingAnOldNotificationCannotDismissItsReplacement()
    {
        var state = new AppPresentationState();
        state.Notify("settings", null, "First message.");
        var old = state.VisibleNotification(null);
        state.Notify("settings", null, "Second message.");
        state.Dismiss(old);
        Assert.Equal("Second message.", state.VisibleNotification(null)?.Message);
    }

    [Fact]
    public void RefreshStampRejectsOldSelectionGenerationPolicyAvailabilityAndRequest()
    {
        var id = Guid.NewGuid();
        var stamp = new UiRefreshStamp(id, 4, "policy-a", true, 10);
        Assert.True(stamp.IsCurrent(id, 4, "policy-a", true, 10));
        Assert.False(stamp.IsCurrent(Guid.NewGuid(), 4, "policy-a", true, 10));
        Assert.False(stamp.IsCurrent(id, 5, "policy-a", true, 10));
        Assert.False(stamp.IsCurrent(id, 4, "policy-b", true, 10));
        Assert.False(stamp.IsCurrent(id, 4, "policy-a", false, 10));
        Assert.False(stamp.IsCurrent(id, 4, "policy-a", true, 11));
    }

    [Theory]
    [InlineData(AiConnectionState.Conflict)]
    [InlineData(AiConnectionState.ServerUnavailable)]
    public void CheckAgainIsAReadOnlyActionForBlockedAiConfigurations(AiConnectionState status)
    {
        var client = new AiClientDefinition("test", "Test", "Test client");
        var connection = new AiConnectionItemViewModel(client) { IsBusy = false };
        connection.Apply(new(client, status, "Needs checking."));
        Assert.Equal("Check again", connection.ActionLabel);
        Assert.True(connection.RequiresReadOnlyCheck);
        Assert.False(connection.IsConfigured);
    }

    private static ProjectItemViewModel Project(int count, int pending = 0, int ready = 0, int attention = 0,
        int processing = 0, bool paused = false) => new(new ProjectSummary(Guid.NewGuid(), "Project",
        paused ? ProjectState.Paused : ProjectState.Active, [], 1, count, pending, ready, attention, null)
    {
        ReadyCount = ready, AttentionCount = attention,
        Work = new(Math.Max(0, pending - processing), 0, processing, 0, null)
    });

    private static IndexingActivitySnapshot Activity(Guid projectId) => new(Guid.NewGuid(), projectId,
        Guid.NewGuid(), "file.pdf", IndexingPipelineStage.ExtractingContent, TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow);

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
