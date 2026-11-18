using System.Globalization;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;

using ContextMole.Core;

namespace ContextMole.Infrastructure;

public sealed record ModelInstallProgress(
    string Stage,
    string AssetName,
    long BytesReceived = 0,
    long? TotalBytes = null)
{
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : null;
}

public sealed record ModelInstallResult(
    string ModelDirectory,
    GraniteValidationResult? Validation);

public sealed class GraniteModelInstaller : IDisposable
{
    public const string GemmaTermsUrl = "https://ai.google.dev/gemma/terms";
    private readonly IAppPaths _paths;
    private readonly IEmbeddingModelSettings _modelSettings;
    private readonly IGlobalCpuBudget _cpuBudget;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public GraniteModelInstaller(
        IAppPaths paths,
        IEmbeddingModelSettings modelSettings,
        IGlobalCpuBudget cpuBudget)
    {
        _paths = paths;
        _modelSettings = modelSettings;
        _cpuBudget = cpuBudget;
        _client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("ContextMole/1.0");
    }

    public bool IsSupported => !(OperatingSystem.IsMacOS() &&
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64);

    public bool HasRecordedTermsAcceptance
    {
        get
        {
            var path = Path.Combine(_paths.AssetsDirectory, "gemma-terms-acceptance.json");
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                return root.TryGetProperty("terms", out var terms) && terms.GetString() == GemmaTermsUrl;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
        }
    }

    public bool IsModelInstalled(EmbeddingModelChoice choice)
    {
        if (!GraniteEmbeddingModels.IsSupported(choice)) return false;
        var model = GraniteEmbeddingModels.Get(choice);
        var directory = GraniteModelInstallation.GetDirectory(_paths, model);
        var useQuantized = GraniteEmbeddingProfiles.UseQuantized(directory, model);
        var modelPath = Path.Combine(directory, useQuantized ? "model_quint8_avx2.onnx" : "model.onnx");
        return File.Exists(Path.Combine(directory, "tokenizer.json")) && File.Exists(modelPath) &&
               GraniteModelInstallation.IsComplete(_paths, model) &&
               (!useQuantized || File.Exists(Path.Combine(directory, "validation.json")));
    }

    public bool HasModelAssets(EmbeddingModelChoice choice)
    {
        if (!GraniteEmbeddingModels.IsSupported(choice)) return false;
        var model = GraniteEmbeddingModels.Get(choice);
        var directory = GraniteModelInstallation.GetDirectory(_paths, model);
        var useQuantized = GraniteEmbeddingProfiles.UseQuantized(directory, model);
        var modelPath = Path.Combine(directory, useQuantized ? "model_quint8_avx2.onnx" : "model.onnx");
        return File.Exists(Path.Combine(directory, "tokenizer.json")) && File.Exists(modelPath);
    }

    public bool NeedsTokenizationValidation(EmbeddingModelChoice choice)
    {
        if (!GraniteEmbeddingModels.IsSupported(choice)) return false;
        var model = GraniteEmbeddingModels.Get(choice);
        return GraniteEmbeddingProfiles.NeedsTokenizationValidation(
            GraniteModelInstallation.GetDirectory(_paths, model), model);
    }

    public void MarkModelForRepair(EmbeddingModelChoice choice, string reason) =>
        GraniteModelInstallation.MarkForRepair(_paths, GraniteEmbeddingModels.Get(choice), reason);

