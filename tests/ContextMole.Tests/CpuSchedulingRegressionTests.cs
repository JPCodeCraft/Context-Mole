using ContextMole.Core;
using ContextMole.Indexing;
using ContextMole.Infrastructure;

using Microsoft.Extensions.Logging;

namespace ContextMole.Tests;

public sealed class CpuSchedulingRegressionTests
{
    [Fact]
    public void IndexingPublicValuesAndCpuOnlyConstructorArePreserved()
    {
        Assert.Equal(0, (int)IndexingPipelineStage.InspectingSource);
        Assert.Equal(1, (int)IndexingPipelineStage.Hashing);
        Assert.Equal(2, (int)IndexingPipelineStage.PreparingRevision);
        Assert.Equal(3, (int)IndexingPipelineStage.ExtractingContent);
        Assert.Equal(4, (int)IndexingPipelineStage.ChunkingText);
        Assert.Equal(5, (int)IndexingPipelineStage.GeneratingEmbeddings);
        Assert.Equal(6, (int)IndexingPipelineStage.VerifyingSource);
        Assert.Equal(7, (int)IndexingPipelineStage.WritingIndex);
        Assert.Equal(8, (int)IndexingPipelineStage.RecordingError);
        Assert.Equal(11, (int)IndexingPipelineStage.WaitingForCpu);

        var activityConstructor = new[]
        {
            typeof(Guid), typeof(Guid), typeof(Guid), typeof(string), typeof(IndexingPipelineStage),
            typeof(TimeSpan), typeof(TimeSpan), typeof(DateTimeOffset)
        };
        Assert.NotNull(typeof(IndexingActivitySnapshot).GetConstructor(activityConstructor));
        Assert.Contains(typeof(IndexingActivitySnapshot).GetMethods(), method =>
            method.Name == "Deconstruct" && method.GetParameters().Length == 8);

        var signature = new[]
        {
            typeof(IIndexWriter),
            typeof(ISearchStore),
            typeof(IAppPaths),
            typeof(IDocumentExtractor),
            typeof(IEmbeddingGenerator),
            typeof(IndexingActivityTracker),
            typeof(EmbeddingPolicyRefreshTracker),
            typeof(IGlobalCpuBudget),
            typeof(ILogger<IndexingCoordinator>)
        };
        Assert.NotNull(typeof(IndexingCoordinator).GetConstructor(signature));
    }

