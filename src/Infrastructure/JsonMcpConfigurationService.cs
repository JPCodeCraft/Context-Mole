using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

using ContextMole.Core;

namespace ContextMole.Infrastructure;

public sealed class JsonMcpConfigurationService : IAiClientConnection
{
    private const string ServerName = "context-mole";
    private const string ManagedEnvironmentName = "CONTEXTMOLE_MANAGED_CONNECTION";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(PathComparer);
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _rootProperty;
    private readonly IAppPaths _appPaths;
    private readonly McpServerDeploymentService _deployment;
    private readonly string? _transportType;
    private readonly bool _includeAllTools;

    public JsonMcpConfigurationService(
        AiClientDefinition client,
        string configPath,
        string rootProperty,
        IAppPaths appPaths,
        McpServerDeploymentService deployment,
        string? transportType = null,
        bool includeAllTools = false)
    {
        Client = client;
        ConfigPath = Path.GetFullPath(configPath);
        _rootProperty = rootProperty;
        _appPaths = appPaths;
        _deployment = deployment;
        _transportType = transportType;
        _includeAllTools = includeAllTools;
    }

    public AiClientDefinition Client { get; }
    public string ConfigPath { get; }
    string? IAiClientConnection.ConfigPath => ConfigPath;

    public async Task<AiConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var read = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
        if (!TryParseRoot(read, out var root, out var error)) return Conflict(error!);
        if (!TryGetServers(root!, out var servers, out error)) return Conflict(error!);

        if (servers is not null && servers.TryGetPropertyValue(ServerName, out var existing))
        {
            if (existing is not JsonObject entry || !IsManaged(entry))
                return Conflict($"An existing {ServerName} entry in {Client.DisplayName} is not managed by Context Mole and was left unchanged.");
            if (!TryValidateManagedEntry(entry, out error)) return Conflict(error!);

            var configuredDataDirectory = entry["env"] is JsonObject environment
                ? TryGetFullPath(ReadString(environment, "CONTEXTMOLE_DATA_DIR"))
                : null;
            if (!string.Equals(configuredDataDirectory, Path.GetFullPath(_appPaths.DataDirectory), PathComparison))
            {
                return Status(AiConnectionState.UpdateRequired,
                    "This connection points to a different Context Mole data directory. Update it to use the current shared index.");
            }

            var serverPath = TryGetFullPath(ReadString(entry, "command"));
            var candidate = _deployment.ResolveCandidate();
            if (serverPath is null)
            {
                return candidate is null
                    ? Status(AiConnectionState.Broken,
                        $"Configured, but the MCP server command is invalid or missing. Reinstall Context Mole or remove this connection.")
                    : Status(AiConnectionState.UpdateRequired,
                        "The configured MCP server command is invalid or missing. Update this connection to repair it.");
            }

            if (NeedsRepair(entry))
            {
                return Status(AiConnectionState.UpdateRequired,
                    "This managed connection has incomplete or changed launch settings. Update it to restore the supported configuration.",
                    serverPath);
            }

            if (McpServerDeploymentService.IsRepositoryBuildOutput(serverPath))
            {
                return Status(AiConnectionState.UpdateRequired,
                    "This configuration uses mutable development output. Update it to use an isolated MCP server deployment.",
                    serverPath);
            }

            if (candidate is not null &&
                !string.Equals(serverPath, _deployment.GetRegistrationPath(candidate), PathComparison))
            {
                return Status(AiConnectionState.UpdateRequired,
                    "A newer local MCP server build is available. Update this configuration.", serverPath);
            }

            if (!File.Exists(serverPath))
            {
                return candidate is null
                    ? Status(AiConnectionState.Broken,
                        "Configured, but the MCP server executable is missing. Reinstall Context Mole or remove this connection.")
                    : Status(AiConnectionState.UpdateRequired,
                        "The configured MCP server executable is missing. Update this connection to restore it.", serverPath);
            }

            return Status(AiConnectionState.Connected,
                $"Configured. Reload {Client.DisplayName} if Context Mole is not visible yet.", serverPath);
        }

