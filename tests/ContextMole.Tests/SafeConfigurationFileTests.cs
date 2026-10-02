using System.Text;
using ContextMole.Infrastructure;

namespace ContextMole.Tests;

public sealed class SafeConfigurationFileTests
{
    [Fact]
    public async Task ExistingSaveRetainsExactOriginalBytesAndPrivateMode()
    {
        using var folder = new TemporaryFolder();
        var target = Path.Combine(folder.Path, "mcp.json");
        var originalBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("original")).ToArray();
        await File.WriteAllBytesAsync(target, originalBytes, TestContext.Current.CancellationToken);
        SetPrivateMode(target);

        await SafeConfigurationFile.WriteAsync(target, "original", "updated", "test configuration",
            TestContext.Current.CancellationToken);

        Assert.Equal("updated", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        var backup = Assert.Single(Directory.GetFiles(folder.Path, "*.bak"));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backup, TestContext.Current.CancellationToken));
        AssertPrivateMode(target);
        AssertPrivateMode(backup);
        Assert.Empty(Directory.GetFiles(folder.Path, "*.partial"));
    }

    [Fact]
    public async Task FinalReadRaceRetainsActualCompetingSaveAndReportsAppliedUpdate()
    {
        using var folder = new TemporaryFolder();
        var target = Path.Combine(folder.Path, "mcp.json");
        var prepared = Path.Combine(folder.Path, "prepared.partial");
        await File.WriteAllTextAsync(target, "original", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(prepared, "our update", TestContext.Current.CancellationToken);
        SetPrivateMode(prepared);
        // Model an editor's atomic rename immediately after the caller's final expected-content check.
        var external = Path.Combine(folder.Path, "external");
        await File.WriteAllTextAsync(external, "last-moment external save", TestContext.Current.CancellationToken);
        SetPrivateMode(external);
        File.Move(external, target, overwrite: true);

        var exception = await Assert.ThrowsAsync<IOException>(() => SafeConfigurationFile.CommitPreparedFileAsync(
            prepared, target, "original", targetExisted: true, "test configuration",
            TestContext.Current.CancellationToken));

        Assert.Equal("our update", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        var backups = Directory.GetFiles(folder.Path, "*.bak");
        Assert.Equal(2, backups.Length);
        var displaced = Assert.Single(backups, path => !path.Contains("contextmole-expected-"));
        var original = Assert.Single(backups, path => path.Contains("contextmole-expected-"));
        Assert.Equal("last-moment external save", await File.ReadAllTextAsync(displaced, TestContext.Current.CancellationToken));
        Assert.Equal("original", await File.ReadAllTextAsync(original, TestContext.Current.CancellationToken));
        Assert.Contains(displaced, exception.Message);
        Assert.Contains(original, exception.Message);
        Assert.Contains("competing save is preserved", exception.Message);
        Assert.Contains("update may already be active", exception.Message);
        AssertPrivateMode(displaced);
        AssertPrivateMode(original);
        Assert.False(File.Exists(prepared));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.partial"));
    }

    [Fact]
    public async Task MissingTargetRaceNeverOverwritesEvenAnEmptyExternalFile()
    {
        using var folder = new TemporaryFolder();
        var target = Path.Combine(folder.Path, "mcp.json");
        var prepared = Path.Combine(folder.Path, "prepared.partial");
        await File.WriteAllTextAsync(prepared, "our update", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(target, string.Empty, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() => SafeConfigurationFile.CommitPreparedFileAsync(
            prepared, target, string.Empty, targetExisted: false, "test configuration",
            TestContext.Current.CancellationToken));

        Assert.Equal(string.Empty, await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal("our update", await File.ReadAllTextAsync(prepared, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.bak"));
    }

    [Fact]
    public async Task DeletedExistingTargetIsNotSilentlyRecreated()
    {
        using var folder = new TemporaryFolder();
        var target = Path.Combine(folder.Path, "mcp.json");
        var prepared = Path.Combine(folder.Path, "prepared.partial");
        await File.WriteAllTextAsync(prepared, "our update", TestContext.Current.CancellationToken);

        Func<Task> commit = () => SafeConfigurationFile.CommitPreparedFileAsync(
            prepared, target, string.Empty, targetExisted: true, "test configuration",
            TestContext.Current.CancellationToken);
        // Windows ReplaceFile reports a missing destination as the IOException subclass
        // FileNotFoundException; the Unix atomic exchange reports IOException directly.
        if (OperatingSystem.IsWindows())
            await Assert.ThrowsAsync<FileNotFoundException>(commit);
        else
            await Assert.ThrowsAsync<IOException>(commit);

        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.bak"));
    }

    [Fact]
    public async Task ExistingConflictDoesNotCommitOrLeavePartials()
    {
        using var folder = new TemporaryFolder();
        var target = Path.Combine(folder.Path, "mcp.json");
        await File.WriteAllTextAsync(target, "external save", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() => SafeConfigurationFile.WriteAsync(target, "original", "our update",
            "test configuration", TestContext.Current.CancellationToken));

        Assert.Equal("external save", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task CancelledSaveDoesNotCreateFilesOrDirectories()
    {
        using var folder = new TemporaryFolder();
        var target = Path.Combine(folder.Path, "not-created", "mcp.json");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SafeConfigurationFile.WriteAsync(target,
            string.Empty, "our update", "test configuration", cancellation.Token));

        Assert.Empty(Directory.GetFileSystemEntries(folder.Path));
    }

    [Fact]
    public async Task CancelledPreparedCommitLeavesExistingConfigurationUntouched()
    {
        using var folder = new TemporaryFolder();
        var target = Path.Combine(folder.Path, "mcp.json");
        var prepared = Path.Combine(folder.Path, "prepared.partial");
        await File.WriteAllTextAsync(target, "original", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(prepared, "our update", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SafeConfigurationFile.CommitPreparedFileAsync(
            prepared, target, "original", targetExisted: true, "test configuration", cancellation.Token));

        Assert.Equal("original", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal("our update", await File.ReadAllTextAsync(prepared, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.bak"));
    }

    [Fact]
    public async Task NewConfigurationIsPrivateAndHasNoBackupOrPartial()
    {
        using var folder = new TemporaryFolder();
        var target = Path.Combine(folder.Path, "new", "mcp.json");

        await SafeConfigurationFile.WriteAsync(target, string.Empty, "our update", "test configuration",
            TestContext.Current.CancellationToken);

        Assert.Equal("our update", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        AssertPrivateMode(target);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
    }

    [Fact]
    public async Task SaveThroughSymbolicLinkPreservesTheLinkAndTargetPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        using var folder = new TemporaryFolder();
        var target = Path.Combine(folder.Path, "real.json");
        var link = Path.Combine(folder.Path, "mcp.json");
        await File.WriteAllTextAsync(target, "original", TestContext.Current.CancellationToken);
        SetPrivateMode(target);
        File.CreateSymbolicLink(link, target);

        await SafeConfigurationFile.WriteAsync(link, "original", "updated", "test configuration",
            TestContext.Current.CancellationToken);

        Assert.Equal(target, new FileInfo(link).LinkTarget);
        Assert.Equal("updated", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal("updated", await File.ReadAllTextAsync(link, TestContext.Current.CancellationToken));
        AssertPrivateMode(target);
        Assert.Single(Directory.GetFiles(folder.Path, "real.json.contextmole-*.bak"));
        Assert.Empty(Directory.GetFiles(folder.Path, "mcp.json.contextmole-*.bak"));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.partial"));
    }

    [Fact]
    public async Task ChangedTargetSymlinkIsNotReplaced()
    {
        if (OperatingSystem.IsWindows()) return;
        using var folder = new TemporaryFolder();
        var destination = Path.Combine(folder.Path, "elsewhere.json");
        var target = Path.Combine(folder.Path, "mcp.json");
        var prepared = Path.Combine(folder.Path, "prepared.partial");
        await File.WriteAllTextAsync(destination, "external save", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(prepared, "our update", TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(target, destination);

        await Assert.ThrowsAsync<IOException>(() => SafeConfigurationFile.CommitPreparedFileAsync(
            prepared, target, "original", targetExisted: true, "test configuration",
            TestContext.Current.CancellationToken));

        Assert.Equal(destination, new FileInfo(target).LinkTarget);
        Assert.Equal("external save", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.bak"));
    }

    private static void SetPrivateMode(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void AssertPrivateMode(string path)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "context-mole-safe-config-tests", Guid.NewGuid().ToString("N"));

        public TemporaryFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
