using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MCPIndexSearch.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace MCPIndexSearch.Infrastructure;

/// <summary>
/// Local PP-OCRv6 medium text detection and recognition. The implementation
/// follows the preprocessing and CTC policy shipped with PaddleOCR 3.7 while
/// keeping the runtime limited to ONNX Runtime and SkiaSharp.
/// </summary>
public sealed class PpOcrV6Engine : IOcrEngine, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = true
    };

    public const string DetectorModelId = "PaddlePaddle/PP-OCRv6_medium_det_onnx";
    public const string DetectorRevision = "61323801669c338b7891481ec7bac61ce31b576a";
    public const string DetectorSha256 = "eb13b44b25bb36f89528b68720af8a61d9cf381176107f465db1757b65d086e1";
    public const string RecognizerModelId = "PaddlePaddle/PP-OCRv6_medium_rec_onnx";
    public const string RecognizerRevision = "50c7eacafc52fa7bcf4194e8cd08e46f8558504b";
    public const string RecognizerSha256 = "9c09abf0957f7968c7586464b7397b84ad2387a0497a351af40e9acc71b673ba";
    public const string RecognizerConfigSha256 = "991b700facf5b50a7de193468207d5f4255b538dde0d312ae3b7c7a9b6873129";

    private const int DetectionLimitSide = 736;
    private const int DetectionMaximumSide = 4000;
    private const float DetectionThreshold = 0.2f;
    private const float DetectionBoxThreshold = 0.45f;
    private const float DetectionUnclipRatio = 1.4f;
    private const int RecognitionHeight = 48;
    private const int RecognitionWidth = 320;

    private readonly IAppPaths _paths;
    private readonly ICpuUsageSettings _cpuUsageSettings;
    private readonly IGlobalCpuBudget _cpuBudget;
    private readonly IDisposable? _ownedCpuBudget;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateGate = new();
    private InferenceSession? _detector;
    private InferenceSession? _recognizer;
    private IReadOnlyList<string>? _characters;
    private string? _detectorInputName;
    private string? _recognizerInputName;
    private volatile bool _isAvailable;
    private int _disposed;
    private string? _unavailableReason;
    private int _configuredThreadCount;

    public PpOcrV6Engine(IAppPaths paths) : this(paths, CreateStandaloneCpuDependencies(paths))
    {
    }

    private PpOcrV6Engine(IAppPaths paths, StandaloneCpuDependencies dependencies)
        : this(paths, dependencies.Settings, dependencies.Budget)
    {
        _ownedCpuBudget = dependencies.Budget;
    }

    public PpOcrV6Engine(
        IAppPaths paths,
        ICpuUsageSettings cpuUsageSettings,
        IGlobalCpuBudget cpuBudget)
    {
        _paths = paths;
        _cpuUsageSettings = cpuUsageSettings;
        _cpuBudget = cpuBudget;
        _client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("MCPIndexSearch/1.0");
        LoadCore(_cpuUsageSettings.ThreadLimit);
    }

    public bool IsAvailable => _isAvailable;

    public string? UnavailableReason
    {
        get
        {
            lock (_stateGate) return _unavailableReason;
        }
    }

    public async Task EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsAvailable) return;
        if (!IsPlatformSupported())
        {
            throw new McpIndexException("ocr_platform_unsupported",
                "PP-OCRv6 is unavailable because ONNX Runtime 1.29 does not provide an Intel macOS native library.");
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var operationToken = operation.Token;
        await _installGate.WaitAsync(operationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (IsAvailable) return;

            var directory = ModelDirectory;
            Directory.CreateDirectory(directory);
            var assets = new[]
            {
                new DownloadAsset("PP-OCRv6 detector", BuildUrl(DetectorModelId, DetectorRevision, "inference.onnx"),
                    Path.Combine(directory, "detector.onnx"), DetectorSha256),
                new DownloadAsset("PP-OCRv6 recognizer", BuildUrl(RecognizerModelId, RecognizerRevision, "inference.onnx"),
                    Path.Combine(directory, "recognizer.onnx"), RecognizerSha256),
                new DownloadAsset("PP-OCRv6 character dictionary", BuildUrl(RecognizerModelId, RecognizerRevision, "inference.yml"),
                    Path.Combine(directory, "recognizer.yml"), RecognizerConfigSha256)
            };

            foreach (var asset in assets)
            {
                await DownloadVerifiedAsync(asset, operationToken).ConfigureAwait(false);
            }

            await WritePolicyAsync(directory, operationToken).ConfigureAwait(false);
            LoadCore(_cpuUsageSettings.ThreadLimit);
            if (!IsAvailable)
            {
                throw new McpIndexException("ocr_initialization_failed",
                    UnavailableReason ?? "PP-OCRv6 could not be initialized.", true);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpIndexException)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetUnavailable($"PP-OCRv6 setup failed: {exception.Message}");
            throw new McpIndexException("ocr_setup_failed", UnavailableReason!, true);
        }
        finally
        {
            _installGate.Release();
        }
    }

    public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource();
        deadline.CancelAfter(request.Timeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token, deadline.Token);
        var operationToken = operation.Token;

        var inferenceGateHeld = false;
        try
        {
            await EnsureAvailableAsync(operationToken).ConfigureAwait(false);
            using var cpuCapacity = await _cpuBudget.AcquireFullCapacityAsync(operationToken).ConfigureAwait(false);
            await _inferenceGate.WaitAsync(operationToken).ConfigureAwait(false);
            inferenceGateHeld = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_configuredThreadCount != cpuCapacity.ThreadCount)
                LoadCore(cpuCapacity.ThreadCount);

            return RecognizeCore(request.ImageBytes.Span, operationToken);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested &&
                                                 !_lifetime.IsCancellationRequested)
        {
            return new OcrResult(string.Empty, null, TimedOut: true);
        }
        catch (OnnxRuntimeException exception)
        {
            throw new McpIndexException("ocr_failed", $"PP-OCRv6 inference failed: {exception.Message}", true);
        }
        finally
        {
            if (inferenceGateHeld) _inferenceGate.Release();
        }
    }

    private OcrResult RecognizeCore(ReadOnlySpan<byte> imageBytes, CancellationToken cancellationToken)
    {
        InferenceSession detector;
        InferenceSession recognizer;
        IReadOnlyList<string> characters;
        string detectorInputName;
        string recognizerInputName;
        lock (_stateGate)
        {
            detector = _detector ?? throw new McpIndexException("ocr_unavailable", "PP-OCRv6 detector is unavailable.", true);
            recognizer = _recognizer ?? throw new McpIndexException("ocr_unavailable", "PP-OCRv6 recognizer is unavailable.", true);
            characters = _characters ?? throw new McpIndexException("ocr_unavailable", "PP-OCRv6 dictionary is unavailable.", true);
            detectorInputName = _detectorInputName!;
            recognizerInputName = _recognizerInputName!;
        }

        using var source = DecodeImage(imageBytes);
        cancellationToken.ThrowIfCancellationRequested();

        var boxes = DetectText(source, detector, detectorInputName, cancellationToken);
        if (boxes.Count == 0) return new OcrResult(string.Empty, null);

        var lines = new List<RecognizedLine>(boxes.Count);
        foreach (var box in boxes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var crop = Crop(source, box);
            var recognized = RecognizeLine(crop, recognizer, recognizerInputName, characters, cancellationToken);
            if (!string.IsNullOrWhiteSpace(recognized.Text))
            {
                lines.Add(new RecognizedLine(box, recognized.Text.Trim(), recognized.Confidence));
            }
        }

        if (lines.Count == 0) return new OcrResult(string.Empty, null);
        lines.Sort(CompareReadingOrder);

        double weightedConfidence = 0;
        long confidenceWeight = 0;
        foreach (var line in lines)
        {
            var weight = Math.Max(1, line.Text.Length);
            weightedConfidence += line.Confidence * weight;
            confidenceWeight += weight;
        }

        return new OcrResult(string.Join(Environment.NewLine, lines.Select(line => line.Text)),
            confidenceWeight == 0 ? null : weightedConfidence / confidenceWeight);
    }

    private static SKBitmap DecodeImage(ReadOnlySpan<byte> imageBytes)
    {
        try
        {
            return SKBitmap.Decode(imageBytes.ToArray())
                ?? throw new McpIndexException("ocr_image_invalid", "The image could not be decoded.");
        }
        catch (McpIndexException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new McpIndexException("ocr_image_invalid", "The image could not be decoded.")
            {
                Source = exception.Source
            };
        }
    }

    private static List<TextBox> DetectText(
        SKBitmap source,
        InferenceSession session,
        string inputName,
        CancellationToken cancellationToken)
    {
        var (width, height) = GetDetectionSize(source.Width, source.Height);
        using var resized = Resize(source, width, height);
        var input = new DenseTensor<float>([1, 3, height, width]);
        FillDetectionTensor(resized, input);
        cancellationToken.ThrowIfCancellationRequested();

        using var output = RunWithCancellation(session,
            [NamedOnnxValue.CreateFromTensor(inputName, input)], cancellationToken);
        var map = output.First().AsTensor<float>();
        var dimensions = map.Dimensions.ToArray();
        int mapHeight;
        int mapWidth;
        if (dimensions.Length == 4 && dimensions[0] == 1 && dimensions[1] == 1)
        {
            mapHeight = dimensions[2];
            mapWidth = dimensions[3];
        }
        else if (dimensions.Length == 3 && dimensions[0] == 1)
        {
            mapHeight = dimensions[1];
            mapWidth = dimensions[2];
        }
        else
        {
            throw new McpIndexException("ocr_model_output_invalid",
                $"Expected PP-OCRv6 detector output [1,1,h,w], received [{string.Join(',', dimensions)}].");
        }

        var probabilities = map.ToArray();
        return ExtractConnectedTextBoxes(probabilities, mapWidth, mapHeight, source.Width, source.Height, cancellationToken);
    }

    private static List<TextBox> ExtractConnectedTextBoxes(
        float[] probabilities,
        int width,
        int height,
        int sourceWidth,
        int sourceHeight,
        CancellationToken cancellationToken)
    {
        var visited = new bool[checked(width * height)];
        var queue = ArrayPool<int>.Shared.Rent(visited.Length);
        var boxes = new List<TextBox>();
        try
        {
            for (var start = 0; start < visited.Length; start++)
            {
                if ((start & 0x7fff) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (visited[start] || probabilities[start] <= DetectionThreshold) continue;

                var head = 0;
                var tail = 0;
                queue[tail++] = start;
                visited[start] = true;
                var minX = width;
                var minY = height;
                var maxX = 0;
                var maxY = 0;
                var componentPixels = 0;

                while (head < tail)
                {
                    var position = queue[head++];
                    var y = position / width;
                    var x = position - y * width;
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                    componentPixels++;

                    for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var nx = x + dx;
                        var ny = y + dy;
                        if ((uint)nx >= (uint)width || (uint)ny >= (uint)height) continue;
                        var neighbor = ny * width + nx;
                        if (visited[neighbor] || probabilities[neighbor] <= DetectionThreshold) continue;
                        visited[neighbor] = true;
                        queue[tail++] = neighbor;
                    }
                }

                var boxWidth = maxX - minX + 1;
                var boxHeight = maxY - minY + 1;
                if (componentPixels < 3 || Math.Min(boxWidth, boxHeight) < 3) continue;

                double score = 0;
                for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++)
                    score += probabilities[y * width + x];
                score /= boxWidth * boxHeight;
                if (score < DetectionBoxThreshold) continue;

                var perimeter = 2d * (boxWidth + boxHeight);
                var expansion = perimeter <= 0 ? 0 : boxWidth * boxHeight * DetectionUnclipRatio / perimeter;
                minX = Math.Max(0, (int)Math.Floor(minX - expansion));
                minY = Math.Max(0, (int)Math.Floor(minY - expansion));
                maxX = Math.Min(width - 1, (int)Math.Ceiling(maxX + expansion));
                maxY = Math.Min(height - 1, (int)Math.Ceiling(maxY + expansion));

                var left = Math.Clamp((int)Math.Floor((double)minX / width * sourceWidth), 0, sourceWidth - 1);
                var top = Math.Clamp((int)Math.Floor((double)minY / height * sourceHeight), 0, sourceHeight - 1);
                var right = Math.Clamp((int)Math.Ceiling((double)(maxX + 1) / width * sourceWidth), left + 1, sourceWidth);
                var bottom = Math.Clamp((int)Math.Ceiling((double)(maxY + 1) / height * sourceHeight), top + 1, sourceHeight);
                if (right - left >= 3 && bottom - top >= 3)
                    boxes.Add(new TextBox(left, top, right, bottom));
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queue);
        }

        return boxes;
    }

    private static (string Text, double Confidence) RecognizeLine(
        SKBitmap crop,
        InferenceSession session,
        string inputName,
        IReadOnlyList<string> characters,
        CancellationToken cancellationToken)
    {
        var input = new DenseTensor<float>([1, 3, RecognitionHeight, RecognitionWidth]);
        FillRecognitionTensor(crop, input);
        cancellationToken.ThrowIfCancellationRequested();
        using var output = RunWithCancellation(session,
            [NamedOnnxValue.CreateFromTensor(inputName, input)], cancellationToken);
        var probabilities = output.First().AsTensor<float>();
        var dimensions = probabilities.Dimensions.ToArray();
        if (dimensions.Length != 3 || dimensions[0] != 1)
        {
            throw new McpIndexException("ocr_model_output_invalid",
                $"Expected PP-OCRv6 recognizer output [1,time,classes], received [{string.Join(',', dimensions)}].");
        }

        var steps = dimensions[1];
        var classCount = dimensions[2];
        if (characters.Count != classCount)
        {
            throw new McpIndexException("ocr_model_output_invalid",
                $"PP-OCRv6 recognizer exposes {classCount} classes, but its pinned dictionary contains {characters.Count} entries.");
        }

        var values = probabilities.ToArray();
        var builder = new StringBuilder();
        double confidence = 0;
        var selected = 0;
        var previousClass = -1;
        for (var step = 0; step < steps; step++)
        {
            var offset = step * classCount;
            var bestClass = 0;
            var bestProbability = values[offset];
            for (var candidate = 1; candidate < classCount; candidate++)
            {
                var probability = values[offset + candidate];
                if (probability <= bestProbability) continue;
                bestProbability = probability;
                bestClass = candidate;
            }

            if (bestClass != 0 && bestClass != previousClass)
            {
                builder.Append(characters[bestClass]);
                confidence += bestProbability;
                selected++;
            }
            previousClass = bestClass;
        }

        return (builder.ToString(), selected == 0 ? 0 : confidence / selected * 100d);
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

    private static void FillDetectionTensor(SKBitmap image, DenseTensor<float> tensor)
    {
        var pixels = image.Pixels;
        var plane = checked(image.Width * image.Height);
        var buffer = tensor.Buffer.Span;
        for (var index = 0; index < pixels.Length; index++)
        {
            var pixel = pixels[index];
            buffer[index] = (pixel.Blue / 255f - 0.485f) / 0.229f;
            buffer[plane + index] = (pixel.Green / 255f - 0.456f) / 0.224f;
            buffer[2 * plane + index] = (pixel.Red / 255f - 0.406f) / 0.225f;
        }
    }

    private static void FillRecognitionTensor(SKBitmap image, DenseTensor<float> tensor)
    {
        var resizedWidth = Math.Clamp((int)Math.Ceiling(RecognitionHeight * (double)image.Width / image.Height), 1, RecognitionWidth);
        using var resized = Resize(image, resizedWidth, RecognitionHeight);
        var pixels = resized.Pixels;
        var plane = RecognitionHeight * RecognitionWidth;
        var buffer = tensor.Buffer.Span;
        for (var y = 0; y < RecognitionHeight; y++)
        for (var x = 0; x < resizedWidth; x++)
        {
            var pixel = pixels[y * resizedWidth + x];
            var index = y * RecognitionWidth + x;
            buffer[index] = pixel.Blue / 127.5f - 1f;
            buffer[plane + index] = pixel.Green / 127.5f - 1f;
            buffer[2 * plane + index] = pixel.Red / 127.5f - 1f;
        }
    }

    private static (int Width, int Height) GetDetectionSize(int width, int height)
    {
        var ratio = Math.Min(width, height) < DetectionLimitSide
            ? DetectionLimitSide / (double)Math.Min(width, height)
            : 1d;
        var resizedWidth = (int)(width * ratio);
        var resizedHeight = (int)(height * ratio);
        if (Math.Max(resizedWidth, resizedHeight) > DetectionMaximumSide)
        {
            ratio *= DetectionMaximumSide / (double)Math.Max(resizedWidth, resizedHeight);
            resizedWidth = (int)(width * ratio);
            resizedHeight = (int)(height * ratio);
        }

        resizedWidth = Math.Max(32, (int)Math.Round(resizedWidth / 32d) * 32);
        resizedHeight = Math.Max(32, (int)Math.Round(resizedHeight / 32d) * 32);
        return (resizedWidth, resizedHeight);
    }

    private static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        var result = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(source, new SKRect(0, 0, width, height),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
        canvas.Flush();
        return result;
    }

    private static SKBitmap Crop(SKBitmap source, TextBox box)
    {
        var width = box.Right - box.Left;
        var height = box.Bottom - box.Top;
        var result = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source,
            new SKRect(box.Left, box.Top, box.Right, box.Bottom),
            new SKRect(0, 0, width, height),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
        canvas.Flush();
        return result;
    }

    private static int CompareReadingOrder(RecognizedLine left, RecognizedLine right)
    {
        var leftHeight = left.Box.Bottom - left.Box.Top;
        var rightHeight = right.Box.Bottom - right.Box.Top;
        var sameLineTolerance = Math.Max(6, Math.Min(leftHeight, rightHeight) / 2);
        if (Math.Abs(left.Box.Top - right.Box.Top) <= sameLineTolerance)
            return left.Box.Left.CompareTo(right.Box.Left);
        return left.Box.Top.CompareTo(right.Box.Top);
    }

    private void LoadCore(int threadCount)
    {
        if (!IsPlatformSupported())
        {
            ReplaceResources(null, null, null, null, null,
                "PP-OCRv6 is unavailable because ONNX Runtime 1.29 does not provide an Intel macOS native library.");
            return;
        }

        var detectorPath = Path.Combine(ModelDirectory, "detector.onnx");
        var recognizerPath = Path.Combine(ModelDirectory, "recognizer.onnx");
        var dictionaryPath = Path.Combine(ModelDirectory, "recognizer.yml");
        if (!File.Exists(detectorPath) || !File.Exists(recognizerPath) || !File.Exists(dictionaryPath))
        {
            ReplaceResources(null, null, null, null, null,
                "Preparing the local PP-OCRv6 medium model…");
            return;
        }

        InferenceSession? detector = null;
        InferenceSession? recognizer = null;
        try
        {
            var options = new SessionOptions
            {
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = threadCount,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            detector = new InferenceSession(detectorPath, options);
            recognizer = new InferenceSession(recognizerPath, options);
            var characters = LoadCharacters(dictionaryPath);
            ReplaceResources(detector, recognizer, characters,
                detector.InputMetadata.Keys.Single(), recognizer.InputMetadata.Keys.Single(), null);
            _configuredThreadCount = threadCount;
            detector = null;
            recognizer = null;
        }
        catch (Exception exception)
        {
            detector?.Dispose();
            recognizer?.Dispose();
            ReplaceResources(null, null, null, null, null, $"PP-OCRv6 initialization failed: {exception.Message}");
        }
    }

    private void ReplaceResources(
        InferenceSession? detector,
        InferenceSession? recognizer,
        IReadOnlyList<string>? characters,
        string? detectorInputName,
        string? recognizerInputName,
        string? unavailableReason)
    {
        InferenceSession? previousDetector;
        InferenceSession? previousRecognizer;
        lock (_stateGate)
        {
            previousDetector = _detector;
            previousRecognizer = _recognizer;
            _detector = detector;
            _recognizer = recognizer;
            _characters = characters;
            _detectorInputName = detectorInputName;
            _recognizerInputName = recognizerInputName;
            _unavailableReason = unavailableReason;
            _isAvailable = detector is not null && recognizer is not null && characters is not null;
        }
        previousDetector?.Dispose();
        previousRecognizer?.Dispose();
    }

    private static IReadOnlyList<string> LoadCharacters(string path)
    {
        var characters = new List<string> { "blank" };
        var inDictionary = false;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (!inDictionary)
            {
                if (line.Trim().Equals("character_dict:", StringComparison.Ordinal)) inDictionary = true;
                continue;
            }

            if (!line.StartsWith("  - ", StringComparison.Ordinal))
            {
                if (characters.Count > 1) break;
                continue;
            }
            characters.Add(ParseYamlScalar(line[4..]));
        }

        if (characters.Count < 100)
            throw new InvalidDataException("The PP-OCRv6 character dictionary is missing or malformed.");
        characters.Add(" ");
        return characters;
    }

    private static string ParseYamlScalar(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return JsonSerializer.Deserialize<string>(value, JsonOptions) ?? string.Empty;
        return value;
    }

    private async Task DownloadVerifiedAsync(DownloadAsset asset, CancellationToken cancellationToken)
    {
        if (File.Exists(asset.Target) && await HasExpectedHashAsync(asset.Target, asset.Sha256, cancellationToken).ConfigureAwait(false))
            return;

        var partial = asset.Target + ".partial";
        if (File.Exists(partial) && await HasExpectedHashAsync(partial, asset.Sha256, cancellationToken).ConfigureAwait(false))
        {
            File.Move(partial, asset.Target, true);
            return;
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
                SetUnavailable(total is > 0
                    ? $"Downloading {asset.Name}… {received * 100d / total.Value:F0}%"
                    : $"Downloading {asset.Name}… {received / (1024d * 1024d):F1} MB");
            }
            await local.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        SetUnavailable($"Verifying {asset.Name}…");
        var actual = await HashAsync(partial, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new McpIndexException("asset_checksum_mismatch",
                $"Checksum verification failed for {asset.Name}. Expected {asset.Sha256}, received {actual}.");
        }
        File.Move(partial, asset.Target, true);
    }

    private async Task WritePolicyAsync(string directory, CancellationToken cancellationToken)
    {
        var policy = new
        {
            engine = "PP-OCRv6_medium",
            license = "Apache-2.0",
            installed_utc = DateTimeOffset.UtcNow,
            detector = new { model_id = DetectorModelId, revision = DetectorRevision, sha256 = DetectorSha256 },
            recognizer = new { model_id = RecognizerModelId, revision = RecognizerRevision, sha256 = RecognizerSha256 },
            dictionary_sha256 = RecognizerConfigSha256
        };
        var target = Path.Combine(directory, "policy.json");
        var partial = target + ".partial";
        await File.WriteAllTextAsync(partial,
            JsonSerializer.Serialize(policy, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(partial, target, true);
    }

    private void SetUnavailable(string message)
    {
        lock (_stateGate)
        {
            if (!_isAvailable) _unavailableReason = message;
        }
    }

    private static string BuildUrl(string modelId, string revision, string fileName) =>
        $"https://huggingface.co/{modelId}/resolve/{revision}/{fileName}?download=true";

    private static bool IsPlatformSupported() => !(OperatingSystem.IsMacOS() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64);

    private string ModelDirectory => Path.Combine(_paths.AssetsDirectory, "pp-ocrv6-medium",
        $"{DetectorRevision[..12]}-{RecognizerRevision[..12]}");

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

        _installGate.Wait();
        try
        {
            _inferenceGate.Wait();
            try
            {
                ReplaceResources(null, null, null, null, null, "PP-OCRv6 is shutting down.");
            }
            finally
            {
                _inferenceGate.Release();
            }
        }
        finally
        {
            _installGate.Release();
        }

        _client.Dispose();
        _ownedCpuBudget?.Dispose();
        _lifetime.Dispose();
    }

    private static StandaloneCpuDependencies CreateStandaloneCpuDependencies(IAppPaths paths)
    {
        var settings = new CpuUsageSettings(paths);
        return new StandaloneCpuDependencies(settings, new GlobalCpuBudget(settings));
    }

    private sealed record DownloadAsset(string Name, string Url, string Target, string Sha256);
    private sealed record StandaloneCpuDependencies(CpuUsageSettings Settings, GlobalCpuBudget Budget);
    private readonly record struct TextBox(int Left, int Top, int Right, int Bottom);
    private sealed record RecognizedLine(TextBox Box, string Text, double Confidence);
}
