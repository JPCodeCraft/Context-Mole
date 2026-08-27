using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.Intrinsics.X86;
using MCPIndexSearch.Core;

namespace MCPIndexSearch.Infrastructure;

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
    private readonly IGlobalCpuBudget _cpuBudget;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public GraniteModelInstaller(IAppPaths paths, IGlobalCpuBudget cpuBudget)
    {
        _paths = paths;
        _cpuBudget = cpuBudget;
        _client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("MCPIndexSearch/1.0");
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
                return root.TryGetProperty("terms", out var terms) && terms.GetString() == GemmaTermsUrl &&
                       root.TryGetProperty("granite_revision", out var revision) && revision.GetString() == GraniteEmbeddingGenerator.Revision;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
        }
    }

    public async Task<ModelInstallResult> InstallAsync(
        bool gemmaTermsAccepted,
        IProgress<ModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!gemmaTermsAccepted && !HasRecordedTermsAcceptance)
            throw new McpIndexException("terms_not_accepted", "The Gemma terms must be accepted before installing this tokenizer.");
        if (!IsSupported)
            throw new McpIndexException("model_platform_unsupported", "ONNX Runtime 1.29 does not provide an Intel macOS native library.");

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var operationToken = operation.Token;
        await _gate.WaitAsync(operationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var directory = Path.Combine(_paths.AssetsDirectory, "granite", GraniteEmbeddingGenerator.Revision);
            Directory.CreateDirectory(directory);
            var useQuantized = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                System.Runtime.InteropServices.Architecture.X64 && Avx2.IsSupported;
            var assets = BuildAssets(directory, useQuantized);

            foreach (var asset in assets)
            {
                await DownloadVerifiedAsync(asset, progress, operationToken).ConfigureAwait(false);
            }

            GraniteValidationResult? validation = null;
            if (useQuantized)
            {
                progress?.Report(new ModelInstallProgress("validating", "Comparing optimized and full-precision models"));
                using var cpuCapacity = await _cpuBudget.AcquireFullCapacityAsync(operationToken).ConfigureAwait(false);
                validation = await Task.Run(
                    () => GraniteEmbeddingDiagnostics.ValidateProfiles(_paths, cpuCapacity.ThreadCount, operationToken),
                    operationToken).ConfigureAwait(false);
                await WriteValidationAsync(directory, validation, operationToken).ConfigureAwait(false);
            }

            await WriteAcceptanceAsync(directory, assets, operationToken).ConfigureAwait(false);
            progress?.Report(new ModelInstallProgress("complete", "Semantic-search model is ready"));
            return new ModelInstallResult(directory, validation);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<DownloadAsset> BuildAssets(string directory, bool useQuantized)
    {
        var root = $"https://huggingface.co/{GraniteEmbeddingGenerator.ModelId}/resolve/{GraniteEmbeddingGenerator.Revision}";
        var assets = new List<DownloadAsset>
        {
            new("Granite tokenizer", $"{root}/tokenizer.json?download=true", Path.Combine(directory, "tokenizer.json"), GraniteEmbeddingGenerator.TokenizerSha)
        };
        if (useQuantized)
        {
            assets.Add(new DownloadAsset("Granite optimized model", $"{root}/onnx/model_quint8_avx2.onnx?download=true",
                Path.Combine(directory, "model_quint8_avx2.onnx"), GraniteEmbeddingGenerator.QuantizedSha));
        }
        assets.Add(new DownloadAsset("Granite full-precision model", $"{root}/onnx/model.onnx?download=true",
            Path.Combine(directory, "model.onnx"), GraniteEmbeddingGenerator.Fp32Sha));
        return assets;
    }

    private async Task DownloadVerifiedAsync(
        DownloadAsset asset,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(asset.Target))
        {
            progress?.Report(new ModelInstallProgress("verifying", asset.Name));
            if (await HasExpectedHashAsync(asset.Target, asset.Sha256, cancellationToken).ConfigureAwait(false))
            {
                progress?.Report(new ModelInstallProgress("verified", asset.Name));
                return;
            }
        }

        var partial = asset.Target + ".partial";
        if (File.Exists(partial))
        {
            progress?.Report(new ModelInstallProgress("verifying", $"partial {asset.Name}"));
            if (await HasExpectedHashAsync(partial, asset.Sha256, cancellationToken).ConfigureAwait(false))
            {
                File.Move(partial, asset.Target, true);
                progress?.Report(new ModelInstallProgress("verified", asset.Name));
                return;
            }
        }

        var existingLength = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        if (existingLength > 0) request.Headers.Range = new RangeHeaderValue(existingLength, null);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append) existingLength = 0;
        var total = response.Content.Headers.ContentRange?.Length ??
            (response.Content.Headers.ContentLength is { } remaining ? existingLength + remaining : null);
        progress?.Report(new ModelInstallProgress("downloading", asset.Name, existingLength, total));

        await using (var remote = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var local = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None,
                         1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[1024 * 1024];
            var received = existingLength;
            while (true)
            {
                var read = await remote.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                progress?.Report(new ModelInstallProgress("downloading", asset.Name, received, total));
            }
            await local.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new ModelInstallProgress("verifying", asset.Name));
        var actual = await HashAsync(partial, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new McpIndexException("asset_checksum_mismatch",
                $"Checksum verification failed for {asset.Name}. Expected {asset.Sha256}, received {actual}.");
        }

        File.Move(partial, asset.Target, true);
    }

    private async Task WriteAcceptanceAsync(
        string modelDirectory,
        IReadOnlyList<DownloadAsset> assets,
        CancellationToken cancellationToken)
    {
        var acceptance = new
        {
            terms = GemmaTermsUrl,
            accepted_utc = DateTimeOffset.UtcNow,
            model_id = GraniteEmbeddingGenerator.ModelId,
            granite_revision = GraniteEmbeddingGenerator.Revision,
            files = assets.Select(asset => new { name = Path.GetFileName(asset.Target), sha256 = asset.Sha256 }).ToArray()
        };
        await WriteAtomicTextAsync(Path.Combine(_paths.AssetsDirectory, "gemma-terms-acceptance.json"),
            JsonSerializer.Serialize(acceptance, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        await WriteAtomicTextAsync(Path.Combine(_paths.AssetsDirectory, "THIRD-PARTY-NOTICES.txt"),
            "IBM Granite Embedding model: Apache License 2.0.\n" +
            $"The derived tokenizer is subject to the Gemma Terms of Use: {GemmaTermsUrl}\n" +
            $"Model revision: {GraniteEmbeddingGenerator.Revision}\n", cancellationToken).ConfigureAwait(false);
        await WriteAtomicTextAsync(Path.Combine(modelDirectory, "installation-complete"),
            GraniteEmbeddingGenerator.Revision, cancellationToken).ConfigureAwait(false);
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
              "decision": {{JsonSerializer.Serialize(validation.Decision)}}
            }
            """, cancellationToken);

    private static async Task WriteAtomicTextAsync(string target, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".partial";
        await File.WriteAllTextAsync(partial, content, cancellationToken).ConfigureAwait(false);
        File.Move(partial, target, true);
    }

    private static async Task<bool> HasExpectedHashAsync(string path, string expected, CancellationToken cancellationToken) =>
        string.Equals(await HashAsync(path, cancellationToken).ConfigureAwait(false), expected, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
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

    private sealed record DownloadAsset(string Name, string Url, string Target, string Sha256);
}
