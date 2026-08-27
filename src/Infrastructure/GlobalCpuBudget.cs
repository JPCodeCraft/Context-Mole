using MCPIndexSearch.Core;

namespace MCPIndexSearch.Infrastructure;

public sealed class GlobalCpuBudget : IGlobalCpuBudget, IDisposable
{
    private readonly object _gate = new();
    private readonly ICpuUsageSettings _settings;
    private readonly AsyncLocal<WorkerLease?> _ambientWorker = new();
    private readonly LinkedList<Waiter> _workerWaiters = [];
    private readonly LinkedList<Waiter> _fullCapacityWaiters = [];
    private int _activeCapacity;
    private bool _disposed;

    public GlobalCpuBudget(ICpuUsageSettings settings)
    {
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
    }

    public int MaximumWorkerCount => _settings.MaximumThreadLimit;

    public async ValueTask<ICpuWorkerLease> AcquireWorkerAsync(CancellationToken cancellationToken)
    {
        await AcquireWorkerGrantAsync(cancellationToken, resumePriority: false).ConfigureAwait(false);
        return new WorkerLease(this);
    }

    public async ValueTask<ICpuFullCapacityLease> AcquireFullCapacityAsync(CancellationToken cancellationToken)
    {
        var worker = _ambientWorker.Value;
        var request = AcquireFullCapacityGrantAsync(worker, cancellationToken);
        try
        {
            var grant = await request.Grant.ConfigureAwait(false);
            return new FullCapacityLease(this, grant.Capacity, request.SuspendedWorker ? worker : null);
        }
        catch
        {
            if (request.SuspendedWorker)
                await ResumeWorkerAsync(worker!).ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
        Waiter[] waiting;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            waiting = _workerWaiters.Concat(_fullCapacityWaiters).ToArray();
            _workerWaiters.Clear();
            _fullCapacityWaiters.Clear();
            foreach (var waiter in waiting) waiter.Node = null;
        }

        _settings.Changed -= OnSettingsChanged;
        var exception = new ObjectDisposedException(nameof(GlobalCpuBudget));
        foreach (var waiter in waiting) waiter.Completion.TrySetException(exception);
    }

    private Task<CapacityGrant> AcquireWorkerGrantAsync(CancellationToken cancellationToken, bool resumePriority)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var limit = _settings.ThreadLimit;
            if (_activeCapacity < limit && (resumePriority || _fullCapacityWaiters.Count == 0))
            {
                _activeCapacity++;
                return Task.FromResult(new CapacityGrant(1));
            }

