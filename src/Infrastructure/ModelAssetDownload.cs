using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

using ContextMole.Core;

namespace ContextMole.Infrastructure;

/// <summary>One pinned model file: where it comes from, where it is kept, and the SHA-256 it must match.</summary>
internal sealed record ModelAsset(string Name, string Url, string Target, string Sha256);

internal enum ModelAssetDownloadStage
{
    /// <summary>An existing complete file is being checked against its pinned checksum.</summary>
    CheckingExisting,
    /// <summary>An existing partial download is being checked, in case it already holds the whole file.</summary>
    CheckingPartial,
    /// <summary>An existing complete or partial file matched; nothing is downloaded.</summary>
    Verified,
    /// <summary>The server refused to resume a partial download, so it starts again from the first byte.</summary>
    Restarting,
    Downloading,
    /// <summary>The downloaded bytes are being checked before they replace the target.</summary>
    VerifyingDownload
}

internal readonly record struct ModelAssetDownloadProgress(
    ModelAssetDownloadStage Stage,
    ModelAsset Asset,
    long BytesReceived = 0,
    long? TotalBytes = null);

/// <summary>
/// Downloads pinned model files for OCR and semantic search: resumes partial files, checks every file against
/// its pinned checksum, and fails a transfer that stops receiving data instead of waiting on it for hours.
/// </summary>
internal static class ModelAssetDownload
{
    /// <summary>How long a connection may go without response headers or new bytes before the download fails.</summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromMinutes(2);
    private const int BufferSize = 1024 * 1024;

    public static async Task DownloadVerifiedAsync(HttpClient client, ModelAsset asset,
        IProgress<ModelAssetDownloadProgress>? progress, TimeSpan stallTimeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stallTimeout, TimeSpan.Zero);
        if (File.Exists(asset.Target))
        {
            progress?.Report(new(ModelAssetDownloadStage.CheckingExisting, asset));
            if (await HasExpectedHashAsync(asset.Target, asset.Sha256, cancellationToken).ConfigureAwait(false))
            {
                progress?.Report(new(ModelAssetDownloadStage.Verified, asset));
                return;
            }
        }

        var partial = asset.Target + ".partial";
        if (File.Exists(partial))
        {
            progress?.Report(new(ModelAssetDownloadStage.CheckingPartial, asset));
            if (await HasExpectedHashAsync(partial, asset.Sha256, cancellationToken).ConfigureAwait(false))
            {
                File.Move(partial, asset.Target, true);
                progress?.Report(new(ModelAssetDownloadStage.Verified, asset));
                return;
            }
        }

        if (!await TryDownloadAsync(client, asset, partial, progress, stallTimeout, cancellationToken)
                .ConfigureAwait(false))
        {
            // A refused range deleted the partial file, so this attempt starts at the first byte.
            progress?.Report(new(ModelAssetDownloadStage.Restarting, asset));
            await TryDownloadAsync(client, asset, partial, progress, stallTimeout, cancellationToken)
                .ConfigureAwait(false);
        }

        progress?.Report(new(ModelAssetDownloadStage.VerifyingDownload, asset));
        var actual = await HashAsync(partial, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new ContextMoleException("asset_checksum_mismatch",
                $"Checksum verification failed for {asset.Name}. Expected {asset.Sha256}, received {actual}.");
        }

        File.Move(partial, asset.Target, true);
    }

    /// <summary>
    /// Writes the remaining bytes of <paramref name="asset"/> to <paramref name="partial"/>. Returns false, after
    /// deleting the partial file, when the server refuses to resume it.
    /// </summary>
    private static async Task<bool> TryDownloadAsync(HttpClient client, ModelAsset asset, string partial,
        IProgress<ModelAssetDownloadProgress>? progress, TimeSpan stallTimeout, CancellationToken cancellationToken)
    {
        var existingLength = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        if (existingLength > 0) request.Headers.Range = new RangeHeaderValue(existingLength, null);
        stall.CancelAfter(stallTimeout);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsStall(stall, cancellationToken))
        {
            throw Stalled(asset, stallTimeout);
        }

        using (response)
        {
            if (existingLength > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(partial);
                return false;
            }
            response.EnsureSuccessStatusCode();

            var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!append) existingLength = 0;
            var total = response.Content.Headers.ContentRange?.Length ??
                (response.Content.Headers.ContentLength is { } remaining ? existingLength + remaining : null);
            progress?.Report(new(ModelAssetDownloadStage.Downloading, asset, existingLength, total));

            try
            {
                await using var remote = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                await using var local = new FileStream(partial, append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[BufferSize];
                var received = existingLength;
                while (true)
                {
                    // Every read gets a fresh window: a slow transfer is fine, a silent one is not.
                    stall.CancelAfter(stallTimeout);
                    var read = await remote.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new(ModelAssetDownloadStage.Downloading, asset, received, total));
                }
                await local.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException &&
                                              IsStall(stall, cancellationToken))
            {
                // The bytes received so far stay in the partial file, so the next attempt resumes from them.
                throw Stalled(asset, stallTimeout);
            }
        }
        return true;
    }

    private static bool IsStall(CancellationTokenSource stall, CancellationToken cancellationToken) =>
        stall.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

    private static ContextMoleException Stalled(ModelAsset asset, TimeSpan stallTimeout) => new("model_download_stalled",
        $"Downloading {asset.Name} stopped: no data arrived for {FormatDuration(stallTimeout)}. " +
        "Check the internet connection; the download resumes where it stopped on the next attempt.", true);

    private static string FormatDuration(TimeSpan duration) => duration.TotalMinutes >= 1
        ? $"{duration.TotalMinutes:0.#} minute{(duration.TotalMinutes == 1 ? string.Empty : "s")}"
        : $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds))} seconds";

    public static async Task<bool> HasExpectedHashAsync(string path, string expected, CancellationToken cancellationToken) =>
        string.Equals(await HashAsync(path, cancellationToken).ConfigureAwait(false), expected, StringComparison.OrdinalIgnoreCase);

    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
