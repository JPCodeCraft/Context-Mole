using System.Collections.Concurrent;

namespace ContextMole.Core;

public readonly record struct SystemMemorySnapshot(
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    long ProcessPrivateBytes);

public sealed record MemoryWorkEstimate(long EstimatedBytes, string Workload)
{
    public bool MayRequestNestedUpgrade { get; init; }
    public Guid? CorrelationId { get; init; }
}

public enum MemoryAdmissionWaitReason
{
    QueuedBehindWork,
    SystemMemory,
    ProcessSoftLimit,
    NestedSerialization,
    Exclusive
}

public sealed record MemoryAdmissionWaitSnapshot(
    Guid CorrelationId,
    MemoryAdmissionWaitReason Reason,
    int QueuePosition,
    long RequestedBytes,
    long AvailablePhysicalBytes,
    long RequiredAvailableBytes,
    long RequiredReserveBytes,
    long SystemReserveBytes,
    long HardSafetyReserveBytes,
    long ProcessPrivateBytes,
    long ProcessSoftLimitBytes,
    DateTimeOffset WaitingSinceUtc,
    DateTimeOffset ObservedUtc);

public interface IMemoryAdmissionStatusStore
{
    void Publish(MemoryAdmissionWaitSnapshot snapshot);
    void Clear(Guid correlationId);
    bool TryGet(Guid correlationId, out MemoryAdmissionWaitSnapshot snapshot);
}

public sealed class MemoryAdmissionStatusStore : IMemoryAdmissionStatusStore
{
    private readonly ConcurrentDictionary<Guid, MemoryAdmissionWaitSnapshot> _snapshots = new();

    public void Publish(MemoryAdmissionWaitSnapshot snapshot) =>
        _snapshots[snapshot.CorrelationId] = snapshot;

    public void Clear(Guid correlationId) => _snapshots.TryRemove(correlationId, out _);

    public bool TryGet(Guid correlationId, out MemoryAdmissionWaitSnapshot snapshot) =>
        _snapshots.TryGetValue(correlationId, out snapshot!);
}

public interface ISystemMemorySnapshotProvider
{
    SystemMemorySnapshot Capture();
}

public interface IMemoryLease : IDisposable
{
    long ReservedBytes { get; }
    bool IsExclusive { get; }
    SystemMemorySnapshot AdmissionSnapshot { get; }
    long ProcessSoftLimitBytes { get; }
    long SystemReserveBytes { get; }
    IDisposable Activate() => NoopMemoryLeaseActivation.Instance;
}

public interface IMemoryAdmissionController
{
    ValueTask<IMemoryLease> AcquireAsync(MemoryWorkEstimate estimate,
        CancellationToken cancellationToken = default);
}

internal sealed class NoopMemoryLeaseActivation : IDisposable
{
    public static NoopMemoryLeaseActivation Instance { get; } = new();
    public void Dispose()
    {
    }
}
