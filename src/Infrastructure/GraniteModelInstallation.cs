using ContextMole.Core;

namespace ContextMole.Infrastructure;

internal static class GraniteModelInstallation
{
    public const string CompletionMarker = "installation-complete";
    public const string RepairMarker = "repair-required";

    public static string GetDirectory(IAppPaths paths, GraniteEmbeddingModelDefinition model) =>
        Path.Combine(paths.AssetsDirectory, "granite", model.Revision);

    public static bool IsComplete(IAppPaths paths, GraniteEmbeddingModelDefinition model)
    {
        var directory = GetDirectory(paths, model);
        if (File.Exists(Path.Combine(directory, RepairMarker))) return false;
        return File.Exists(Path.Combine(directory, CompletionMarker));
    }

    public static void MarkForRepair(IAppPaths paths, GraniteEmbeddingModelDefinition model, string reason)
    {
        var directory = GetDirectory(paths, model);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, RepairMarker),
            $"{DateTimeOffset.UtcNow:O}\n{reason}");
    }

}