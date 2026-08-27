using System.Diagnostics;
using MCPIndexSearch.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.HuggingFace.Tokenizer;

namespace MCPIndexSearch.Infrastructure;

public sealed record GraniteValidationResult(
    bool QuantizedEnabled,
    double MeanCorrespondingCosine,
    double MeanTop10Overlap,
    double QuantizedMillisecondsPerVector,
    double Fp32MillisecondsPerVector,
    long PeakWorkingSetBytes,
    string Decision);

public static class GraniteEmbeddingDiagnostics
{
    public static GraniteValidationResult ValidateProfiles(
        IAppPaths paths,
        GraniteEmbeddingModelDefinition model,
        int threadCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(paths.AssetsDirectory, "granite", model.Revision);
        var tokenizerPath = Path.Combine(directory, "tokenizer.json");
        var quantizedPath = Path.Combine(directory, "model_quint8_avx2.onnx");
        var fp32Path = Path.Combine(directory, "model.onnx");
        if (!File.Exists(tokenizerPath) || !File.Exists(quantizedPath) || !File.Exists(fp32Path))
            throw new McpIndexException("model_unavailable", "Both Granite profiles and the tokenizer are required for validation.");

        string[] documents =
        [
            "Contrato de prestação de serviços e cláusulas de pagamento.", "Relatório financeiro trimestral e fluxo de caixa.",
            "Manual de segurança para equipamentos industriais.", "Ata da reunião do conselho administrativo.", "Política de privacidade e proteção de dados pessoais.",
            "Service agreement with payment and termination clauses.", "Quarterly financial report and cash flow analysis.",
            "Industrial equipment safety and maintenance handbook.", "Minutes from the board of directors meeting.", "Privacy policy and personal data protection.",
            "Contrato de servicios con cláusulas de pago y terminación.", "Informe financiero trimestral y análisis de caja.",
            "Manual de seguridad y mantenimiento industrial.", "Acta de la reunión del consejo de administración.", "Política de privacidad y protección de datos personales."
        ];
        string[] queries =
        [
            "cláusulas de pagamento do contrato", "segurança de equipamentos", "board meeting minutes",
            "cash flow report", "protección de datos personales", "mantenimiento industrial"
        ];
        var all = documents.Concat(queries).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        using var tokenizer = Tokenizer.FromFile(tokenizerPath);

        var quantWatch = new Stopwatch();
        List<float[]> quantVectors;
        cancellationToken.ThrowIfCancellationRequested();
        using (var quantized = CreateSession(quantizedPath, threadCount))
        {
            quantWatch.Start();
            quantVectors = Embed(quantized, tokenizer, all, model, cancellationToken);
            quantWatch.Stop();
        }

        var fpWatch = new Stopwatch();
        List<float[]> fpVectors;
        cancellationToken.ThrowIfCancellationRequested();
        using (var fp32 = CreateSession(fp32Path, threadCount))
        {
            fpWatch.Start();
            fpVectors = Embed(fp32, tokenizer, all, model, cancellationToken);
            fpWatch.Stop();
        }

        var meanCosine = quantVectors.Zip(fpVectors).Average(pair => Dot(pair.First, pair.Second));
        var overlaps = new List<double>();
        for (var query = 0; query < queries.Length; query++)
        {
            var queryIndex = documents.Length + query;
            var quantTop = Enumerable.Range(0, documents.Length).OrderByDescending(index => Dot(quantVectors[queryIndex], quantVectors[index])).Take(10).ToHashSet();
            var fpTop = Enumerable.Range(0, documents.Length).OrderByDescending(index => Dot(fpVectors[queryIndex], fpVectors[index])).Take(10).ToHashSet();
            overlaps.Add(quantTop.Intersect(fpTop).Count() / 10d);
        }
        var meanOverlap = overlaps.Average();
        var enabled = meanCosine >= 0.995 && meanOverlap >= 0.90;
        var marker = Path.Combine(directory, "quantization-disabled");
        if (enabled)
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
        else
        {
            File.WriteAllText(marker, $"Disabled after parity validation: cosine={meanCosine:F6}, top10_overlap={meanOverlap:P2}");
        }

        return new GraniteValidationResult(enabled, meanCosine, meanOverlap,
            quantWatch.Elapsed.TotalMilliseconds / all.Length, fpWatch.Elapsed.TotalMilliseconds / all.Length,
            Process.GetCurrentProcess().PeakWorkingSet64,
            enabled ? "Quantized profile passed parity thresholds." : "Quantized profile failed parity thresholds; FP32 is required.");
    }

    private static InferenceSession CreateSession(string path, int threadCount) => new(path, new SessionOptions
    {
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        InterOpNumThreads = 1,
        IntraOpNumThreads = threadCount,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
    });

    private static List<float[]> Embed(
        InferenceSession session,
        Tokenizer tokenizer,
        IReadOnlyList<string> texts,
        GraniteEmbeddingModelDefinition model,
        CancellationToken cancellationToken)
    {
        var outputVectors = new List<float[]>(texts.Count);
        for (var offset = 0; offset < texts.Count; offset += 8)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = texts.Skip(offset).Take(8).Select(text =>
                new[] { model.BosTokenId }.Concat(tokenizer.Encode(text, false).First().Ids.Take(511)
                    .Select(id => (long)id)).ToArray()).ToArray();
            var length = batch.Max(ids => ids.Length);
            var inputIds = new DenseTensor<long>([batch.Length, length]);
            var attention = new DenseTensor<long>([batch.Length, length]);
            for (var row = 0; row < batch.Length; row++)
            for (var token = 0; token < batch[row].Length; token++)
            {
                inputIds[row, token] = batch[row][token];
                attention[row, token] = 1;
            }
            using var results = RunWithCancellation(session,
                [
                    NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                    NamedOnnxValue.CreateFromTensor("attention_mask", attention)
                ], cancellationToken);
            var output = results.First().AsTensor<float>();
            var dimensions = output.Dimensions.ToArray();
            if (dimensions.Length != 3 || dimensions[0] != batch.Length ||
                dimensions[2] != model.SourceDimensions || model.Dimensions > model.SourceDimensions)
                throw new McpIndexException("model_output_invalid",
                    $"Expected Granite output [batch,tokens,{model.SourceDimensions}], received [{string.Join(',', dimensions)}].");
            for (var row = 0; row < batch.Length; row++)
            {
                var vector = new float[model.Dimensions];
                double norm = 0;
                for (var dimension = 0; dimension < vector.Length; dimension++)
                {
                    vector[dimension] = output[row, 0, dimension];
                    norm += vector[dimension] * vector[dimension];
                }
                if (norm <= double.Epsilon)
                    throw new McpIndexException("model_output_invalid", "Granite produced a zero-length embedding.");
                var divisor = (float)Math.Sqrt(norm);
                for (var dimension = 0; dimension < vector.Length; dimension++) vector[dimension] /= divisor;
                outputVectors.Add(vector);
            }
        }
        return outputVectors;
    }

    private static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunWithCancellation(
        InferenceSession session,
        IReadOnlyCollection<NamedOnnxValue> inputs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var runOptions = new RunOptions();
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((RunOptions)state!).Terminate = true, runOptions);
        try
        {
            return session.Run(inputs, session.OutputMetadata.Keys.ToArray(), runOptions);
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static double Dot(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        double sum = 0;
        for (var index = 0; index < left.Count; index++) sum += left[index] * right[index];
        return sum;
    }
}
