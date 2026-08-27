using System.Runtime.Intrinsics.X86;
using System.Runtime.InteropServices;
using MCPIndexSearch.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.HuggingFace.Tokenizer;

namespace MCPIndexSearch.Infrastructure;

public sealed class GraniteEmbeddingGenerator : IEmbeddingGenerator
{
    public const string ModelId = "ibm-granite/granite-embedding-311m-multilingual-r2";
    public const string Revision = "44399559930365213510b1ee2eb15ded83374f0e";
    public const string TokenizerSha = "0087c868b33bad550a78a08d19798cfd7f713cde4f020803b8f51f405503e15f";
    public const string QuantizedSha = "f1fdd44e7e1ac51f12ab7957c7bd092e064d596c288513bf9d326842f669edee";
    public const string Fp32Sha = "75f9f258bf5013f5fe8a4dad61dd0fd16ac0cbaa7a106e3d3f41c2d04a42d541";

    private readonly IAppPaths _paths;
    private readonly ICpuUsageSettings _cpuUsageSettings;
    private readonly IGlobalCpuBudget _cpuBudget;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private Tokenizer? _tokenizer;
    private InferenceSession? _session;
    private volatile bool _isAvailable;
    private string? _unavailableReason;
    private EmbeddingPolicy? _policy;
    private int _configuredThreadCount;

    public GraniteEmbeddingGenerator(
        IAppPaths paths,
        ICpuUsageSettings cpuUsageSettings,
        IGlobalCpuBudget cpuBudget)
    {
        _paths = paths;
        _cpuUsageSettings = cpuUsageSettings;
        _cpuBudget = cpuBudget;
        LoadCore(_cpuUsageSettings.ThreadLimit);
    }

    public bool IsAvailable => _isAvailable;
    public string? UnavailableReason => _unavailableReason;
    public EmbeddingPolicy? Policy => _policy;

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LoadCore(_cpuUsageSettings.ThreadLimit);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void LoadCore(int threadCount)
    {
        var modelDirectory = Path.Combine(_paths.AssetsDirectory, "granite", Revision);
        var tokenizerPath = Path.Combine(modelDirectory, "tokenizer.json");
        var useQuantized = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.X64 && Avx2.IsSupported &&
            !File.Exists(Path.Combine(modelDirectory, "quantization-disabled"));
        var modelFile = useQuantized ? "model_quint8_avx2.onnx" : "model.onnx";
        var modelPath = Path.Combine(modelDirectory, modelFile);
        var modelSha = useQuantized ? QuantizedSha : Fp32Sha;
        _policy = new EmbeddingPolicy(ModelId, Revision, modelSha, TokenizerSha,
            useQuantized ? "quint8-avx2" : "fp32", 768, 384, "cls", "l2-after-matryoshka");

        // ONNX Runtime stopped publishing Intel macOS binaries after 1.23. The
        // mandated 1.29 package therefore cannot load natively for osx-x64.
        // Keep that RID useful and predictable through the normal keyword-only
        // degradation path instead of probing an incompatible host-RID asset.
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            ReplaceResources(null, null,
                "ONNX Runtime 1.29 does not provide an Intel macOS native library; using keyword search on osx-x64.");
            return;
        }

        if (!File.Exists(tokenizerPath) || !File.Exists(modelPath))
        {
            ReplaceResources(null, null, "Semantic-search model is not installed.");
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
            ReplaceResources(tokenizer, session, null);
            _configuredThreadCount = threadCount;
            tokenizer = null;
            session = null;
        }
        catch (Exception exception)
        {
            tokenizer?.Dispose();
            session?.Dispose();
            ReplaceResources(null, null, $"Granite initialization failed: {exception.Message}");
        }
    }

    private void ReplaceResources(Tokenizer? tokenizer, InferenceSession? session, string? unavailableReason)
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

    public Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken) =>
        EmbedAsync(passages, 512, cancellationToken);

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        var result = await EmbedAsync([query], 256, cancellationToken).ConfigureAwait(false);
        return result[0];
    }

    private async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, int maximumTokens, CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        var all = new List<float[]>(texts.Count);
        using var cpuCapacity = await _cpuBudget.AcquireFullCapacityAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_configuredThreadCount != cpuCapacity.ThreadCount)
                LoadCore(cpuCapacity.ThreadCount);

            Tokenizer? tokenizer;
            InferenceSession? session;
            lock (_stateGate)
            {
                tokenizer = _tokenizer;
                session = _session;
            }
            if (tokenizer is null || session is null)
            {
                throw new McpIndexException("model_unavailable", UnavailableReason ?? "Granite assets are unavailable.");
            }

            for (var offset = 0; offset < texts.Count; offset += 8)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(8, texts.Count - offset);
                var encoded = new List<long[]>(count);
                lock (tokenizer)
                {
                    for (var index = 0; index < count; index++)
                    {
                        var ids = new[] { 2L }.Concat(tokenizer.Encode(texts[offset + index], false).First().Ids
                            .Take(maximumTokens - 1)
                            .Select(value => (long)value)
                            ).ToArray();
                        encoded.Add(ids);
                    }
                }

                try
                {
                    all.AddRange(RunBatch(session, encoded));
                }
                catch (Exception exception) when (count > 1 && IsMemoryPressure(exception))
                {
                    foreach (var item in encoded)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        all.Add(RunBatch(session, [item])[0]);
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return all;
    }

    private static IReadOnlyList<float[]> RunBatch(InferenceSession session, IReadOnlyList<long[]> encoded)
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
        if (dimensions.Length != 3 || dimensions[0] != encoded.Count || dimensions[2] < 384)
            throw new McpIndexException("model_output_invalid",
                $"Expected Granite output [batch,tokens,>=384], received [{string.Join(',', dimensions)}].");

        var vectors = new List<float[]>(encoded.Count);
        for (var batch = 0; batch < encoded.Count; batch++)
        {
            var vector = new float[384];
            double squaredNorm = 0;
            for (var dimension = 0; dimension < vector.Length; dimension++)
            {
                vector[dimension] = output[batch, 0, dimension];
                squaredNorm += vector[dimension] * vector[dimension];
            }
            if (squaredNorm <= double.Epsilon)
                throw new McpIndexException("model_output_invalid", "Granite produced a zero-length embedding.");
            var divisor = (float)Math.Sqrt(squaredNorm);
            for (var dimension = 0; dimension < vector.Length; dimension++) vector[dimension] /= divisor;
            vectors.Add(vector);
        }
        return vectors;
    }

    private static bool IsMemoryPressure(Exception exception) => exception is OutOfMemoryException ||
        exception is OnnxRuntimeException && (exception.Message.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
                                               exception.Message.Contains("alloc", StringComparison.OrdinalIgnoreCase));

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ReplaceResources(null, null, "Embedding generator is shutting down.");
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
