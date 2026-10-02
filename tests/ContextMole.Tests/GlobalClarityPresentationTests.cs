using ContextMole.App.UI;
using ContextMole.App.UI.ViewModels;
using ContextMole.Core;
using ContextMole.Infrastructure;

namespace ContextMole.Tests;

public sealed class GlobalClarityPresentationTests
{
    [Fact]
    public void LaterRefreshFailureRetainsLastCheckedTimeAndHasStaleDataUntilRecovery()
    {
        var time = new ManualTime();
        var state = new AppPresentationState(time);
        state.ProjectsLoaded();
        var first = state.LastProjectsLoadedUtc;
        time.Advance(TimeSpan.FromMinutes(5));
        state.ProjectsFailed("Index temporarily unavailable.");
        Assert.True(state.HasStaleProjectData);
        Assert.Equal(first, state.LastProjectsLoadedUtc);
        Assert.Equal(AppStatusTone.Error, state.CurrentStatus([]).Tone);
        state.ProjectsLoaded();
        Assert.False(state.HasStaleProjectData);
        Assert.Equal(time.GetUtcNow(), state.LastProjectsLoadedUtc);
    }

    [Fact]
    public void NewActionFeedbackIsVisibleAndUnresolvedOlderFailureReturnsAfterItExpires()
    {
        var time = new ManualTime();
        var state = new AppPresentationState(time);
        state.Notify("ocr", null, "OCR needs attention.", isError: true);
        state.Notify("save", null, "Project saved.");
        Assert.Equal("Project saved.", state.VisibleNotification(null)?.Message);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("OCR needs attention.", state.VisibleNotification(null)?.Message);
    }

    [Fact]
    public void OperationCompletionKeepsNewerSameSourceResultAndClearsOnlyOldFailure()
    {
        var state = new AppPresentationState();
        var projectId = Guid.NewGuid();
        state.Notify("retry_action", projectId, "An earlier retry failed.", isError: true);
        var before = state.NotificationFor("retry_action", projectId);
        state.Notify("retry_action", projectId, "Queued 1 failed file for retry.");
        state.Dismiss(before);
        Assert.Equal("Queued 1 failed file for retry.", state.VisibleNotification(projectId)?.Message);
        var unchanged = state.NotificationFor("retry_action", projectId);
        state.Dismiss(unchanged);
        Assert.Null(state.NotificationFor("retry_action", projectId));
    }

    [Fact]
    public void UnchosenOptionalClientFailureDoesNotContributeOverallAttention()
    {
        var client = new AiClientDefinition("optional", "Optional tool", "Optional configuration");
        var row = new AiConnectionItemViewModel(client) { IsBusy = false };
        row.Apply(new(client, AiConnectionState.ServerUnavailable, "Bundle unavailable."));
        Assert.True(row.IsErrorStatus);
        Assert.False(row.NeedsAttention);
        row.BeginOperation(AiConnectionOperation.Configuring, explicitlyChosen: true);
        Assert.Contains("Configuring", row.Message);
        row.IsBusy = false;
        Assert.True(row.NeedsAttention);
    }

    [Fact]
    public void UnsupportedPlatformClientIsNeutralAndCannotOfferFutileRetry()
    {
        var client = new AiClientDefinition("unsupported", "Unavailable tool", "Unavailable here");
        var row = new AiConnectionItemViewModel(client) { IsBusy = false };
        row.Apply(new(client, AiConnectionState.UnsupportedPlatform, "Not supported on this platform."));
        Assert.True(row.IsUnsupportedPlatform);
        Assert.False(row.IsErrorStatus);
        Assert.False(row.NeedsAttention);
        Assert.False(row.CanChange);
        Assert.False(row.CanShowAutomaticSetupAction);
    }

    [Theory]
    [InlineData(AiConnectionState.Broken)]
    [InlineData(AiConnectionState.UpdateRequired)]
    public void ManagedClientProblemsRemainActionable(AiConnectionState failure)
    {
        var client = new AiClientDefinition("managed", "Configured tool", "Existing configuration");
        var row = new AiConnectionItemViewModel(client) { IsBusy = false };
        row.Apply(new(client, failure, "Configuration needs attention."));
        Assert.True(row.NeedsAttention);
    }

    [Theory]
    [InlineData("Downloading PP-OCRv6 detector… 42%", 42)]
    [InlineData("Downloading PP-OCRv6 recognizer… 100%", 100)]
    [InlineData("Downloading PP-OCRv6 detector… 120%", 100)]
    public void OcrExposesActualPerFileProgress(string reason, double expected)
    {
        var message = OcrPresentation.StatusMessage(true, false, reason);
        Assert.Equal(reason, message);
        Assert.Equal(expected, OcrPresentation.DownloadPercent(message));
    }

    [Theory]
    [InlineData("Verifying PP-OCRv6 detector…")]
    [InlineData("Downloading PP-OCRv6 detector… 15.5 MB")]
    [InlineData("OCR setup failed: retry after 5% packet loss.")]
    public void OcrDoesNotInventProgressForUnknownTotalOrVerification(string reason) =>
        Assert.Null(OcrPresentation.DownloadPercent(reason));

    [Fact]
    public void EmptyProjectWaitsForFirstScanBeforeSettledNoFilesStatus()
    {
        var state = new AppPresentationState();
        state.ProjectsLoaded();
        var project = new ProjectItemViewModel(new(Guid.NewGuid(), "New project", ProjectState.Active,
            [], 1, 0, 0, 0, 0, null));
        Assert.Equal(AppStatusTone.Busy, state.CurrentStatus([project]).Tone);
        Assert.Equal("Waiting for the first folder scan", state.CurrentStatus([project]).Message);
        project.UpdateRuntime(new([], null, 0), isInitialScanComplete: true);
        Assert.Equal("No supported files found", state.CurrentStatus([project]).Message);
    }

    [Fact]
    public void UpdateFailurePreservesAvailableVersionCauseAndLastAttempt()
    {
        var now = DateTimeOffset.Parse("2026-10-02T15:00:00Z");
        var check = ApplicationUpdateService.CreateErrorSnapshot("1.0", null, "Network unreachable", now);
        var download = ApplicationUpdateService.CreateErrorSnapshot("1.0", "1.1", "Connection interrupted", now);
        Assert.Contains("Could not check", check.Message);
        Assert.Contains("Network unreachable", check.Message);
        Assert.Null(check.AvailableVersion);
        Assert.Contains("Could not download version 1.1", download.Message);
        Assert.Contains("Connection interrupted", download.Message);
        Assert.Equal("1.1", download.AvailableVersion);
        Assert.Equal(now, download.LastAttemptUtc);
    }

    [Fact]
    public void StartupFailureProvidesEvidenceRetentionAndBackupInstructions()
    {
        var message = Program.FormatStartupFailure("Migration rejected", "/fixture/index.db", "/fixture/logs");
        Assert.Contains("Keep this database", message);
        Assert.Contains("do not delete", message);
        Assert.Contains("WAL/SHM", message);
        Assert.Contains("Close Context Mole before making a backup", message);
        Assert.Contains("/fixture/index.db", message);
        Assert.Contains("/fixture/logs", message);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-02T14:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
