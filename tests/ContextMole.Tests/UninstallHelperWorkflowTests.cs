using System.Collections.Concurrent;
using System.Diagnostics;

using ContextMole.Core;
using ContextMole.Infrastructure;
using ContextMole.UninstallHelper;

namespace ContextMole.Tests;

public sealed class UninstallHelperWorkflowTests
{
    private const string GateProbeDataDirectory = "CONTEXTMOLE_TEST_GATE_PROBE_DATA_DIR";
    private const string GateProbeExpectedBlocked = "CONTEXTMOLE_TEST_GATE_PROBE_EXPECTED_BLOCKED";

    [Fact]
    public async Task KeepFlowWaitsForParentAndUninstallerThenRemovesOnlyTheMarker()
    {
        using var layout = new TemporaryUninstallLayout();
        var marker = ContextMoleProcessCoordination.RequestShutdown(layout.DataDirectory, TimeSpan.FromMinutes(1));
        var events = new List<string>();
        var errors = new List<string>();

        var result = await UninstallWorkflow.ExecuteAsync(
            new UninstallWorkflowRequest(layout.DataDirectory, marker.RequestId, DeleteData: false,
                TimeSpan.FromSeconds(1)),
            new UninstallWorkflowOperations(
                () =>
                {
                    events.Add("parent-exit");
                    Assert.True(ContextMoleProcessCoordination.IsShutdownRequested(layout.DataDirectory));
                    return Task.FromResult(true);
                },
                () =>
                {
                    events.Add("uninstaller");
                    Assert.True(ContextMoleProcessCoordination.IsShutdownRequested(layout.DataDirectory));
                    return Task.FromResult(0);
                },
                () =>
                {
                    events.Add("marker-refresh-stopped");
                    return Task.CompletedTask;
                },
                () => ContextMoleProcessCoordination.RefreshShutdownRequest(
                    layout.DataDirectory, marker.RequestId, TimeSpan.FromMinutes(1)),
                (dataDirectory, requestId) =>
                {
                    events.Add("marker-removed");
                    return ContextMoleProcessCoordination.RemoveShutdownRequest(dataDirectory, requestId);
                },
                () => events.Add("startup-removed"),
                (_, _) => throw new InvalidOperationException("Keep data must not invoke deletion."),
                errors.Add,
                () => events.Add("temporary-helper-cleanup")));

        Assert.Equal(0, result);
        Assert.Empty(errors);
        Assert.True(Directory.Exists(layout.DataDirectory));
        Assert.Equal(layout.DataBytes, File.ReadAllBytes(layout.DataFile));
        Assert.Equal(layout.OutsideBytes, File.ReadAllBytes(layout.OutsideFile));
        Assert.False(ContextMoleProcessCoordination.IsShutdownRequested(layout.DataDirectory));
        AssertOrdered(events, "parent-exit", "uninstaller", "startup-removed", "marker-refresh-stopped",
            "marker-removed", "temporary-helper-cleanup");
    }

    [Fact]
    public async Task KeepFlowTreatsAnAlreadyAbsentExactMarkerAsSuccessfulCleanup()
    {
        using var layout = new TemporaryUninstallLayout();
        var errors = new List<string>();
        var removalCalls = 0;
        var activeChecks = 0;

        var result = await UninstallWorkflow.ExecuteAsync(
            new UninstallWorkflowRequest(layout.DataDirectory, Guid.NewGuid(), DeleteData: false,
                TimeSpan.FromSeconds(1)),
            new UninstallWorkflowOperations(
                () => Task.FromResult(true),
                () => Task.FromResult(0),
                () => Task.CompletedTask,
                () => true,
                (_, _) =>
                {
                    removalCalls++;
                    return false;
                },
                () => { },
                (_, _) => throw new Xunit.Sdk.XunitException("Keep data must not invoke deletion."),
                errors.Add,
                () => { },
                (_, _) =>
                {
                    activeChecks++;
                    return false;
                }));

        Assert.Equal(0, result);
        Assert.Equal(1, removalCalls);
        Assert.Equal(1, activeChecks);
        Assert.Empty(errors);
        Assert.True(File.Exists(layout.DataFile));
    }

