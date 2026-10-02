using System.Runtime.InteropServices;

using ContextMole.App.UI;
using ContextMole.App.UI.ViewModels;
using ContextMole.Core;
using ContextMole.Infrastructure;

namespace ContextMole.Tests;

public sealed class RetiredModelCacheCleanupTests
{
    [Fact]
    public void DeletesExactlyTheKnownInstallerFilesAndPartialsAndIsIdempotent()
    {
        using var fixture = new Fixture();
        foreach (var name in RetiredModelCacheCleanup.FileNames) fixture.WriteRetired(name);
        var sentinels = fixture.WriteProtectedSentinels();
        var cleanup = new RetiredModelCacheCleanup(fixture.Paths);

        var first = Program.RunRetiredModelCleanup(cleanup);
        Assert.Null(first.RootIssue);
        Assert.Empty(first.PendingFiles);
        Assert.Empty(first.SkippedFiles);
        Assert.Equal(RetiredModelCacheCleanup.FileNames.Order(), first.DeletedFiles.Order());
        Assert.All(RetiredModelCacheCleanup.FileNames, name => Assert.False(File.Exists(fixture.Retired(name))));
        Assert.True(Directory.Exists(fixture.RevisionDirectory));
        fixture.AssertUnchanged(sentinels);
        var repeated = Program.RunRetiredModelCleanup(cleanup);
        Assert.Equal(RetiredModelCacheCleanupResult.Empty, repeated);
    }

    [Fact]
    public void AnIncompleteDownloadWithoutInstallationMarkerIsRemoved()
    {
        using var fixture = new Fixture();
        fixture.WriteRetired("model.onnx.partial");
        fixture.WriteRetired("tokenizer.json.partial");
        var result = new RetiredModelCacheCleanup(fixture.Paths).TryCleanup();
        Assert.Equal(2, result.DeletedFiles.Count);
        Assert.False(result.NeedsAttention);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("traversal")]
    [InlineData("root")]
    [InlineData("outside-assets")]
    [InlineData("traversal-assets")]
    public void InvalidOrCustomAssetsRootsFailClosedAndStartupContinues(string kind)
    {
        using var fixture = new Fixture();
        var sentinel = fixture.WriteRetired("model.onnx");
        var paths = kind switch
        {
            "relative" => fixture.Paths with { DataDirectory = "relative-data" },
            "traversal" => fixture.Paths with { DataDirectory = Path.Combine(fixture.Root, "unused", "..", "data") },
            "root" => fixture.Paths with { DataDirectory = Path.GetPathRoot(fixture.Root)! },
            "outside-assets" => fixture.Paths with { AssetsDirectory = Path.Combine(fixture.Root, "custom-models") },
            _ => fixture.Paths with { AssetsDirectory = Path.Combine(fixture.Paths.DataDirectory, "unused", "..", "assets") }
        };
        var result = Program.RunRetiredModelCleanup(new RetiredModelCacheCleanup(paths));
        Assert.NotNull(result.RootIssue);
        Assert.Empty(result.DeletedFiles);
        Assert.Equal(sentinel, File.ReadAllBytes(fixture.Retired("model.onnx")));
    }

