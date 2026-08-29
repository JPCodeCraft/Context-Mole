using System.Text.Json;

using ContextMole.Core;
using ContextMole.Infrastructure;

namespace ContextMole.Tests;

public sealed class SystemMemoryTests
{
    private const long Mebibyte = 1024L * 1024;
    private const long Gibibyte = 1024L * Mebibyte;

    [Fact]
    public void CleanupThresholdsScaleWithPhysicalMemory()
    {
        Assert.Equal(1536L * Mebibyte,
            MemoryPressurePolicy.CalculateProcessCleanupThreshold(4L * Gibibyte));
        Assert.Equal(2L * Gibibyte,
            MemoryPressurePolicy.CalculateProcessCleanupThreshold(8L * Gibibyte));
        Assert.Equal(4L * Gibibyte,
            MemoryPressurePolicy.CalculateProcessCleanupThreshold(16L * Gibibyte));
        Assert.Equal(4L * Gibibyte,
            MemoryPressurePolicy.CalculateProcessCleanupThreshold(64L * Gibibyte));

        Assert.Equal(2L * Gibibyte, MemoryPressurePolicy.CalculateSystemReserve(8L * Gibibyte));
        Assert.Equal(16L * Gibibyte * 15 / 100,
            MemoryPressurePolicy.CalculateSystemReserve(16L * Gibibyte));
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

    private sealed class FixedPhysicalMemorySource(long totalBytes, long availableBytes) : IPhysicalMemorySource
    {
        public PhysicalMemorySnapshot Capture() => new(totalBytes, availableBytes);
    }

    private sealed class FakeProcessMemoryReader(params ProcessMemorySample[] samples) : IProcessMemoryReader
    {
        private readonly Dictionary<int, ProcessMemorySample> _samples =
            samples.ToDictionary(item => item.ProcessId);
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
