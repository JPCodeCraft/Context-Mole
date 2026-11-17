namespace ContextMole.Indexing;

/// <summary>
/// Bounds how much memory concurrent extractions of large source files may claim. Extraction reads a whole
/// source (and its attachments) into memory, so several workers that each pick up a file of hundreds of megabytes
/// could exhaust RAM. Files below <see cref="ThresholdBytes"/> are admitted immediately; larger files reserve an
/// estimate of their working set from <see cref="BudgetBytes"/> and wait in arrival order until it fits. A single
/// file larger than the whole budget is still admitted, alone.
/// </summary>
public sealed class SourceMemoryAdmission
{
    private const long Mebibyte = 1024L * 1024;
    private const long Gibibyte = 1024L * Mebibyte;
    /// <summary>Raw bytes, decoded text and parser structures are alive together during extraction.</summary>
    internal const int WorkingSetMultiplier = 3;
    public const long DefaultThresholdBytes = 32L * Mebibyte;

    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly object _gate = new();
    private TaskCompletionSource _released = NewSignal();
    private long _reserved;

    public SourceMemoryAdmission(long budgetBytes, long thresholdBytes = DefaultThresholdBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(thresholdBytes);
        BudgetBytes = budgetBytes;
        ThresholdBytes = thresholdBytes;
    }

    public long BudgetBytes { get; }
    public long ThresholdBytes { get; }

    internal long ReservedBytes
    {
        get { lock (_gate) return _reserved; }
    }

    /// <summary>A quarter of the memory available to this process, between 1 GiB and 8 GiB.</summary>
    public static long CalculateBudget(long totalAvailableMemoryBytes) => totalAvailableMemoryBytes <= 0
        ? 2 * Gibibyte
        : Math.Clamp(totalAvailableMemoryBytes / 4, Gibibyte, 8 * Gibibyte);

    public static SourceMemoryAdmission CreateForCurrentProcess() =>
        new(CalculateBudget(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes));

    public async ValueTask<Lease> AcquireAsync(long sourceBytes, CancellationToken cancellationToken)
    {
        if (sourceBytes < ThresholdBytes) return Lease.None;
        var reservation = sourceBytes > BudgetBytes / WorkingSetMultiplier
            ? BudgetBytes
            : sourceBytes * WorkingSetMultiplier;
        // One waiter at a time accumulates capacity, so large files are admitted in arrival order and two
        // partially admitted files can never block each other.
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Task released;
                lock (_gate)
                {
                    if (_reserved + reservation <= BudgetBytes)
                    {
                        _reserved += reservation;
                        return new Lease(this, reservation);
                    }
                    released = _released.Task;
                }
                await released.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    private void Release(long reservation)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            _reserved -= reservation;
            signal = _released;
            _released = NewSignal();
        }
        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A reservation that is returned once, however many times it is disposed.</summary>
    public sealed class Lease : IDisposable
    {
        internal static readonly Lease None = new(null, 0);
        private SourceMemoryAdmission? _owner;

        internal Lease(SourceMemoryAdmission? owner, long reservation)
        {
            _owner = owner;
            ReservedBytes = reservation;
        }

        public long ReservedBytes { get; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(ReservedBytes);
    }
}