    [Fact]
    public async Task ConcurrentWorkersCanYieldToSerializedFullCpuWorkWithoutDeadlock()
    {
        var settings = new FixedCpuUsageSettings(logicalProcessorCount: 8);
        using var cpu = new GlobalCpuBudget(settings);
        var readyCount = 0;
        var bothWorkersReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RunFullCapacityWorkAsync()
        {
            using var worker = await cpu.AcquireWorkerAsync(TestContext.Current.CancellationToken);
            using var activation = worker.Activate();
            if (Interlocked.Increment(ref readyCount) == 2) bothWorkersReady.TrySetResult();
            await bothWorkersReady.Task.WaitAsync(TestContext.Current.CancellationToken);

            using var fullCapacity = await cpu.AcquireFullCapacityAsync(TestContext.Current.CancellationToken);
            Assert.Equal(settings.ThreadLimit, fullCapacity.ThreadCount);
        }

        await Task.WhenAll(
                Task.Run(RunFullCapacityWorkAsync, TestContext.Current.CancellationToken),
                Task.Run(RunFullCapacityWorkAsync, TestContext.Current.CancellationToken))
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OcrInferenceUsesFullProfileCapacityAndReleasesItOnFailure()
    {
        using var paths = new TemporaryAppPaths();
        WriteIdentityOcrAssets(paths);
        var cpuSettings = new FixedCpuUsageSettings(logicalProcessorCount: 8);
        var cpuBudget = new RecordingCpuBudget(cpuSettings);
        using var engine = new PpOcrV6Engine(paths, cpuSettings, cpuBudget);
        engine.MarkAssetsPrepared();

        await engine.EnsureAvailableAsync(TestContext.Current.CancellationToken);
        Assert.True(engine.IsAvailable);
        Assert.Equal(0, cpuBudget.FullCapacityAcquisitions);

        var exception = await Assert.ThrowsAsync<ContextMoleException>(() => engine.RecognizeAsync(
            new OcrRequest(ReadOnlyMemory<byte>.Empty, ".png", TimeSpan.FromSeconds(10)),
            TestContext.Current.CancellationToken));

        Assert.Equal("ocr_image_invalid", exception.Code);
        Assert.Equal(1, cpuBudget.FullCapacityAcquisitions);
        Assert.Equal(1, cpuBudget.FullCapacityDisposals);
        Assert.Equal(cpuSettings.ThreadLimit, cpuBudget.LastThreadCount);
    }

    private static void WriteIdentityOcrAssets(IAppPaths paths)
    {
        var modelDirectory = Path.Combine(paths.AssetsDirectory, "pp-ocrv6-medium",
            $"{PpOcrV6Engine.DetectorRevision[..12]}-{PpOcrV6Engine.RecognizerRevision[..12]}");
        Directory.CreateDirectory(modelDirectory);
        var identityModel = Convert.FromBase64String(
            "CAo6TAoZCgVpbnB1dBIGb3V0cHV0IghJZGVudGl0eRIEdGlueVoTCgVpbnB1dBIKCggIARIECgIIAWIUCgZvdXRwdXQSCgoICAESBAoCCAFCAhAN");
        File.WriteAllBytes(Path.Combine(modelDirectory, "detector.onnx"), identityModel);
        File.WriteAllBytes(Path.Combine(modelDirectory, "recognizer.onnx"), identityModel);
        File.WriteAllLines(Path.Combine(modelDirectory, "recognizer.yml"),
            ["character_dict:", .. Enumerable.Range(0, 100).Select(index => $"  - char{index}")]);
    }

    private sealed class FixedCpuUsageSettings(int logicalProcessorCount) : ICpuUsageSettings
    {
        public CpuUsageProfile Profile => CpuUsageProfile.Normal;
        public int LogicalProcessorCount { get; } = logicalProcessorCount;
        public int ThreadLimit => CpuUsageSettings.CalculateThreadLimit(Profile, LogicalProcessorCount);
        public int MaximumThreadLimit =>
            CpuUsageSettings.CalculateThreadLimit(CpuUsageProfile.Heavy, LogicalProcessorCount);
        public void SetProfile(CpuUsageProfile profile)
        {
            if (profile != Profile) throw new NotSupportedException("This test setting is fixed.");
        }
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class RecordingCpuBudget(ICpuUsageSettings settings) : IGlobalCpuBudget
    {
        private int _fullCapacityAcquisitions;
        private int _fullCapacityDisposals;

        public int MaximumWorkerCount => settings.MaximumThreadLimit;
        public int FullCapacityAcquisitions => Volatile.Read(ref _fullCapacityAcquisitions);
        public int FullCapacityDisposals => Volatile.Read(ref _fullCapacityDisposals);
        public int LastThreadCount { get; private set; }

        public ValueTask<ICpuWorkerLease> AcquireWorkerAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ICpuFullCapacityLease> AcquireFullCapacityAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _fullCapacityAcquisitions);
            LastThreadCount = settings.ThreadLimit;
            return ValueTask.FromResult<ICpuFullCapacityLease>(
                new FullCapacityLease(LastThreadCount,
                    () => Interlocked.Increment(ref _fullCapacityDisposals)));
        }

        private sealed class FullCapacityLease(int threadCount, Action onDispose) : ICpuFullCapacityLease
        {
            private int _disposed;
            public int ThreadCount { get; } = threadCount;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose();
            }
        }
    }

    private sealed class TemporaryAppPaths : IAppPaths, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ContextMole.Tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryAppPaths()
        {
            DataDirectory = Path.Combine(_root, "data");
            DatabasePath = Path.Combine(DataDirectory, "index.db");
            AssetsDirectory = Path.Combine(_root, "assets");
            LogsDirectory = Path.Combine(_root, "logs");
            TempDirectory = Path.Combine(_root, "temp");
            Directory.CreateDirectory(DataDirectory);
        }

        public string DataDirectory { get; }
        public string DatabasePath { get; }
        public string AssetsDirectory { get; }
        public string LogsDirectory { get; }
        public string TempDirectory { get; }
        public string CpuSettingsPath => Path.Combine(DataDirectory, "ui-state", "cpu-usage-profile.txt");
        public string EmbeddingSettingsPath => Path.Combine(DataDirectory, "ui-state", "embedding-model.txt");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
