using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

using ContextMole.Core;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using Tokenizers.HuggingFace.Tokenizer;

namespace ContextMole.Infrastructure;

public sealed class GraniteEmbeddingGenerator : IEmbeddingGenerator
{
    private readonly IAppPaths _paths;
    private readonly ICpuUsageSettings _cpuUsageSettings;
    private readonly IEmbeddingModelSettings _modelSettings;
    private readonly IGlobalCpuBudget _cpuBudget;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private Tokenizer? _tokenizer;
    private InferenceSession? _session;
    private volatile bool _isAvailable;
    private string? _unavailableReason;
    private EmbeddingPolicy? _policy;
    private EmbeddingModelChoice? _loadedModel;
    private int _configuredThreadCount;
    private string? _installationFingerprint;

    public GraniteEmbeddingGenerator(
        IAppPaths paths,
        ICpuUsageSettings cpuUsageSettings,
        IEmbeddingModelSettings modelSettings,
        IGlobalCpuBudget cpuBudget)
    {
        _paths = paths;
        _cpuUsageSettings = cpuUsageSettings;
        _modelSettings = modelSettings;
        _cpuBudget = cpuBudget;
        _unavailableReason = $"{GraniteEmbeddingModels.Get(_modelSettings.Model).DisplayName} is loading.";
    }

    public bool IsAvailable => _isAvailable;
    public string? UnavailableReason
    {
        get { lock (_stateGate) return _unavailableReason; }
    }
    public EmbeddingPolicy? Policy
    {
        get { lock (_stateGate) return _policy; }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        _modelSettings.RefreshFromDisk();
        var selectedModel = GraniteEmbeddingModels.Get(_modelSettings.Model);
        var installationFingerprint = GetInstallationFingerprint(selectedModel);
        if (CanReuseAttempt(selectedModel.Choice, _cpuUsageSettings.ThreadLimit, installationFingerprint)) return;

        using var cpuCapacity = await _cpuBudget.AcquireFullCapacityAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _modelSettings.RefreshFromDisk();
            var model = GraniteEmbeddingModels.Get(_modelSettings.Model);
            installationFingerprint = GetInstallationFingerprint(model);
            if (CanReuseAttempt(model.Choice, cpuCapacity.ThreadCount, installationFingerprint)) return;
            await Task.Run(() => LoadCore(model, cpuCapacity.ThreadCount, installationFingerprint), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool CanReuseAttempt(EmbeddingModelChoice choice, int threadCount, string installationFingerprint)
    {
        lock (_stateGate)
            return _loadedModel == choice && _configuredThreadCount == threadCount &&
                   string.Equals(_installationFingerprint, installationFingerprint, StringComparison.Ordinal);
    }

    private void LoadCore(
        GraniteEmbeddingModelDefinition model,
        int threadCount,
        string installationFingerprint)
    {
        var modelDirectory = GraniteModelInstallation.GetDirectory(_paths, model);
        var tokenizerPath = Path.Combine(modelDirectory, "tokenizer.json");
        var quantizedSupported = RuntimeInformation.ProcessArchitecture == Architecture.X64 && Avx2.IsSupported;
        var useQuantized = quantizedSupported &&
            !File.Exists(Path.Combine(modelDirectory, "quantization-disabled"));
        var modelFile = useQuantized ? "model_quint8_avx2.onnx" : "model.onnx";
        var modelPath = Path.Combine(modelDirectory, modelFile);
        var modelSha = useQuantized ? model.QuantizedSha : model.Fp32Sha;
        var policy = new EmbeddingPolicy(model.ModelId, model.Revision, modelSha, model.TokenizerSha,
            useQuantized ? "quint8-avx2" : "fp32", model.SourceDimensions, model.Dimensions,
            model.Pooling, model.Normalization);

        // ONNX Runtime stopped publishing Intel macOS binaries after 1.23. The
        // mandated 1.29 package therefore cannot load natively for osx-x64.
        // Keep that RID useful and predictable through the normal keyword-only
        // degradation path instead of probing an incompatible host-RID asset.
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            ReplaceResources(null, null,
                "ONNX Runtime 1.29 does not provide an Intel macOS native library; using keyword search on osx-x64.",
                policy, model.Choice, threadCount, installationFingerprint);
            return;
        }

        if (!File.Exists(tokenizerPath) || !File.Exists(modelPath))
        {
            ReplaceResources(null, null, $"{model.DisplayName} is not installed.", policy, model.Choice, threadCount,
                installationFingerprint);
            return;
        }
        if (!GraniteModelInstallation.IsComplete(_paths, model) ||
            (useQuantized && !File.Exists(Path.Combine(modelDirectory, "validation.json"))))
        {
            ReplaceResources(null, null, "Semantic-search model installation has not finished validation.",
                policy, model.Choice, threadCount, installationFingerprint);
            return;
        }

        Tokenizer? tokenizer = null;
        InferenceSession? session = null;
        try
        {
            tokenizer = Tokenizer.FromFile(tokenizerPath);
            var options = new SessionOptions
            {
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = threadCount,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            session = new InferenceSession(modelPath, options);
            ReplaceResources(tokenizer, session, null, policy, model.Choice, threadCount, installationFingerprint);
            tokenizer = null;
            session = null;
        }
        catch (Exception exception)
        {
            tokenizer?.Dispose();
            session?.Dispose();
            ReplaceResources(null, null, $"Granite initialization failed: {exception.Message}",
                policy, model.Choice, threadCount, installationFingerprint);
        }
    }

    private void ReplaceResources(
        Tokenizer? tokenizer,
        InferenceSession? session,
        string? unavailableReason,
        EmbeddingPolicy? policy,
        EmbeddingModelChoice? loadedModel,
        int configuredThreadCount,
        string? installationFingerprint)
    {
        Tokenizer? previousTokenizer;
        InferenceSession? previousSession;
        lock (_stateGate)
        {
            previousTokenizer = _tokenizer;
            previousSession = _session;
            _tokenizer = tokenizer;
            _session = session;
            _unavailableReason = unavailableReason;
            _isAvailable = tokenizer is not null && session is not null;
            _policy = policy;
            _loadedModel = loadedModel;
            _configuredThreadCount = configuredThreadCount;
            _installationFingerprint = installationFingerprint;
        }
        previousSession?.Dispose();
        previousTokenizer?.Dispose();
    }

    public int CountTokens(string text)
    {
        lock (_stateGate)
        {
            if (_tokenizer is null)
            {
                return Math.Max(1, (int)Math.Ceiling(text.Length / 3.5));
            }

            lock (_tokenizer)
            {
                return 1 + _tokenizer.Encode(text, false).First().Ids.Count;
            }
        }
    }

    public Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken) =>
        EmbedAsync(passages, 512, cancellationToken);

    public async Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        var result = await EmbedAsync([query], 256, cancellationToken).ConfigureAwait(false);
        return new QueryEmbedding(result.Vectors[0], result.Policy);
    }

