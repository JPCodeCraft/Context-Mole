using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ContextMole.App.UI.ViewModels;

/// <summary>Expose the OCR engine's per-file progress without inventing overall completion.</summary>
internal static partial class OcrPresentation
{
    public static bool IsPlatformSupported => !(OperatingSystem.IsMacOS() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64);

    public static string StatusMessage(bool preparing, bool available, string? reason) =>
        !string.IsNullOrWhiteSpace(reason) ? reason
        : preparing ? "Preparing local OCR files…"
        : available ? "OCR files are ready. The model loads only when a scanned document or image needs it."
        : "OCR could not be prepared. Check your connection and try again.";

    public static double? DownloadPercent(string message)
    {
        if (!message.StartsWith("Downloading ", StringComparison.OrdinalIgnoreCase)) return null;
        var match = PercentPattern().Match(message);
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Number,
            CultureInfo.InvariantCulture, out var percent) ? Math.Clamp(percent, 0, 100) : null;
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentPattern();
}
