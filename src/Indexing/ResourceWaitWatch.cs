using System.Diagnostics;

namespace ContextMole.Indexing;

/// <summary>
/// Calls back while a wait goes on: once after <c>firstWarning</c>, then every <c>repeat</c>, until disposed. Waiting
/// for a CPU slot or source memory is otherwise silent, so a stuck wait would leave nothing in the log.
/// </summary>
internal sealed class ResourceWaitWatch : IDisposable
{
    private readonly Timer _timer;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Action<TimeSpan> _warn;
    private int _disposed;

    public ResourceWaitWatch(TimeSpan firstWarning, TimeSpan repeat, Action<TimeSpan> warn)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(firstWarning, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(repeat, TimeSpan.Zero);
        _warn = warn;
        _timer = new Timer(static state => ((ResourceWaitWatch)state!).Fire(), this, firstWarning, repeat);
    }

    private void Fire()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _warn(Stopwatch.GetElapsedTime(_started));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A diagnostic must never take down the timer thread or the indexing it describes.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _timer.Dispose();
    }
}