    [Fact]
    public async Task PersistentMarkerRemovalFailureRetriesAndReportsExactManualPath()
    {
        using var layout = new TemporaryUninstallLayout();
        var requestId = Guid.NewGuid();
        var errors = new List<string>();
        var removalCalls = 0;
        var activeChecks = 0;

        var result = await UninstallWorkflow.ExecuteAsync(
            new UninstallWorkflowRequest(layout.DataDirectory, requestId, DeleteData: false,
                TimeSpan.FromSeconds(1)),
            new UninstallWorkflowOperations(
                () => Task.FromResult(true),
                () => Task.FromResult(0),
                () => Task.CompletedTask,
                () => true,
                (_, _) =>
                {
                    removalCalls++;
                    return false;
                },
                () => { },
                (_, _) => throw new Xunit.Sdk.XunitException("Keep data must not invoke deletion."),
                errors.Add,
                () => { },
                (_, id) =>
                {
                    Assert.Equal(requestId, id);
                    activeChecks++;
                    return true;
                }));

        Assert.Equal(7, result);
        Assert.Equal(3, removalCalls);
        Assert.Equal(3, activeChecks);
        var error = Assert.Single(errors);
        Assert.Contains(ContextMoleProcessCoordination.GetShutdownMarkerPath(layout.DataDirectory), error,
            StringComparison.Ordinal);
        Assert.Contains("manually", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not remove indexed source files", error, StringComparison.Ordinal);
        Assert.True(File.Exists(layout.DataFile));
    }

    [Fact]
    public async Task DeleteFlowWaitsForLeaseReleaseBeforeDeletingOnlyTheDataDirectory()
    {
        using var layout = new TemporaryUninstallLayout();
        var lease = ContextMoleProcessCoordination.AcquireLease(layout.DataDirectory, "test-ui");
        var marker = ContextMoleProcessCoordination.RequestShutdown(layout.DataDirectory, TimeSpan.FromMinutes(1));
        var events = new ConcurrentQueue<string>();
        var deletionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var workflow = UninstallWorkflow.ExecuteAsync(
            new UninstallWorkflowRequest(layout.DataDirectory, marker.RequestId, DeleteData: true,
                TimeSpan.FromSeconds(2)),
            new UninstallWorkflowOperations(
                () =>
                {
                    events.Enqueue("parent-exit");
                    return Task.FromResult(true);
                },
                () =>
                {
                    events.Enqueue("uninstaller");
                    Assert.True(ContextMoleProcessCoordination.IsShutdownRequested(layout.DataDirectory));
                    return Task.FromResult(0);
                },
                () =>
                {
                    events.Enqueue("marker-refresh-stopped");
                    return Task.CompletedTask;
                },
                () => ContextMoleProcessCoordination.RefreshShutdownRequest(
                    layout.DataDirectory, marker.RequestId, TimeSpan.FromMinutes(1)),
                ContextMoleProcessCoordination.RemoveShutdownRequest,
                () => events.Enqueue("startup-removed"),
                async (dataDirectory, timeout) =>
                {
                    events.Enqueue("waiting-for-leases");
                    deletionEntered.TrySetResult();
                    var deadline = DateTimeOffset.UtcNow.Add(timeout);
                    string? lastError = null;
                    while (DateTimeOffset.UtcNow < deadline)
                    {
                        if (SafeWindowsDataDeletion.TryReleaseStaleLeases(dataDirectory, out lastError))
                        {
                            events.Enqueue("leases-released");
                            SafeWindowsDataDeletion.DeleteTreeWithoutFollowingReparsePoints(
                                dataDirectory, dataDirectory, deleteRoot: true);
                            events.Enqueue("data-deleted");
                            return ContextMoleDataDeletionResult.Success;
                        }
                        await Task.Delay(10, TestContext.Current.CancellationToken);
                    }
                    return new ContextMoleDataDeletionResult(false, lastError ?? "lease timeout");
                },
                message => throw new Xunit.Sdk.XunitException($"Unexpected uninstall error: {message}"),
                () => events.Enqueue("temporary-helper-cleanup")));

        await deletionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(workflow.IsCompleted);
        Assert.True(File.Exists(layout.DataFile));
        Assert.True(File.Exists(lease.Path));

        lease.Dispose();
        var result = await workflow.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        Assert.False(Directory.Exists(layout.DataDirectory));
        Assert.Equal(layout.OutsideBytes, File.ReadAllBytes(layout.OutsideFile));
        AssertOrdered(events.ToArray(), "parent-exit", "uninstaller", "startup-removed",
            "marker-refresh-stopped", "waiting-for-leases", "leases-released", "data-deleted",
            "temporary-helper-cleanup");
    }

    [Fact]
    public async Task UninstallerNonzeroExitKeepsDataRemovesMarkerAndSkipsCleanupActions()
    {
        using var layout = new TemporaryUninstallLayout();
        var marker = ContextMoleProcessCoordination.RequestShutdown(layout.DataDirectory, TimeSpan.FromMinutes(1));
        var events = new List<string>();
        var errors = new List<string>();

        var result = await UninstallWorkflow.ExecuteAsync(
            new UninstallWorkflowRequest(layout.DataDirectory, marker.RequestId, DeleteData: true,
                TimeSpan.FromSeconds(1)),
            new UninstallWorkflowOperations(
                () =>
                {
                    events.Add("parent-exit");
                    return Task.FromResult(true);
                },
                () =>
                {
                    events.Add("uninstaller-failed");
                    return Task.FromResult(23);
                },
                () =>
                {
                    events.Add("marker-refresh-stopped");
                    return Task.CompletedTask;
                },
                () => ContextMoleProcessCoordination.RefreshShutdownRequest(
                    layout.DataDirectory, marker.RequestId, TimeSpan.FromMinutes(1)),
                (dataDirectory, requestId) =>
                {
                    events.Add("marker-removed");
                    return ContextMoleProcessCoordination.RemoveShutdownRequest(dataDirectory, requestId);
                },
                () => events.Add("unexpected-startup-removal"),
                (_, _) =>
                {
                    events.Add("unexpected-data-deletion");
                    return Task.FromResult(ContextMoleDataDeletionResult.Success);
                },
                errors.Add,
                () => events.Add("temporary-helper-cleanup")));

        Assert.Equal(3, result);
        Assert.DoesNotContain("unexpected-startup-removal", events);
        Assert.DoesNotContain("unexpected-data-deletion", events);
        Assert.Contains("code 23", Assert.Single(errors), StringComparison.Ordinal);
        Assert.True(File.Exists(layout.DataFile));
        Assert.Equal(layout.DataBytes, File.ReadAllBytes(layout.DataFile));
        Assert.Equal(layout.OutsideBytes, File.ReadAllBytes(layout.OutsideFile));
        Assert.False(ContextMoleProcessCoordination.IsShutdownRequested(layout.DataDirectory));
        AssertOrdered(events, "parent-exit", "uninstaller-failed", "marker-refresh-stopped", "marker-removed",
            "temporary-helper-cleanup");
    }

    [Fact]
    public async Task UninstallerLaunchExceptionBecomesFailureAndKeepsData()
    {
        using var layout = new TemporaryUninstallLayout();
        var marker = ContextMoleProcessCoordination.RequestShutdown(layout.DataDirectory, TimeSpan.FromMinutes(1));
        var errors = new List<string>();
        var markerRemovalCount = 0;
        var temporaryCleanupCount = 0;

        var result = await UninstallWorkflow.ExecuteAsync(
            new UninstallWorkflowRequest(layout.DataDirectory, marker.RequestId, DeleteData: true,
                TimeSpan.FromSeconds(1)),
            new UninstallWorkflowOperations(
                () => Task.FromResult(true),
                () => Task.FromException<int>(new InvalidOperationException("fake launcher refused to start")),
                () => Task.CompletedTask,
                () => ContextMoleProcessCoordination.RefreshShutdownRequest(
                    layout.DataDirectory, marker.RequestId, TimeSpan.FromMinutes(1)),
                (dataDirectory, requestId) =>
                {
                    markerRemovalCount++;
                    return ContextMoleProcessCoordination.RemoveShutdownRequest(dataDirectory, requestId);
                },
                () => throw new Xunit.Sdk.XunitException("Startup cleanup must not run."),
                (_, _) => throw new Xunit.Sdk.XunitException("Data deletion must not run."),
                errors.Add,
                () => temporaryCleanupCount++));

        Assert.Equal(1, result);
        Assert.Equal(1, markerRemovalCount);
        Assert.Equal(1, temporaryCleanupCount);
        Assert.Contains("fake launcher refused to start", Assert.Single(errors), StringComparison.Ordinal);
        Assert.True(File.Exists(layout.DataFile));
        Assert.Equal(layout.OutsideBytes, File.ReadAllBytes(layout.OutsideFile));
        Assert.False(ContextMoleProcessCoordination.IsShutdownRequested(layout.DataDirectory));
    }

    [Fact]
    public async Task DeletionFailurePreservesRemainingDataAndReportsItsExactPath()
    {
        using var layout = new TemporaryUninstallLayout();
        var marker = ContextMoleProcessCoordination.RequestShutdown(layout.DataDirectory, TimeSpan.FromMinutes(1));
        var errors = new List<string>();

        var result = await UninstallWorkflow.ExecuteAsync(
            new UninstallWorkflowRequest(layout.DataDirectory, marker.RequestId, DeleteData: true,
                TimeSpan.FromSeconds(1)),
            new UninstallWorkflowOperations(
                () => Task.FromResult(true),
                () => Task.FromResult(0),
                () => Task.CompletedTask,
                () => ContextMoleProcessCoordination.RefreshShutdownRequest(
                    layout.DataDirectory, marker.RequestId, TimeSpan.FromMinutes(1)),
                ContextMoleProcessCoordination.RemoveShutdownRequest,
                () => { },
                (_, _) => Task.FromResult(new ContextMoleDataDeletionResult(false, "fake locked file")),
                errors.Add,
                () => { }));

        Assert.Equal(5, result);
        var error = Assert.Single(errors);
        Assert.Contains(layout.DataDirectory, error, StringComparison.Ordinal);
        Assert.Contains("remove it manually", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fake locked file", error, StringComparison.Ordinal);
        Assert.True(File.Exists(layout.DataFile));
        Assert.Equal(layout.DataBytes, File.ReadAllBytes(layout.DataFile));
        Assert.Equal(layout.OutsideBytes, File.ReadAllBytes(layout.OutsideFile));
        Assert.False(ContextMoleProcessCoordination.IsShutdownRequested(layout.DataDirectory));
    }

    [Fact]
    public async Task ParentExitTimeoutNeverLaunchesTheUninstallerAndKeepsData()
    {
        using var layout = new TemporaryUninstallLayout();
        var marker = ContextMoleProcessCoordination.RequestShutdown(layout.DataDirectory, TimeSpan.FromMinutes(1));
        var launched = false;

        var result = await UninstallWorkflow.ExecuteAsync(
            new UninstallWorkflowRequest(layout.DataDirectory, marker.RequestId, DeleteData: true,
                TimeSpan.FromSeconds(1)),
            new UninstallWorkflowOperations(
                () => Task.FromResult(false),
                () =>
                {
                    launched = true;
                    return Task.FromResult(0);
                },
                () => Task.CompletedTask,
                () => ContextMoleProcessCoordination.RefreshShutdownRequest(
                    layout.DataDirectory, marker.RequestId, TimeSpan.FromMinutes(1)),
                ContextMoleProcessCoordination.RemoveShutdownRequest,
                () => { },
                (_, _) => Task.FromResult(ContextMoleDataDeletionResult.Success),
                _ => { },
                () => { }));

        Assert.Equal(2, result);
        Assert.False(launched);
        Assert.True(File.Exists(layout.DataFile));
        Assert.Equal(layout.OutsideBytes, File.ReadAllBytes(layout.OutsideFile));
        Assert.False(ContextMoleProcessCoordination.IsShutdownRequested(layout.DataDirectory));
    }

    [Fact]
    public async Task MissingShutdownMarkerAbortsBeforeWaitingForTheParentOrLaunching()
    {
        using var layout = new TemporaryUninstallLayout();
        var marker = ContextMoleProcessCoordination.RequestShutdown(layout.DataDirectory, TimeSpan.FromMinutes(1));
        Assert.True(ContextMoleProcessCoordination.RemoveShutdownRequest(layout.DataDirectory, marker.RequestId));
        var waitedForParent = false;
        var launched = false;
        var temporaryCleanupCount = 0;
        var errors = new List<string>();

        var result = await UninstallWorkflow.ExecuteAsync(
            new UninstallWorkflowRequest(layout.DataDirectory, marker.RequestId, DeleteData: true,
                TimeSpan.FromSeconds(1)),
            new UninstallWorkflowOperations(
                () =>
                {
                    waitedForParent = true;
                    return Task.FromResult(true);
                },
                () =>
                {
                    launched = true;
                    return Task.FromResult(0);
                },
                () => Task.CompletedTask,
                () => ContextMoleProcessCoordination.RefreshShutdownRequest(
                    layout.DataDirectory, marker.RequestId, TimeSpan.FromMinutes(1)),
                ContextMoleProcessCoordination.RemoveShutdownRequest,
                () => throw new Xunit.Sdk.XunitException("Startup cleanup must not run."),
                (_, _) => throw new Xunit.Sdk.XunitException("Data deletion must not run."),
                errors.Add,
                () => temporaryCleanupCount++));

        Assert.Equal(6, result);
        Assert.False(waitedForParent);
        Assert.False(launched);
        Assert.Equal(1, temporaryCleanupCount);
        Assert.Contains("shutdown coordination could not be verified", Assert.Single(errors),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(layout.DataFile));
        Assert.Equal(layout.OutsideBytes, File.ReadAllBytes(layout.OutsideFile));
    }

    [Fact]
    public async Task TransientCoordinationFailureImmediatelyBeforeVelopackAbortsSafely()
    {
        using var layout = new TemporaryUninstallLayout();
        var marker = ContextMoleProcessCoordination.RequestShutdown(layout.DataDirectory, TimeSpan.FromMinutes(1));
        var refreshCalls = 0;
        var launched = false;
        var parentExited = false;

        var result = await UninstallWorkflow.ExecuteAsync(
            new UninstallWorkflowRequest(layout.DataDirectory, marker.RequestId, DeleteData: true,
                TimeSpan.FromSeconds(1)),
            new UninstallWorkflowOperations(
                () =>
                {
                    parentExited = true;
                    return Task.FromResult(true);
                },
                () =>
                {
                    launched = true;
                    return Task.FromResult(0);
                },
                () => Task.CompletedTask,
                () => ++refreshCalls == 1,
                ContextMoleProcessCoordination.RemoveShutdownRequest,
                () => throw new Xunit.Sdk.XunitException("Startup cleanup must not run."),
                (_, _) => throw new Xunit.Sdk.XunitException("Data deletion must not run."),
                _ => { },
                () => { }));

        Assert.Equal(6, result);
        Assert.True(parentExited);
        Assert.False(launched);
        Assert.Equal(2, refreshCalls);
        Assert.True(File.Exists(layout.DataFile));
        Assert.Equal(layout.OutsideBytes, File.ReadAllBytes(layout.OutsideFile));
        Assert.False(ContextMoleProcessCoordination.IsShutdownRequested(layout.DataDirectory));
    }

    [Fact]
    public async Task RenewalFailureAfterSuccessfulUninstallerPreventsDataDeletion()
    {
        using var layout = new TemporaryUninstallLayout();
        var marker = ContextMoleProcessCoordination.RequestShutdown(layout.DataDirectory, TimeSpan.FromMinutes(1));
        var events = new List<string>();
        var refreshCalls = 0;
        var deletionCalled = false;
        var errors = new List<string>();

        var result = await UninstallWorkflow.ExecuteAsync(
            new UninstallWorkflowRequest(layout.DataDirectory, marker.RequestId, DeleteData: true,
                TimeSpan.FromSeconds(1)),
            new UninstallWorkflowOperations(
                () =>
                {
                    events.Add("parent-exit");
                    return Task.FromResult(true);
                },
                () =>
                {
                    events.Add("uninstaller");
                    return Task.FromResult(0);
                },
                () =>
                {
                    events.Add("marker-refresh-stopped");
                    return Task.CompletedTask;
                },
                () => ++refreshCalls < 3,
                (dataDirectory, requestId) =>
                {
                    events.Add("marker-removed");
                    return ContextMoleProcessCoordination.RemoveShutdownRequest(dataDirectory, requestId);
                },
                () => events.Add("startup-removed"),
                (_, _) =>
                {
                    deletionCalled = true;
                    return Task.FromResult(ContextMoleDataDeletionResult.Success);
                },
                errors.Add,
                () => events.Add("temporary-helper-cleanup")));

        Assert.Equal(6, result);
        Assert.Equal(3, refreshCalls);
        Assert.False(deletionCalled);
        Assert.Contains("was uninstalled", Assert.Single(errors), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(layout.DataFile));
        Assert.Equal(layout.DataBytes, File.ReadAllBytes(layout.DataFile));
        Assert.Equal(layout.OutsideBytes, File.ReadAllBytes(layout.OutsideFile));
        Assert.False(ContextMoleProcessCoordination.IsShutdownRequested(layout.DataDirectory));
        AssertOrdered(events, "parent-exit", "uninstaller", "startup-removed", "marker-refresh-stopped",
            "marker-removed", "temporary-helper-cleanup");
    }

    [Fact]
    public void ExternalUninstallGatePreventsLeaseAdmissionAfterMarkerAndDataRemoval()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var layout = new TemporaryUninstallLayout();
        var marker = ContextMoleProcessCoordination.RequestShutdown(layout.DataDirectory, TimeSpan.FromMinutes(1));
        var gate = ContextMoleExternalUninstallGate.AcquireForUninstall(
            layout.DataDirectory, TimeSpan.FromSeconds(2));
        try
        {
            Assert.True(ContextMoleProcessCoordination.RemoveShutdownRequest(
                layout.DataDirectory, marker.RequestId));
            SafeWindowsDataDeletion.DeleteTreeWithoutFollowingReparsePoints(
                layout.DataDirectory, layout.DataDirectory, deleteRoot: true);
            Assert.False(Directory.Exists(layout.DataDirectory));

            var exception = Assert.Throws<ContextMoleException>(() =>
                ContextMoleProcessCoordination.AcquireLease(layout.DataDirectory, "test-mcp"));
            Assert.Equal("application_shutting_down", exception.Code);
            Assert.False(Directory.Exists(layout.DataDirectory));
            Assert.Equal(layout.OutsideBytes, File.ReadAllBytes(layout.OutsideFile));
        }
        finally
        {
            gate.Dispose();
        }

        using var lease = ContextMoleProcessCoordination.AcquireLease(layout.DataDirectory, "test-mcp");
        Assert.True(File.Exists(lease.Path));
        Assert.True(Directory.Exists(layout.DataDirectory));
        Assert.Equal(layout.OutsideBytes, File.ReadAllBytes(layout.OutsideFile));
    }

    [Fact]
    public void ExternalUninstallGateBlocksAppPathsInitializationBeforeFilesystemCreation()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var layout = new TemporaryUninstallLayout();
        Assert.False(Directory.Exists(layout.FreshDataDirectory));
        var gate = ContextMoleExternalUninstallGate.AcquireForUninstall(
            layout.FreshDataDirectory, TimeSpan.FromSeconds(2));
        try
        {
            var exception = Assert.Throws<ContextMoleException>(() => new AppPaths(layout.FreshDataDirectory));
            Assert.Equal("application_shutting_down", exception.Code);
            Assert.False(Directory.Exists(layout.FreshDataDirectory));
            Assert.Equal(layout.OutsideBytes, File.ReadAllBytes(layout.OutsideFile));
        }
        finally
        {
            gate.Dispose();
        }

        var paths = new AppPaths(layout.FreshDataDirectory);
        Assert.True(Directory.Exists(paths.DataDirectory));
        Assert.True(Directory.Exists(paths.AssetsDirectory));
        Assert.True(Directory.Exists(paths.LogsDirectory));
        Assert.True(Directory.Exists(paths.TempDirectory));
        Assert.Equal(layout.OutsideBytes, File.ReadAllBytes(layout.OutsideFile));
    }

