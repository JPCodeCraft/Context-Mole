using System.Collections;
using System.Text;

using ContextMole.Core;

using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace ContextMole.Infrastructure;

public sealed class CodexMcpConfigurationService : IAiClientConnection
{
    private const string ServerName = "context-mole";
    private const string BeginMarker = "# BEGIN Context Mole managed MCP server";
    private const string EndMarker = "# END Context Mole managed MCP server";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string[] ServerKey = ["mcp_servers", ServerName];

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
        var analysis = Analyze(config);
        if (analysis.Error is not null) return Status(AiConnectionState.Conflict, analysis.Error);
        if (analysis.Block is not null)
        {
            var server = analysis.Block.Server;
            var restriction = GetLaunchRestriction(server);
            if (restriction is not null) return Status(AiConnectionState.Conflict, restriction);

            var configuredDataDirectory = server.TryGetValue("env", out var environment) && environment is TomlTable env
                ? GetFullPath(env, "CONTEXTMOLE_DATA_DIR")
                : null;
            if (!string.Equals(configuredDataDirectory, Path.GetFullPath(_appPaths.DataDirectory), PathComparison))
            {
                return Status(AiConnectionState.UpdateRequired,
                    "This OpenAI connection has missing or different Context Mole data-directory settings. Update it to use the current shared index.");
            }

            var serverPath = GetFullPath(server, "command");
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
            // Validate ownership before deployment or configuration changes. A textual marker alone is not ownership.
            var config = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
            var analysis = Analyze(config);
            if (analysis.Error is not null) return Status(AiConnectionState.Conflict, analysis.Error);
            if (analysis.Block is not null && GetLaunchRestriction(analysis.Block.Server) is { } restriction)
                return Status(AiConnectionState.Conflict, restriction);

            var candidate = _deployment.ResolveCandidate();
            if (candidate is null)
            {
                return Status(AiConnectionState.ServerUnavailable,
                    "The MCP server executable was not found. Publish or reinstall the application bundle, then try again.");
            }

            var serverPath = await _deployment.PrepareAsync(candidate, cancellationToken).ConfigureAwait(false);
            string updated;
            if (analysis.Block is { } block)
            {
                var text = config[block.Start..block.End];
                if (!TryUpdateLaunchSettings(text, serverPath, Path.GetFullPath(_appPaths.DataDirectory), out var updatedBlock))
                    return Status(AiConnectionState.Conflict,
                        "The managed OpenAI launch settings could not be updated safely without changing custom settings. The configuration was left unchanged.");
                updated = string.Concat(config.AsSpan(0, block.Start), updatedBlock, config.AsSpan(block.End));
            }
            else
            {
                updated = AppendBlock(config, BuildManagedBlock(serverPath));
            }

            // Validate the finished document too: appending a table can collide with an inline/closed parent table.
            var updatedAnalysis = Analyze(updated);
            if (updatedAnalysis.Error is not null || updatedAnalysis.Block is null)
                return Status(AiConnectionState.Conflict,
                    "The OpenAI configuration could not be updated safely. Its existing settings were left unchanged.");
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
            var analysis = Analyze(config);
            if (analysis.Error is not null) return Status(AiConnectionState.Conflict, analysis.Error);
            if (analysis.Block is not { } block)
                return Status(AiConnectionState.Disconnected, "Already not configured.");

            // Analyze has already proved that removing exactly this range preserves every unrelated TOML value.
            var updated = string.Concat(config.AsSpan(0, block.Start), config.AsSpan(block.End));
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
        CONTEXTMOLE_DATA_DIR = "{{EscapeToml(Path.GetFullPath(_appPaths.DataDirectory))}}"
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

    private static string? GetLaunchRestriction(TomlTable server)
    {
        if (server.TryGetValue("enabled", out var enabled))
        {
            if (enabled is false)
                return "This managed OpenAI connection is disabled in the client configuration. Enable it there before updating its launch settings.";
            if (enabled is not bool)
                return "The managed OpenAI connection has an invalid enabled setting. Correct it in the client configuration before updating.";
        }

        if (server.TryGetValue("args", out var args) &&
            (args is not TomlArray arguments || arguments.Any(argument => argument is not string)))
            return "The managed OpenAI connection has invalid command arguments. Correct them in the client configuration before updating.";
        if (server.TryGetValue("env", out var environment) &&
            (environment is not TomlTable env || env.Any(pair => pair.Value is not string)))
            return "The managed OpenAI connection has invalid environment settings. Correct them in the client configuration before updating.";
        if (server.ContainsKey("url"))
            return "The managed OpenAI connection also contains HTTP transport settings. Resolve them in the client configuration before updating the local stdio connection.";
        return null;
    }

    private static string? GetFullPath(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value) || value is not string path ||
            string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private sealed record ManagedBlock(int Start, int End, TomlTable Server);
    private sealed record Analysis(ManagedBlock? Block = null, string? Error = null);

    private static Analysis Analyze(string config)
    {
        if (!TryParse(config, out var document, out var model))
            return new(Error: "The OpenAI configuration is not valid TOML. Correct it before changing this connection; it was left unchanged.");

        var markers = document.Tokens(includeCommentsAndWhitespaces: true)
            .OfType<SyntaxTrivia>()
            .Where(trivia => trivia.Kind == TokenKind.Comment &&
                (trivia.Text!.TrimEnd() == BeginMarker || trivia.Text!.TrimEnd() == EndMarker) &&
                IsWholeLineComment(config, trivia.Span))
            .ToArray();
        var server = FindServer(model);
        if (markers.Length == 0)
        {
            if (server is not null)
                return new(Error: "An existing Context Mole entry is already present in the OpenAI configuration. It is not managed by this application and was left unchanged.");
            if (model.TryGetValue("mcp_servers", out var servers) && servers is not TomlTable)
                return new(Error: "The OpenAI mcp_servers setting is not a TOML table. Correct it before changing this connection; it was left unchanged.");
            return new();
        }

        if (markers.Length != 2 || markers[0].Text!.TrimEnd() != BeginMarker ||
            markers[1].Text!.TrimEnd() != EndMarker)
            return OwnershipConflict();

        var start = LineStart(config, markers[0].Span.Start.Offset);
        var end = LineEnd(config, markers[1].Span.End.Offset + 1);
        var blockText = config[start..end];
        if (!TryParse(blockText, out _, out var blockModel) || blockModel.Count != 1 ||
            !blockModel.TryGetValue("mcp_servers", out var blockServersValue) ||
            blockServersValue is not TomlTable blockServers || blockServers.Count != 1 ||
            !blockServers.TryGetValue(ServerName, out var blockServerValue) ||
            blockServerValue is not TomlTable blockServer || !ModelEquals(server, blockServer))
            return OwnershipConflict();

        var remainder = string.Concat(config.AsSpan(0, start), config.AsSpan(end));
        if (!TryParse(remainder, out _, out var remainderModel) || FindServer(remainderModel) is not null ||
            !UnrelatedSettingsEqual(model, remainderModel))
            return OwnershipConflict();
        return new(new(start, end, blockServer));
    }

    private static Analysis OwnershipConflict() => new(Error:
        "The OpenAI configuration has incomplete or ambiguous Context Mole ownership markers. Check the managed section before changing this connection; it was left unchanged.");

    private static bool TryParse(string text, out DocumentSyntax document, out TomlTable model)
    {
        document = new();
        model = new();
        try
        {
            document = SyntaxParser.Parse(text);
            if (document.HasErrors) return false;
            model = TomlSerializer.Deserialize<TomlTable>(text) ?? new();
            return true;
        }
        catch (TomlException)
        {
            return false;
        }
    }

    private static object? FindServer(TomlTable model) =>
        model.TryGetValue("mcp_servers", out var servers) && servers is TomlTable table &&
        table.TryGetValue(ServerName, out var server) ? server : null;

    private static bool UnrelatedSettingsEqual(TomlTable before, TomlTable after)
    {
        var beforeRoot = before.Where(pair => pair.Key != "mcp_servers").ToDictionary();
        var afterRoot = after.Where(pair => pair.Key != "mcp_servers").ToDictionary();
        if (!ModelEquals(beforeRoot, afterRoot)) return false;
        var beforeServers = before.TryGetValue("mcp_servers", out var first) && first is TomlTable firstTable
            ? firstTable.Where(pair => pair.Key != ServerName).ToDictionary() : [];
        var afterServers = after.TryGetValue("mcp_servers", out var second) && second is TomlTable secondTable
            ? secondTable.ToDictionary() : [];
        return ModelEquals(beforeServers, afterServers);
    }

    private static bool ModelEquals(object? first, object? second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first is null || second is null) return false;
        if (first is IDictionary<string, object> firstTable && second is IDictionary<string, object> secondTable)
            return firstTable.Count == secondTable.Count && firstTable.All(pair =>
                secondTable.TryGetValue(pair.Key, out var value) && ModelEquals(pair.Value, value));
        if (first is TomlTable firstToml && second is TomlTable secondToml)
            return firstToml.Count == secondToml.Count && firstToml.All(pair =>
                secondToml.TryGetValue(pair.Key, out var value) && ModelEquals(pair.Value, value));
        if (first is not string && first is IEnumerable firstItems && second is not string && second is IEnumerable secondItems)
        {
            var firstArray = firstItems.Cast<object>().ToArray();
            var secondArray = secondItems.Cast<object>().ToArray();
            return firstArray.Length == secondArray.Length && firstArray.Zip(secondArray).All(pair => ModelEquals(pair.First, pair.Second));
        }
        return first.Equals(second);
    }

