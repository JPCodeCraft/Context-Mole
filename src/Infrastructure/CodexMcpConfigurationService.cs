using System.Text;
using System.Text.RegularExpressions;

using ContextMole.Core;

namespace ContextMole.Infrastructure;

public sealed partial class CodexMcpConfigurationService : IAiClientConnection
{
    private const string ServerName = "context-mole";
    private const string BeginMarker = "# BEGIN Context Mole managed MCP server";
    private const string EndMarker = "# END Context Mole managed MCP server";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IAppPaths _appPaths;
    private readonly McpServerDeploymentService _deployment;

    public CodexMcpConfigurationService(IAppPaths appPaths)
        : this(appPaths, new McpServerDeploymentService(appPaths))
    {
    }

    public CodexMcpConfigurationService(IAppPaths appPaths, McpServerDeploymentService deployment)
    {
        _appPaths = appPaths;
        _deployment = deployment;
    }

    public AiClientDefinition Client => AiClientCatalog.Codex;

    public string ConfigPath
    {
        get
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            var directory = string.IsNullOrWhiteSpace(codexHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : Path.GetFullPath(codexHome);
            return Path.Combine(directory, "config.toml");
        }
    }

    string? IAiClientConnection.ConfigPath => ConfigPath;

    public async Task<AiConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var config = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
        if (TryGetManagedBlock(config, out var start, out var end))
        {
            var managedBlock = config[start..end];
            if (!UnmanagedServerHeaderRegex().IsMatch(managedBlock) ||
                !ManagedEnvironmentHeaderRegex().IsMatch(managedBlock) ||
                DisabledManagedServerRegex().IsMatch(managedBlock))
            {
                return Status(AiConnectionState.UpdateRequired,
                    "This managed OpenAI connection has incomplete or disabled launch settings. Update it to restore the supported configuration.");
            }

            var configuredDataDirectory = TryGetManagedDataDirectory(config, start, end);
            if (!string.Equals(configuredDataDirectory, Path.GetFullPath(_appPaths.DataDirectory), PathComparison))
            {
                return Status(AiConnectionState.UpdateRequired,
                    "This OpenAI connection points to a different Context Mole data directory. Update it to use the current shared index.");
            }

            var serverPath = TryGetManagedServerPath(config, start, end);
            var candidate = _deployment.ResolveCandidate();
            if (serverPath is null)
            {
                return candidate is null
                    ? Status(AiConnectionState.Broken,
                        "Configured for OpenAI clients, but the MCP server command is invalid or missing. Reinstall Context Mole or remove this connection.")
                    : Status(AiConnectionState.UpdateRequired,
                        "The configured MCP server command is invalid or missing. Update this AI connection to repair it.");
            }

            if (McpServerDeploymentService.IsRepositoryBuildOutput(serverPath))
            {
                return Status(AiConnectionState.UpdateRequired,
                    "This OpenAI configuration uses mutable development output. Update it to stage an isolated MCP server.",
                    serverPath);
            }

            if (candidate is not null &&
                !string.Equals(serverPath, _deployment.GetRegistrationPath(candidate), PathComparison))
            {
                return Status(AiConnectionState.UpdateRequired,
                    "A newer local MCP server build is available. Update the OpenAI connection.", serverPath);
            }

            if (!File.Exists(serverPath))
            {
                return candidate is null
                    ? Status(AiConnectionState.Broken,
                        "Configured for OpenAI clients, but the MCP server executable is missing. Reinstall Context Mole or remove this connection.")
                    : Status(AiConnectionState.UpdateRequired,
                        "The configured MCP server executable is missing. Update the OpenAI connection to restore it.", serverPath);
            }

            return Status(AiConnectionState.Connected,
                "Configured. Restart ChatGPT desktop, Codex CLI, or the Codex IDE extension if Context Mole is not visible yet.",
                serverPath);
        }

        if (UnmanagedServerHeaderRegex().IsMatch(config))
        {
            return Status(AiConnectionState.Conflict,
                "An existing Context Mole entry is already present in the OpenAI configuration. It was left unchanged.");
        }

