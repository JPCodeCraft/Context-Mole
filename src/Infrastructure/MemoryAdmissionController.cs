using System.Diagnostics;

using ContextMole.Core;

namespace ContextMole.Infrastructure;

public sealed class MemoryAdmissionController : IMemoryAdmissionController, IDisposable
{
    private const long Mebibyte = 1024L * 1024;
    private const long Gibibyte = 1024L * Mebibyte;
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumSizeBasedWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExclusiveFallbackBatchWindow = TimeSpan.FromMilliseconds(200);
    internal const int MaximumSizeBasedBypasses = 4;

    private readonly object _gate = new();
    private readonly ISystemMemorySnapshotProvider _snapshots;
    private readonly IMemoryAdmissionStatusStore _statuses;
    private readonly AsyncLocal<MemoryLease?> _ambientLease = new();
    private readonly LinkedList<Waiter> _upgradeWaiters = [];
    private readonly LinkedList<Waiter> _waiters = [];
    private readonly HashSet<MemoryLease> _activeLeases = [];
    private readonly Timer _timer;
    private readonly Timer _fallbackBatchTimer;
    private long _activeReservations;
    private int _activeLeaseCount;
    private bool _exclusiveLeaseActive;
    private bool _hasLastMemoryObservation;
    private SystemMemorySnapshot _lastMemorySnapshot;
    private long _lastProcessLimit;
    private long _lastSystemReserve;
    private long _lastHardSafetyReserve;
    private bool _disposed;

    public MemoryAdmissionController(ISystemMemorySnapshotProvider snapshots)
        : this(snapshots, new MemoryAdmissionStatusStore())
    {
    }

    public MemoryAdmissionController(
        ISystemMemorySnapshotProvider snapshots,
        IMemoryAdmissionStatusStore statuses)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _statuses = statuses ?? throw new ArgumentNullException(nameof(statuses));
        _timer = new Timer(static state => ((MemoryAdmissionController)state!).Recheck(), this,
            RecheckInterval, RecheckInterval);
        _fallbackBatchTimer = new Timer(static state => ((MemoryAdmissionController)state!).Recheck(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public static long CalculateProcessSoftLimit(long totalPhysicalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalPhysicalBytes);
        return Math.Clamp(totalPhysicalBytes / 4, 1536L * Mebibyte, 4L * Gibibyte);
    }

    public static long CalculateSystemReserve(long totalPhysicalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalPhysicalBytes);
        return Math.Max(SaturatingMultiply(totalPhysicalBytes, 15) / 100, 2L * Gibibyte);
    }

