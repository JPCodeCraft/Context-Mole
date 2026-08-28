using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

using ContextMole.Broker.Protocol;
using ContextMole.Core;

using Tokenizers.HuggingFace.Tokenizer;

namespace ContextMole.Infrastructure;

/// <summary>
/// Keeps only the small tokenizer in the calling process. ONNX sessions and vector caches live in the broker.
/// </summary>
public sealed class BrokerEmbeddingGenerator : IEmbeddingGenerator
{
    private readonly BrokerRpcClient _broker;
    private readonly IAppPaths _paths;
    private readonly IEmbeddingModelSettings _modelSettings;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly object _stateGate = new();
    private Tokenizer? _tokenizer;
    private EmbeddingPolicy? _policy;
    private string? _unavailableReason = "Semantic model metadata is loading.";
    private string? _fingerprint;
    private bool _disposed;

    public BrokerEmbeddingGenerator(BrokerRpcClient broker, IAppPaths paths,
        IEmbeddingModelSettings modelSettings)
    {
        _broker = broker;
        _paths = paths;
        _modelSettings = modelSettings;
    }

    public bool IsAvailable
    {
        get { lock (_stateGate) return _tokenizer is not null && _unavailableReason is null; }
    }

    public string? UnavailableReason
    {
        get { lock (_stateGate) return _unavailableReason; }
    }

    public EmbeddingPolicy? Policy
    {
        get { lock (_stateGate) return _policy; }
    }

    /// <summary>
    /// Refreshes local policy/tokenizer metadata only. It deliberately makes no broker request and never loads ONNX.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _modelSettings.RefreshFromDisk();
            var model = GraniteEmbeddingModels.Get(_modelSettings.Model);
            var directory = Path.Combine(_paths.AssetsDirectory, "granite", model.Revision);
            var fingerprint = GetFingerprint(directory, model);
            lock (_stateGate)
                if (string.Equals(_fingerprint, fingerprint, StringComparison.Ordinal)) return;

            await Task.Run(() => LoadTokenizerMetadata(model, directory, fingerprint), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public int CountTokens(string text)
    {
        lock (_stateGate)
        {
            if (_tokenizer is null) return Math.Max(1, (int)Math.Ceiling(text.Length / 3.5));
            lock (_tokenizer)
                return 1 + _tokenizer.Encode(text, false).First().Ids.Count;
        }
    }

    public async Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages,
        CancellationToken cancellationToken)
    {
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        EnsureAvailable();
        try
        {
            var result = await _broker.EmbedPassagesAsync(passages, cancellationToken).ConfigureAwait(false);
            UpdatePolicy(result.Policy);
            return result;
        }
        catch (BrokerRpcException exception)
        {
            throw new ContextMoleException(exception.Code, exception.Message, exception.Retryable);
        }
    }

    public async Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        EnsureAvailable();
        try
        {
            var result = await _broker.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
            UpdatePolicy(result.Policy);
            return result;
        }
        catch (BrokerRpcException exception)
        {
            throw new ContextMoleException(exception.Code, exception.Message, exception.Retryable);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _reloadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            lock (_stateGate)
            {
                _tokenizer?.Dispose();
                _tokenizer = null;
                _policy = null;
                _unavailableReason = "Embedding generator is shutting down.";
            }
        }
        finally
        {
            _reloadGate.Release();
            _reloadGate.Dispose();
        }
    }

    private void LoadTokenizerMetadata(GraniteEmbeddingModelDefinition model, string directory, string fingerprint)
    {
        var tokenizerPath = Path.Combine(directory, "tokenizer.json");
        var quantized = RuntimeInformation.ProcessArchitecture == Architecture.X64 && Avx2.IsSupported &&
                        !File.Exists(Path.Combine(directory, "quantization-disabled"));
        var modelPath = Path.Combine(directory, quantized ? "model_quint8_avx2.onnx" : "model.onnx");
        var policy = new EmbeddingPolicy(model.ModelId, model.Revision,
            quantized ? model.QuantizedSha : model.Fp32Sha, model.TokenizerSha,
            quantized ? "quint8-avx2" : "fp32", model.SourceDimensions, model.Dimensions,
            model.Pooling, model.Normalization);
        var complete = File.Exists(Path.Combine(directory, "installation-complete")) &&
                       !File.Exists(Path.Combine(directory, "repair-required")) &&
                       File.Exists(tokenizerPath) && File.Exists(modelPath) &&
                       (!quantized || File.Exists(Path.Combine(directory, "validation.json")));
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            ReplaceTokenizer(null, policy,
                "ONNX Runtime 1.29 does not provide an Intel macOS native library; using keyword search on osx-x64.",
                fingerprint);
            return;
        }
        if (!complete)
        {
            ReplaceTokenizer(null, policy, $"{model.DisplayName} is not installed.", fingerprint);
            return;
        }

        Tokenizer? tokenizer = null;
        try
        {
            tokenizer = Tokenizer.FromFile(tokenizerPath);
            ReplaceTokenizer(tokenizer, policy, null, fingerprint);
            tokenizer = null;
        }
        catch (Exception exception)
        {
            ReplaceTokenizer(null, policy, $"Granite tokenizer initialization failed: {exception.Message}",
                fingerprint);
        }
        finally
        {
            tokenizer?.Dispose();
        }
    }

    private void ReplaceTokenizer(Tokenizer? tokenizer, EmbeddingPolicy policy, string? unavailableReason,
        string fingerprint)
    {
        Tokenizer? previous;
        lock (_stateGate)
        {
            previous = _tokenizer;
            _tokenizer = tokenizer;
            _policy = policy;
            _unavailableReason = unavailableReason;
            _fingerprint = fingerprint;
        }
        previous?.Dispose();
    }

    private void UpdatePolicy(EmbeddingPolicy policy)
    {
        lock (_stateGate) _policy = policy;
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
            throw new ContextMoleException("model_unavailable",
                UnavailableReason ?? "Granite assets are unavailable.");
    }

    private static string GetFingerprint(string directory, GraniteEmbeddingModelDefinition model) => string.Join('|',
        model.Choice,
        FileFingerprint(Path.Combine(directory, "tokenizer.json")),
        FileFingerprint(Path.Combine(directory, "model_quint8_avx2.onnx")),
        FileFingerprint(Path.Combine(directory, "model.onnx")),
        FileFingerprint(Path.Combine(directory, "installation-complete")),
        FileFingerprint(Path.Combine(directory, "repair-required")),
        FileFingerprint(Path.Combine(directory, "validation.json")),
        FileFingerprint(Path.Combine(directory, "quantization-disabled")));

    private static string FileFingerprint(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "-";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"error:{exception.HResult}";
        }
    }
}