    [Fact]
    public void MissingRootsAreAnIdempotentNoOpWithoutCreatingDirectories()
    {
        using var fixture = new Fixture();
        var missing = Path.Combine(fixture.Root, "missing");
        var paths = fixture.Paths with { DataDirectory = missing, AssetsDirectory = Path.Combine(missing, "assets") };
        var cleanup = new RetiredModelCacheCleanup(paths);
        Assert.Equal(RetiredModelCacheCleanupResult.Empty, cleanup.TryCleanup());
        Assert.Equal(RetiredModelCacheCleanupResult.Empty, cleanup.TryCleanup());
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void FileAndDirectoryLeavesArePreservedWhenLinkedOrNotOrdinary()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Root, "outside.onnx");
        File.WriteAllText(target, "Shared target remains unchanged.");
        var fileLink = fixture.Retired("model.onnx");
        File.CreateSymbolicLink(fileLink, target);
        var directoryTarget = Path.Combine(fixture.Root, "outside-directory");
        Directory.CreateDirectory(directoryTarget);
        var directoryLink = fixture.Retired("tokenizer.json");
        Directory.CreateSymbolicLink(directoryLink, directoryTarget);
        Directory.CreateDirectory(fixture.Retired("validation.json"));
        var result = new RetiredModelCacheCleanup(fixture.Paths).TryCleanup();
        Assert.Null(result.RootIssue);
        Assert.Equal(new[] { "model.onnx", "tokenizer.json", "validation.json" }.Order(), result.SkippedFiles.Order());
        Assert.Empty(result.DeletedFiles);
        Assert.NotNull(new FileInfo(fileLink).LinkTarget);
        Assert.NotNull(new DirectoryInfo(directoryLink).LinkTarget);
        Assert.Equal("Shared target remains unchanged.", File.ReadAllText(target));
        File.Delete(fileLink);
        Directory.Delete(directoryLink);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("assets")]
    [InlineData("granite")]
    [InlineData("revision")]
    [InlineData("ancestor")]
    public void EveryLinkedDirectoryComponentIsRejected(string linkedPart)
    {
        using var fixture = new Fixture();
        fixture.WriteRetired("model.onnx");
        var component = linkedPart switch
        {
            "data" => fixture.Paths.DataDirectory,
            "assets" => fixture.Paths.AssetsDirectory,
            "granite" => Path.Combine(fixture.Paths.AssetsDirectory, "granite"),
            "revision" => fixture.RevisionDirectory,
            _ => fixture.Root
        };
        var target = component + "-physical";
        Directory.Move(component, target);
        Directory.CreateSymbolicLink(component, target);
        try
        {
            var result = Program.RunRetiredModelCleanup(new RetiredModelCacheCleanup(fixture.Paths));
            Assert.NotNull(result.RootIssue);
            Assert.Empty(result.DeletedFiles);
            Assert.True(File.Exists(fixture.Retired("model.onnx")));
            Assert.NotNull(new DirectoryInfo(component).LinkTarget);
        }
        finally
        {
            Directory.Delete(component);
            Directory.Move(target, component);
        }
    }

    [Fact]
    public void SharedHardlinkedAssetIsPreserved()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "shared.onnx");
        File.WriteAllText(outside, "A shared historical experiment asset.");
        var inside = fixture.Retired("model.onnx");
        var created = OperatingSystem.IsWindows() ? CreateHardLinkW(inside, outside, IntPtr.Zero) : link(outside, inside) == 0;
        Assert.True(created);
        var result = new RetiredModelCacheCleanup(fixture.Paths).TryCleanup();
        Assert.Equal(["model.onnx"], result.SkippedFiles);
        Assert.True(File.Exists(inside));
        Assert.Equal(File.ReadAllBytes(outside), File.ReadAllBytes(inside));
    }

    [Fact]
    public void LockedFilesAreReportedWithoutStartupFailureThenRetried()
    {
        using var fixture = new Fixture();
        var sentinel = fixture.WriteRetired("model.onnx");
        fixture.WriteRetired("model.onnx.partial");
        var cleanup = new RetiredModelCacheCleanup(fixture.Paths);
        using (var locked = new FileStream(fixture.Retired("model.onnx"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var first = Program.RunRetiredModelCleanup(cleanup);
            Assert.Equal(["model.onnx"], first.PendingFiles);
            Assert.Equal(["model.onnx.partial"], first.DeletedFiles);
            Assert.Equal(sentinel.Length, locked.Length);
            Assert.Contains("retried", MainViewModel.RetiredModelCleanupMessage(first));
        }
        var retry = Program.RunRetiredModelCleanup(cleanup);
        Assert.Equal(["model.onnx"], retry.DeletedFiles);
        Assert.False(retry.NeedsAttention);
        Assert.False(File.Exists(fixture.Retired("model.onnx")));
    }

    [Fact]
    public void UnexpectedCleanupFailureDoesNotBreakStartupOrAffectOtherFiles()
    {
        using var fixture = new Fixture();
        fixture.WriteRetired("model.onnx");
        fixture.WriteRetired("tokenizer.json");
        var throwOnce = true;
        var cleanup = new RetiredModelCacheCleanup(fixture.Paths, null, beforeFileOpen: path =>
        {
            if (Path.GetFileName(path) == "model.onnx" && throwOnce)
            {
                throwOnce = false;
                throw new InvalidOperationException("Synthetic unexpected startup-cleanup failure.");
            }
        }, beforeDelete: null);
        var first = Program.RunRetiredModelCleanup(cleanup);
        Assert.Equal(["model.onnx"], first.PendingFiles);
        Assert.Equal(["tokenizer.json"], first.DeletedFiles);
        Assert.Equal(["model.onnx"], cleanup.TryCleanup().DeletedFiles);
    }

    [Fact]
    public void SymlinkSwapBeforeOpenNeverDeletesLinkOrItsTarget()
    {
        using var fixture = new Fixture();
        fixture.WriteRetired("model.onnx");
        var outside = Path.Combine(fixture.Root, "outside.onnx");
        File.WriteAllText(outside, "The target must not be opened or deleted.");
        var swapped = false;
        var cleanup = new RetiredModelCacheCleanup(fixture.Paths, null, beforeFileOpen: path =>
        {
            if (Path.GetFileName(path) != "model.onnx" || swapped) return;
            swapped = true;
            File.Delete(path);
            File.CreateSymbolicLink(path, outside);
        }, beforeDelete: null);
        var result = cleanup.TryCleanup();
        Assert.Equal(["model.onnx"], result.SkippedFiles);
        Assert.NotNull(new FileInfo(fixture.Retired("model.onnx")).LinkTarget);
        Assert.Equal("The target must not be opened or deleted.", File.ReadAllText(outside));
        File.Delete(fixture.Retired("model.onnx"));
    }

    [Fact]
    public void UnixLeafSwapAfterOpenIsRecheckedAndProtected()
    {
        if (OperatingSystem.IsWindows()) return; // Pinned leaf blocks replacement; deletion uses its exact handle.
        using var fixture = new Fixture();
        fixture.WriteRetired("model.onnx");
        var outside = Path.Combine(fixture.Root, "outside.onnx");
        File.WriteAllText(outside, "Shared source.");
        var cleanup = new RetiredModelCacheCleanup(fixture.Paths, null, beforeFileOpen: null, beforeDelete: path =>
        {
            File.Move(path, path + ".original");
            File.CreateSymbolicLink(path, outside);
        });
        var result = cleanup.TryCleanup();
        Assert.Equal(["model.onnx"], result.SkippedFiles);
        Assert.True(File.Exists(fixture.Retired("model.onnx.original")));
        Assert.NotNull(new FileInfo(fixture.Retired("model.onnx")).LinkTarget);
        Assert.Equal("Shared source.", File.ReadAllText(outside));
        File.Delete(fixture.Retired("model.onnx"));
    }

    [Fact]
    public void UnixAncestorSwapDuringCleanupPreservesBothTrees()
    {
        if (OperatingSystem.IsWindows()) return; // Pinned ancestor handles prevent replacement.
        using var fixture = new Fixture();
        var original = fixture.WriteRetired("model.onnx");
        var moved = fixture.RevisionDirectory + "-retained";
        var outside = Path.Combine(fixture.Root, "shared-models");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "model.onnx"), "Shared target.");
        var swapped = false;
        var cleanup = new RetiredModelCacheCleanup(fixture.Paths, null, beforeFileOpen: path =>
        {
            if (swapped) return;
            swapped = true;
            Directory.Move(fixture.RevisionDirectory, moved);
            Directory.CreateSymbolicLink(fixture.RevisionDirectory, outside);
        }, beforeDelete: null);
        try
        {
            var result = cleanup.TryCleanup();
            Assert.Empty(result.DeletedFiles);
            Assert.True(result.NeedsAttention);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(moved, "model.onnx")));
            Assert.Equal("Shared target.", File.ReadAllText(Path.Combine(outside, "model.onnx")));
        }
        finally
        {
            Directory.Delete(fixture.RevisionDirectory);
            Directory.Move(moved, fixture.RevisionDirectory);
        }
    }

    [Fact]
    public void UnixFifoIsPreservedWithoutBlockingStartup()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        Assert.Equal(0, mkfifo(fixture.Retired("model.onnx"), 0x180)); // Owner read/write.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var result = Program.RunRetiredModelCleanup(new RetiredModelCacheCleanup(fixture.Paths));
        Assert.Equal(["model.onnx"], result.SkippedFiles);
        Assert.Empty(result.DeletedFiles);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        File.Delete(fixture.Retired("model.onnx"));
    }

    [Fact]
    public void ReadOnlyOrDeniedCacheIsReportedAndCanBeRetried()
    {
        using var fixture = new Fixture();
        fixture.WriteRetired("model.onnx");
        var cleanup = new RetiredModelCacheCleanup(fixture.Paths);
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(fixture.Retired("model.onnx"), FileAttributes.ReadOnly);
            try { Assert.Equal(["model.onnx"], cleanup.TryCleanup().PendingFiles); }
            finally { File.SetAttributes(fixture.Retired("model.onnx"), FileAttributes.Normal); }
        }
        else
        {
            var mode = File.GetUnixFileMode(fixture.RevisionDirectory);
            File.SetUnixFileMode(fixture.RevisionDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try { Assert.Equal(["model.onnx"], cleanup.TryCleanup().PendingFiles); }
            finally { File.SetUnixFileMode(fixture.RevisionDirectory, mode); }
        }
        Assert.Equal(["model.onnx"], cleanup.TryCleanup().DeletedFiles);
    }

    [Fact]
    public async Task ConcurrentNormalCallersSerializeAndDeleteOnlyOnce()
    {
        using var fixture = new Fixture();
        foreach (var name in RetiredModelCacheCleanup.FileNames) fixture.WriteRetired(name);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            new RetiredModelCacheCleanup(fixture.Paths).TryCleanup(), TestContext.Current.CancellationToken)));
        Assert.Equal(RetiredModelCacheCleanup.FileNames.Count, results.Sum(result => result.DeletedFiles.Count));
        Assert.All(results, result => Assert.False(result.NeedsAttention));
    }

    [Fact]
    public void NotificationClearlyDistinguishesRemovalNoOpAndProtectedOrPendingFiles()
    {
        Assert.Null(MainViewModel.RetiredModelCleanupMessage(RetiredModelCacheCleanupResult.Empty));
        Assert.Contains("Removed", MainViewModel.RetiredModelCleanupMessage(new(["model.onnx"], [], [])));
        Assert.Contains("protected", MainViewModel.RetiredModelCleanupMessage(new([], ["model.onnx"], [])));
        Assert.Contains("retried", MainViewModel.RetiredModelCleanupMessage(new([], [], ["model.onnx"])));
        Assert.Contains("kept", MainViewModel.RetiredModelCleanupMessage(new([], [], [], "Unsafe cache root")));
    }

    private sealed record Paths(string DataDirectory, string AssetsDirectory) : IAppPaths
    {
        public string DatabasePath => Path.Combine(DataDirectory, "index.db");
        public string LogsDirectory => Path.Combine(DataDirectory, "logs");
        public string TempDirectory => Path.Combine(DataDirectory, "temp");
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            // macOS /var and /tmp are standard symlinks; tests must use a physical private base.
            var temporaryBase = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
            Root = Path.Combine(temporaryBase, "ContextMole.RetiredCache.Tests", Guid.NewGuid().ToString("N"));
            var data = Path.Combine(Root, "data");
            Paths = new(data, Path.Combine(data, "assets"));
            Directory.CreateDirectory(RevisionDirectory);
        }
        public string Root { get; }
        public Paths Paths { get; }
        public string RevisionDirectory => Path.Combine(Paths.AssetsDirectory, "granite", RetiredModelCacheCleanup.RetiredRevision);
        public string Retired(string name) => Path.Combine(RevisionDirectory, name);
        public byte[] WriteRetired(string name)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"Disposable retired installer fixture: {name}");
            File.WriteAllBytes(Retired(name), bytes);
            return bytes;
        }
        public Dictionary<string, byte[]> WriteProtectedSentinels()
        {
            var current = GraniteEmbeddingModels.Get(EmbeddingModelChoice.Granite97M).Revision;
            var relativePaths = new[]
            {
                $"assets/granite/{current}/model.onnx", $"assets/granite/{current}/tokenizer.json",
                "assets/pp-ocr/model.onnx", "assets/THIRD-PARTY-NOTICES.txt", "assets/gemma-terms-acceptance.json",
                "index.db", "ui-state/embedding-model.txt", "source/document.pdf",
                $"assets/granite/{RetiredModelCacheCleanup.RetiredRevision}/notes.txt",
                $"assets/granite/{RetiredModelCacheCleanup.RetiredRevision}/custom/model.onnx",
                "assets/granite/unknown-revision/model.onnx", "custom-models/model.onnx"
            };
            var result = new Dictionary<string, byte[]>();
            foreach (var relative in relativePaths)
            {
                var path = Path.Combine(Paths.DataDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var bytes = System.Text.Encoding.UTF8.GetBytes($"Protected sentinel: {relative}");
                File.WriteAllBytes(path, bytes);
                result.Add(path, bytes);
            }
            var shared = Path.Combine(Root, "shared", "granite", RetiredModelCacheCleanup.RetiredRevision, "model.onnx");
            Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
            File.WriteAllText(shared, "Shared benchmark asset must remain.");
            result.Add(shared, File.ReadAllBytes(shared));
            return result;
        }
        public void AssertUnchanged(Dictionary<string, byte[]> sentinels) => Assert.All(sentinels, entry =>
            Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key)));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    [DllImport("libc", SetLastError = true)] private static extern int link(string oldPath, string newPath);
    [DllImport("libc", SetLastError = true)] private static extern int mkfifo(string path, uint mode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newPath, string existingPath, IntPtr security);
}