    private static bool IsWholeLineComment(string text, SourceSpan span) =>
        text.AsSpan(LineStart(text, span.Start.Offset), span.Start.Offset - LineStart(text, span.Start.Offset)).Trim().IsEmpty;

    private static int LineStart(string text, int offset)
    {
        while (offset > 0 && text[offset - 1] is not ('\r' or '\n')) offset--;
        return offset;
    }

    private static int LineEnd(string text, int offset)
    {
        while (offset < text.Length && text[offset] is not ('\r' or '\n')) offset++;
        if (offset < text.Length && text[offset] == '\r') offset++;
        if (offset < text.Length && text[offset] == '\n') offset++;
        return offset;
    }

    private static bool TryUpdateLaunchSettings(string block, string serverPath, string dataDirectory, out string updated)
    {
        updated = block;
        // Reparse after each edit, so insertions and source offsets cannot overlap or move another setting.
        return TrySetString(ref updated, [.. ServerKey, "command"], serverPath) &&
            TrySetString(ref updated, [.. ServerKey, "env", "CONTEXTMOLE_DATA_DIR"], dataDirectory) &&
            TryParse(updated, out _, out _);
    }

    private sealed record Container(string[] Path, TableSyntaxBase? Table = null, InlineTableSyntax? Inline = null);
    private sealed record Assignment(string[] Path, ValueSyntax Value);

