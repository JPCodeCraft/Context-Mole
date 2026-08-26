using System.Security.Cryptography;
using System.Text;

using MCPIndexSearch.Core;
using MCPIndexSearch.Infrastructure;

using Microsoft.Extensions.Logging;

namespace MCPIndexSearch.App.UI;

internal sealed class CodexConnectionBannerDismissalStore
{
    private readonly ILogger<CodexConnectionBannerDismissalStore> _logger;
    private readonly string _statePath;

    public CodexConnectionBannerDismissalStore(
        IAppPaths paths,
        ILogger<CodexConnectionBannerDismissalStore> logger)
    {
        _logger = logger;
        _statePath = Path.Combine(paths.DataDirectory, "ui-state", "codex-connection-banner.sha256");
    }

    public bool IsDismissed(CodexMcpConnectionStatus status)
    {
        try
        {
            if (!File.Exists(_statePath)) return false;
            var dismissedFingerprint = File.ReadAllText(_statePath).Trim();
            return string.Equals(dismissedFingerprint, CreateFingerprint(status), StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not read the dismissed Codex connection banner state");
            return false;
        }
    }

    public void Dismiss(CodexMcpConnectionStatus status)
    {
        var directory = Path.GetDirectoryName(_statePath)!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_statePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, CreateFingerprint(status), new UTF8Encoding(false));
            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not save the dismissed Codex connection banner state");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "Could not remove a temporary Codex banner state file");
            }
        }
    }

    private static string CreateFingerprint(CodexMcpConnectionStatus status)
    {
        var value = $"{status.State}\n{status.Message}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