        var resolved = _deployment.ResolveCandidate();
        return resolved is null
            ? Status(AiConnectionState.ServerUnavailable,
                "The MCP server executable was not found. Publish or reinstall the application bundle, then try again.")
            : Status(AiConnectionState.Disconnected,
                "Configure ChatGPT desktop and Codex to search the same local Context Mole index.", resolved.Path);
    }

    public async Task<AiConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidate = _deployment.ResolveCandidate();
            if (candidate is null)
            {
                return Status(AiConnectionState.ServerUnavailable,
                    "The MCP server executable was not found. Publish or reinstall the application bundle, then try again.");
            }

            var serverPath = await _deployment.PrepareAsync(candidate, cancellationToken).ConfigureAwait(false);
            var config = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
            if (!TryGetManagedBlock(config, out var start, out var end) && UnmanagedServerHeaderRegex().IsMatch(config))
            {
                return Status(AiConnectionState.Conflict,
                    "An existing Context Mole entry is already present in the OpenAI configuration. It was left unchanged.",
                    serverPath);
            }

            var managedBlock = BuildManagedBlock(serverPath);
            var updated = start >= 0
                ? string.Concat(config.AsSpan(0, start), managedBlock, config.AsSpan(end))
                : AppendBlock(config, managedBlock);
            if (string.Equals(config, updated, StringComparison.Ordinal))
            {
                return Status(AiConnectionState.Connected,
                    "Already configured. Restart the OpenAI client if Context Mole is not visible yet.", serverPath);
            }

            await WriteConfigSafelyAsync(config, updated, cancellationToken).ConfigureAwait(false);
            return Status(AiConnectionState.Connected,
                "Configured successfully. Restart ChatGPT desktop, Codex CLI, or the Codex IDE extension to load Context Mole.",
                serverPath, restartRequired: true);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<AiConnectionStatus> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
            if (!TryGetManagedBlock(config, out var start, out var end))
            {
                return UnmanagedServerHeaderRegex().IsMatch(config)
                    ? Status(AiConnectionState.Conflict,
                        "The existing Context Mole entry is not managed by this application and was left unchanged.")
                    : Status(AiConnectionState.Disconnected, "Already not configured.");
            }

            var updated = string.Concat(config.AsSpan(0, start), config.AsSpan(end)).TrimEnd() + Environment.NewLine;
            await WriteConfigSafelyAsync(config, updated, cancellationToken).ConfigureAwait(false);
            return Status(AiConnectionState.Disconnected,
                "Removed successfully. Restart the OpenAI client to remove Context Mole from the current session.",
                restartRequired: true);
        }
        finally
        {
            Gate.Release();
        }
    }

    private string BuildManagedBlock(string serverPath) => $$"""
        {{BeginMarker}}
        [mcp_servers.{{ServerName}}]
        command = "{{EscapeToml(serverPath)}}"
        enabled = true
        startup_timeout_sec = 60
        default_tools_approval_mode = "writes"

        [mcp_servers.{{ServerName}}.env]
        CONTEXTMOLE_DATA_DIR = "{{EscapeToml(_appPaths.DataDirectory)}}"
        {{EndMarker}}
        """ + Environment.NewLine;

    private async Task<string> ReadConfigAsync(CancellationToken cancellationToken) =>
        File.Exists(ConfigPath)
            ? await File.ReadAllTextAsync(ConfigPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;

    private async Task WriteConfigSafelyAsync(string expected, string updated, CancellationToken cancellationToken)
        => await SafeConfigurationFile.WriteAsync(ConfigPath, expected, updated, "OpenAI configuration",
            cancellationToken).ConfigureAwait(false);

    private AiConnectionStatus Status(AiConnectionState state, string message, string? serverPath = null,
        bool restartRequired = false) =>
        new(Client, state, message, ConfigPath, serverPath, restartRequired);

    private static string? TryGetManagedServerPath(string config, int start, int end)
    {
        var block = config[start..end];
        var match = ManagedCommandRegex().Match(block);
        return TryGetFullPath(match);
    }

    private static string? TryGetManagedDataDirectory(string config, int start, int end)
    {
        var block = config[start..end];
        return TryGetFullPath(ManagedDataDirectoryRegex().Match(block));
    }

    private static string? TryGetFullPath(Match match)
    {
        if (!match.Success || !TryUnescapeToml(match.Groups["value"].Value, out var value)) return null;
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool TryUnescapeToml(string value, out string result)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '\\')
            {
                builder.Append(character);
                continue;
            }

            if (++index >= value.Length)
            {
                result = string.Empty;
                return false;
            }

            switch (value[index])
            {
                case '\\': builder.Append('\\'); break;
                case '"': builder.Append('"'); break;
                case 'r': builder.Append('\r'); break;
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                default:
                    result = string.Empty;
                    return false;
            }
        }

        result = builder.ToString();
        return true;
    }

    private static bool TryGetManagedBlock(string config, out int start, out int end)
    {
        start = config.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            end = -1;
            return false;
        }

        var markerEnd = config.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (markerEnd < 0)
        {
            end = -1;
            return false;
        }

        end = markerEnd + EndMarker.Length;
        while (end < config.Length && config[end] is '\r' or '\n') end++;
        return true;
    }

    private static string AppendBlock(string config, string block) => string.IsNullOrWhiteSpace(config)
        ? block
        : config.TrimEnd() + Environment.NewLine + Environment.NewLine + block;

    private static string EscapeToml(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    [GeneratedRegex("""(?m)^\s*\[\s*mcp_servers\.(?:context-mole|"context-mole"|'context-mole')\s*\]\s*(?:#.*)?$""")]
    private static partial Regex UnmanagedServerHeaderRegex();

    [GeneratedRegex("""(?m)^\s*command\s*=\s*"(?<value>(?:\\.|[^"\\])*)"\s*$""")]
    private static partial Regex ManagedCommandRegex();

    [GeneratedRegex("""(?m)^\s*CONTEXTMOLE_DATA_DIR\s*=\s*"(?<value>(?:\\.|[^"\\])*)"\s*$""")]
    private static partial Regex ManagedDataDirectoryRegex();

    [GeneratedRegex("""(?m)^\s*\[\s*mcp_servers\.(?:context-mole|"context-mole"|'context-mole')\.env\s*\]\s*(?:#.*)?$""")]
    private static partial Regex ManagedEnvironmentHeaderRegex();

    [GeneratedRegex("""(?m)^\s*enabled\s*=\s*false\s*(?:#.*)?$""", RegexOptions.IgnoreCase)]
    private static partial Regex DisabledManagedServerRegex();

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}