    private async Task<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> texts,
        int maximumTokens,
        CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            var emptyPolicy = Policy ?? throw new ContextMoleException("model_unavailable",
                UnavailableReason ?? "Granite assets are unavailable.");
            return new EmbeddingBatch([], emptyPolicy);
        }

        var all = new List<float[]>(texts.Count);
        using var cpuCapacity = await _cpuBudget.AcquireFullCapacityAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _modelSettings.RefreshFromDisk();
            var selectedModel = GraniteEmbeddingModels.Get(_modelSettings.Model);
            var installationFingerprint = GetInstallationFingerprint(selectedModel);
            if (!CanReuseAttempt(selectedModel.Choice, cpuCapacity.ThreadCount, installationFingerprint))
                LoadCore(selectedModel, cpuCapacity.ThreadCount, installationFingerprint);

            Tokenizer? tokenizer;
            InferenceSession? session;
            EmbeddingPolicy? policy;
            lock (_stateGate)
            {
                tokenizer = _tokenizer;
                session = _session;
                policy = _policy;
            }
            if (tokenizer is null || session is null || policy is null)
            {
                throw new ContextMoleException("model_unavailable", UnavailableReason ?? "Granite assets are unavailable.");
            }

            try
            {
                for (var offset = 0; offset < texts.Count; offset += 8)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(8, texts.Count - offset);
                    var encoded = new List<long[]>(count);
                    lock (tokenizer)
                    {
                        for (var index = 0; index < count; index++)
                        {
                            var ids = new[] { selectedModel.BosTokenId }.Concat(tokenizer.Encode(texts[offset + index], false).First().Ids
                                .Take(maximumTokens - 1)
                                .Select(value => (long)value)
                                ).ToArray();
                            encoded.Add(ids);
                        }
                    }

                    try
                    {
                        all.AddRange(RunBatch(session, encoded, selectedModel.SourceDimensions, selectedModel.Dimensions));
                    }
                    catch (Exception exception) when (count > 1 && IsMemoryPressure(exception))
                    {
                        foreach (var item in encoded)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            all.Add(RunBatch(session, [item], selectedModel.SourceDimensions, selectedModel.Dimensions)[0]);
                        }
                    }
                }
            }
            catch (Exception exception) when (IsPermanentInferenceFailure(exception))
            {
                var reason = $"Granite inference failed and the selected model needs repair: {exception.Message}";
                try
                {
                    GraniteModelInstallation.MarkForRepair(_paths, selectedModel, reason);
                }
                catch (Exception markerException) when (markerException is IOException or UnauthorizedAccessException)
                {
                    reason += $" The repair marker could not be written: {markerException.Message}";
                }
                ReplaceResources(null, null, reason, policy, null, 0, null);
                throw new ContextMoleException("model_inference_failed", reason);
            }

            return new EmbeddingBatch(all, policy);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<float[]> RunBatch(
        InferenceSession session,
        IReadOnlyList<long[]> encoded,
        int sourceDimensions,
        int outputDimensions)
    {
        var sequenceLength = encoded.Max(item => item.Length);
        var inputIds = new DenseTensor<long>([encoded.Count, sequenceLength]);
        var attentionMask = new DenseTensor<long>([encoded.Count, sequenceLength]);
        for (var batch = 0; batch < encoded.Count; batch++)
            for (var token = 0; token < encoded[batch].Length; token++)
            {
                inputIds[batch, token] = encoded[batch][token];
                attentionMask[batch, token] = 1;
            }

        using var results = session.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
        ]);
        var output = results.First().AsTensor<float>();
        var dimensions = output.Dimensions.ToArray();
        if (dimensions.Length != 3 || dimensions[0] != encoded.Count || dimensions[2] != sourceDimensions ||
            outputDimensions > sourceDimensions)
            throw new ContextMoleException("model_output_invalid",
                $"Expected Granite output [batch,tokens,{sourceDimensions}], received [{string.Join(',', dimensions)}].");

        var vectors = new List<float[]>(encoded.Count);
        for (var batch = 0; batch < encoded.Count; batch++)
        {
            var vector = new float[outputDimensions];
            double squaredNorm = 0;
            for (var dimension = 0; dimension < vector.Length; dimension++)
            {
                vector[dimension] = output[batch, 0, dimension];
                squaredNorm += vector[dimension] * vector[dimension];
            }
            if (squaredNorm <= double.Epsilon)
                throw new ContextMoleException("model_output_invalid", "Granite produced a zero-length embedding.");
            var divisor = (float)Math.Sqrt(squaredNorm);
            for (var dimension = 0; dimension < vector.Length; dimension++) vector[dimension] /= divisor;
            vectors.Add(vector);
        }
        return vectors;
    }

    private static bool IsMemoryPressure(Exception exception) => exception is OutOfMemoryException ||
        exception is OnnxRuntimeException && (exception.Message.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
                                               exception.Message.Contains("alloc", StringComparison.OrdinalIgnoreCase));

    private static bool IsPermanentInferenceFailure(Exception exception) =>
        exception is ContextMoleException { Code: "model_output_invalid" } ||
        exception is OnnxRuntimeException && !IsMemoryPressure(exception);

    private string GetInstallationFingerprint(GraniteEmbeddingModelDefinition model)
    {
        var directory = GraniteModelInstallation.GetDirectory(_paths, model);
        return string.Join('|',
            FileFingerprint(Path.Combine(directory, "tokenizer.json")),
            FileFingerprint(Path.Combine(directory, "model_quint8_avx2.onnx")),
            FileFingerprint(Path.Combine(directory, "model.onnx")),
            FileFingerprint(Path.Combine(directory, GraniteModelInstallation.CompletionMarker)),
            FileFingerprint(Path.Combine(directory, GraniteModelInstallation.RepairMarker)),
            FileFingerprint(Path.Combine(directory, "validation.json")),
            FileFingerprint(Path.Combine(directory, "quantization-disabled")));
    }

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

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ReplaceResources(null, null, "Embedding generator is shutting down.", null, null, 0, null);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
