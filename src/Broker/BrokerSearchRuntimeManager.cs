using ContextMole.Core;
using ContextMole.Infrastructure;
using ContextMole.Search;

using Microsoft.Extensions.DependencyInjection;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ContextMole.Tests")]

namespace ContextMole.Broker;

internal interface IBrokerSearchRuntime : IAsyncDisposable
{
    HybridSearchService Search { get; }
    IEmbeddingGenerator Embeddings { get; }
    VectorIndexCache Cache { get; }
}

public sealed class BrokerSearchRuntimeManager : IAsyncDisposable
{
    public static readonly TimeSpan SemanticIdleTimeout = TimeSpan.FromMinutes(2);

    private readonly IAppPaths _paths;
    private readonly ICpuUsageSettings _cpuSettings;
    private readonly IEmbeddingModelSettings _modelSettings;
    private readonly IGlobalCpuBudget _cpuBudget;
    private readonly ISearchStore _store;
    private readonly ISystemMemorySnapshotProvider _memorySnapshots;
    private readonly TimeProvider _timeProvider;
    private readonly Func<IBrokerSearchRuntime> _runtimeFactory;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private IBrokerSearchRuntime? _runtime;
    private int _activeLeases;
    private DateTimeOffset _lastRuntimeActivityUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    public BrokerSearchRuntimeManager(
        IAppPaths paths,
        ICpuUsageSettings cpuSettings,
        IEmbeddingModelSettings modelSettings,
        IGlobalCpuBudget cpuBudget,
        ISearchStore store,
        ISystemMemorySnapshotProvider memorySnapshots,
        TimeProvider timeProvider)
        : this(paths, cpuSettings, modelSettings, cpuBudget, store, memorySnapshots, timeProvider, null)
    {
    }