    private static bool TrySetString(ref string text, string[] path, string value)
    {
        if (!TryParse(text, out var document, out _)) return false;
        var assignments = new List<Assignment>();
        var containers = new List<Container> { new([]) };
        foreach (var pair in document.KeyValues) CollectAssignment(pair, [], assignments, containers);
        foreach (var table in document.Tables)
        {
            // Never insert a property into a table-array element. Existing value spans are still safe to patch.
            var tablePath = GetKey(table.Name!);
            if (table is TableSyntax) containers.Add(new(tablePath, Table: table));
            foreach (var pair in table.Items) CollectAssignment(pair, tablePath, assignments, containers);
        }

        var existing = assignments.SingleOrDefault(assignment => assignment.Path.SequenceEqual(path));
        var replacement = $"\"{EscapeToml(value)}\"";
        if (existing is not null)
        {
            if (existing.Value is StringValueSyntax literal && string.Equals(literal.Value, value, StringComparison.Ordinal))
                return true;
            var span = existing.Value.Span;
            text = string.Concat(text.AsSpan(0, span.Start.Offset), replacement, text.AsSpan(span.End.Offset + 1));
            return true;
        }

        var container = containers.Where(candidate => candidate.Path.Length < path.Length &&
            path.Take(candidate.Path.Length).SequenceEqual(candidate.Path)).MaxBy(candidate => candidate.Path.Length)!;
        var key = string.Join('.', path.Skip(container.Path.Length));
        int insertion;
        string addition;
        if (container.Inline is { } inline)
        {
            insertion = inline.CloseBrace!.Span.Start.Offset;
            var last = inline.Items.LastOrDefault();
            var separator = last is null || last.Comma is not null ? " " : ", ";
            addition = $"{separator}{key} = {replacement} ";
        }
        else
        {
            var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            insertion = container.Table is { } table
                ? LineEnd(text, table.CloseBracket!.Span.End.Offset + 1)
                : LineEnd(text, text.IndexOf(BeginMarker, StringComparison.Ordinal) + BeginMarker.Length);
            addition = $"{key} = {replacement}{newLine}";
        }
        text = string.Concat(text.AsSpan(0, insertion), addition, text.AsSpan(insertion));
        return TryParse(text, out _, out _);
    }

    private static void CollectAssignment(KeyValueSyntax pair, string[] parent, List<Assignment> assignments,
        List<Container> containers)
    {
        var path = parent.Concat(GetKey(pair.Key!)).ToArray();
        assignments.Add(new(path, pair.Value!));
        if (pair.Value is not InlineTableSyntax inline) return;
        containers.Add(new(path, Inline: inline));
        foreach (var item in inline.Items) CollectAssignment(item.KeyValue!, path, assignments, containers);
    }

    private static string[] GetKey(KeySyntax key) => new[] { KeyPart(key.Key!) }
        .Concat(key.DotKeys.Select(dot => KeyPart(dot.Key!))).ToArray();

    private static string KeyPart(BareKeyOrStringValueSyntax key) => key switch
    {
        BareKeySyntax bare => bare.Key!.Text!,
        StringValueSyntax quoted => quoted.Value!,
        _ => throw new InvalidOperationException("Unsupported TOML key syntax.")
    };

    private static string AppendBlock(string config, string block) => string.IsNullOrEmpty(config)
        ? block
        : config + (config.EndsWith('\n') ? Environment.NewLine : Environment.NewLine + Environment.NewLine) + block;

    private static string EscapeToml(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\b' => "\\b",
                '\f' => "\\f",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                < ' ' or '\u007f' => $"\\u{(int)character:X4}",
                _ => character.ToString()
            });
        }
        return builder.ToString();
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
