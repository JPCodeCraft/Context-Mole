using System.Text.Json;

using ContextMole.Core;
using ContextMole.Indexing;
using ContextMole.Infrastructure;

namespace ContextMole.Tests;

public sealed class MemoryAdmissionTests
{
    private const long Mebibyte = 1024L * 1024;
    private const long Gibibyte = 1024L * Mebibyte;

    [Fact]
    public void LimitsScaleWithPhysicalMemoryAndStayWithinProcessBounds()
    {
        Assert.Equal(1536L * Mebibyte, MemoryAdmissionController.CalculateProcessSoftLimit(4L * Gibibyte));
        Assert.Equal(2L * Gibibyte, MemoryAdmissionController.CalculateProcessSoftLimit(8L * Gibibyte));
        Assert.Equal(4L * Gibibyte, MemoryAdmissionController.CalculateProcessSoftLimit(16L * Gibibyte));
        Assert.Equal(4L * Gibibyte, MemoryAdmissionController.CalculateProcessSoftLimit(64L * Gibibyte));

        Assert.Equal(2L * Gibibyte, MemoryAdmissionController.CalculateSystemReserve(8L * Gibibyte));
        Assert.Equal(16L * Gibibyte * 15 / 100,
            MemoryAdmissionController.CalculateSystemReserve(16L * Gibibyte));
        Assert.Equal(256L * Mebibyte,
            MemoryAdmissionController.CalculateHardSafetyReserve(4L * Gibibyte));
        Assert.Equal(512L * Mebibyte,
            MemoryAdmissionController.CalculateHardSafetyReserve(64L * Gibibyte));
    }

    [Fact]
    public void EstimatesUseBaseReservationForOcrCapableDocuments()
    {
        var ocr = IndexingMemoryEstimator.Estimate(Job("scan.pdf", ".pdf"), 2 * Mebibyte);
        var text = IndexingMemoryEstimator.Estimate(Job("notes.txt", ".txt"), 2 * Mebibyte);
        var refresh = IndexingMemoryEstimator.Estimate(
            Job("notes.txt", ".txt", IndexJobKind.EmbeddingRefresh), 2 * Mebibyte);

        Assert.InRange(ocr.EstimatedBytes, 512L * Mebibyte, 1536L * Mebibyte);
        Assert.Equal("pdf-or-image-document", ocr.Workload);
        Assert.True(ocr.MayRequestNestedUpgrade);
        Assert.True(text.MayRequestNestedUpgrade);
        Assert.Equal(MemoryReservationTargets.OcrInferenceBytes, text.MaximumReservationBytes);
        Assert.True(text.EstimatedBytes < refresh.EstimatedBytes);
        Assert.Equal("embedding-refresh", refresh.Workload);
        Assert.False(refresh.MayRequestNestedUpgrade);
    }

