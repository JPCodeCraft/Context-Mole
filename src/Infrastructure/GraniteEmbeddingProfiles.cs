using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;

namespace ContextMole.Infrastructure;

/// <summary>Keeps profile selection consistent without reusing validation from a different input format.</summary>
public static class GraniteEmbeddingProfiles
{
    public static bool UseQuantized(string directory, GraniteEmbeddingModelDefinition model) =>
        CanUseQuantized(directory, model, RuntimeInformation.ProcessArchitecture == Architecture.X64 && Avx2.IsSupported);

    internal static bool CanUseQuantized(string directory, GraniteEmbeddingModelDefinition model, bool supported) =>
        supported && !File.Exists(Path.Combine(directory, "quantization-disabled")) && HasCurrentValidation(directory, model);

    public static bool HasCurrentValidation(string directory, GraniteEmbeddingModelDefinition model)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "validation.json")));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("quantized_enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True)
                return false;
            // Legacy BOS-only validation cannot activate optimized 97M inference.
            if (model.TokenizationVersion is null) return false;
            return root.TryGetProperty("tokenization_version", out var version) &&
                   version.ValueKind == JsonValueKind.String && version.GetString() == model.TokenizationVersion;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public static bool NeedsTokenizationValidation(string directory, GraniteEmbeddingModelDefinition model) =>
        NeedsTokenizationValidation(directory, model, RuntimeInformation.ProcessArchitecture == Architecture.X64 && Avx2.IsSupported);

    internal static bool NeedsTokenizationValidation(string directory, GraniteEmbeddingModelDefinition model, bool supported) =>
        supported && model.TokenizationVersion is not null && File.Exists(Path.Combine(directory, "model_quint8_avx2.onnx")) &&
        !File.Exists(Path.Combine(directory, "quantization-disabled")) && !HasCurrentValidation(directory, model);
}
