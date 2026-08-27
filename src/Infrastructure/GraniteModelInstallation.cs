using System.Text.Json;
using MCPIndexSearch.Core;

namespace MCPIndexSearch.Infrastructure;

internal static class GraniteModelInstallation
{
    public const string CompletionMarker = "installation-complete";
    public const string RepairMarker = "repair-required";

    public static string GetDirectory(IAppPaths paths, GraniteEmbeddingModelDefinition model) =>
        Path.Combine(paths.AssetsDirectory, "granite", model.Revision);

    public static bool IsComplete(
        IAppPaths paths,
        GraniteEmbeddingModelDefinition model,
        bool useQuantized)
    {
        var directory = GetDirectory(paths, model);
        if (File.Exists(Path.Combine(directory, RepairMarker))) return false;
        if (File.Exists(Path.Combine(directory, CompletionMarker))) return true;
        return IsVerifiedLegacy311MInstall(paths, model, directory, useQuantized);
    }

    public static void MarkForRepair(IAppPaths paths, GraniteEmbeddingModelDefinition model, string reason)
    {
        var directory = GetDirectory(paths, model);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, RepairMarker),
            $"{DateTimeOffset.UtcNow:O}\n{reason}");
    }

    private static bool IsVerifiedLegacy311MInstall(
        IAppPaths paths,
        GraniteEmbeddingModelDefinition model,
        string directory,
        bool useQuantized)
    {
        if (!model.RequiresGemmaTerms || !File.Exists(Path.Combine(directory, "validation.json"))) return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(paths.AssetsDirectory, "gemma-terms-acceptance.json")));
            var root = document.RootElement;
            if (!root.TryGetProperty("terms", out var terms) ||
                terms.GetString() != GraniteModelInstaller.GemmaTermsUrl ||
                !root.TryGetProperty("model_id", out var modelId) || modelId.GetString() != model.ModelId ||
                !root.TryGetProperty("granite_revision", out var revision) || revision.GetString() != model.Revision ||
                !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return false;

            return HasAsset(files, "tokenizer.json", model.TokenizerSha) &&
                   HasAsset(files, useQuantized ? "model_quint8_avx2.onnx" : "model.onnx",
                       useQuantized ? model.QuantizedSha : model.Fp32Sha);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool HasAsset(JsonElement files, string name, string sha256)
    {
        foreach (var file in files.EnumerateArray())
        {
            if (file.TryGetProperty("name", out var storedName) &&
                string.Equals(storedName.GetString(), name, StringComparison.Ordinal) &&
                file.TryGetProperty("sha256", out var storedHash) &&
                string.Equals(storedHash.GetString(), sha256, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
