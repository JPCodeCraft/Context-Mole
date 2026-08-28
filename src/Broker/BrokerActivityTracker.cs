namespace ContextMole.Broker;

public sealed class BrokerActivityTracker(TimeProvider timeProvider)
{
    private readonly TimeProvider _timeProvider = timeProvider;
    private long _lastActivityUtcTicks = timeProvider.GetUtcNow().UtcTicks;
    private int _activeRequests;

    public int ActiveRequests => Volatile.Read(ref _activeRequests);
    public DateTimeOffset LastActivityUtc => new(Interlocked.Read(ref _lastActivityUtcTicks), TimeSpan.Zero);

    public IDisposable BeginRequest()
    {
        Touch();
        Interlocked.Increment(ref _activeRequests);
        return new RequestLease(this);
    }

    public void Touch() => Interlocked.Exchange(ref _lastActivityUtcTicks, _timeProvider.GetUtcNow().UtcTicks);

    public bool IsIdle(TimeSpan duration)
    {
        if (ActiveRequests != 0) return false;
        return _timeProvider.GetUtcNow() - LastActivityUtc >= duration;
    }

    private void EndRequest()
    {
        Touch();
        Interlocked.Decrement(ref _activeRequests);
    }

    private sealed class RequestLease(BrokerActivityTracker owner) : IDisposable
    {
        private BrokerActivityTracker? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndRequest();
    }
}
