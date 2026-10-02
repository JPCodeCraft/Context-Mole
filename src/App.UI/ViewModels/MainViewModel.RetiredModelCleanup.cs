using ContextMole.Infrastructure;

namespace ContextMole.App.UI.ViewModels;

internal partial class MainViewModel
{
    internal void ReportRetiredModelCleanup(RetiredModelCacheCleanupResult result)
    {
        var message = RetiredModelCleanupMessage(result);
        if (message is not null) Notify("retired_model_cache_cleanup", message, isError: result.NeedsAttention);
    }

    internal static string? RetiredModelCleanupMessage(RetiredModelCacheCleanupResult result)
    {
        if (result.NeedsAttention)
        {
            var removed = result.DeletedFiles.Count > 0 ? $"Removed {result.DeletedFiles.Count} retired 311M cache files. " : string.Empty;
            return removed + "Some retired 311M cache files were kept because they’re in use, unavailable, or protected. " +
                "Cleanup will be retried the next time Context Mole starts.";
        }
        return result.DeletedFiles.Count > 0
            ? "Removed retired 311M installer files from Context Mole’s managed cache. Granite 97M is the current model."
            : null;
    }
}
