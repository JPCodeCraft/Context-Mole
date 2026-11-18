using System.Reflection;

using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Indexing;
using ContextMole.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
/// <summary>
/// Issue #1: indexing stopped with every file "Waiting for CPU or memory" while the machine sat idle.
/// </summary>
public sealed class IndexingStallRegressionTests
{
    [Fact]
    public async Task TextFilesKeepIndexingWhileTheOcrModelDownloadIsStuck()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(database.Paths.SourceDirectory, "scan.png"), Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="),
            cancellationToken);
        var settings = new SingleSlotCpuUsageSettings();
        using var cpu = new GlobalCpuBudget(settings);
        using var download = new StalledDownloadHandler();
        using var ocr = new PpOcrV6Engine(database.Paths, settings, cpu, PpOcrV6Engine.DefaultSessionIdleTimeout,
            download, TimeSpan.FromMinutes(10), PpOcrV6Engine.DefaultSetupRetryDelay);
        var embeddings = new StorageUnavailableEmbeddings();
        var (projectId, _) = await database.CreateProjectAsync("Scans and notes", cancellationToken);
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new DocumentExtractionRegistry(ocr), embeddings, new IndexingActivityTracker(),
            new EmbeddingPolicyRefreshTracker(), cpu, NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            // The scan is waiting for the OCR model, which will not arrive.
            await download.Started.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            for (var i = 0; i < 3; i++)
                await File.WriteAllTextAsync(Path.Combine(database.Paths.SourceDirectory, $"note-{i}.txt"),
                    $"meeting note {i}", cancellationToken);

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while ((await database.Store.ListProjectsAsync(cancellationToken)).Single(item => item.Id == projectId)
                   .IndexedCount < 3)
            {
                Assert.True(DateTime.UtcNow < deadline,
                    "Text files waited for the CPU slot held by a file whose OCR model was still downloading.");
                await Task.Delay(50, cancellationToken);
            }
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task MixedOcrLargeFilesProfileChangesAndPausesNeverStall()
    {
        const int seed = 3;
        const bool changeProfiles = true;
        const bool pauses = true;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        const int fileCount = 120;
        for (var i = 0; i < fileCount; i++)
        {
            var path = Path.Combine(database.Paths.SourceDirectory, $"file-{i:D4}.txt");
            if (i % 11 == 0)
            {
                await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
                stream.SetLength(40L * 1024 * 1024);
            }
            else await File.WriteAllTextAsync(path, $"content {i}", cancellationToken);
        }

        var settings = new MutableCpuUsageSettings(8, CpuUsageProfile.Normal);
        using var cpu = new GlobalCpuBudget(settings);
        var extractor = new OcrLikeExtractor(cpu, seed);
        var embeddings = new StorageUnavailableEmbeddings();
        var activities = new IndexingActivityTracker();
        var memory = new SourceMemoryAdmission(150L * 1024 * 1024);
        var (projectId, _) = await database.CreateProjectAsync("Stress", cancellationToken);
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            extractor, embeddings, activities, new EmbeddingPolicyRefreshTracker(),
            cpu, NullLogger<IndexingCoordinator>.Instance, memory);

        await coordinator.StartAsync(cancellationToken);
        using var chaosStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var chaos = Task.Run(async () =>
        {
            var random = new Random(seed * 31);
            var profiles = new[] { CpuUsageProfile.Light, CpuUsageProfile.Normal, CpuUsageProfile.Heavy };
            while (!chaosStop.IsCancellationRequested)
            {
                await Task.Delay(random.Next(5, 60), chaosStop.Token).ConfigureAwait(false);
                if (changeProfiles) settings.SetProfile(profiles[random.Next(profiles.Length)]);
                if (pauses && random.Next(6) == 0)
                {
                    coordinator.BeginPause(projectId);
                    await database.Writer.SetProjectPausedAsync(projectId, true, CancellationToken.None);
                    await coordinator.DrainPausedAsync(projectId, CancellationToken.None);
                    coordinator.Resume(projectId);
                    await database.Writer.SetProjectPausedAsync(projectId, false, CancellationToken.None);
                }
            }
        }, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(90);
        var lastIndexed = -1L;
        var lastProgress = DateTime.UtcNow;
        try
        {
            while (true)
            {
                var project = (await database.Store.ListProjectsAsync(cancellationToken)).Single(item => item.Id == projectId);
                if (project.IndexedCount == fileCount && project.PendingCount == 0) break;
                if (project.IndexedCount != lastIndexed)
                {
                    lastIndexed = project.IndexedCount;
                    lastProgress = DateTime.UtcNow;
                }
                if (DateTime.UtcNow - lastProgress > TimeSpan.FromSeconds(20) || DateTime.UtcNow > deadline)
                {
                    var snapshot = activities.GetSnapshot(projectId);
                    Assert.Fail($"Indexing stalled at {project.IndexedCount}/{fileCount} (pending {project.PendingCount}). " +
                        $"Budget {DescribeBudget(cpu)}; memory reserved {memory.ReservedBytes / (1024 * 1024)} MiB; " +
                        $"limit {settings.ThreadLimit}; active items: " +
                        string.Join("; ", snapshot.ActiveItems.Select(item =>
                            $"{Path.GetFileName(item.SourcePath)}={item.Stage}")));
                }
                await Task.Delay(50, cancellationToken);
            }
        }
        finally
        {
            await chaosStop.CancelAsync();
            try { await chaos; } catch (OperationCanceledException) { }
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    private static string DescribeBudget(GlobalCpuBudget cpu)
    {
        var type = typeof(GlobalCpuBudget);
        var active = type.GetField("_activeCapacity", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cpu);
        var waiters = (System.Collections.ICollection)type.GetField("_waiters",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cpu)!;
        return $"active={active}, waiters={waiters.Count}";
    }

    private sealed class OcrLikeExtractor(IGlobalCpuBudget cpu, int seed) : IDocumentExtractor
    {
        private readonly Random _random = new(seed);
        private readonly object _gate = new();

        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken)
        {
            int delay, pages;
            lock (_gate)
            {
                delay = _random.Next(0, 8);
                pages = _random.Next(4) == 0 ? _random.Next(1, 4) : 0;
            }
            await Task.Delay(delay, cancellationToken);
            for (var page = 0; page < pages; page++)
            {
                using var full = await cpu.AcquireFullCapacityAsync(cancellationToken);
                await Task.Delay(2, cancellationToken);
            }
            return new ExtractionResult(new ExtractedNode(Path.GetFileName(request.SourcePath), "text/plain", "root",
                [new ExtractedSection("text " + Path.GetFileName(request.SourcePath),
                    new SourceLocation(LocationKind.Document), ExtractionMethod.NativeText)], []), []);
        }
    }

    /// <summary>One CPU slot and two indexing workers, as on a small machine with the Normal profile.</summary>
    private sealed class SingleSlotCpuUsageSettings : ICpuUsageSettings
    {
        public CpuUsageProfile Profile => CpuUsageProfile.Normal;
        public int LogicalProcessorCount => 3;
        public int ThreadLimit => 1;
        public int MaximumThreadLimit => 2;
        public void SetProfile(CpuUsageProfile profile) => throw new NotSupportedException();
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    /// <summary>A model server that accepts the connection and then never answers.</summary>
    private sealed class StalledDownloadHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class MutableCpuUsageSettings(int logicalProcessorCount, CpuUsageProfile profile) : ICpuUsageSettings
    {
        private CpuUsageProfile _profile = profile;
        public CpuUsageProfile Profile => _profile;
        public int LogicalProcessorCount { get; } = logicalProcessorCount;
        public int ThreadLimit => CpuUsageSettings.CalculateThreadLimit(Profile, LogicalProcessorCount);
        public int MaximumThreadLimit =>
            CpuUsageSettings.CalculateThreadLimit(CpuUsageProfile.Heavy, LogicalProcessorCount);
        public event EventHandler? Changed;

        public void SetProfile(CpuUsageProfile profile)
        {
            if (_profile == profile) return;
            _profile = profile;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
