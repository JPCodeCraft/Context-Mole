using System.Text;

using BitMiracle.LibTiff.Classic;

using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Infrastructure;

using Microsoft.ML.OnnxRuntime.Tensors;

using PDFtoImage;

using SkiaSharp;

namespace ContextMole.Tests;

public sealed class OcrExtractionRegressionTests
{
    [Fact]
    public void RawRasterPreservesColorsStridesAndSlicedMemory()
    {
        byte[] pixels =
        [
            99, 99, 99, 99,
            0, 0, 255, 255, 0, 255, 0, 255, 88, 88, 88, 88,
            255, 0, 0, 255, 255, 255, 255, 255, 77, 77, 77, 77
        ];
        using var bitmap = PpOcrV6Engine.DecodeImage(new OcrRequest(pixels.AsMemory(4), ".bgra",
            TimeSpan.FromSeconds(5), new OcrRasterInfo(2, 2, 12)));

        // The bitmap must retain its managed memory pin across a collection until disposal.
        GC.Collect();
        Assert.Equal(SKColors.Red, bitmap.GetPixel(0, 0));
        Assert.Equal(SKColors.Lime, bitmap.GetPixel(1, 0));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(0, 1));
        Assert.Equal(SKColors.White, bitmap.GetPixel(1, 1));
    }

    [Theory]
    [InlineData(0, 2, 8, 16)]
    [InlineData(2, 2, 7, 16)]
    [InlineData(2, 2, 8, 15)]
    [InlineData(5001, 5000, 20004, 16)]
    public void InvalidRawRasterIsRejectedBeforeReadingPixels(int width, int height, int stride, int length)
    {
        var error = Assert.Throws<ContextMoleException>(() => PpOcrV6Engine.DecodeImage(
            new OcrRequest(new byte[length], ".bgra", TimeSpan.FromSeconds(5),
                new OcrRasterInfo(width, height, stride))));
        Assert.Equal("ocr_image_invalid", error.Code);
    }

    [Fact]
    public void ReusedRecognitionBufferMatchesFreshInputIncludingPadding()
    {
        using var wide = new SKBitmap(320, 48);
        wide.Erase(SKColors.Black);
        using var narrow = new SKBitmap(10, 48);
        narrow.Erase(SKColors.White);
        var reused = new DenseTensor<float>([1, 3, 48, 320]);
        var fresh = new DenseTensor<float>([1, 3, 48, 320]);

        PpOcrV6Engine.FillRecognitionTensor(wide, reused);
        PpOcrV6Engine.FillRecognitionTensor(narrow, reused);
        PpOcrV6Engine.FillRecognitionTensor(narrow, fresh);

        Assert.Equal(fresh.Buffer.ToArray(), reused.Buffer.ToArray());
        Assert.Equal(1f, reused[0, 0, 0, 0]);
        Assert.Equal(0f, reused[0, 0, 0, 10]);
    }

    [Fact]
    public void RecognitionDecodingPreservesCtcDuplicatesBlanksAndConfidence()
    {
        var result = PpOcrV6Engine.DecodeRecognition(
        [
            0.1f, 0.8f, 0.1f,
            0.05f, 0.9f, 0.05f,
            0.7f, 0.2f, 0.1f,
            0.2f, 0.6f, 0.2f,
            0.1f, 0.1f, 0.8f,
            0.05f, 0.05f, 0.9f,
            0.8f, 0.1f, 0.1f
        ], 7, ["blank", "a", "b"]);

        Assert.Equal("aab", result.Text);
        Assert.Equal(73.333333, result.Confidence, precision: 4);
    }

    [Theory]
    [InlineData(100, 48, 320)]
    [InlineData(420, 25, 807)]
    [InlineData(738, 28, 1266)]
    [InlineData(10000, 3, 4096)]
    public void RecognitionWidthPreservesLongLinesWithinBoundedMemory(int width, int height, int expectedWidth)
    {
        Assert.Equal(expectedWidth, PpOcrV6Engine.GetRecognitionWidth(width, height));
    }

    [Fact]
    public void LongRecognitionInputRetainsPixelsBeyondTheOldWidthLimit()
    {
        using var bitmap = new SKBitmap(800, 48);
        bitmap.Erase(SKColors.Black);
        bitmap.SetPixel(700, 24, SKColors.White);
        var tensor = new DenseTensor<float>([1, 3, 48, PpOcrV6Engine.GetRecognitionWidth(bitmap.Width, bitmap.Height)]);

        PpOcrV6Engine.FillRecognitionTensor(bitmap, tensor);

        Assert.Equal(800, tensor.Dimensions[3]);
        Assert.Equal(1f, tensor[0, 0, 24, 700]);
        Assert.Equal(-1f, tensor[0, 0, 24, 699]);
        Assert.Equal(-1f, tensor[0, 0, 24, 701]);
    }

    [Theory]
    [InlineData(960, 320)]
    [InlineData(320, 960)]
    public void SharedRecognitionBufferMatchesFreshTensorWhenWidthChanges(int previousWidth, int currentWidth)
    {
        var buffer = new float[3 * 48 * Math.Max(previousWidth, currentWidth)];
        using var previous = new SKBitmap(previousWidth, 48);
        previous.Erase(SKColors.Black);
        var previousTensor = new DenseTensor<float>(buffer.AsMemory(0, 3 * 48 * previousWidth), [1, 3, 48, previousWidth]);
        PpOcrV6Engine.FillRecognitionTensor(previous, previousTensor);
        using var current = new SKBitmap(currentWidth == 320 ? 100 : currentWidth, 48);
        current.Erase(SKColors.White);
        var reused = new DenseTensor<float>(buffer.AsMemory(0, 3 * 48 * currentWidth), [1, 3, 48, currentWidth]);
        var fresh = new DenseTensor<float>([1, 3, 48, currentWidth]);

        PpOcrV6Engine.FillRecognitionTensor(current, reused);
        PpOcrV6Engine.FillRecognitionTensor(current, fresh);

        Assert.Equal(fresh.Buffer.ToArray(), reused.Buffer.ToArray());
    }

    [Fact]
    public async Task PdfUsesLosslessFullResolutionPixelsAndKeepsPageOrder()
    {
        if (!DesktopRenderingSupported()) return;
        using var workspace = new TemporaryDirectory();
        var path = workspace.File("mixed.pdf");
        const string native = "This native page contains enough searchable words and letters to remain available without requiring any image recognition at all.";
        var pdf = PdfBytes("First", native, "Third");
        await File.WriteAllBytesAsync(path, pdf, TestContext.Current.CancellationToken);
        var engine = new RecordingOcrEngine((_, index) => new OcrResult($"OCR page {index + 1}", 95));

        var result = await new DocumentExtractionRegistry(engine).ExtractAsync(new ExtractionRequest(path),
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Errors);
        Assert.Equal(2, engine.Requests.Count);
        Assert.Equal(new int?[] { 1, 2, 3 }, result.Root.Sections.Select(section => section.Location.Page));
        Assert.Equal(ExtractionMethod.NativeText, result.Root.Sections[1].Method);
        var request = engine.Requests[0];
        var raster = Assert.IsType<OcrRasterInfo>(request.Raster);
        Assert.Equal(300, raster.Width);
        Assert.Equal(300, raster.Height);
#pragma warning disable CA1416 // Runtime guard above limits this fixture to desktop renderers.
        using var rendered = Conversion.ToImage(pdf, 0, options: new RenderOptions { Dpi = 300 });
#pragma warning restore CA1416
        using var image = SKImage.FromBitmap(rendered);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var previousInput = PpOcrV6Engine.DecodeImage(new OcrRequest(png.ToArray(), ".png", request.Timeout));
        using var directInput = PpOcrV6Engine.DecodeImage(request);
        Assert.Equal(previousInput.Pixels, directInput.Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SparsePdfKeepsNativeTextWhenOcrReturnsNoText(bool timedOut)
    {
        if (!DesktopRenderingSupported()) return;
        using var workspace = new TemporaryDirectory();
        var path = workspace.File("sparse.pdf");
        await File.WriteAllBytesAsync(path, PdfBytes("Sparse evidence"), TestContext.Current.CancellationToken);
        var engine = new RecordingOcrEngine((_, _) => new OcrResult(string.Empty, null, TimedOut: timedOut));

        var result = await new DocumentExtractionRegistry(engine).ExtractAsync(new ExtractionRequest(path),
            TestContext.Current.CancellationToken);

        Assert.Contains("Sparse evidence", Assert.Single(result.Root.Sections).Text);
        if (timedOut)
        {
            var error = Assert.Single(result.Errors);
            Assert.Equal("ocr_timeout", error.Code);
            Assert.True(error.Retryable);
        }
        else Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ImageTimeoutIsReportedAsRetryable()
    {
        using var workspace = new TemporaryDirectory();
        var path = workspace.File("timeout.png");
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.White);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, png.ToArray(), TestContext.Current.CancellationToken);
        var engine = new RecordingOcrEngine((_, _) => new OcrResult(string.Empty, null, TimedOut: true));

        var result = await new DocumentExtractionRegistry(engine).ExtractAsync(new ExtractionRequest(path),
            TestContext.Current.CancellationToken);

        var error = Assert.Single(result.Errors);
        Assert.Equal("ocr_timeout", error.Code);
        Assert.True(error.Retryable);
        Assert.Empty(result.Root.Sections);
    }

    [Fact]
    public async Task TiffTimeoutKeepsOtherFramesAndPreservesRgbaColors()
    {
        using var workspace = new TemporaryDirectory();
        var path = workspace.File("frames.tiff");
        using (var tiff = Tiff.Open(path, "w"))
        {
            for (var frame = 0; frame < 2; frame++)
            {
                tiff.SetField(TiffTag.IMAGEWIDTH, 2);
                tiff.SetField(TiffTag.IMAGELENGTH, 1);
                tiff.SetField(TiffTag.SAMPLESPERPIXEL, 3);
                tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
                tiff.SetField(TiffTag.ROWSPERSTRIP, 1);
                tiff.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
                tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
                tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
                tiff.WriteScanline([255, 0, 0, 0, 0, 255], 0);
                tiff.WriteDirectory();
            }
        }
        var engine = new RecordingOcrEngine((_, index) => index == 0
            ? new OcrResult(string.Empty, null, TimedOut: true)
            : new OcrResult("Second frame evidence", 99));

        var result = await new DocumentExtractionRegistry(engine).ExtractAsync(new ExtractionRequest(path),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, engine.Requests.Count);
        var error = Assert.Single(result.Errors);
        Assert.Equal("ocr_timeout", error.Code);
        Assert.True(error.Retryable);
        Assert.Equal(2, Assert.Single(result.Root.Sections).Location.ImageFrame);
        using var firstFrame = PpOcrV6Engine.DecodeImage(engine.Requests[0]);
        Assert.Equal(SKColors.Red, firstFrame.GetPixel(0, 0));
        Assert.Equal(SKColors.Blue, firstFrame.GetPixel(1, 0));
    }

    private static bool DesktopRenderingSupported() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static byte[] PdfBytes(params string[] pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Count {pages.Length} /Kids [{string.Join(' ', Enumerable.Range(0, pages.Length).Select(index => $"{4 + index * 2} 0 R"))}] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        foreach (var text in pages)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Resources << /Font << /F1 3 0 R >> >> /Contents {objects.Count + 2} 0 R >>");
            var commands = $"BT /F1 8 Tf 5 36 Td ({text}) Tj ET";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(commands)} >>\nstream\n{commands}\nendstream");
        }
        var document = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(document.Length);
            document.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = document.Length;
        document.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) document.Append($"{offset:D10} 00000 n \n");
        document.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(document.ToString());
    }

    private sealed class RecordingOcrEngine(Func<OcrRequest, int, OcrResult> recognize) : IOcrEngine
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public List<OcrRequest> Requests { get; } = [];
        public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(recognize(request, Requests.Count - 1));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "ContextMole-OcrTests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(_path);
        public string File(string name) => Path.Combine(_path, name);
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