            var waiter = new Waiter(fullCapacity: false, cancellationToken);
            waiter.Node = resumePriority
                ? _workerWaiters.AddFirst(waiter)
                : _workerWaiters.AddLast(waiter);
            RegisterCancellation(waiter);
            return AwaitWaiterAsync(waiter);
        }
    }

    private FullCapacityRequest AcquireFullCapacityGrantAsync(
        WorkerLease? worker,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var suspendedWorker = worker is not null && ReferenceEquals(worker.Owner, this) &&
                                  worker.State == WorkerLeaseState.Held;
            if (suspendedWorker)
            {
                worker!.State = WorkerLeaseState.Suspended;
                _activeCapacity--;
            }

            if (_activeCapacity == 0 && _fullCapacityWaiters.Count == 0)
            {
                var capacity = _settings.ThreadLimit;
                _activeCapacity = capacity;
                return new FullCapacityRequest(Task.FromResult(new CapacityGrant(capacity)), suspendedWorker);
            }

            var waiter = new Waiter(fullCapacity: true, cancellationToken);
            waiter.Node = _fullCapacityWaiters.AddLast(waiter);
            RegisterCancellation(waiter);
            PumpWaitersLocked();
            return new FullCapacityRequest(AwaitWaiterAsync(waiter), suspendedWorker);
        }
    }

    private static async Task<CapacityGrant> AwaitWaiterAsync(Waiter waiter)
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
        waiter.CancellationRegistration = waiter.CancellationToken.Register(
            static state =>
            {
                var (owner, pending) = ((GlobalCpuBudget, Waiter))state!;
                owner.CancelWaiter(pending);
            },
            (this, waiter));
    }

    private void CancelWaiter(Waiter waiter)
    {
        lock (_gate)
        {
            if (waiter.Node is null) return;
            if (waiter.FullCapacity)
                _fullCapacityWaiters.Remove(waiter.Node);
            else
                _workerWaiters.Remove(waiter.Node);
            waiter.Node = null;
            waiter.Completion.TrySetCanceled(waiter.CancellationToken);
            PumpWaitersLocked();
        }
    }

    private async Task ResumeWorkerAsync(WorkerLease worker)
    {
        try
        {
            await AcquireWorkerGrantAsync(CancellationToken.None, resumePriority: true).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        lock (_gate)
        {
            if (worker.State == WorkerLeaseState.Suspended)
            {
                worker.State = WorkerLeaseState.Held;
                return;
            }

            _activeCapacity--;
            PumpWaitersLocked();
        }
    }

    private void ReleaseWorker(WorkerLease worker)
    {
        lock (_gate)
        {
            if (worker.State == WorkerLeaseState.Disposed) return;
            if (worker.State == WorkerLeaseState.Held) _activeCapacity--;
            worker.State = WorkerLeaseState.Disposed;
            PumpWaitersLocked();
        }
    }

    private void ReleaseFullCapacity(FullCapacityLease lease)
    {
        lock (_gate)
        {
            if (lease.IsDisposed) return;
            lease.IsDisposed = true;
            _activeCapacity -= lease.ThreadCount;
            if (lease.SuspendedWorker is { State: WorkerLeaseState.Suspended } worker)
            {
                worker.State = WorkerLeaseState.Held;
                _activeCapacity++;
            }
            PumpWaitersLocked();
        }
    }

    private IDisposable Activate(WorkerLease worker)
    {
        lock (_gate)
        {
            if (worker.State != WorkerLeaseState.Held)
                throw new ObjectDisposedException(nameof(ICpuWorkerLease));
        }

        var previous = _ambientWorker.Value;
        _ambientWorker.Value = worker;
        return new AmbientScope(this, previous);
    }

    private void PumpWaitersLocked()
    {
        if (_disposed) return;
        var limit = _settings.ThreadLimit;
        if (_fullCapacityWaiters.First is { } fullNode)
        {
            if (_activeCapacity != 0) return;
            var waiter = fullNode.Value;
            _fullCapacityWaiters.Remove(fullNode);
            waiter.Node = null;
            _activeCapacity = limit;
            waiter.Completion.TrySetResult(new CapacityGrant(limit));
            return;
        }

        while (_activeCapacity < limit && _workerWaiters.First is { } workerNode)
        {
            var waiter = workerNode.Value;
            _workerWaiters.Remove(workerNode);
            waiter.Node = null;
            _activeCapacity++;
            waiter.Completion.TrySetResult(new CapacityGrant(1));
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs args)
    {
        lock (_gate) PumpWaitersLocked();
    }

    private sealed record CapacityGrant(int Capacity);
    private sealed record FullCapacityRequest(Task<CapacityGrant> Grant, bool SuspendedWorker);

    private sealed class Waiter(bool fullCapacity, CancellationToken cancellationToken)
    {
        public bool FullCapacity { get; } = fullCapacity;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<CapacityGrant> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }

    private sealed class WorkerLease(GlobalCpuBudget owner) : ICpuWorkerLease
    {
        public GlobalCpuBudget Owner { get; } = owner;
        public WorkerLeaseState State { get; set; } = WorkerLeaseState.Held;
        public IDisposable Activate() => Owner.Activate(this);
        public void Dispose() => Owner.ReleaseWorker(this);
    }

    private sealed class FullCapacityLease(
        GlobalCpuBudget owner,
        int threadCount,
        WorkerLease? suspendedWorker) : ICpuFullCapacityLease
    {
        public int ThreadCount { get; } = threadCount;
        public WorkerLease? SuspendedWorker { get; } = suspendedWorker;
        public bool IsDisposed { get; set; }
        public void Dispose() => owner.ReleaseFullCapacity(this);
    }

    private sealed class AmbientScope(GlobalCpuBudget owner, WorkerLease? previous) : IDisposable
    {
        private GlobalCpuBudget? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null) current._ambientWorker.Value = previous;
        }
    }

    private enum WorkerLeaseState
    {
        Held,
        Suspended,
        Disposed
    }
}
