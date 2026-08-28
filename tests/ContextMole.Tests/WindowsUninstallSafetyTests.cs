using ContextMole.Core;

namespace ContextMole.Tests;

public sealed class WindowsUninstallSafetyTests
{
    [Fact]
    public void ProcessLease_AndShutdownMarker_CoordinateWithoutKillingProcesses()
    {
        using var paths = new TemporaryPaths();
        var lease = ContextMoleProcessCoordination.AcquireLease(paths, "test-ui");
        Assert.True(File.Exists(lease.Path));

        var shutdown = ContextMoleProcessCoordination.RequestShutdown(paths, TimeSpan.FromMinutes(1));
        Assert.True(ContextMoleProcessCoordination.IsShutdownRequested(paths));

        var exception = Assert.Throws<ContextMoleException>(() =>
            ContextMoleProcessCoordination.AcquireLease(paths, "test-mcp"));
        Assert.Equal("application_shutting_down", exception.Code);

        Assert.True(ContextMoleProcessCoordination.RemoveShutdownRequest(paths.DataDirectory, shutdown.RequestId));
        using var secondLease = ContextMoleProcessCoordination.AcquireLease(paths, "test-mcp");
        Assert.True(File.Exists(secondLease.Path));

        lease.Dispose();
        Assert.False(File.Exists(lease.Path));
    }

    [Fact]
    public async Task ExpiredShutdownMarker_DoesNotBlockNewProcesses()
    {
        using var paths = new TemporaryPaths();
        ContextMoleProcessCoordination.RequestShutdown(paths, TimeSpan.FromMilliseconds(20));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.False(ContextMoleProcessCoordination.IsShutdownRequested(paths));
        using var lease = ContextMoleProcessCoordination.AcquireLease(paths, "test");
        Assert.True(File.Exists(lease.Path));
    }

    [Fact]
    public async Task RefreshedShutdownMarker_RemainsActivePastItsOriginalExpiry()
    {
        using var paths = new TemporaryPaths();
        var request = ContextMoleProcessCoordination.RequestShutdown(paths, TimeSpan.FromMilliseconds(80));
        await Task.Delay(40, TestContext.Current.CancellationToken);

        Assert.True(ContextMoleProcessCoordination.RefreshShutdownRequest(
            paths.DataDirectory,
            request.RequestId,
            TimeSpan.FromMinutes(1)));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.True(ContextMoleProcessCoordination.IsShutdownRequested(paths));
        Assert.True(ContextMoleProcessCoordination.RemoveShutdownRequest(paths.DataDirectory, request.RequestId));
    }

    [Fact]
    public async Task SuccessfulMarkerRemovalCannotBeRecreatedByAConcurrentRefresh()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var paths = new TemporaryPaths();
        var request = ContextMoleProcessCoordination.RequestShutdown(paths, TimeSpan.FromMinutes(1));
        using var removalHasGate = new ManualResetEventSlim();
        using var allowRemoval = new ManualResetEventSlim();

        var removal = Task.Run(() => ContextMoleProcessCoordination.RemoveShutdownRequest(
            paths.DataDirectory,
            request.RequestId,
            () =>
            {
                removalHasGate.Set();
                allowRemoval.Wait(TestContext.Current.CancellationToken);
            }), TestContext.Current.CancellationToken);
        Assert.True(removalHasGate.Wait(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));

        var refresh = Task.Run(() => ContextMoleProcessCoordination.RefreshShutdownRequest(
            paths.DataDirectory,
            request.RequestId,
            TimeSpan.FromMinutes(1)), TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        allowRemoval.Set();

        Assert.True(await removal);
        Assert.False(await refresh);
        Assert.False(ContextMoleProcessCoordination.IsShutdownRequested(paths));
        Assert.False(File.Exists(ContextMoleProcessCoordination.GetShutdownMarkerPath(paths.DataDirectory)));
    }

