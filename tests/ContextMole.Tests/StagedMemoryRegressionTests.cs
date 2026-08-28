using ContextMole.Core;
using ContextMole.Indexing;
using ContextMole.Infrastructure;

using Microsoft.Extensions.Logging;

namespace ContextMole.Tests;

public sealed class StagedMemoryRegressionTests
{
    private const long Mebibyte = 1024L * 1024;
    private const long Gibibyte = 1024L * Mebibyte;
    private const long OcrTargetBytes = 2304L * Mebibyte;

    [Fact]
    public async Task PdfBaseReservationIsAdmittedOnFourGibibyteMachine()
    {
        var estimate = IndexingMemoryEstimator.Estimate(Job("text-only.pdf", ".pdf"), 2L * Mebibyte);
        using var controller = new MemoryAdmissionController(new FixedSnapshotProvider(
            new SystemMemorySnapshot(4L * Gibibyte, 4L * Gibibyte, 256L * Mebibyte)));

        using var lease = await controller.AcquireAsync(estimate, TestContext.Current.CancellationToken);

        Assert.True(estimate.MayRequestNestedUpgrade);
        Assert.Equal(520L * Mebibyte, estimate.EstimatedBytes);
        Assert.Equal(estimate.EstimatedBytes, lease.ReservedBytes);
        Assert.False(lease.IsExclusive);
        Assert.Equal(1536L * Mebibyte, lease.ProcessSoftLimitBytes);
        Assert.Equal(2L * Gibibyte, lease.SystemReserveBytes);
    }

    [Fact]
    public async Task NestedOcrAdmissionReservesOnlyDeltaAndReleasesExclusiveUpgrade()
    {
        using var controller = CreateEightGibibyteController();
        var baseBytes = 512L * Mebibyte;
        var baseEstimate = UpgradeCapableEstimate(baseBytes, "pdf-base");
        using var parent = await controller.AcquireAsync(baseEstimate, TestContext.Current.CancellationToken);
        IMemoryLease upgrade;
        using (parent.Activate())
        {
            upgrade = await controller.AcquireAsync(
                new MemoryWorkEstimate(OcrTargetBytes, "ocr-inference"),
                TestContext.Current.CancellationToken);
        }
        Assert.Equal(OcrTargetBytes - baseBytes, upgrade.ReservedBytes);
        Assert.Equal(OcrTargetBytes, parent.ReservedBytes);
        Assert.True(upgrade.IsExclusive);
        Assert.True(parent.IsExclusive);

        var followerTask = controller.AcquireAsync(
            new MemoryWorkEstimate(128L * Mebibyte, "follower"),
            TestContext.Current.CancellationToken).AsTask();
        await Task.Yield();
        Assert.False(followerTask.IsCompleted);

        upgrade.Dispose();
        Assert.Equal(baseBytes, parent.ReservedBytes);
        Assert.False(parent.IsExclusive);
        using var follower = await followerTask.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.False(follower.IsExclusive);
    }

