using ContextMole.Core;

namespace ContextMole.Indexing;

public static class IndexingMemoryEstimator
{
    private const long Mebibyte = 1024L * 1024;
    private const long Gibibyte = 1024L * Mebibyte;

    private static readonly HashSet<string> OcrExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };

    private static readonly HashSet<string> ContainerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".dotx", ".dotm", ".xlsx", ".xlsm", ".xltx", ".xltm",
        ".pptx", ".pptm", ".ppsx", ".ppsm", ".potx", ".potm", ".odt", ".ods", ".odp",
        ".epub", ".mht", ".mhtml", ".msg", ".eml", ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz"
    };

    public static MemoryWorkEstimate Estimate(IndexJobLease job, long sourceBytes)
    {
        ArgumentNullException.ThrowIfNull(job);
        sourceBytes = Math.Max(0, sourceBytes);
        if (job.Kind == IndexJobKind.EmbeddingRefresh)
            return new MemoryWorkEstimate(Clamp(Add(768L * Mebibyte, Scale(sourceBytes, 2)),
                768L * Mebibyte, 2L * Gibibyte), "embedding-refresh");

        var extension = NormalizeExtension(job.Extension, job.SourcePath);
        if (OcrExtensions.Contains(extension))
            return new MemoryWorkEstimate(Clamp(Add(512L * Mebibyte, Scale(sourceBytes, 4)),
                512L * Mebibyte, 1536L * Mebibyte), "pdf-or-image-document")
            {
                MaximumReservationBytes = MemoryReservationTargets.OcrInferenceBytes
            };
        if (ContainerExtensions.Contains(extension) || extension.Equals(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            var baseReservation = Clamp(Add(1L * Gibibyte, Scale(sourceBytes, 4)),
                1L * Gibibyte, 2560L * Mebibyte);
            return new MemoryWorkEstimate(baseReservation, "container-document")
            {
                MaximumReservationBytes = Math.Max(baseReservation, MemoryReservationTargets.OcrInferenceBytes)
            };
        }

        var textBaseReservation = Clamp(Add(128L * Mebibyte, Scale(sourceBytes, 5)),
            128L * Mebibyte, 1280L * Mebibyte);
        return new MemoryWorkEstimate(textBaseReservation, "text-document")
        {
            // The extractor deliberately content-sniffs PDF data regardless of the file extension.
            // Keep late OCR safe without duplicating that classifier or introducing a TOCTOU gap.
            MaximumReservationBytes = Math.Max(textBaseReservation, MemoryReservationTargets.OcrInferenceBytes)
        };
    }

    private static string NormalizeExtension(string extension, string path)
    {
        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return ".tar.gz";
        if (string.IsNullOrWhiteSpace(extension)) return Path.GetExtension(path);
        return extension.StartsWith('.') ? extension : $".{extension}";
    }

    private static long Scale(long value, int multiplier) =>
        value > long.MaxValue / multiplier ? long.MaxValue : value * multiplier;

    private static long Add(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
    private static long Clamp(long value, long minimum, long maximum) => Math.Clamp(value, minimum, maximum);
}