    [Fact]
    public async Task ExternalUninstallGateCoordinatesLeaseAdmissionAcrossProcesses()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var layout = new TemporaryUninstallLayout();
        Assert.False(Directory.Exists(layout.FreshDataDirectory));
        var gate = ContextMoleExternalUninstallGate.AcquireForUninstall(
            layout.FreshDataDirectory, TimeSpan.FromSeconds(2));
        try
        {
            var blocked = await RunGateProbeProcessAsync(layout.FreshDataDirectory, expectedBlocked: true);
            Assert.Equal(0, blocked.ExitCode);
            Assert.False(Directory.Exists(layout.FreshDataDirectory));
        }
        finally
        {
            gate.Dispose();
        }

        var allowed = await RunGateProbeProcessAsync(layout.FreshDataDirectory, expectedBlocked: false);
        Assert.Equal(0, allowed.ExitCode);
        Assert.True(Directory.Exists(layout.FreshDataDirectory));
        Assert.Equal(layout.OutsideBytes, File.ReadAllBytes(layout.OutsideFile));
    }

    [Fact]
    public void ExternalUninstallGateCrossProcessChildProbe()
    {
        var dataDirectory = Environment.GetEnvironmentVariable(GateProbeDataDirectory);
        if (string.IsNullOrWhiteSpace(dataDirectory)) return;

        var expectedBlocked = bool.Parse(Environment.GetEnvironmentVariable(GateProbeExpectedBlocked)!);
        if (expectedBlocked)
        {
            var exception = Assert.Throws<ContextMoleException>(() =>
                ContextMoleProcessCoordination.AcquireLease(dataDirectory, "cross-process-probe"));
            Assert.Equal("application_shutting_down", exception.Code);
            Assert.False(Directory.Exists(dataDirectory));
            return;
        }

        using var lease = ContextMoleProcessCoordination.AcquireLease(dataDirectory, "cross-process-probe");
        Assert.True(File.Exists(lease.Path));
    }

    private static async Task<(int ExitCode, string Output)> RunGateProbeProcessAsync(
        string dataDirectory,
        bool expectedBlocked)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "ContextMole.Tests.exe");
        Assert.True(File.Exists(executable), $"The cross-process test runner was not found: {executable}");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--filter-method");
        startInfo.ArgumentList.Add(
            $"{typeof(UninstallHelperWorkflowTests).FullName}.{nameof(ExternalUninstallGateCrossProcessChildProbe)}");
        startInfo.Environment[GateProbeDataDirectory] = dataDirectory;
        startInfo.Environment[GateProbeExpectedBlocked] = expectedBlocked.ToString();

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The cross-process uninstall-gate probe could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var output = (await standardOutput) + Environment.NewLine + await standardError;
        Assert.True(process.ExitCode == 0, $"Cross-process gate probe failed:{Environment.NewLine}{output}");
        return (process.ExitCode, output);
    }

    private static void AssertOrdered(IReadOnlyList<string> events, params string[] expected)
    {
        var previous = -1;
        foreach (var name in expected)
        {
            var index = events.IndexOf(name);
            Assert.True(index > previous,
                $"Expected '{name}' after position {previous}, but events were: {string.Join(", ", events)}");
            previous = index;
        }
    }

    private sealed class TemporaryUninstallLayout : IDisposable
    {
        private readonly string _ownedRoot;

        public TemporaryUninstallLayout()
        {
            var testRoot = Path.Combine(Path.GetTempPath(), "ContextMole-uninstall-workflow-tests");
            _ownedRoot = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
            DataDirectory = Path.Combine(_ownedRoot, "data", "ContextMole");
            FreshDataDirectory = Path.Combine(_ownedRoot, "fresh-data", "ContextMole");
            var outsideDirectory = Path.Combine(_ownedRoot, "indexed-source");
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(outsideDirectory);

            DataFile = Path.Combine(DataDirectory, "index.db");
            OutsideFile = Path.Combine(outsideDirectory, "source-document.bin");
            DataBytes = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
            OutsideBytes = Enumerable.Range(0, 256).Select(value => (byte)(255 - value)).ToArray();
            File.WriteAllBytes(DataFile, DataBytes);
            File.WriteAllBytes(OutsideFile, OutsideBytes);
        }

        public string DataDirectory { get; }
        public string FreshDataDirectory { get; }
        public string DataFile { get; }
        public string OutsideFile { get; }
        public byte[] DataBytes { get; }
        public byte[] OutsideBytes { get; }

        public void Dispose()
        {
            var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                "ContextMole-uninstall-workflow-tests"));
            var owned = Path.GetFullPath(_ownedRoot);
            var relative = Path.GetRelativePath(testRoot, owned);
            if (Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("The test directory escaped its owned temporary root.");
            if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
        }
    }
}

file static class UninstallWorkflowTestCollectionExtensions
{
    public static int IndexOf(this IReadOnlyList<string> items, string value)
    {
        for (var index = 0; index < items.Count; index++)
            if (string.Equals(items[index], value, StringComparison.Ordinal)) return index;
        return -1;
    }
}