        var resolved = _deployment.ResolveCandidate();
        return resolved is null
            ? Status(AiConnectionState.ServerUnavailable,
                "The MCP server executable was not found. Publish or reinstall the application bundle, then try again.")
            : Status(AiConnectionState.Disconnected,
                $"Configure {Client.DisplayName} to search the same local Context Mole index.", resolved.Path);
    }

    public async Task<AiConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var gate = Gates.GetOrAdd(ConfigPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
            if (!TryParseRoot(read, out var root, out var error)) return Conflict(error!);
            if (!TryGetServers(root!, out var servers, out error)) return Conflict(error!);

            JsonObject? managedEntry = null;
            if (servers is not null && servers.TryGetPropertyValue(ServerName, out var existing))
            {
                if (existing is not JsonObject entry || !IsManaged(entry))
                    return Conflict($"An existing {ServerName} entry in {Client.DisplayName} is not managed by Context Mole and was left unchanged.");
                if (!TryValidateManagedEntry(entry, out error)) return Conflict(error!);
                managedEntry = entry;
            }

            var candidate = _deployment.ResolveCandidate();
            if (candidate is null)
            {
                return Status(AiConnectionState.ServerUnavailable,
                    "The MCP server executable was not found. Publish or reinstall the application bundle, then try again.");
            }

            var serverPath = await _deployment.PrepareAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (!TryGetOrCreateServers(root!, out servers, out error)) return Conflict(error!);
            servers![ServerName] = BuildEntry(serverPath, managedEntry);
            var updated = Serialize(root!);
            if (string.Equals(read, updated, StringComparison.Ordinal))
            {
                return Status(AiConnectionState.Connected,
                    $"Already configured. Reload {Client.DisplayName} if Context Mole is not visible yet.", serverPath);
            }

            await WriteConfigSafelyAsync(read, updated, cancellationToken).ConfigureAwait(false);
            return Status(AiConnectionState.Connected,
                $"Configured successfully. Reload {Client.DisplayName}, approve the local server if prompted, and ask it to list projects.",
                serverPath, restartRequired: true);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AiConnectionStatus> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var gate = Gates.GetOrAdd(ConfigPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
            if (!TryParseRoot(read, out var root, out var error)) return Conflict(error!);
            if (!TryGetServers(root!, out var servers, out error)) return Conflict(error!);
            if (servers is null || !servers.TryGetPropertyValue(ServerName, out var existing))
                return Status(AiConnectionState.Disconnected, "Already not configured.");
            if (existing is not JsonObject entry || !IsManaged(entry))
                return Conflict($"The existing {ServerName} entry in {Client.DisplayName} is not managed by Context Mole and was left unchanged.");

            servers.Remove(ServerName);
            var updated = Serialize(root!);
            await WriteConfigSafelyAsync(read, updated, cancellationToken).ConfigureAwait(false);
            return Status(AiConnectionState.Disconnected,
                $"Removed successfully. Reload {Client.DisplayName} to remove Context Mole from the current session.",
                restartRequired: true);
        }
        finally
        {
            gate.Release();
        }
    }

    private JsonObject BuildEntry(string serverPath, JsonObject? existing = null)
    {
        // Ownership covers launch settings, not client permissions or user preferences.
        var entry = existing?.DeepClone().AsObject() ?? new JsonObject();
        entry["command"] = serverPath;
        entry["args"] = new JsonArray();
        var environment = entry["env"] as JsonObject ?? new JsonObject();
        environment["CONTEXTMOLE_DATA_DIR"] = _appPaths.DataDirectory;
        environment[ManagedEnvironmentName] = "1";
        if (entry["env"] is null) entry["env"] = environment;
        if (_transportType is not null && !entry.ContainsKey("type")) entry["type"] = _transportType;
        if (_includeAllTools && !entry.ContainsKey("tools")) entry["tools"] = new JsonArray("*");
        return entry;
    }

    private static bool IsManaged(JsonObject entry) =>
        entry["env"] is JsonObject environment &&
        string.Equals(ReadString(environment, ManagedEnvironmentName), "1", StringComparison.Ordinal);

    private bool TryValidateManagedEntry(JsonObject entry, out string? error)
    {
        if (entry.TryGetPropertyValue("disabled", out var disabled))
        {
            if (disabled is not JsonValue value || !value.TryGetValue<bool>(out var isDisabled))
            {
                error = $"The managed {ServerName} disabled setting must be a JSON boolean. Correct it in {Client.DisplayName}'s configuration; the entry was left unchanged.";
                return false;
            }

            if (isDisabled)
            {
                error = $"This managed connection is disabled in {Client.DisplayName}. Enable it explicitly in the client or set disabled to false before configuring it. Context Mole left the entry unchanged.";
                return false;
            }
        }

        var expectedType = _transportType == "local" ? "\"local\" or \"stdio\"" : $"\"{_transportType ?? "stdio"}\"";
        foreach (var property in new[] { "type", "transportType" })
        {
            if (!entry.TryGetPropertyValue(property, out _)) continue;
            if (IsSupportedTransport(ReadString(entry, property))) continue;
            error = $"The managed {ServerName} {property} setting is incompatible with the local server. Set it to {expectedType} in {Client.DisplayName}'s configuration; the entry was left unchanged.";
            return false;
        }

        if (entry.ContainsKey("url") || entry.ContainsKey("transport"))
        {
            error = $"The managed {ServerName} entry contains URL or nested transport settings that may override its local command. Restore the command-based local configuration in {Client.DisplayName} before configuring it; the entry was left unchanged.";
            return false;
        }

        foreach (var (name, value) in entry["env"]!.AsObject())
        {
            if (value is JsonValue environmentValue && environmentValue.TryGetValue<string>(out _)) continue;
            error = $"The managed {ServerName} environment variable \"{name}\" must have a JSON string value. Correct or remove it in {Client.DisplayName}'s configuration; the entry was left unchanged.";
            return false;
        }

        if (_includeAllTools && entry.TryGetPropertyValue("tools", out var tools) &&
            (tools is not JsonArray toolArray || toolArray.Any(tool =>
                tool is not JsonValue value || !value.TryGetValue<string>(out _))))
        {
            error = $"The managed {ServerName} tools setting must be an array of strings. Correct it in {Client.DisplayName}'s configuration; the entry was left unchanged.";
            return false;
        }

        error = null;
        return true;
    }

    private bool IsSupportedTransport(string? transport) =>
        string.Equals(transport, _transportType ?? "stdio", StringComparison.Ordinal) ||
        (_transportType == "local" && string.Equals(transport, "stdio", StringComparison.Ordinal));

    private bool NeedsRepair(JsonObject entry)
    {
        if (_transportType is not null && !IsSupportedTransport(ReadString(entry, "type")))
            return true;

        if (entry.TryGetPropertyValue("args", out var arguments) &&
            (arguments is not JsonArray argumentArray || argumentArray.Count != 0))
            return true;

        return _includeAllTools && !entry.ContainsKey("tools");
    }

    private static string? ReadString(JsonObject value, string propertyName) =>
        value[propertyName] is JsonValue item && item.TryGetValue<string>(out var result) ? result : null;

    private static string? TryGetFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private async Task<string> ReadConfigAsync(CancellationToken cancellationToken) =>
        File.Exists(ConfigPath)
            ? await File.ReadAllTextAsync(ConfigPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;

    private static bool TryParseRoot(string config, out JsonObject? root, out string? error)
    {
        if (string.IsNullOrWhiteSpace(config))
        {
            root = new JsonObject();
            error = null;
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(config, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (TryFindDuplicateProperty(document.RootElement, "$", out error))
            {
                root = null;
                return false;
            }

            root = JsonNode.Parse(document.RootElement.GetRawText(), documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            }) as JsonObject;
            error = root is null ? "The configuration root must be a JSON object." : null;
            return root is not null;
        }
        catch (JsonException exception)
        {
            root = null;
            error = $"The configuration is not valid JSON and was left unchanged: {exception.Message}";
            return false;
        }
    }

    private static bool TryFindDuplicateProperty(JsonElement element, string path, out string? error)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    error = $"The configuration contains a duplicate JSON property \"{property.Name}\" at {path}. Remove the duplicate properties before configuring or removing this connection; the file was left unchanged.";
                    return true;
                }

                if (TryFindDuplicateProperty(property.Value, $"{path}.{property.Name}", out error)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindDuplicateProperty(item, $"{path}[{index++}]", out error)) return true;
            }
        }

        error = null;
        return false;
    }

    private bool TryGetServers(JsonObject root, out JsonObject? servers, out string? error)
    {
        if (!root.TryGetPropertyValue(_rootProperty, out var value))
        {
            servers = null;
            error = null;
            return true;
        }

        servers = value as JsonObject;
        error = servers is null ? $"The {_rootProperty} setting must be a JSON object." : null;
        return servers is not null;
    }

    private bool TryGetOrCreateServers(JsonObject root, out JsonObject? servers, out string? error)
    {
        if (TryGetServers(root, out servers, out error) && servers is not null) return true;
        if (error is not null) return false;
        servers = new JsonObject();
        root[_rootProperty] = servers;
        return true;
    }

    private static string Serialize(JsonObject root) => root.ToJsonString(WriteOptions) + Environment.NewLine;

    private async Task WriteConfigSafelyAsync(string expected, string updated, CancellationToken cancellationToken)
        => await SafeConfigurationFile.WriteAsync(ConfigPath, expected, updated,
            $"{Client.DisplayName} configuration", cancellationToken).ConfigureAwait(false);

    private AiConnectionStatus Conflict(string message, string? serverPath = null) =>
        Status(AiConnectionState.Conflict, message, serverPath);

    private AiConnectionStatus Status(AiConnectionState state, string message, string? serverPath = null,
        bool restartRequired = false) =>
        new(Client, state, message, ConfigPath, serverPath, restartRequired);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
