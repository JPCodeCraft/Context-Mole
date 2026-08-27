using System.Collections.Concurrent;

namespace MCPIndexSearch.Indexing;

public sealed class EmbeddingPolicyRefreshTracker
{
    private readonly ConcurrentDictionary<Guid, string> _checkedPolicies = [];
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public async Task RunExclusiveAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public bool TryBeginRefresh(Guid projectId, string policyKey)
    {
        while (true)
        {
            if (!_checkedPolicies.TryGetValue(projectId, out var checkedPolicy))
            {
                if (_checkedPolicies.TryAdd(projectId, policyKey)) return true;
                continue;
            }

            if (string.Equals(checkedPolicy, policyKey, StringComparison.Ordinal)) return false;
            if (_checkedPolicies.TryUpdate(projectId, policyKey, checkedPolicy)) return true;
        }
    }

    public bool IsRefreshPending(Guid projectId, string policyKey) =>
        _checkedPolicies.TryGetValue(projectId, out var checkedPolicy) &&
        string.Equals(checkedPolicy, policyKey, StringComparison.Ordinal);

    public void Clear() => _checkedPolicies.Clear();

    public void CancelRefresh(Guid projectId, string policyKey)
    {
        if (_checkedPolicies.TryGetValue(projectId, out var checkedPolicy) &&
            string.Equals(checkedPolicy, policyKey, StringComparison.Ordinal))
            _checkedPolicies.TryRemove(projectId, out _);
    }
}
