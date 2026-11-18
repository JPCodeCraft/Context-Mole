using System.Net;
using System.Security.Cryptography;

using ContextMole.Core;
using ContextMole.Infrastructure;

namespace ContextMole.Tests;

/// <summary>
/// Issue #1: indexing sat in "Waiting for CPU or memory" with an idle machine. Files that need OCR kept their CPU
/// slot while the OCR model download ran, and a download that stopped receiving data waited for hours.
/// </summary>
public sealed class ModelSetupStallTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SuspendedWorkerGivesItsSlotAwayAndResumesAheadOfNewWork()
    {
        using var cpu = new GlobalCpuBudget(new SingleSlotCpuSettings());
        using var first = await cpu.AcquireWorkerAsync(Token);
        var second = cpu.AcquireWorkerAsync(Token).AsTask();
        Assert.False(second.IsCompleted);

        ICpuWorkerLease secondLease;
        Task<ICpuWorkerLease> third;
        Task resumed;
        using (first.Activate())
        {
            var suspension = cpu.SuspendWorker();
            secondLease = await second.WaitAsync(TimeSpan.FromSeconds(5), Token);
            third = cpu.AcquireWorkerAsync(Token).AsTask();
            resumed = suspension.DisposeAsync().AsTask();
        }

        Assert.False(resumed.IsCompleted);
        secondLease.Dispose();
        await resumed.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(third.IsCompleted);
        first.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5), Token)).Dispose();
    }

    [Fact]
    public async Task SuspendingWithoutAnActiveWorkerDoesNothing()
    {
        using var cpu = new GlobalCpuBudget(new SingleSlotCpuSettings());
        using var held = await cpu.AcquireWorkerAsync(Token);
        await using (cpu.SuspendWorker())
        {
            var waiting = cpu.AcquireWorkerAsync(Token).AsTask();
            await Task.Delay(50, Token);
            Assert.False(waiting.IsCompleted);
            held.Dispose();
            (await waiting.WaitAsync(TimeSpan.FromSeconds(5), Token)).Dispose();
        }
    }

    [Fact]
    public async Task OcrWaitingForItsModelDownloadLetsOtherFilesUseTheCpuSlot()
    {
        using var paths = new TemporaryAppPaths();
        var settings = new SingleSlotCpuSettings();
        using var cpu = new GlobalCpuBudget(settings);
        using var handler = new ScriptedHandler((_, token) => StalledHeadersAsync(token));
        using var engine = new PpOcrV6Engine(paths, settings, cpu, PpOcrV6Engine.DefaultSessionIdleTimeout, handler,
            TimeSpan.FromMinutes(5), PpOcrV6Engine.DefaultSetupRetryDelay);
        using var stopOcr = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var ocrFile = Task.Run(async () =>
        {
            using var worker = await cpu.AcquireWorkerAsync(Token);
            using var active = worker.Activate();
            await engine.RecognizeAsync(new OcrRequest(ReadOnlyMemory<byte>.Empty, ".png", TimeSpan.FromSeconds(5)),
                stopOcr.Token);
        }, Token);
        await handler.FirstRequest.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // The download is stuck, but a file that needs no OCR still gets the only CPU slot.
        using (await cpu.AcquireWorkerAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token))
        {
        }

        await stopOcr.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ocrFile.WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    [Fact]
    public async Task OcrRequestsWaitingForAFailedDownloadDoNotStartItAgain()
    {
        using var paths = new TemporaryAppPaths();
        var settings = new SingleSlotCpuSettings();
        using var cpu = new GlobalCpuBudget(settings);
        using var handler = new ScriptedHandler((_, token) => StalledHeadersAsync(token));
        using var engine = new PpOcrV6Engine(paths, settings, cpu, PpOcrV6Engine.DefaultSessionIdleTimeout, handler,
            TimeSpan.FromMilliseconds(300), PpOcrV6Engine.DefaultSetupRetryDelay);

        // Settings starts the download; two OCR pages queue behind it.
        var setup = engine.PrepareAssetsAsync(Token);
        await handler.FirstRequest.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var pages = Enumerable.Range(0, 2).Select(_ => engine.RecognizeAsync(
            new OcrRequest(ReadOnlyMemory<byte>.Empty, ".png", TimeSpan.FromSeconds(5)), Token)).ToArray();

        var setupFailure = await Assert.ThrowsAsync<ContextMoleException>(() => setup.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Equal("model_download_stalled", setupFailure.Code);
        foreach (var page in pages)
        {
            var failure = await Assert.ThrowsAsync<ContextMoleException>(() => page.WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.Equal("ocr_setup_failed", failure.Code);
            Assert.True(failure.Retryable);
        }

        // A later page fails at once too, while an explicit setup from Settings still tries again.
        var later = await Assert.ThrowsAsync<ContextMoleException>(() => engine.RecognizeAsync(
            new OcrRequest(ReadOnlyMemory<byte>.Empty, ".png", TimeSpan.FromSeconds(5)), Token));
        Assert.Equal("ocr_setup_failed", later.Code);
        Assert.Equal(1, handler.RequestCount);
        await Assert.ThrowsAsync<ContextMoleException>(() => engine.PrepareAssetsAsync(Token));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadThatStopsReceivingDataFailsAndResumesFromTheBytesItKept()
    {
        using var directory = new TemporaryDirectory();
        var content = RandomNumberGenerator.GetBytes(64 * 1024);
        var asset = Asset(directory.Path, content);
        using var stalling = new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream(content.AsMemory(0, 1000).ToArray()))
            {
                Headers = { ContentLength = content.Length }
            }
        }));
        using (var client = new HttpClient(stalling, disposeHandler: false))
        {
            var failure = await Assert.ThrowsAsync<ContextMoleException>(() => ModelAssetDownload.DownloadVerifiedAsync(
                client, asset, null, TimeSpan.FromMilliseconds(300), Token).WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.Equal("model_download_stalled", failure.Code);
            Assert.True(failure.Retryable);
        }
        Assert.Equal(1000, new FileInfo(asset.Target + ".partial").Length);

        using var resuming = new ScriptedHandler((request, _) =>
        {
            var from = (int)request.Headers.Range!.Ranges.Single().From!.Value;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content[from..])
            });
        });
        using (var client = new HttpClient(resuming, disposeHandler: false))
            await ModelAssetDownload.DownloadVerifiedAsync(client, asset, null, TimeSpan.FromSeconds(5), Token);
        Assert.Equal(content, await File.ReadAllBytesAsync(asset.Target, Token));
        Assert.False(File.Exists(asset.Target + ".partial"));
    }

    [Fact]
    public async Task ServerThatNeverAnswersFailsAsAStalledDownload()
    {
        using var directory = new TemporaryDirectory();
        var asset = Asset(directory.Path, [1, 2, 3]);
        using var handler = new ScriptedHandler((_, token) => StalledHeadersAsync(token));
        using var client = new HttpClient(handler, disposeHandler: false);

        var failure = await Assert.ThrowsAsync<ContextMoleException>(() => ModelAssetDownload.DownloadVerifiedAsync(
            client, asset, null, TimeSpan.FromMilliseconds(200), Token).WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Equal("model_download_stalled", failure.Code);
    }

    [Fact]
    public async Task RefusedResumeStartsTheDownloadAgain()
    {
        using var directory = new TemporaryDirectory();
        var content = RandomNumberGenerator.GetBytes(4096);
        var asset = Asset(directory.Path, content);
        await File.WriteAllBytesAsync(asset.Target + ".partial", new byte[content.Length], Token);
        var stages = new List<ModelAssetDownloadStage>();
        using var handler = new ScriptedHandler((request, _) => Task.FromResult(request.Headers.Range is null
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
            : new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)));
        using var client = new HttpClient(handler, disposeHandler: false);

        await ModelAssetDownload.DownloadVerifiedAsync(client, asset, new InlineProgress(update => stages.Add(update.Stage)),
            TimeSpan.FromSeconds(5), Token);

        Assert.Equal(content, await File.ReadAllBytesAsync(asset.Target, Token));
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains(ModelAssetDownloadStage.Restarting, stages);
    }

    private static ModelAsset Asset(string directory, byte[] content) => new("test model",
        "https://models.invalid/model.onnx", Path.Combine(directory, "model.onnx"),
        Convert.ToHexStringLower(SHA256.HashData(content)));

    private static async Task<HttpResponseMessage> StalledHeadersAsync(CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("Unreachable.");
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public Task FirstRequest => _firstRequest.Task;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            _firstRequest.TrySetResult();
            return respond(request, cancellationToken);
        }
    }

    /// <summary>Returns its prefix, then never sends another byte until the read is canceled.</summary>
    private sealed class StallingStream(byte[] prefix) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class InlineProgress(Action<ModelAssetDownloadProgress> report) : IProgress<ModelAssetDownloadProgress>
    {
        public void Report(ModelAssetDownloadProgress value) => report(value);
    }

    /// <summary>One CPU slot, as on a small machine with the Normal profile.</summary>
    private sealed class SingleSlotCpuSettings : ICpuUsageSettings
    {
        public CpuUsageProfile Profile => CpuUsageProfile.Normal;
        public int LogicalProcessorCount => 2;
        public int ThreadLimit => 1;
        public int MaximumThreadLimit => 1;
        public void SetProfile(CpuUsageProfile profile) => throw new NotSupportedException();
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ContextMole.Tests",
            Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class TemporaryAppPaths : IAppPaths, IDisposable
    {
        private readonly TemporaryDirectory _root = new();

        public TemporaryAppPaths()
        {
            DataDirectory = Path.Combine(_root.Path, "data");
            DatabasePath = Path.Combine(DataDirectory, "index.db");
            AssetsDirectory = Path.Combine(_root.Path, "assets");
            LogsDirectory = Path.Combine(_root.Path, "logs");
            TempDirectory = Path.Combine(_root.Path, "temp");
            Directory.CreateDirectory(DataDirectory);
        }

        public string DataDirectory { get; }
        public string DatabasePath { get; }
        public string AssetsDirectory { get; }
        public string LogsDirectory { get; }
        public string TempDirectory { get; }
        public void Dispose() => _root.Dispose();
    }
}