    [Fact]
    public async Task TextDocumentsShareOcrHeadroomWithoutReservingItPerRoot()
    {
        using var controller = new MemoryAdmissionController(new MutableSnapshotProvider(
            new SystemMemorySnapshot(8L * Gibibyte, 8L * Gibibyte, 256L * Mebibyte)));
        var estimate = IndexingMemoryEstimator.Estimate(Job("notes.txt", ".txt"), 2 * Mebibyte);

        using var first = await controller.AcquireAsync(estimate, TestContext.Current.CancellationToken);
        using var second = await controller.AcquireAsync(estimate, TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(estimate.EstimatedBytes, first.ReservedBytes);
        Assert.Equal(estimate.EstimatedBytes, second.ReservedBytes);
        Assert.True(second.ReservedBytes < estimate.MaximumReservationBytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedWorkPreservesSharedOcrHeadroomInEitherAdmissionOrder(bool ocrCapableFirst)
    {
        using var controller = new MemoryAdmissionController(new MutableSnapshotProvider(
            new SystemMemorySnapshot(8L * Gibibyte, 3L * Gibibyte, 256L * Mebibyte)));
        var ocrCapable = IndexingMemoryEstimator.Estimate(Job("notes.txt", ".txt"), 0);
        var refresh = IndexingMemoryEstimator.Estimate(
            Job("notes.txt", ".txt", IndexJobKind.EmbeddingRefresh), 0);
        var firstEstimate = ocrCapableFirst ? ocrCapable : refresh;
        var secondEstimate = ocrCapableFirst ? refresh : ocrCapable;

        using var first = await controller.AcquireAsync(firstEstimate,
            TestContext.Current.CancellationToken);
        var secondTask = controller.AcquireAsync(secondEstimate,
            TestContext.Current.CancellationToken).AsTask();
        await Task.Yield();
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(secondEstimate.EstimatedBytes, second.ReservedBytes);
    }

    [Fact]
    public async Task AdmissionUsesReservationsToBoundConcurrentWork()
    {
        var snapshots = new MutableSnapshotProvider(Snapshot(privateBytes: 1L * Gibibyte));
        using var controller = new MemoryAdmissionController(snapshots);
        var estimate = new MemoryWorkEstimate(1500L * Mebibyte, "test");
        using var first = await controller.AcquireAsync(estimate, TestContext.Current.CancellationToken);
        using var second = await controller.AcquireAsync(estimate, TestContext.Current.CancellationToken);

        var thirdTask = controller.AcquireAsync(estimate, TestContext.Current.CancellationToken).AsTask();
        await Task.Yield();
        Assert.False(thirdTask.IsCompleted);

        first.Dispose();
        using var third = await thirdTask.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.False(third.IsExclusive);
    }

    [Fact]
    public async Task SmallerRequestBypassesBlockedLargeRequestAndCancellationStillWorks()
    {
        var snapshots = new MutableSnapshotProvider(Snapshot(privateBytes: 1L * Gibibyte));
        using var controller = new MemoryAdmissionController(snapshots);
        using var active = await controller.AcquireAsync(
            new MemoryWorkEstimate(1536L * Mebibyte, "active"), TestContext.Current.CancellationToken);
        using var headCancellation = new CancellationTokenSource();

        var head = controller.AcquireAsync(new MemoryWorkEstimate(2L * Gibibyte, "head"),
            headCancellation.Token).AsTask();
        var tail = controller.AcquireAsync(new MemoryWorkEstimate(128L * Mebibyte, "tail"),
            TestContext.Current.CancellationToken).AsTask();
        using var admittedTail = await tail.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.False(head.IsCompleted);

        headCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await head);
    }

    [Fact]
    public async Task RepeatedSizeBasedBypassesDrainActiveWorkAndProtectTheLargeWaiter()
    {
        using var controller = new MemoryAdmissionController(new MutableSnapshotProvider(Snapshot()));
        var blocker = await controller.AcquireAsync(
            new MemoryWorkEstimate(1L * Gibibyte, "active"), TestContext.Current.CancellationToken);
        var large = controller.AcquireAsync(
            new MemoryWorkEstimate(4L * Gibibyte, "large"), TestContext.Current.CancellationToken).AsTask();
        await Task.Yield();
        Assert.False(large.IsCompleted);

        for (var index = 0; index < MemoryAdmissionController.MaximumSizeBasedBypasses; index++)
        {
            using var small = await controller.AcquireAsync(
                new MemoryWorkEstimate(128L * Mebibyte, $"small-{index}"),
                TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
        }

        var laterSmall = controller.AcquireAsync(
            new MemoryWorkEstimate(128L * Mebibyte, "later-small"),
            TestContext.Current.CancellationToken).AsTask();
        await Task.Yield();
        Assert.False(large.IsCompleted);
        Assert.False(laterSmall.IsCompleted);

        blocker.Dispose();
        using var admittedLarge = await large.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.False(laterSmall.IsCompleted);

        admittedLarge.Dispose();
        using var admittedSmall = await laterSmall.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OversizeWorkRunsExclusivelyAndBlocksFollowers()
    {
        var snapshots = new MutableSnapshotProvider(new SystemMemorySnapshot(
            32L * Gibibyte, 20L * Gibibyte, 1L * Gibibyte));
        using var controller = new MemoryAdmissionController(snapshots);
        using var oversize = await controller.AcquireAsync(
            new MemoryWorkEstimate(5L * Gibibyte, "oversize"), TestContext.Current.CancellationToken);
        Assert.True(oversize.IsExclusive);

        var followerTask = controller.AcquireAsync(new MemoryWorkEstimate(128L * Mebibyte, "follower"),
            TestContext.Current.CancellationToken).AsTask();
        await Task.Yield();
        Assert.False(followerTask.IsCompleted);

        oversize.Dispose();
        using var follower = await followerTask.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.False(follower.IsExclusive);
    }

    [Fact]
    public async Task SoleWorkBypassesSoftTargetsButKeepsTheHardSafetyReserve()
    {
        var snapshots = new MutableSnapshotProvider(new SystemMemorySnapshot(
            16L * Gibibyte, 2500L * Mebibyte, 5L * Gibibyte));
        using var controller = new MemoryAdmissionController(snapshots);
        using var admitted = await controller.AcquireAsync(
            new MemoryWorkEstimate(1L * Gibibyte, "retained-native"),
            TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.True(admitted.IsExclusive);
        Assert.True(admitted.AdmissionSnapshot.ProcessPrivateBytes > admitted.ProcessSoftLimitBytes);
        Assert.True(admitted.AdmissionSnapshot.AvailablePhysicalBytes - admitted.ReservedBytes >=
                    MemoryAdmissionController.CalculateHardSafetyReserve(16L * Gibibyte));
    }

    [Fact]
    public async Task ExclusiveFallbackBrieflyBatchesIdleRequestsAndChoosesTheSmallest()
    {
        using var controller = new MemoryAdmissionController(new MutableSnapshotProvider(
            new SystemMemorySnapshot(16L * Gibibyte, 2500L * Mebibyte, 5L * Gibibyte)));
        var heavy = controller.AcquireAsync(new MemoryWorkEstimate(1L * Gibibyte, "heavy"),
            TestContext.Current.CancellationToken).AsTask();
        Assert.False(heavy.IsCompleted);
        var small = controller.AcquireAsync(new MemoryWorkEstimate(128L * Mebibyte, "small"),
            TestContext.Current.CancellationToken).AsTask();

        var admittedSmall = await small.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.True(admittedSmall.IsExclusive);
        Assert.False(heavy.IsCompleted);

        admittedSmall.Dispose();
        using var admittedHeavy = await heavy.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.True(admittedHeavy.IsExclusive);
    }

    [Fact]
    public async Task WaitingRequestIsReevaluatedWhenSystemMemoryRecovers()
    {
        var snapshots = new MutableSnapshotProvider(Snapshot(
            availableBytes: 1200L * Mebibyte, privateBytes: 1L * Gibibyte));
        using var controller = new MemoryAdmissionController(snapshots);
        var pending = controller.AcquireAsync(new MemoryWorkEstimate(1L * Gibibyte, "waiting"),
            TestContext.Current.CancellationToken).AsTask();
        await Task.Yield();
        Assert.False(pending.IsCompleted);

        snapshots.Value = Snapshot(availableBytes: 2L * Gibibyte, privateBytes: 1L * Gibibyte);
        using var admitted = await pending.WaitAsync(TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);
        Assert.True(admitted.AdmissionSnapshot.AvailablePhysicalBytes >= 2L * Gibibyte);
    }

    [Fact]
    public async Task CorrelatedWaitPublishesMemoryReasonAndClearsOnCancellation()
    {
        var statuses = new MemoryAdmissionStatusStore();
        var correlationId = Guid.NewGuid();
        using var controller = new MemoryAdmissionController(new MutableSnapshotProvider(Snapshot(
            availableBytes: 1200L * Mebibyte, privateBytes: 1L * Gibibyte)), statuses);
        using var cancellation = new CancellationTokenSource();

        var pending = controller.AcquireAsync(new MemoryWorkEstimate(1L * Gibibyte, "waiting")
        {
            CorrelationId = correlationId
        }, cancellation.Token).AsTask();

        Assert.True(statuses.TryGet(correlationId, out var status));
        Assert.Equal(MemoryAdmissionWaitReason.SystemMemory, status.Reason);
        Assert.Equal(1, status.QueuePosition);
        Assert.Equal(1L * Gibibyte, status.RequestedBytes);
        Assert.Equal(1200L * Mebibyte, status.AvailablePhysicalBytes);
        Assert.Equal(1L * Gibibyte + status.HardSafetyReserveBytes, status.RequiredAvailableBytes);
        Assert.True(status.WaitingSinceUtc <= status.ObservedUtc);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.False(statuses.TryGet(correlationId, out _));
    }

    [Fact]
    public async Task OnlyScheduledHeadReportsMemoryShortfallAndFollowingWaiterReportsQueue()
    {
        var statuses = new MemoryAdmissionStatusStore();
        using var controller = new MemoryAdmissionController(new MutableSnapshotProvider(
            new SystemMemorySnapshot(16L * Gibibyte, 300L * Mebibyte, 1L * Gibibyte)), statuses);
        var largerId = Guid.NewGuid();
        var smallerId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var larger = controller.AcquireAsync(new MemoryWorkEstimate(128L * Mebibyte, "larger")
        {
            CorrelationId = largerId
        }, cancellation.Token).AsTask();
        var smaller = controller.AcquireAsync(new MemoryWorkEstimate(64L * Mebibyte, "smaller")
        {
            CorrelationId = smallerId
        }, cancellation.Token).AsTask();

        Assert.True(statuses.TryGet(smallerId, out var head));
        Assert.True(statuses.TryGet(largerId, out var queued));
        Assert.Equal(1, head.QueuePosition);
        Assert.Equal(MemoryAdmissionWaitReason.SystemMemory, head.Reason);
        Assert.Equal(2, queued.QueuePosition);
        Assert.Equal(MemoryAdmissionWaitReason.QueuedBehindWork, queued.Reason);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await larger);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await smaller);
    }

    [Fact]
    public async Task QueuePositionsReflectSizeAwareOrderWhileExclusiveWorkRuns()
    {
        var statuses = new MemoryAdmissionStatusStore();
        using var controller = new MemoryAdmissionController(new MutableSnapshotProvider(Snapshot()), statuses);
        var blocker = await controller.AcquireAsync(new MemoryWorkEstimate(5L * Gibibyte, "exclusive"),
            TestContext.Current.CancellationToken);
        Assert.True(blocker.IsExclusive);
        var largeId = Guid.NewGuid();
        var smallId = Guid.NewGuid();
        var large = controller.AcquireAsync(new MemoryWorkEstimate(3L * Gibibyte, "large")
        {
            CorrelationId = largeId
        }, TestContext.Current.CancellationToken).AsTask();
        var small = controller.AcquireAsync(new MemoryWorkEstimate(128L * Mebibyte, "small")
        {
            CorrelationId = smallId
        }, TestContext.Current.CancellationToken).AsTask();

        Assert.True(statuses.TryGet(smallId, out var smallStatus));
        Assert.True(statuses.TryGet(largeId, out var largeStatus));
        Assert.Equal(MemoryAdmissionWaitReason.Exclusive, smallStatus.Reason);
        Assert.Equal(1, smallStatus.QueuePosition);
        Assert.Equal(2, largeStatus.QueuePosition);

        blocker.Dispose();
        using var admittedSmall = await small.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        using var admittedLarge = await large.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.False(statuses.TryGet(smallId, out _));
        Assert.False(statuses.TryGet(largeId, out _));
    }

    [Fact]
    public async Task IdleControllerDoesNotKeepSamplingProcessLeases()
    {
        var snapshots = new CountingSnapshotProvider(Snapshot());
        using var controller = new MemoryAdmissionController(snapshots);
        using (await controller.AcquireAsync(new MemoryWorkEstimate(128L * Mebibyte, "sample-once"),
                   TestContext.Current.CancellationToken))
        {
        }
        var captureCount = snapshots.Captures;

        await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);

        Assert.Equal(captureCount, snapshots.Captures);
    }

    [Fact]
    public async Task EstimateLargerThanUsablePhysicalMemoryFailsAndDoesNotBlockSmallerWork()
    {
        var snapshots = new MutableSnapshotProvider(default);
        using var controller = new MemoryAdmissionController(snapshots);
        var impossible = controller.AcquireAsync(new MemoryWorkEstimate(4L * Gibibyte, "impossible"),
            TestContext.Current.CancellationToken).AsTask();
        var smaller = controller.AcquireAsync(new MemoryWorkEstimate(128L * Mebibyte, "smaller"),
            TestContext.Current.CancellationToken).AsTask();
        Assert.False(impossible.IsCompleted);
        Assert.False(smaller.IsCompleted);

        snapshots.Value = new SystemMemorySnapshot(4L * Gibibyte, 4L * Gibibyte, 0);

        var exception = await Assert.ThrowsAsync<ContextMoleException>(async () => await impossible);
        Assert.Equal("memory_estimate_exceeds_system_capacity", exception.Code);
        Assert.False(exception.Retryable);
        using var admitted = await smaller.WaitAsync(TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public void SnapshotAggregatesCurrentAndValidatedLiveLeaseProcessesOnlyOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "ContextMole.MemoryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var start = DateTimeOffset.UtcNow.AddMinutes(-5);
            var reader = new FakeProcessMemoryReader(
                new ProcessMemorySample(101, start, 100 * Mebibyte),
                new ProcessMemorySample(202, start.AddSeconds(1), 250 * Mebibyte),
                new ProcessMemorySample(303, start.AddSeconds(9), 500 * Mebibyte));
            WriteLease(root, "live-a.lease", 202, start.AddSeconds(1));
            WriteLease(root, "live-duplicate.lease", 202, start.AddSeconds(1));
            WriteLease(root, "reused-pid.lease", 303, start.AddSeconds(2));
            WriteLease(root, "dead.lease", 404, start.AddSeconds(3));
            WriteLease(root, "unavailable.lease", 505, start.AddSeconds(4));
            WriteLease(root, "held-unavailable.lease", 606, start.AddSeconds(5));
            reader.UnavailableProcessIds.Add(505);
            reader.UnavailableProcessIds.Add(606);
            var leases = ContextMoleProcessCoordination.GetLeasesDirectory(root);
            using var heldUnavailable = new FileStream(Path.Combine(leases, "held-unavailable.lease"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            File.WriteAllText(Path.Combine(leases, "invalid.lease"), "not json");
            File.WriteAllBytes(Path.Combine(leases, "empty.lease"), []);
            File.WriteAllBytes(Path.Combine(leases, "oversized.lease"), new byte[4097]);
            var heldInvalid = new FileStream(Path.Combine(leases, "held-invalid.lease"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            heldInvalid.WriteByte((byte)'{');
            heldInvalid.Flush(true);

            var aggregator = new ContextMoleProcessMemoryAggregator(root, reader, currentProcessId: 101);
            var provider = new SystemMemorySnapshotProvider(
                new FixedPhysicalMemorySource(16L * Gibibyte, 8L * Gibibyte), aggregator);

            var snapshot = provider.Capture();

            Assert.Equal(16L * Gibibyte, snapshot.TotalPhysicalBytes);
            Assert.Equal(8L * Gibibyte, snapshot.AvailablePhysicalBytes);
            Assert.Equal(350L * Mebibyte, snapshot.ProcessPrivateBytes);
            Assert.Equal(1, reader.ReadCounts[202]);
            Assert.False(File.Exists(Path.Combine(leases, "reused-pid.lease")));
            Assert.False(File.Exists(Path.Combine(leases, "dead.lease")));
            Assert.False(File.Exists(Path.Combine(leases, "unavailable.lease")));
            Assert.False(File.Exists(Path.Combine(leases, "invalid.lease")));
            Assert.False(File.Exists(Path.Combine(leases, "empty.lease")));
            Assert.False(File.Exists(Path.Combine(leases, "oversized.lease")));
            Assert.True(File.Exists(Path.Combine(leases, "live-a.lease")));
            Assert.True(File.Exists(Path.Combine(leases, "held-unavailable.lease")));
            Assert.True(File.Exists(Path.Combine(leases, "held-invalid.lease")));
            heldUnavailable.Dispose();
            heldInvalid.Dispose();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RuntimeProcessReaderUsesStatusResultsForLiveAndMissingProcesses()
    {
        var reader = new RuntimeProcessMemoryReader();

        Assert.Equal(ProcessMemoryReadStatus.Success,
            reader.Read(Environment.ProcessId, out var current));
        Assert.Equal(Environment.ProcessId, current.ProcessId);
        Assert.True(current.PrivateBytes > 0);
        Assert.Equal(ProcessMemoryReadStatus.NotFound,
            reader.Read(int.MaxValue, out _));
    }

    private static IndexJobLease Job(string sourcePath, string extension,
        IndexJobKind kind = IndexJobKind.Index) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), sourcePath, extension, 1, kind, 0);

    private static SystemMemorySnapshot Snapshot(long availableBytes = 16L * Gibibyte,
        long privateBytes = 0) => new(16L * Gibibyte, availableBytes, privateBytes);

    private static void WriteLease(string dataDirectory, string fileName, int processId,
        DateTimeOffset processStartUtc)
    {
        var directory = ContextMoleProcessCoordination.GetLeasesDirectory(dataDirectory);
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new
        {
            processId,
            role = "test",
            processStartUtc,
            acquiredUtc = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.WriteAllText(Path.Combine(directory, fileName), json);
    }

    private sealed class MutableSnapshotProvider(SystemMemorySnapshot value) : ISystemMemorySnapshotProvider
    {
        private readonly object _gate = new();
        private SystemMemorySnapshot _value = value;

        public SystemMemorySnapshot Value
        {
            get { lock (_gate) return _value; }
            set { lock (_gate) _value = value; }
        }

        public SystemMemorySnapshot Capture() => Value;
    }

    private sealed class CountingSnapshotProvider(SystemMemorySnapshot value) : ISystemMemorySnapshotProvider
    {
        private int _captures;
        public int Captures => Volatile.Read(ref _captures);

        public SystemMemorySnapshot Capture()
        {
            Interlocked.Increment(ref _captures);
            return value;
        }
    }

    private sealed class FixedPhysicalMemorySource(long totalBytes, long availableBytes) : IPhysicalMemorySource
    {
        public PhysicalMemorySnapshot Capture() => new(totalBytes, availableBytes);
    }

    private sealed class FakeProcessMemoryReader(params ProcessMemorySample[] samples) : IProcessMemoryReader
    {
        private readonly Dictionary<int, ProcessMemorySample> _samples = samples.ToDictionary(item => item.ProcessId);
        public HashSet<int> UnavailableProcessIds { get; } = [];
        public Dictionary<int, int> ReadCounts { get; } = [];

        public ProcessMemoryReadStatus Read(int processId, out ProcessMemorySample sample)
        {
            ReadCounts[processId] = ReadCounts.GetValueOrDefault(processId) + 1;
            if (UnavailableProcessIds.Contains(processId))
            {
                sample = default;
                return ProcessMemoryReadStatus.Unavailable;
            }
            return _samples.TryGetValue(processId, out sample)
                ? ProcessMemoryReadStatus.Success
                : ProcessMemoryReadStatus.NotFound;
        }
    }
}
