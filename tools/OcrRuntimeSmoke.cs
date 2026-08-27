#:property TargetFramework=net10.0
#:project ../src/Core/ContextMole.Core.csproj
#:project ../src/Documents/ContextMole.Documents.csproj
#:project ../src/Infrastructure/ContextMole.Infrastructure.csproj

using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Infrastructure;
using SkiaSharp;

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTEXTMOLE_DATA_DIR")))
    throw new InvalidOperationException("Set CONTEXTMOLE_DATA_DIR to an isolated smoke directory.");

var paths = new AppPaths();
using var engine = new PpOcrV6Engine(paths);
await engine.EnsureAvailableAsync();

var scanPath = Path.Combine(paths.TempDirectory, "pp-ocrv6-smoke.png");
using (var bitmap = new SKBitmap(1600, 460, SKColorType.Bgra8888, SKAlphaType.Premul))
using (var canvas = new SKCanvas(bitmap))
using (var typeface = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default)
using (var font = new SKFont(typeface, 72))
using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
{
    canvas.Clear(SKColors.White);
    canvas.DrawText("INVOICE 2026", 60, 120, SKTextAlign.Left, font, paint);
    canvas.DrawText("Contrato local 384", 60, 230, SKTextAlign.Left, font, paint);
    canvas.DrawText("Informação útil español", 60, 340, SKTextAlign.Left, font, paint);
    canvas.Flush();
    using var image = SKImage.FromBitmap(bitmap);
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    await File.WriteAllBytesAsync(scanPath, encoded.ToArray());
}

var imageBytes = await File.ReadAllBytesAsync(scanPath);
var direct = await engine.RecognizeAsync(new OcrRequest(imageBytes, ".png", TimeSpan.FromMinutes(2)), CancellationToken.None);
if (direct.TimedOut || direct.Text.Length < 20)
    throw new InvalidOperationException($"Direct PP-OCRv6 smoke failed. timedOut={direct.TimedOut}, text='{direct.Text}'");

var pdfPath = Path.Combine(paths.TempDirectory, "pp-ocrv6-scanned-smoke.pdf");
using (var bitmap = SKBitmap.Decode(imageBytes))
using (var document = SKDocument.CreatePdf(pdfPath))
{
    var canvas = document.BeginPage(bitmap.Width, bitmap.Height);
    canvas.DrawBitmap(bitmap, new SKPoint(0, 0),
        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
    document.EndPage();
    document.Close();
}

var extracted = await new DocumentExtractionRegistry(engine)
    .ExtractAsync(new ExtractionRequest(pdfPath), CancellationToken.None);
var page = extracted.Root.Sections.SingleOrDefault(section => section.Method == ExtractionMethod.Ocr);
if (page is null || page.Location.Page != 1 || page.Text.Length < 20)
    throw new InvalidOperationException($"Scanned-PDF fallback smoke failed: {string.Join(" | ", extracted.Errors.Select(error => error.Message))}");

Console.WriteLine($"OCR_SMOKE_OK confidence={direct.Confidence:F1} direct={direct.Text.Replace(Environment.NewLine, " | ")} pdf={page.Text.Replace(Environment.NewLine, " | ")}");