    public static long CalculateHardSafetyReserve(long totalPhysicalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalPhysicalBytes);
        return Math.Clamp(SaturatingMultiply(totalPhysicalBytes, 2) / 100,
            256L * Mebibyte, 512L * Mebibyte);
    }

    public ValueTask<IMemoryLease> AcquireAsync(MemoryWorkEstimate estimate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(estimate.EstimatedBytes);
        if (estimate.MaximumReservationBytes < estimate.EstimatedBytes)
            throw new ArgumentOutOfRangeException(nameof(estimate),
                "The maximum reservation cannot be smaller than the initial reservation.");
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var ambient = _ambientLease.Value;
            if (ambient is { State: not MemoryLeaseState.Disposed } &&
                ReferenceEquals(ambient.Owner, this))
                return AcquireUpgradeLocked(ambient, estimate, cancellationToken);

            var waiter = new Waiter(estimate, cancellationToken);
            waiter.Node = _waiters.AddLast(waiter);
            RegisterCancellation(waiter);
            PumpLocked();
            return new ValueTask<IMemoryLease>(AwaitLeaseAsync(waiter));
        }
    }

    public void Dispose()
    {
        Waiter[] waiters;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            waiters = _upgradeWaiters.Concat(_waiters).ToArray();
            _upgradeWaiters.Clear();
            _waiters.Clear();
            foreach (var waiter in waiters)
            {
                waiter.Node = null;
                ClearStatus(waiter);
                if (waiter.Parent is { State: MemoryLeaseState.UpgradePending } parent)
                {
                    parent.PendingUpgrade = null;
                    parent.State = MemoryLeaseState.Held;
                }
            }
        }

        _timer.Dispose();
        _fallbackBatchTimer.Dispose();
        var exception = new ObjectDisposedException(nameof(MemoryAdmissionController));
        foreach (var waiter in waiters) waiter.Completion.TrySetException(exception);
    }

    private ValueTask<IMemoryLease> AcquireUpgradeLocked(MemoryLease parent,
        MemoryWorkEstimate estimate, CancellationToken cancellationToken)
    {
        if (estimate.CorrelationId is null && parent.CorrelationId is { } parentCorrelation)
            estimate = estimate with { CorrelationId = parentCorrelation };
        var requestedTarget = estimate.EstimatedBytes;
        if (requestedTarget <= parent.CurrentReservedBytes)
        {
            return ValueTask.FromResult<IMemoryLease>(new NestedMemoryLease(this, parent, 0,
                controlsUpgrade: false, parent.IsExclusive, parent.AdmissionSnapshot,
                parent.ProcessSoftLimitBytes, parent.SystemReserveBytes));
        }

        if (parent.State != MemoryLeaseState.Held)
        {
            throw new ContextMoleException(
                "nested_memory_upgrade_already_active",
                "A larger nested memory reservation cannot start while another upgrade is active.",
                false);
        }

        if (requestedTarget > parent.MaximumReservationBytes)
        {
            throw new ContextMoleException(
                "nested_memory_estimate_exceeded",
                $"The operation requested a nested memory reservation of {requestedTarget} bytes, " +
                $"above its declared maximum of {parent.MaximumReservationBytes} bytes.",
                false);
        }

        var waiter = new Waiter(estimate, cancellationToken, parent);
        waiter.Node = _upgradeWaiters.AddLast(waiter);
        parent.PendingUpgrade = waiter;
        parent.State = MemoryLeaseState.UpgradePending;
        RegisterCancellation(waiter);
        PumpLocked();
        return new ValueTask<IMemoryLease>(AwaitLeaseAsync(waiter));
    }

    private static async Task<IMemoryLease> AwaitLeaseAsync(Waiter waiter)
    {
        try
        {
            return await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            await waiter.CancellationRegistration.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void RegisterCancellation(Waiter waiter)
    {
        if (!waiter.CancellationToken.CanBeCanceled) return;
        waiter.CancellationRegistration = waiter.CancellationToken.Register(static state =>
        {
            var (owner, pending) = ((MemoryAdmissionController, Waiter))state!;
            owner.Cancel(pending);
        }, (this, waiter));
    }

    private void Recheck()
    {
        lock (_gate) PumpLocked();
    }

    private void Cancel(Waiter waiter)
    {
        lock (_gate)
        {
            if (waiter.Node is null) return;
            if (waiter.IsUpgrade)
            {
                _upgradeWaiters.Remove(waiter.Node);
                if (waiter.Parent is { State: MemoryLeaseState.UpgradePending } parent &&
                    ReferenceEquals(parent.PendingUpgrade, waiter))
                {
                    parent.PendingUpgrade = null;
                    parent.State = MemoryLeaseState.Held;
                }
            }
            else
            {
                _waiters.Remove(waiter.Node);
            }

            waiter.Node = null;
            ClearStatus(waiter);
            waiter.Completion.TrySetCanceled(waiter.CancellationToken);
            PumpLocked();
        }
    }

    private void PumpLocked()
    {
        if (_disposed || (_upgradeWaiters.Count == 0 && _waiters.Count == 0)) return;
        if (_exclusiveLeaseActive &&
            _upgradeWaiters.First?.Value.Parent is not { BaseIsExclusive: true })
        {
            PublishLastObservationLocked(MemoryAdmissionWaitReason.Exclusive);
            return;
        }

        SystemMemorySnapshot snapshot;
        try
        {
            snapshot = _snapshots.Capture();
        }
        catch
        {
            PublishLastObservationLocked(MemoryAdmissionWaitReason.SystemMemory);
            return;
        }

        if (snapshot.TotalPhysicalBytes <= 0 || snapshot.AvailablePhysicalBytes < 0 ||
            snapshot.ProcessPrivateBytes < 0)
        {
            PublishLastObservationLocked(MemoryAdmissionWaitReason.SystemMemory);
            return;
        }

        var processLimit = CalculateProcessSoftLimit(snapshot.TotalPhysicalBytes);
        var systemReserve = CalculateSystemReserve(snapshot.TotalPhysicalBytes);
        var hardSafetyReserve = CalculateHardSafetyReserve(snapshot.TotalPhysicalBytes);
        var maximumWorkReservation = SaturatingSubtract(snapshot.TotalPhysicalBytes, hardSafetyReserve);
        _hasLastMemoryObservation = true;
        _lastMemorySnapshot = snapshot;
        _lastProcessLimit = processLimit;
        _lastSystemReserve = systemReserve;
        _lastHardSafetyReserve = hardSafetyReserve;

        if (_upgradeWaiters.Count != 0 &&
            _activeLeases.Any(lease => lease.State == MemoryLeaseState.Upgraded))
        {
            PublishPendingStatusesLocked(snapshot, processLimit, systemReserve, hardSafetyReserve,
                MemoryAdmissionWaitReason.NestedSerialization);
            return;
        }

        while (_upgradeWaiters.First is { } upgradeNode)
        {
            var waiter = upgradeNode.Value;
            var parent = waiter.Parent!;
            var requestedTarget = waiter.Estimate.EstimatedBytes;
            if (requestedTarget > maximumWorkReservation)
            {
                RemoveFailedUpgradeLocked(waiter, CapacityException(waiter, requestedTarget,
                    maximumWorkReservation));
                continue;
            }

            if (parent.State != MemoryLeaseState.UpgradePending ||
                !ReferenceEquals(parent.PendingUpgrade, waiter))
            {
                RemoveFailedUpgradeLocked(waiter, new ObjectDisposedException(nameof(IMemoryLease)));
                continue;
            }

            var delta = Math.Max(0, requestedTarget - parent.CurrentReservedBytes);
            var projectedReservations = SaturatingAdd(_activeReservations, delta);
            var processProjection = SaturatingAdd(snapshot.ProcessPrivateBytes, projectedReservations);
            var availableProjection = SaturatingSubtract(snapshot.AvailablePhysicalBytes,
                projectedReservations);
            var hardAvailableProjection = availableProjection;
            var normal = processProjection <= processLimit && availableProjection >= systemReserve;
            var exclusive = !normal && hardAvailableProjection >= hardSafetyReserve;
            if (!normal && !exclusive)
            {
                PublishPendingStatusesLocked(snapshot, processLimit, systemReserve, hardSafetyReserve);
                return;
            }

            _upgradeWaiters.RemoveFirst();
            waiter.Node = null;
            ClearStatus(waiter);
            parent.PendingUpgrade = null;
            _activeReservations = projectedReservations;
            parent.CurrentReservedBytes = requestedTarget;
            parent.UpgradeIsExclusive = exclusive;
            parent.State = MemoryLeaseState.Upgraded;
            _exclusiveLeaseActive = parent.IsExclusive;
            var lease = new NestedMemoryLease(this, parent, delta, controlsUpgrade: true,
                parent.IsExclusive, snapshot, processLimit, systemReserve);
            parent.ActiveUpgrade = lease;
            waiter.Completion.TrySetResult(lease);
            if (_upgradeWaiters.Count != 0 || _waiters.Count != 0)
                PublishPendingStatusesLocked(snapshot, processLimit, systemReserve, hardSafetyReserve);
            return;
        }

        if (_exclusiveLeaseActive)
        {
            PublishPendingStatusesLocked(snapshot, processLimit, systemReserve, hardSafetyReserve);
            return;
        }
        RemoveImpossibleWaitersLocked(maximumWorkReservation);
        while (_waiters.Count != 0)
        {
            var protectedNode = FindFairnessProtectedWaiter();
            LinkedListNode<Waiter>? selectedNode = null;
            var admission = RootAdmission.None;
            if (protectedNode is not null)
            {
                admission = EvaluateRootAdmission(protectedNode.Value, snapshot, processLimit,
                    systemReserve, hardSafetyReserve);
                if (admission != RootAdmission.None)
                {
                    selectedNode = protectedNode;
                }
                else if (_activeLeaseCount != 0)
                {
                    // Stop admitting small work until current leases drain. Once the aged waiter
                    // becomes the sole operation it can use the hard-safe exclusive fallback.
                    PublishPendingStatusesLocked(snapshot, processLimit, systemReserve, hardSafetyReserve);
                    return;
                }
            }

            if (selectedNode is null && !TryFindSmallestAdmissibleWaiter(snapshot, processLimit,
                    systemReserve, hardSafetyReserve, out selectedNode, out admission))
            {
                PublishPendingStatusesLocked(snapshot, processLimit, systemReserve, hardSafetyReserve);
                return;
            }

            if (admission == RootAdmission.Exclusive && ShouldBatchExclusiveFallback(selectedNode!))
            {
                PublishPendingStatusesLocked(snapshot, processLimit, systemReserve, hardSafetyReserve);
                return;
            }

            MarkEarlierWaitersBypassed(selectedNode!);
            var waiter = selectedNode!.Value;
            var requested = waiter.Estimate.EstimatedBytes;
            var projectedReservations = SaturatingAdd(_activeReservations, requested);

            _waiters.Remove(selectedNode);
            waiter.Node = null;
            ClearStatus(waiter);
            _activeReservations = projectedReservations;
            _activeLeaseCount++;
            var exclusive = admission == RootAdmission.Exclusive;
            var lease = new MemoryLease(this, requested, waiter.Estimate.MaximumReservationBytes,
                exclusive, snapshot, processLimit, systemReserve, waiter.Estimate.CorrelationId);
            _activeLeases.Add(lease);

            _exclusiveLeaseActive = exclusive;
            waiter.Completion.TrySetResult(lease);
            if (exclusive)
            {
                PublishPendingStatusesLocked(snapshot, processLimit, systemReserve, hardSafetyReserve);
                return;
            }
        }
    }

    private void RemoveImpossibleWaitersLocked(long maximumWorkReservation)
    {
        var node = _waiters.First;
        while (node is not null)
        {
            var next = node.Next;
            var waiter = node.Value;
            if (waiter.Estimate.EstimatedBytes > maximumWorkReservation)
            {
                _waiters.Remove(node);
                waiter.Node = null;
                ClearStatus(waiter);
                waiter.Completion.TrySetException(CapacityException(waiter,
                    waiter.Estimate.EstimatedBytes, maximumWorkReservation));
            }
            node = next;
        }
    }

    private LinkedListNode<Waiter>? FindFairnessProtectedWaiter()
    {
        var now = Stopwatch.GetTimestamp();
        for (var node = _waiters.First; node is not null; node = node.Next)
        {
            if (node.Value.SizeBasedBypasses >= MaximumSizeBasedBypasses ||
                Stopwatch.GetElapsedTime(node.Value.EnqueuedTimestamp, now) >= MaximumSizeBasedWait)
                return node;
        }
        return null;
    }

    private bool ShouldBatchExclusiveFallback(LinkedListNode<Waiter> selectedNode)
    {
        var now = Stopwatch.GetTimestamp();
        if (selectedNode.Value.SizeBasedBypasses >= MaximumSizeBasedBypasses ||
            Stopwatch.GetElapsedTime(selectedNode.Value.EnqueuedTimestamp, now) >= MaximumSizeBasedWait)
            return false;

        var oldestTimestamp = _waiters.Min(waiter => waiter.EnqueuedTimestamp);
        var elapsed = Stopwatch.GetElapsedTime(oldestTimestamp, now);
        if (elapsed >= ExclusiveFallbackBatchWindow) return false;
        var remaining = ExclusiveFallbackBatchWindow - elapsed;
        if (remaining < TimeSpan.FromMilliseconds(1)) remaining = TimeSpan.FromMilliseconds(1);
        _fallbackBatchTimer.Change(remaining, Timeout.InfiniteTimeSpan);
        return true;
    }

    private bool TryFindSmallestAdmissibleWaiter(
        SystemMemorySnapshot snapshot,
        long processLimit,
        long systemReserve,
        long hardSafetyReserve,
        out LinkedListNode<Waiter>? selectedNode,
        out RootAdmission admission)
    {
        selectedNode = null;
        admission = RootAdmission.None;
        for (var node = _waiters.First; node is not null; node = node.Next)
        {
            var candidateAdmission = EvaluateRootAdmission(node.Value, snapshot, processLimit,
                systemReserve, hardSafetyReserve);
            if (candidateAdmission == RootAdmission.None) continue;
            if (selectedNode is not null &&
                selectedNode.Value.Estimate.EstimatedBytes <= node.Value.Estimate.EstimatedBytes) continue;
            selectedNode = node;
            admission = candidateAdmission;
        }
        return selectedNode is not null;
    }

    private RootAdmission EvaluateRootAdmission(
        Waiter waiter,
        SystemMemorySnapshot snapshot,
        long processLimit,
        long systemReserve,
        long hardSafetyReserve)
    {
        var projectedReservations = SaturatingAdd(_activeReservations,
            waiter.Estimate.EstimatedBytes);
        // Preserve enough physical headroom for any admitted parser to reach its declared nested
        // maximum. This is a one-operation claim, not live process memory and not a per-parser
        // reservation: OCR upgrades are serialized, so one parser can upgrade, finish, and release
        // the headroom before the next one upgrades. Root work remains governed by its base
        // reservations and the process soft target.
        var protectedReservations = SaturatingAdd(projectedReservations,
            MaximumNestedHeadroom(waiter.Estimate));
        var protectedAvailableProjection = SaturatingSubtract(snapshot.AvailablePhysicalBytes,
            protectedReservations);
        var baseProcessProjection = SaturatingAdd(snapshot.ProcessPrivateBytes, projectedReservations);
        var baseAvailableProjection = SaturatingSubtract(snapshot.AvailablePhysicalBytes,
            projectedReservations);
        if (baseProcessProjection <= processLimit && baseAvailableProjection >= systemReserve)
        {
            var hasActiveNestedClaim = _activeLeases.Any(lease =>
                lease.State != MemoryLeaseState.Disposed &&
                lease.MaximumReservationBytes > lease.BaseReservedBytes);
            var sharedNestedHeadroomRequired = waiter.Estimate.MayRequestNestedUpgrade ||
                                               hasActiveNestedClaim;
            if (_activeLeaseCount == 0 || !sharedNestedHeadroomRequired ||
                protectedAvailableProjection >= hardSafetyReserve)
                return RootAdmission.Normal;
        }

        // Soft targets should bound concurrency, not halt indexing completely. When no other
        // indexing lease is active, one operation may run exclusively as long as it leaves the
        // hard OS-safety floor intact.
        return _activeLeaseCount == 0 && baseAvailableProjection >= hardSafetyReserve
            ? RootAdmission.Exclusive
            : RootAdmission.None;
    }

    private long MaximumNestedHeadroom(MemoryWorkEstimate candidate)
    {
        var headroom = Math.Max(0, candidate.MaximumReservationBytes - candidate.EstimatedBytes);
        foreach (var lease in _activeLeases)
        {
            if (lease.State == MemoryLeaseState.Disposed) continue;
            headroom = Math.Max(headroom,
                Math.Max(0, lease.MaximumReservationBytes - lease.CurrentReservedBytes));
        }
        return headroom;
    }

    private static void MarkEarlierWaitersBypassed(LinkedListNode<Waiter> selectedNode)
    {
        for (var node = selectedNode.List?.First; node is not null && !ReferenceEquals(node, selectedNode);
             node = node.Next)
        {
            if (node.Value.SizeBasedBypasses < MaximumSizeBasedBypasses)
                node.Value.SizeBasedBypasses++;
        }
    }

    private void PublishLastObservationLocked(MemoryAdmissionWaitReason reason)
    {
        if (_hasLastMemoryObservation)
        {
            PublishPendingStatusesLocked(_lastMemorySnapshot, _lastProcessLimit, _lastSystemReserve,
                _lastHardSafetyReserve, reason);
            return;
        }

        PublishPendingStatusesLocked(default, 0, 0, 0, reason);
    }

    private void PublishPendingStatusesLocked(
        SystemMemorySnapshot snapshot,
        long processLimit,
        long systemReserve,
        long hardSafetyReserve,
        MemoryAdmissionWaitReason? reasonOverride = null)
    {
        var position = 1;
        foreach (var waiter in _upgradeWaiters)
            PublishWaiter(waiter, position++, snapshot, processLimit, systemReserve,
                hardSafetyReserve, reasonOverride);

        var now = Stopwatch.GetTimestamp();
        var ordered = _waiters
            .Select((waiter, index) => new
            {
                Waiter = waiter,
                Index = index,
                Protected = waiter.SizeBasedBypasses >= MaximumSizeBasedBypasses ||
                            Stopwatch.GetElapsedTime(waiter.EnqueuedTimestamp, now) >= MaximumSizeBasedWait
            })
            .OrderBy(item => item.Protected ? 0 : 1)
            .ThenBy(item => item.Protected ? item.Index : item.Waiter.Estimate.EstimatedBytes)
            .ThenBy(item => item.Index);
        foreach (var item in ordered)
            PublishWaiter(item.Waiter, position++, snapshot, processLimit, systemReserve,
                hardSafetyReserve, reasonOverride);
    }

    private void PublishWaiter(
        Waiter waiter,
        int queuePosition,
        SystemMemorySnapshot snapshot,
        long processLimit,
        long systemReserve,
        long hardSafetyReserve,
        MemoryAdmissionWaitReason? reasonOverride)
    {
        if (waiter.Estimate.CorrelationId is not { } correlationId) return;

        var requestedReservation = waiter.IsUpgrade
            ? Math.Max(0, waiter.Estimate.EstimatedBytes - waiter.Parent!.CurrentReservedBytes)
            : waiter.Estimate.EstimatedBytes;
        var projectedReservations = SaturatingAdd(_activeReservations, requestedReservation);
        var protectedReservations = waiter.IsUpgrade
            ? projectedReservations
            : SaturatingAdd(projectedReservations, MaximumNestedHeadroom(waiter.Estimate));
        var processProjection = SaturatingAdd(snapshot.ProcessPrivateBytes, projectedReservations);
        var availableProjection = SaturatingSubtract(snapshot.AvailablePhysicalBytes,
            projectedReservations);
        var hardAvailableProjection = SaturatingSubtract(snapshot.AvailablePhysicalBytes,
            protectedReservations);
        var reason = reasonOverride ?? DetermineWaitReason(waiter, processProjection,
            availableProjection, hardAvailableProjection, processLimit, systemReserve,
            hardSafetyReserve);
        if (queuePosition > 1 && reason != MemoryAdmissionWaitReason.NestedSerialization)
            reason = MemoryAdmissionWaitReason.QueuedBehindWork;
        var canUseHardFallback = waiter.IsUpgrade || _activeLeaseCount == 0;
        var requiredReserve = canUseHardFallback ? hardSafetyReserve : systemReserve;
        var requiredReservation = protectedReservations;
        _statuses.Publish(new MemoryAdmissionWaitSnapshot(
            correlationId,
            reason,
            queuePosition,
            waiter.Estimate.EstimatedBytes,
            snapshot.AvailablePhysicalBytes,
            SaturatingAdd(requiredReservation, requiredReserve),
            requiredReserve,
            systemReserve,
            hardSafetyReserve,
            snapshot.ProcessPrivateBytes,
            processLimit,
            waiter.EnqueuedUtc,
            DateTimeOffset.UtcNow));
    }

    private MemoryAdmissionWaitReason DetermineWaitReason(
        Waiter waiter,
        long processProjection,
        long availableProjection,
        long hardAvailableProjection,
        long processLimit,
        long systemReserve,
        long hardSafetyReserve)
    {
        if (_exclusiveLeaseActive && !(waiter.IsUpgrade && waiter.Parent!.BaseIsExclusive))
            return MemoryAdmissionWaitReason.Exclusive;
        if (hardAvailableProjection < hardSafetyReserve)
            return MemoryAdmissionWaitReason.SystemMemory;
        if (processProjection > processLimit)
            return MemoryAdmissionWaitReason.ProcessSoftLimit;
        if (availableProjection < systemReserve)
            return MemoryAdmissionWaitReason.SystemMemory;
        return MemoryAdmissionWaitReason.QueuedBehindWork;
    }

    private void ClearStatus(Waiter waiter)
    {
        if (waiter.Estimate.CorrelationId is { } correlationId) _statuses.Clear(correlationId);
    }

    private static ContextMoleException CapacityException(Waiter waiter, long requested,
        long maximumWorkReservation) => new(
        "memory_estimate_exceeds_system_capacity",
        $"The estimated {waiter.Estimate.Workload} memory requirement ({requested} bytes) " +
        $"exceeds this system's hard-safe physical-memory capacity ({maximumWorkReservation} bytes).",
        false);

    private void RemoveFailedUpgradeLocked(Waiter waiter, Exception exception)
    {
        if (waiter.Node is not null) _upgradeWaiters.Remove(waiter.Node);
        waiter.Node = null;
        ClearStatus(waiter);
        if (waiter.Parent is { } parent && ReferenceEquals(parent.PendingUpgrade, waiter))
        {
            parent.PendingUpgrade = null;
            if (parent.State == MemoryLeaseState.UpgradePending) parent.State = MemoryLeaseState.Held;
        }
        waiter.Completion.TrySetException(exception);
    }

    private IDisposable Activate(MemoryLease lease)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(lease.Owner, this) || lease.State == MemoryLeaseState.Disposed)
                throw new ObjectDisposedException(nameof(IMemoryLease));
        }

        var previous = _ambientLease.Value;
        _ambientLease.Value = lease;
        return new AmbientScope(this, previous);
    }

    private void Release(MemoryLease lease)
    {
        lock (_gate)
        {
            if (lease.State == MemoryLeaseState.Disposed) return;
            if (lease.PendingUpgrade is { Node: not null } pending)
            {
                _upgradeWaiters.Remove(pending.Node);
                pending.Node = null;
                ClearStatus(pending);
                pending.Completion.TrySetException(new ObjectDisposedException(nameof(IMemoryLease)));
            }

            if (lease.ActiveUpgrade is { } activeUpgrade) activeUpgrade.IsDisposed = true;
            _activeReservations = Math.Max(0, _activeReservations - lease.CurrentReservedBytes);
            _activeLeaseCount = Math.Max(0, _activeLeaseCount - 1);
            _activeLeases.Remove(lease);
            if (lease.IsExclusive) _exclusiveLeaseActive = false;
            lease.PendingUpgrade = null;
            lease.ActiveUpgrade = null;
            lease.CurrentReservedBytes = 0;
            lease.State = MemoryLeaseState.Disposed;
            PumpLocked();
        }
    }

    private void Release(NestedMemoryLease lease)
    {
        lock (_gate)
        {
            if (lease.IsDisposed) return;
            lease.IsDisposed = true;
            if (!lease.ControlsUpgrade || lease.Parent.State != MemoryLeaseState.Upgraded ||
                !ReferenceEquals(lease.Parent.ActiveUpgrade, lease)) return;

            _activeReservations = Math.Max(0, _activeReservations - lease.ReservedBytes);
            lease.Parent.CurrentReservedBytes = lease.Parent.BaseReservedBytes;
            lease.Parent.ActiveUpgrade = null;
            lease.Parent.UpgradeIsExclusive = false;
            lease.Parent.State = MemoryLeaseState.Held;
            _exclusiveLeaseActive = lease.Parent.BaseIsExclusive;
            PumpLocked();
        }
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long SaturatingSubtract(long left, long right) => left <= right ? 0 : left - right;

    private static long SaturatingMultiply(long value, long multiplier) =>
        value > long.MaxValue / multiplier ? long.MaxValue : value * multiplier;

    private sealed class Waiter(
        MemoryWorkEstimate estimate,
        CancellationToken cancellationToken,
        MemoryLease? parent = null)
    {
        public MemoryWorkEstimate Estimate { get; } = estimate;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public MemoryLease? Parent { get; } = parent;
        public bool IsUpgrade => Parent is not null;
        public long EnqueuedTimestamp { get; } = Stopwatch.GetTimestamp();
        public DateTimeOffset EnqueuedUtc { get; } = DateTimeOffset.UtcNow;
        public int SizeBasedBypasses { get; set; }
        public TaskCompletionSource<IMemoryLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }

    private sealed class MemoryLease(
        MemoryAdmissionController owner,
        long reservedBytes,
        long maximumReservationBytes,
        bool isExclusive,
        SystemMemorySnapshot admissionSnapshot,
        long processSoftLimitBytes,
        long systemReserveBytes,
        Guid? correlationId) : IMemoryLease
    {
        public MemoryAdmissionController Owner { get; } = owner;
        public long BaseReservedBytes { get; } = reservedBytes;
        public long CurrentReservedBytes { get; set; } = reservedBytes;
        public long ReservedBytes => CurrentReservedBytes;
        public long MaximumReservationBytes { get; } = maximumReservationBytes;
        public bool BaseIsExclusive { get; } = isExclusive;
        public bool UpgradeIsExclusive { get; set; }
        public bool IsExclusive => BaseIsExclusive || UpgradeIsExclusive;
        public SystemMemorySnapshot AdmissionSnapshot { get; } = admissionSnapshot;
        public long ProcessSoftLimitBytes { get; } = processSoftLimitBytes;
        public long SystemReserveBytes { get; } = systemReserveBytes;
        public Guid? CorrelationId { get; } = correlationId;
        public MemoryLeaseState State { get; set; } = MemoryLeaseState.Held;
        public Waiter? PendingUpgrade { get; set; }
        public NestedMemoryLease? ActiveUpgrade { get; set; }
        public IDisposable Activate() => Owner.Activate(this);
        public void Dispose() => Owner.Release(this);
    }

    private sealed class NestedMemoryLease(
        MemoryAdmissionController owner,
        MemoryLease parent,
        long reservedBytes,
        bool controlsUpgrade,
        bool isExclusive,
        SystemMemorySnapshot admissionSnapshot,
        long processSoftLimitBytes,
        long systemReserveBytes) : IMemoryLease
    {
        public MemoryLease Parent { get; } = parent;
        public long ReservedBytes { get; } = reservedBytes;
        public bool ControlsUpgrade { get; } = controlsUpgrade;
        public bool IsExclusive { get; } = isExclusive;
        public SystemMemorySnapshot AdmissionSnapshot { get; } = admissionSnapshot;
        public long ProcessSoftLimitBytes { get; } = processSoftLimitBytes;
        public long SystemReserveBytes { get; } = systemReserveBytes;
        public bool IsDisposed { get; set; }
        public IDisposable Activate() => owner.Activate(Parent);
        public void Dispose() => owner.Release(this);
    }

    private sealed class AmbientScope(MemoryAdmissionController owner, MemoryLease? previous) : IDisposable
    {
        private MemoryAdmissionController? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null) current._ambientLease.Value = previous;
        }
    }

    private enum MemoryLeaseState
    {
        Held,
        UpgradePending,
        Upgraded,
        Disposed
    }

    private enum RootAdmission
    {
        None,
        Normal,
        Exclusive
    }
}