    public Task<ModelInstallResult> InstallAsync(
        bool gemmaTermsAccepted,
        IProgress<ModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        InstallAsync(_modelSettings.Model, gemmaTermsAccepted, progress, cancellationToken);

    public async Task<ModelInstallResult> InstallAsync(
        EmbeddingModelChoice choice,
        bool gemmaTermsAccepted,
        IProgress<ModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!GraniteEmbeddingModels.IsSupported(choice))
            throw new ContextMoleException("model_not_supported", "Only Granite Multilingual 97M can be installed.");
        var model = GraniteEmbeddingModels.Get(choice);
        if (model.RequiresGemmaTerms && !gemmaTermsAccepted && !HasRecordedTermsAcceptance)
            throw new ContextMoleException("terms_not_accepted", "The Gemma terms must be accepted before installing this tokenizer.");
        if (!IsSupported)
            throw new ContextMoleException("model_platform_unsupported", "ONNX Runtime 1.29 does not provide an Intel macOS native library.");

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var operationToken = operation.Token;
        await _gate.WaitAsync(operationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var directory = GraniteModelInstallation.GetDirectory(_paths, model);
            Directory.CreateDirectory(directory);
            var useQuantized = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                System.Runtime.InteropServices.Architecture.X64 && Avx2.IsSupported;
            var assets = BuildAssets(model, directory, useQuantized);

            foreach (var asset in assets)
            {
                await DownloadVerifiedAsync(asset, progress, operationToken).ConfigureAwait(false);
            }

            GraniteValidationResult? validation = null;
            if (useQuantized)
            {
                progress?.Report(new ModelInstallProgress("waiting_cpu", "Waiting for processor capacity"));
                using var cpuCapacity = await _cpuBudget.AcquireFullCapacityAsync(operationToken).ConfigureAwait(false);
                progress?.Report(new ModelInstallProgress("validating", "Comparing optimized and full-precision models"));
                validation = await Task.Run(
                    () => GraniteEmbeddingDiagnostics.ValidateProfiles(_paths, model, cpuCapacity.ThreadCount, operationToken),
                    operationToken).ConfigureAwait(false);
                await WriteValidationAsync(directory, validation, operationToken).ConfigureAwait(false);
            }

            await WriteInstallationMetadataAsync(model, directory, assets, operationToken).ConfigureAwait(false);
            progress?.Report(new ModelInstallProgress("complete", $"{model.DisplayName} is ready"));
            return new ModelInstallResult(directory, validation);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<ModelAsset> BuildAssets(
        GraniteEmbeddingModelDefinition model,
        string directory,
        bool useQuantized)
    {
        var root = $"https://huggingface.co/{model.ModelId}/resolve/{model.Revision}";
        var assets = new List<ModelAsset>
        {
            new($"{model.DisplayName} tokenizer", $"{root}/tokenizer.json?download=true",
                Path.Combine(directory, "tokenizer.json"), model.TokenizerSha)
        };
        if (useQuantized)
        {
            assets.Add(new ModelAsset($"{model.DisplayName} optimized model",
                $"{root}/onnx/model_quint8_avx2.onnx?download=true",
                Path.Combine(directory, "model_quint8_avx2.onnx"), model.QuantizedSha));
        }
        assets.Add(new ModelAsset($"{model.DisplayName} full-precision model", $"{root}/onnx/model.onnx?download=true",
            Path.Combine(directory, "model.onnx"), model.Fp32Sha));
        return assets;
    }

    private Task DownloadVerifiedAsync(
        ModelAsset asset,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken) =>
        ModelAssetDownload.DownloadVerifiedAsync(_client, asset, progress is null ? null : new InlineProgress(update =>
            progress.Report(update.Stage switch
            {
                ModelAssetDownloadStage.CheckingExisting or ModelAssetDownloadStage.VerifyingDownload =>
                    new ModelInstallProgress("verifying", update.Asset.Name),
                ModelAssetDownloadStage.CheckingPartial => new ModelInstallProgress("verifying", $"partial {update.Asset.Name}"),
                ModelAssetDownloadStage.Verified => new ModelInstallProgress("verified", update.Asset.Name),
                ModelAssetDownloadStage.Restarting =>
                    new ModelInstallProgress("downloading", $"Restarting {update.Asset.Name} from the beginning"),
                _ => new ModelInstallProgress("downloading", update.Asset.Name, update.BytesReceived, update.TotalBytes)
            })), ModelAssetDownload.DefaultStallTimeout, cancellationToken);

    private async Task WriteInstallationMetadataAsync(
        GraniteEmbeddingModelDefinition model,
        string modelDirectory,
        IReadOnlyList<ModelAsset> assets,
        CancellationToken cancellationToken)
    {
        if (model.RequiresGemmaTerms)
        {
            var acceptance = new
            {
                terms = GemmaTermsUrl,
                accepted_utc = DateTimeOffset.UtcNow,
                model_id = model.ModelId,
                granite_revision = model.Revision,
                files = assets.Select(asset => new { name = Path.GetFileName(asset.Target), sha256 = asset.Sha256 }).ToArray()
            };
            await WriteAtomicTextAsync(Path.Combine(_paths.AssetsDirectory, "gemma-terms-acceptance.json"),
                JsonSerializer.Serialize(acceptance, new JsonSerializerOptions { WriteIndented = true }), cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteAtomicTextAsync(Path.Combine(_paths.AssetsDirectory, "THIRD-PARTY-NOTICES.txt"),
            "IBM Granite Embedding 97M: Apache License 2.0.\n" +
            $"Any retained legacy Granite 311M tokenizer files remain subject to the Gemma Terms of Use: {GemmaTermsUrl}\n",
            cancellationToken).ConfigureAwait(false);
        await WriteAtomicTextAsync(Path.Combine(modelDirectory, GraniteModelInstallation.CompletionMarker),
            model.Revision, cancellationToken).ConfigureAwait(false);
        var repairMarker = Path.Combine(modelDirectory, GraniteModelInstallation.RepairMarker);
        if (File.Exists(repairMarker)) File.Delete(repairMarker);
    }

    private static Task WriteValidationAsync(string directory, GraniteValidationResult validation, CancellationToken cancellationToken) =>
        WriteAtomicTextAsync(Path.Combine(directory, "validation.json"),
            $$"""
            {
              "quantized_enabled": {{validation.QuantizedEnabled.ToString().ToLowerInvariant()}},
              "mean_corresponding_cosine": {{validation.MeanCorrespondingCosine.ToString("R", CultureInfo.InvariantCulture)}},
              "mean_top_10_overlap": {{validation.MeanTop10Overlap.ToString("R", CultureInfo.InvariantCulture)}},
              "quantized_milliseconds_per_vector": {{validation.QuantizedMillisecondsPerVector.ToString("R", CultureInfo.InvariantCulture)}},
              "fp32_milliseconds_per_vector": {{validation.Fp32MillisecondsPerVector.ToString("R", CultureInfo.InvariantCulture)}},
              "peak_working_set_bytes": {{validation.PeakWorkingSetBytes}},
              "decision": {{JsonSerializer.Serialize(validation.Decision)}},
              "tokenization_version": {{JsonSerializer.Serialize(validation.TokenizationVersion)}}
            }
            """, cancellationToken);

    private static async Task WriteAtomicTextAsync(string target, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".partial";
        await File.WriteAllTextAsync(partial, content, cancellationToken).ConfigureAwait(false);
        File.Move(partial, target, true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _gate.Wait();
        try
        {
            _client.Dispose();
        }
        finally
        {
            _gate.Release();
            _lifetime.Dispose();
        }
    }

    /// <summary>Reports synchronously, so progress arrives in order on the downloading thread.</summary>
    private sealed class InlineProgress(Action<ModelAssetDownloadProgress> report) : IProgress<ModelAssetDownloadProgress>
    {
        public void Report(ModelAssetDownloadProgress value) => report(value);
    }
}