    [Fact]
    public async Task SoleNestedOcrUpgradeBypassesSoftTargetsWithinHardSafetyFloor()
    {
        using var controller = new MemoryAdmissionController(new FixedSnapshotProvider(
            new SystemMemorySnapshot(8L * Gibibyte, 4L * Gibibyte, 3L * Gibibyte)));
        using var parent = await controller.AcquireAsync(
            UpgradeCapableEstimate(512L * Mebibyte, "pdf-base"),
            TestContext.Current.CancellationToken);
        Assert.True(parent.IsExclusive);

        using var activation = parent.Activate();
        using var upgrade = await controller.AcquireAsync(
            new MemoryWorkEstimate(OcrTargetBytes, "ocr-inference"),
            TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.True(upgrade.IsExclusive);
        Assert.Equal(OcrTargetBytes - 512L * Mebibyte, upgrade.ReservedBytes);
        Assert.True(upgrade.AdmissionSnapshot.AvailablePhysicalBytes - OcrTargetBytes >=
                    MemoryAdmissionController.CalculateHardSafetyReserve(8L * Gibibyte));
    }

    [Fact]
    public async Task NestedOcrWaitInheritsCorrelationAndClearsOnCancellation()
    {
        var correlationId = Guid.NewGuid();
        var statuses = new MemoryAdmissionStatusStore();
        using var controller = new MemoryAdmissionController(new FixedSnapshotProvider(
            new SystemMemorySnapshot(8L * Gibibyte, 1800L * Mebibyte, 3L * Gibibyte)), statuses);
        using var parent = await controller.AcquireAsync(
            UpgradeCapableEstimate(512L * Mebibyte, "pdf-base") with { CorrelationId = correlationId },
            TestContext.Current.CancellationToken);
        using var activation = parent.Activate();
        using var cancellation = new CancellationTokenSource();

        var pending = controller.AcquireAsync(
            new MemoryWorkEstimate(OcrTargetBytes, "ocr-inference"), cancellation.Token).AsTask();

        Assert.True(statuses.TryGet(correlationId, out var status));
        Assert.Equal(MemoryAdmissionWaitReason.SystemMemory, status.Reason);
        Assert.Equal(OcrTargetBytes, status.RequestedBytes);
        Assert.Equal(1, status.QueuePosition);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.False(statuses.TryGet(correlationId, out _));
    }

    [Fact]
    public async Task CancelledNestedUpgradeRestoresParentAndCanBeRetried()
    {
        using var controller = CreateEightGibibyteController();
        var baseBytes = 512L * Mebibyte;
        using var parent = await controller.AcquireAsync(
            UpgradeCapableEstimate(baseBytes, "pdf-base"), TestContext.Current.CancellationToken);
        using var blockerParent = await controller.AcquireAsync(
            UpgradeCapableEstimate(baseBytes, "second-pdf-base"),
            TestContext.Current.CancellationToken);
        IMemoryLease blocker;
        using (blockerParent.Activate())
        {
            blocker = await controller.AcquireAsync(
                new MemoryWorkEstimate(OcrTargetBytes, "ocr-inference"),
                TestContext.Current.CancellationToken);
        }
        using var activation = parent.Activate();
        using var cancellation = new CancellationTokenSource();

        var pending = controller.AcquireAsync(
            new MemoryWorkEstimate(OcrTargetBytes, "ocr-inference"), cancellation.Token).AsTask();
        await Task.Yield();
        Assert.False(pending.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.Equal(baseBytes, parent.ReservedBytes);
        Assert.False(parent.IsExclusive);

        blocker.Dispose();
        using var retry = await controller.AcquireAsync(
            new MemoryWorkEstimate(OcrTargetBytes, "ocr-inference"),
            TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.True(retry.IsExclusive);
        Assert.Equal(OcrTargetBytes - baseBytes, retry.ReservedBytes);
    }

    [Fact]
    public async Task UpgradeCapableWorkAndFullCpuCapacityDoNotDeadlock()
    {
        using var controller = CreateEightGibibyteController();
        var estimate = UpgradeCapableEstimate(512L * Mebibyte, "pdf-base");
        using var firstParent = await controller.AcquireAsync(estimate, TestContext.Current.CancellationToken);
        using var secondParent = await controller.AcquireAsync(estimate, TestContext.Current.CancellationToken);
        var settings = new FixedCpuUsageSettings(logicalProcessorCount: 8);
        using var cpu = new GlobalCpuBudget(settings);
        var readyCount = 0;
        var bothWorkersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RunOcrAsync(IMemoryLease parent)
        {
            using var worker = await cpu.AcquireWorkerAsync(TestContext.Current.CancellationToken);
            using var memoryActivation = parent.Activate();
            using var workerActivation = worker.Activate();
            if (Interlocked.Increment(ref readyCount) == 2) bothWorkersReady.TrySetResult();
            await bothWorkersReady.Task.WaitAsync(TestContext.Current.CancellationToken);

            using var fullCapacity = await cpu.AcquireFullCapacityAsync(TestContext.Current.CancellationToken);
            using var upgrade = await controller.AcquireAsync(
                new MemoryWorkEstimate(OcrTargetBytes, "ocr-inference"),
                TestContext.Current.CancellationToken);
            Assert.Equal(settings.ThreadLimit, fullCapacity.ThreadCount);
        }

        await Task.WhenAll(
                Task.Run(() => RunOcrAsync(firstParent), TestContext.Current.CancellationToken),
                Task.Run(() => RunOcrAsync(secondParent), TestContext.Current.CancellationToken))
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MultipleUpgradeCapableParsersShareOneReservedUpgradeHeadroom()
    {
        using var controller = new MemoryAdmissionController(new FixedSnapshotProvider(
            new SystemMemorySnapshot(16L * Gibibyte, 16L * Gibibyte, 1280L * Mebibyte)));
        var estimate = UpgradeCapableEstimate(1L * Gibibyte, "container-base");

        using var first = await controller.AcquireAsync(estimate, TestContext.Current.CancellationToken);
        using var second = await controller.AcquireAsync(estimate, TestContext.Current.CancellationToken);
        var thirdTask = controller.AcquireAsync(estimate, TestContext.Current.CancellationToken).AsTask();
        await Task.Yield();
        Assert.False(thirdTask.IsCompleted);

        IMemoryLease firstUpgrade;
        using (first.Activate())
        {
            firstUpgrade = await controller.AcquireAsync(
                new MemoryWorkEstimate(OcrTargetBytes, "ocr-inference"),
                TestContext.Current.CancellationToken);
        }

        Task<IMemoryLease> secondUpgradeTask;
        using (second.Activate())
        {
            secondUpgradeTask = controller.AcquireAsync(
                new MemoryWorkEstimate(OcrTargetBytes, "ocr-inference"),
                TestContext.Current.CancellationToken).AsTask();
        }
        await Task.Yield();
        Assert.False(secondUpgradeTask.IsCompleted);

        firstUpgrade.Dispose();
        using var secondUpgrade = await secondUpgradeTask.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(OcrTargetBytes - 1L * Gibibyte, secondUpgrade.ReservedBytes);

        secondUpgrade.Dispose();
        first.Dispose();
        using var third = await thirdTask.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NestedHardFallbackIncludesEveryActiveRootReservation()
    {
        using var controller = new MemoryAdmissionController(new FixedSnapshotProvider(
            new SystemMemorySnapshot(16L * Gibibyte, 5L * Gibibyte, 3L * Gibibyte)));
        var estimate = UpgradeCapableEstimate(512L * Mebibyte, "pdf-base");
        using var first = await controller.AcquireAsync(estimate, TestContext.Current.CancellationToken);
        using var second = await controller.AcquireAsync(estimate, TestContext.Current.CancellationToken);

        using var activation = first.Activate();
        using var upgrade = await controller.AcquireAsync(
            new MemoryWorkEstimate(OcrTargetBytes, "ocr-inference"),
            TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.True(upgrade.IsExclusive);
        Assert.True(upgrade.AdmissionSnapshot.AvailablePhysicalBytes -
                    (second.ReservedBytes + OcrTargetBytes) >=
                    MemoryAdmissionController.CalculateHardSafetyReserve(16L * Gibibyte));
    }

    [Fact]
    public void IndexingPublicCompatibilityValuesAndConstructorArePreserved()
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
        Assert.Equal(9, (int)IndexingPipelineStage.WaitingForMemory);
        Assert.Equal(10, (int)IndexingPipelineStage.QueuedForAdmission);
        Assert.Equal(11, (int)IndexingPipelineStage.WaitingForCpu);

        var activityConstructor = new[]
        {
            typeof(Guid), typeof(Guid), typeof(Guid), typeof(string), typeof(IndexingPipelineStage),
            typeof(TimeSpan), typeof(TimeSpan), typeof(DateTimeOffset)
        };
        Assert.NotNull(typeof(IndexingActivitySnapshot).GetConstructor(activityConstructor));
        Assert.Contains(typeof(IndexingActivitySnapshot).GetMethods(), method =>
            method.Name == "Deconstruct" && method.GetParameters().Length == 8);

        var legacySignature = new[]
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
        Assert.NotNull(typeof(IndexingCoordinator).GetConstructor(legacySignature));
    }

    [Fact]
    public async Task OcrSessionLoadAndInferenceAreWrappedByFullTargetReservation()
    {
        using var paths = new TemporaryAppPaths();
        WriteIdentityOcrAssets(paths);
        var memory = new RecordingMemoryAdmissionController();
        var cpuSettings = new FixedCpuUsageSettings(logicalProcessorCount: 8);
        var cpuBudget = new ObservingCpuBudget(cpuSettings, () => memory.ActiveActivations > 0);
        using var engine = new PpOcrV6Engine(paths, cpuSettings, cpuBudget, memory);
        engine.MarkAssetsPrepared();

        await engine.PrepareAssetsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(memory.Estimates);

        await engine.EnsureAvailableAsync(TestContext.Current.CancellationToken);
        Assert.True(engine.IsAvailable);
        Assert.Equal(1, memory.Acquisitions);
        Assert.Equal(1, memory.Activations);
        Assert.Equal(1, memory.Disposals);

        var exception = await Assert.ThrowsAsync<ContextMoleException>(() => engine.RecognizeAsync(
            new OcrRequest(ReadOnlyMemory<byte>.Empty, ".png", TimeSpan.FromSeconds(10)),
            TestContext.Current.CancellationToken));

        Assert.Equal("ocr_image_invalid", exception.Code);
        Assert.Equal(2, memory.Acquisitions);
        Assert.Equal(2, memory.Activations);
        Assert.Equal(2, memory.Disposals);
        Assert.Equal(0, memory.ActiveActivations);
        Assert.All(memory.Estimates, estimate =>
        {
            Assert.Equal(OcrTargetBytes, estimate.EstimatedBytes);
            Assert.Equal("ocr-inference", estimate.Workload);
        });
        Assert.True(cpuBudget.FullCapacityWasAcquiredBeforeMemory);
    }

    private static MemoryAdmissionController CreateEightGibibyteController() => new(
        new FixedSnapshotProvider(new SystemMemorySnapshot(
            8L * Gibibyte, 8L * Gibibyte, 256L * Mebibyte)));

    private static MemoryWorkEstimate UpgradeCapableEstimate(long bytes, string workload) =>
        new(bytes, workload)
        {
            MaximumReservationBytes = Math.Max(bytes, OcrTargetBytes)
        };

    private static IndexJobLease Job(string sourcePath, string extension) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), sourcePath, extension, 1,
        IndexJobKind.Index, 0);

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

    private sealed class FixedSnapshotProvider(SystemMemorySnapshot value) : ISystemMemorySnapshotProvider
    {
        public SystemMemorySnapshot Capture() => value;
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

    private sealed class ObservingCpuBudget(ICpuUsageSettings settings, Func<bool> memoryIsActive)
        : IGlobalCpuBudget
    {
        public int MaximumWorkerCount => settings.MaximumThreadLimit;
        public bool FullCapacityWasAcquiredBeforeMemory { get; private set; }

        public ValueTask<ICpuWorkerLease> AcquireWorkerAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ICpuFullCapacityLease> AcquireFullCapacityAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FullCapacityWasAcquiredBeforeMemory = !memoryIsActive();
            return ValueTask.FromResult<ICpuFullCapacityLease>(new FullCapacityLease(settings.ThreadLimit));
        }

        private sealed class FullCapacityLease(int threadCount) : ICpuFullCapacityLease
        {
            public int ThreadCount { get; } = threadCount;
            public void Dispose()
            {
            }
        }
    }

    private sealed class RecordingMemoryAdmissionController : IMemoryAdmissionController
    {
        private int _acquisitions;
        private int _activations;
        private int _activeActivations;
        private int _disposals;
        public List<MemoryWorkEstimate> Estimates { get; } = [];
        public int Acquisitions => Volatile.Read(ref _acquisitions);
        public int Activations => Volatile.Read(ref _activations);
        public int ActiveActivations => Volatile.Read(ref _activeActivations);
        public int Disposals => Volatile.Read(ref _disposals);

        public ValueTask<IMemoryLease> AcquireAsync(MemoryWorkEstimate estimate,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Estimates.Add(estimate);
            Interlocked.Increment(ref _acquisitions);
            return ValueTask.FromResult<IMemoryLease>(new RecordingLease(this, estimate.EstimatedBytes));
        }

        private sealed class RecordingLease(RecordingMemoryAdmissionController owner, long reservedBytes)
            : IMemoryLease
        {
            private int _disposed;
            public long ReservedBytes { get; } = reservedBytes;
            public bool IsExclusive => false;
            public SystemMemorySnapshot AdmissionSnapshot => default;
            public long ProcessSoftLimitBytes => long.MaxValue;
            public long SystemReserveBytes => 0;

            public IDisposable Activate()
            {
                Interlocked.Increment(ref owner._activations);
                Interlocked.Increment(ref owner._activeActivations);
                return new Activation(owner);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Increment(ref owner._disposals);
            }
        }

        private sealed class Activation(RecordingMemoryAdmissionController owner) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Decrement(ref owner._activeActivations);
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