    [Fact]
    public void MarkerRemoval_ReinspectsAFileSwappedToASymbolicLinkBeforeOpen()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var paths = new TemporaryPaths();
        var request = ContextMoleProcessCoordination.RequestShutdown(paths, TimeSpan.FromMinutes(1));
        var markerPath = ContextMoleProcessCoordination.GetShutdownMarkerPath(paths.DataDirectory);
        var outsideFile = Path.Combine(paths.Root, "outside-marker-target.bin");
        var outsideBytes = Enumerable.Range(0, 384).Select(value => (byte)(value % 239)).ToArray();
        File.WriteAllBytes(outsideFile, outsideBytes);
        var swapped = false;

        try
        {
            Assert.False(ContextMoleProcessCoordination.RemoveShutdownRequest(
                paths.DataDirectory,
                request.RequestId,
                () =>
                {
                    File.Delete(markerPath);
                    File.CreateSymbolicLink(markerPath, outsideFile);
                    swapped = true;
                }));
        }
        catch (Exception exception) when (!swapped &&
                                          exception is UnauthorizedAccessException or IOException)
        {
            return;
        }
        finally
        {
            if (File.Exists(markerPath)) File.Delete(markerPath);
        }

        Assert.True(swapped);
        Assert.Equal(outsideBytes, File.ReadAllBytes(outsideFile));
    }

    [Fact]
    public void MarkerOperations_CoordinationFailureIsNonThrowing()
    {
        using var paths = new TemporaryPaths();
        var request = ContextMoleProcessCoordination.RequestShutdown(paths, TimeSpan.FromMinutes(1));
        var gatePath = Path.Combine(
            ContextMoleProcessCoordination.GetCoordinationDirectory(paths.DataDirectory),
            "coordination.lock");
        File.Delete(gatePath);
        Directory.CreateDirectory(gatePath);

        Assert.False(ContextMoleProcessCoordination.RefreshShutdownRequest(
            paths.DataDirectory,
            request.RequestId,
            TimeSpan.FromMinutes(1)));
        Assert.False(ContextMoleProcessCoordination.RemoveShutdownRequest(
            paths.DataDirectory,
            request.RequestId));
        Assert.False(ContextMoleProcessCoordination.TryRemoveShutdownRequestWithRetry(
            paths.DataDirectory,
            request.RequestId,
            out var retryError,
            maximumAttempts: 2,
            retryDelay: TimeSpan.Zero));
        Assert.Contains(
            ContextMoleProcessCoordination.GetShutdownMarkerPath(paths.DataDirectory),
            retryError,
            StringComparison.Ordinal);
        Assert.Contains("Do not remove indexed source files", retryError, StringComparison.OrdinalIgnoreCase);
        Assert.True(ContextMoleProcessCoordination.IsShutdownRequested(paths));

        Directory.Delete(gatePath);
        Assert.True(ContextMoleProcessCoordination.RemoveShutdownRequest(paths.DataDirectory, request.RequestId));
    }

    [Fact]
    public void ActiveLease_MustReleaseBeforeCleanupCanProceed()
    {
        using var paths = new TemporaryPaths();
        using var lease = ContextMoleProcessCoordination.AcquireLease(paths, "test");

        Assert.False(SafeWindowsDataDeletion.TryReleaseStaleLeases(paths.DataDirectory, out var error));
        Assert.NotNull(error);

        lease.Dispose();
        Assert.True(SafeWindowsDataDeletion.TryReleaseStaleLeases(paths.DataDirectory, out error));
        Assert.Null(error);
    }

    [Fact]
    public async Task PublicCleanup_RejectsArbitraryDirectory_AndLeavesEveryFileUntouched()
    {
        using var paths = new TemporaryPaths();
        var applicationFile = Path.Combine(paths.DataDirectory, "index.db");
        await File.WriteAllTextAsync(applicationFile, "index", TestContext.Current.CancellationToken);

        var result = await SafeWindowsDataDeletion.DeleteCanonicalDirectoryAsync(
            paths.DataDirectory,
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken);

        Assert.False(result.Deleted);
        Assert.True(File.Exists(applicationFile));
        Assert.Equal("index", await File.ReadAllTextAsync(applicationFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TreeCleanup_NeverInterpretsIndexedSourcePaths()
    {
        using var paths = new TemporaryPaths();
        var sourceDirectory = Path.Combine(paths.Root, "source");
        var ownedDirectory = Path.Combine(paths.Root, "owned-data");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(ownedDirectory);
        var sourceFile = Path.Combine(sourceDirectory, "important.txt");
        var fakeDatabase = Path.Combine(ownedDirectory, "index.db");
        await File.WriteAllTextAsync(sourceFile, "must survive", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(fakeDatabase, sourceFile, TestContext.Current.CancellationToken);

        SafeWindowsDataDeletion.DeleteTreeWithoutFollowingReparsePoints(
            ownedDirectory,
            ownedDirectory,
            deleteRoot: true);

        Assert.False(Directory.Exists(ownedDirectory));
        Assert.True(File.Exists(sourceFile));
        Assert.Equal("must survive", await File.ReadAllTextAsync(sourceFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TreeCleanup_RemovesReparsePointWithoutTraversingItsTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var paths = new TemporaryPaths();
        var sourceDirectory = Path.Combine(paths.Root, "source");
        var ownedDirectory = Path.Combine(paths.Root, "owned-data");
        var link = Path.Combine(ownedDirectory, "indexed-source-link");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(ownedDirectory);
        var sourceFile = Path.Combine(sourceDirectory, "important.txt");
        File.WriteAllText(sourceFile, "must survive");

        try
        {
            Directory.CreateSymbolicLink(link, sourceDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        SafeWindowsDataDeletion.DeleteTreeWithoutFollowingReparsePoints(
            ownedDirectory,
            ownedDirectory,
            deleteRoot: true);

        Assert.False(Directory.Exists(ownedDirectory));
        Assert.True(File.Exists(sourceFile));
    }

    [Fact]
    public void TreeCleanup_RejectsAReparsePointRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var paths = new TemporaryPaths();
        var sourceDirectory = Path.Combine(paths.Root, "source-root");
        var linkedRoot = Path.Combine(paths.Root, "linked-data-root");
        Directory.CreateDirectory(sourceDirectory);
        var sourceFile = Path.Combine(sourceDirectory, "important.txt");
        File.WriteAllText(sourceFile, "must survive");

        try
        {
            Directory.CreateSymbolicLink(linkedRoot, sourceDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        Assert.Throws<IOException>(() =>
            SafeWindowsDataDeletion.DeleteTreeWithoutFollowingReparsePoints(
                linkedRoot,
                linkedRoot,
                deleteRoot: true));
        Assert.True(File.Exists(sourceFile));
    }

    [Fact]
    public void TreeCleanup_ReinspectsAChildSwappedToAReparsePointBeforeOpen()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var paths = new TemporaryPaths();
        var outsideDirectory = Path.Combine(paths.Root, "outside-swap-target");
        var ownedDirectory = Path.Combine(paths.Root, "owned-swap-data");
        var swappedDirectory = Path.Combine(ownedDirectory, "swap-me");
        Directory.CreateDirectory(outsideDirectory);
        Directory.CreateDirectory(swappedDirectory);
        var outsideFile = Path.Combine(outsideDirectory, "source.bin");
        var outsideBytes = Enumerable.Range(0, 1024).Select(value => (byte)(value % 251)).ToArray();
        File.WriteAllBytes(outsideFile, outsideBytes);
        var swapped = false;

        try
        {
            SafeWindowsDataDeletion.DeleteTreeWithoutFollowingReparsePoints(
                ownedDirectory,
                ownedDirectory,
                deleteRoot: true,
                beforeEntryOpen: entry =>
                {
                    if (swapped || !string.Equals(entry, swappedDirectory, StringComparison.OrdinalIgnoreCase)) return;
                    Directory.Delete(swappedDirectory);
                    Directory.CreateSymbolicLink(swappedDirectory, outsideDirectory);
                    swapped = true;
                });
        }
        catch (Exception exception) when (!swapped &&
                                          exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        Assert.True(swapped);
        Assert.False(Directory.Exists(ownedDirectory));
        Assert.Equal(outsideBytes, File.ReadAllBytes(outsideFile));
    }

    [Fact]
    public void StaleLeaseCleanup_ReinspectsALeaseSwappedToASymbolicLinkBeforeOpen()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var paths = new TemporaryPaths();
        var lifecycleDirectory = ContextMoleProcessCoordination.GetCoordinationDirectory(paths.DataDirectory);
        var leasesDirectory = ContextMoleProcessCoordination.GetLeasesDirectory(paths.DataDirectory);
        Directory.CreateDirectory(leasesDirectory);
        var leasePath = Path.Combine(leasesDirectory, "stale.lease");
        var outsideFile = Path.Combine(paths.Root, "outside-source.bin");
        var outsideBytes = Enumerable.Range(0, 512).Select(value => (byte)(255 - value % 251)).ToArray();
        File.WriteAllText(leasePath, "stale");
        File.WriteAllBytes(outsideFile, outsideBytes);
        var swapped = false;

        try
        {
            Assert.False(SafeWindowsDataDeletion.TryReleaseStaleLeases(
                paths.DataDirectory,
                out var error,
                beforeLeaseOpen: candidate =>
                {
                    if (swapped || !string.Equals(candidate, leasePath, StringComparison.OrdinalIgnoreCase)) return;
                    File.Delete(leasePath);
                    File.CreateSymbolicLink(leasePath, outsideFile);
                    swapped = true;
                }));
            Assert.Contains("reparse point", error, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (!swapped &&
                                          exception is UnauthorizedAccessException or IOException)
        {
            return;
        }
        finally
        {
            if (File.Exists(leasePath)) File.Delete(leasePath);
        }

        Assert.True(swapped);
        Assert.True(Directory.Exists(lifecycleDirectory));
        Assert.Equal(outsideBytes, File.ReadAllBytes(outsideFile));
    }

    [Fact]
    public void TreeCleanup_RemovesNestedPathsBeyondLegacyMaxPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var paths = new TemporaryPaths();
        var ownedDirectory = Path.Combine(paths.Root, "owned-long-data");
        var nestedDirectory = ownedDirectory;
        for (var index = 0; index < 8; index++)
            nestedDirectory = Path.Combine(nestedDirectory, $"materialization-{index:D2}-{new string('x', 28)}");
        Directory.CreateDirectory(nestedDirectory);
        var nestedFile = Path.Combine(nestedDirectory, "model-cache.bin");
        File.WriteAllBytes(nestedFile, [1, 2, 3, 4]);
        Assert.True(nestedFile.Length > 260);

        SafeWindowsDataDeletion.DeleteTreeWithoutFollowingReparsePoints(
            ownedDirectory,
            ownedDirectory,
            deleteRoot: true);

        Assert.False(Directory.Exists(ownedDirectory));
    }

    [Fact]
    public void ShutdownRequest_RejectsAReparsePointLifecycleDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var paths = new TemporaryPaths();
        var externalDirectory = Path.Combine(paths.Root, "external-lifecycle");
        var lifecycleLink = ContextMoleProcessCoordination.GetCoordinationDirectory(paths.DataDirectory);
        Directory.CreateDirectory(externalDirectory);

        try
        {
            Directory.CreateSymbolicLink(lifecycleLink, externalDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        try
        {
            Assert.Throws<IOException>(() =>
                ContextMoleProcessCoordination.RequestShutdown(paths, TimeSpan.FromMinutes(1)));
            Assert.False(File.Exists(Path.Combine(
                externalDirectory,
                ContextMoleProcessCoordination.ShutdownMarkerFileName)));
        }
        finally
        {
            if (Directory.Exists(lifecycleLink)) Directory.Delete(lifecycleLink, recursive: false);
        }
    }

    [Fact]
    public void ShutdownRequest_RejectsAReparsePointDataRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var paths = new TemporaryPaths();
        var externalDirectory = Path.Combine(paths.Root, "external-data");
        Directory.CreateDirectory(externalDirectory);
        var externalFile = Path.Combine(externalDirectory, "source.txt");
        File.WriteAllText(externalFile, "must survive");
        Directory.Delete(paths.DataDirectory, recursive: true);

        try
        {
            Directory.CreateSymbolicLink(paths.DataDirectory, externalDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        try
        {
            Assert.Throws<IOException>(() =>
                ContextMoleProcessCoordination.RequestShutdown(paths, TimeSpan.FromMinutes(1)));
            Assert.True(File.Exists(externalFile));
        }
        finally
        {
            if (Directory.Exists(paths.DataDirectory)) Directory.Delete(paths.DataDirectory, recursive: false);
        }
    }

    [Fact]
    public void StaleLeaseCleanup_RejectsAReparsePointLeasesDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var paths = new TemporaryPaths();
        var externalDirectory = Path.Combine(paths.Root, "external-leases");
        var lifecycleDirectory = ContextMoleProcessCoordination.GetCoordinationDirectory(paths.DataDirectory);
        var leasesLink = ContextMoleProcessCoordination.GetLeasesDirectory(paths.DataDirectory);
        Directory.CreateDirectory(externalDirectory);
        Directory.CreateDirectory(lifecycleDirectory);
        var externalLease = Path.Combine(externalDirectory, "source.lease");
        File.WriteAllText(externalLease, "must survive");

        try
        {
            Directory.CreateSymbolicLink(leasesLink, externalDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        try
        {
            Assert.False(SafeWindowsDataDeletion.TryReleaseStaleLeases(paths.DataDirectory, out var error));
            Assert.Contains("reparse point", error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(externalLease));
            Assert.Equal("must survive", File.ReadAllText(externalLease));
        }
        finally
        {
            if (Directory.Exists(leasesLink)) Directory.Delete(leasesLink, recursive: false);
        }
    }

    [Fact]
    public void CanonicalWindowsTarget_HasExactLocalAppDataBoundary()
    {
        if (!OperatingSystem.IsWindows()) return;
        var expected = ContextMoleLocalData.GetDefaultDataDirectory();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.True(ContextMoleLocalData.IsCanonicalWindowsDataDirectory(expected));
        Assert.True(ContextMoleLocalData.IsCanonicalWindowsDataDirectory(expected + Path.DirectorySeparatorChar));
        Assert.False(ContextMoleLocalData.IsCanonicalWindowsDataDirectory(localAppData));
        Assert.False(ContextMoleLocalData.IsCanonicalWindowsDataDirectory(Path.Combine(localAppData, "ContextMole-Other")));
        Assert.False(ContextMoleLocalData.IsCanonicalWindowsDataDirectory(Path.Combine(expected, "nested")));
    }

    private sealed class TemporaryPaths : IAppPaths, IDisposable
    {
        public TemporaryPaths()
        {
            Root = Path.Combine(Path.GetTempPath(), "ContextMole.Tests", Guid.NewGuid().ToString("N"));
            DataDirectory = Path.Combine(Root, "data");
            DatabasePath = Path.Combine(DataDirectory, "index.db");
            AssetsDirectory = Path.Combine(DataDirectory, "assets");
            LogsDirectory = Path.Combine(DataDirectory, "logs");
            TempDirectory = Path.Combine(DataDirectory, "temp");
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(AssetsDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(TempDirectory);
        }

        public string Root { get; }
        public string DataDirectory { get; }
        public string DatabasePath { get; }
        public string AssetsDirectory { get; }
        public string LogsDirectory { get; }
        public string TempDirectory { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