    internal BrokerSearchRuntimeManager(
        IAppPaths paths,
        ICpuUsageSettings cpuSettings,
        IEmbeddingModelSettings modelSettings,
        IGlobalCpuBudget cpuBudget,
        ISearchStore store,
        ISystemMemorySnapshotProvider memorySnapshots,
        TimeProvider timeProvider,
        Func<IBrokerSearchRuntime>? runtimeFactory)
    {
        _paths = paths;
        _cpuSettings = cpuSettings;
        _modelSettings = modelSettings;
        _cpuBudget = cpuBudget;
        _store = store;
        _memorySnapshots = memorySnapshots;
        _timeProvider = timeProvider;
        _runtimeFactory = runtimeFactory ?? CreateRuntime;
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await lease.Runtime.Search.SearchAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public void RefreshEmbeddingMetadata() => _modelSettings.RefreshFromDisk();

    public async Task<Broker.Protocol.BrokerEmbeddingStatus> GetEmbeddingStatusAsync(
        CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runtime is null) return GetUnloadedEmbeddingStatus();
            var embeddings = _runtime.Embeddings;
            return new Broker.Protocol.BrokerEmbeddingStatus(embeddings.IsAvailable, embeddings.UnavailableReason,
                embeddings.Policy);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<int> CountTokensAsync(string text, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        await lease.Runtime.Embeddings.ReloadAsync(cancellationToken).ConfigureAwait(false);
        return lease.Runtime.Embeddings.CountTokens(text);
    }

    public async Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages,
        CancellationToken cancellationToken)
    {
        await using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        await lease.Runtime.Embeddings.ReloadAsync(cancellationToken).ConfigureAwait(false);
        return await lease.Runtime.Embeddings.EmbedPassagesAsync(passages, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        await lease.Runtime.Embeddings.ReloadAsync(cancellationToken).ConfigureAwait(false);
        return await lease.Runtime.Embeddings.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> UnloadIfIdleAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runtime is null || _activeLeases != 0 ||
                _timeProvider.GetUtcNow() - _lastRuntimeActivityUtc < SemanticIdleTimeout)
                return false;
            var runtime = _runtime;
            _runtime = null;
            // Keep runtime retirement inside the state gate. Acquisitions may queue here, but cannot
            // construct a replacement ONNX session until the old session has released its native memory.
            await runtime.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<bool> ClearVectorCacheUnderPressureAsync(CancellationToken cancellationToken)
    {
        var snapshot = _memorySnapshots.Capture();
        if (snapshot.TotalPhysicalBytes <= 0) return false;
        var reserve = MemoryPressurePolicy.CalculateSystemReserve(snapshot.TotalPhysicalBytes);
        var processLimit = MemoryPressurePolicy.CalculateProcessCleanupThreshold(snapshot.TotalPhysicalBytes);
        if (snapshot.AvailablePhysicalBytes >= reserve && snapshot.ProcessPrivateBytes < processLimit) return false;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runtime is null || _runtime.Cache.CurrentBytes == 0) return false;
            _runtime.Cache.Clear();
            return true;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        IBrokerSearchRuntime? runtime;
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            runtime = _runtime;
            _runtime = null;
        }
        finally
        {
            _stateGate.Release();
            _stateGate.Dispose();
        }
        if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<RuntimeLease> AcquireAsync(CancellationToken cancellationToken)
    {
        _cpuSettings.RefreshFromDisk();
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _runtime ??= _runtimeFactory();
            _activeLeases++;
            _lastRuntimeActivityUtc = _timeProvider.GetUtcNow();
            return new RuntimeLease(this, _runtime);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private IBrokerSearchRuntime CreateRuntime()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_paths);
        services.AddSingleton<IAppPaths>(_paths);
        services.AddSingleton(_cpuSettings);
        services.AddSingleton<ICpuUsageSettings>(_cpuSettings);
        services.AddSingleton(_modelSettings);
        services.AddSingleton<IEmbeddingModelSettings>(_modelSettings);
        services.AddSingleton(_cpuBudget);
        services.AddSingleton<IGlobalCpuBudget>(_cpuBudget);
        services.AddSingleton(_store);
        services.AddSingleton<ISearchStore>(_store);
        services.AddSingleton<IEmbeddingGenerator, GraniteEmbeddingGenerator>();
        var totalPhysicalBytes = _memorySnapshots.Capture().TotalPhysicalBytes;
        var cacheBudget = totalPhysicalBytes > 0
            ? VectorIndexCache.CalculateAdaptiveBudget(totalPhysicalBytes)
            : VectorIndexCache.DefaultByteBudget;
        services.AddContextMoleSearch(cacheBudget);
        var provider = services.BuildServiceProvider();
        return new Runtime(provider, provider.GetRequiredService<HybridSearchService>(),
            provider.GetRequiredService<IEmbeddingGenerator>(), provider.GetRequiredService<VectorIndexCache>());
    }

    private Broker.Protocol.BrokerEmbeddingStatus GetUnloadedEmbeddingStatus()
    {
        _modelSettings.RefreshFromDisk();
        var model = GraniteEmbeddingModels.Get(_modelSettings.Model);
        var directory = Path.Combine(_paths.AssetsDirectory, "granite", model.Revision);
        var quantized = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                        System.Runtime.InteropServices.Architecture.X64 &&
                        System.Runtime.Intrinsics.X86.Avx2.IsSupported &&
                        !File.Exists(Path.Combine(directory, "quantization-disabled"));
        var policy = new EmbeddingPolicy(model.ModelId, model.Revision,
            quantized ? model.QuantizedSha : model.Fp32Sha, model.TokenizerSha,
            quantized ? "quint8-avx2" : "fp32", model.SourceDimensions, model.Dimensions,
            model.Pooling, model.Normalization);
        var complete = File.Exists(Path.Combine(directory, "installation-complete")) &&
                       !File.Exists(Path.Combine(directory, "repair-required")) &&
                       File.Exists(Path.Combine(directory, "tokenizer.json")) &&
                       File.Exists(Path.Combine(directory, quantized ? "model_quint8_avx2.onnx" : "model.onnx")) &&
                       (!quantized || File.Exists(Path.Combine(directory, "validation.json")));
        if (OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.X64)
            return new Broker.Protocol.BrokerEmbeddingStatus(false,
                "ONNX Runtime 1.29 does not provide an Intel macOS native library; using keyword search on osx-x64.",
                policy);
        return new Broker.Protocol.BrokerEmbeddingStatus(complete,
            complete ? "The semantic model is installed and will load on first use." : $"{model.DisplayName} is not installed.",
            policy);
    }

    private async ValueTask ReleaseAsync()
    {
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _activeLeases--;
            _lastRuntimeActivityUtc = _timeProvider.GetUtcNow();
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private sealed record Runtime(ServiceProvider Provider, HybridSearchService Search,
        IEmbeddingGenerator Embeddings, VectorIndexCache Cache) : IBrokerSearchRuntime
    {
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private sealed class RuntimeLease(BrokerSearchRuntimeManager owner, IBrokerSearchRuntime runtime) : IAsyncDisposable
    {
        private BrokerSearchRuntimeManager? _owner = owner;
        public IBrokerSearchRuntime Runtime { get; } = runtime;
        public async ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null) await owner.ReleaseAsync().ConfigureAwait(false);
        }
    }
}
