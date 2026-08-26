#:property TargetFramework=net10.0
#:project ../src/Core/MCPIndexSearch.Core.csproj
#:project ../src/Infrastructure/MCPIndexSearch.Infrastructure.csproj

using MCPIndexSearch.Infrastructure;

var data = Environment.GetEnvironmentVariable("MCPINDEXSEARCH_DATA_DIR")
    ?? throw new InvalidOperationException("Set MCPINDEXSEARCH_DATA_DIR to an isolated smoke directory.");
var codexHome = Path.Combine(data, "codex-home");
Directory.CreateDirectory(codexHome);
Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
var developmentRoot = Path.Combine(data, "development-root");
var serverDirectory = Path.Combine(developmentRoot, "src", "Mcp", "bin", "Debug", "net10.0");
Directory.CreateDirectory(serverDirectory);
await File.WriteAllTextAsync(Path.Combine(developmentRoot, "MCPIndexSearch.slnx"), "<Solution />");
var server = Path.Combine(serverDirectory, OperatingSystem.IsWindows() ? "MCPIndexSearch.Mcp.exe" : "MCPIndexSearch.Mcp");
await File.WriteAllTextAsync(server, "smoke placeholder");
var dependency = Path.Combine(serverDirectory, "MCPIndexSearch.Core.dll");
await File.WriteAllTextAsync(dependency, "dependency placeholder");
Environment.SetEnvironmentVariable("MCPINDEXSEARCH_MCP_PATH", server);
var configPath = Path.Combine(codexHome, "config.toml");
const string preserved = "# user setting\nmodel = \"gpt-5\"\n";
var escapedServer = EscapeToml(server);
var legacyManaged = $"""
    {preserved.TrimEnd()}

    # BEGIN MCPIndexSearch managed MCP server
    [mcp_servers.mcp-index-search]
    command = "{escapedServer}"
    enabled = true

    [mcp_servers.mcp-index-search.env]
    MCPINDEXSEARCH_DATA_DIR = "{EscapeToml(data)}"
    # END MCPIndexSearch managed MCP server
    """ + Environment.NewLine;
await File.WriteAllTextAsync(configPath, legacyManaged);

var service = new CodexMcpConfigurationService(new AppPaths());
var legacyStatus = await service.GetStatusAsync();
if (legacyStatus.State != CodexMcpConnectionState.UpdateRequired)
    throw new InvalidOperationException($"Development output was not marked for migration: {legacyStatus.State}");
var connected = await service.ConnectAsync();
if (connected.State != CodexMcpConnectionState.Connected || !connected.RestartRequired)
    throw new InvalidOperationException($"Connect failed: {connected.State} {connected.Message}");
var stagedRoot = Path.Combine(data, "mcp-server", "deployments");
if (connected.ServerPath is null ||
    string.Equals(connected.ServerPath, server, StringComparison.OrdinalIgnoreCase) ||
    !Path.GetFullPath(connected.ServerPath).StartsWith(Path.GetFullPath(stagedRoot) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase) ||
    !File.Exists(Path.Combine(Path.GetDirectoryName(connected.ServerPath)!, Path.GetFileName(dependency))))
    throw new InvalidOperationException("The development MCP server was not staged with its dependencies.");
var configured = await File.ReadAllTextAsync(configPath);
if (!configured.Contains(preserved.TrimEnd(), StringComparison.Ordinal) ||
    !configured.Contains("# BEGIN MCPIndexSearch managed MCP server", StringComparison.Ordinal) ||
    !configured.Contains("MCPINDEXSEARCH_DATA_DIR", StringComparison.Ordinal) ||
    configured.Contains($"command = \"{escapedServer}\"", StringComparison.Ordinal))
    throw new InvalidOperationException("The managed configuration block or preserved user content is missing.");

var idempotent = await service.ConnectAsync();
if (idempotent.RestartRequired || idempotent.ServerPath != connected.ServerPath ||
    await File.ReadAllTextAsync(configPath) != configured)
    throw new InvalidOperationException("A repeated connect was not idempotent.");
var stableStatus = await service.GetStatusAsync();
if (stableStatus.State != CodexMcpConnectionState.Connected)
    throw new InvalidOperationException($"An unchanged staged deployment was not connected: {stableStatus.State}");

await File.AppendAllTextAsync(dependency, " updated");
var upgradeStatus = await service.GetStatusAsync();
if (upgradeStatus.State != CodexMcpConnectionState.UpdateRequired)
    throw new InvalidOperationException($"A changed development build was not offered as an update: {upgradeStatus.State}");
var upgraded = await service.ConnectAsync();
var upgradedConfig = await File.ReadAllTextAsync(configPath);
if (!upgraded.RestartRequired || upgraded.ServerPath is null || upgraded.ServerPath == connected.ServerPath ||
    upgradedConfig == configured || !File.Exists(upgraded.ServerPath))
    throw new InvalidOperationException("A changed development build did not create a versioned deployment.");

var disconnected = await service.DisconnectAsync();
var afterDisconnect = await File.ReadAllTextAsync(configPath);
if (disconnected.State != CodexMcpConnectionState.Disconnected ||
    afterDisconnect.Contains("MCPIndexSearch managed MCP server", StringComparison.Ordinal) ||
    !afterDisconnect.Contains(preserved.TrimEnd(), StringComparison.Ordinal))
    throw new InvalidOperationException("Disconnect did not remove only the managed block.");
if (Directory.EnumerateFiles(codexHome, "*.bak").Count() != 3)
    throw new InvalidOperationException("Expected safety backups for migration, upgrade, and disconnect.");

var conflictText = afterDisconnect + "\n[mcp_servers.mcp-index-search]\ncommand = \"custom-server\"\n";
await File.WriteAllTextAsync(configPath, conflictText);
var conflict = await service.ConnectAsync();
if (conflict.State != CodexMcpConnectionState.Conflict || await File.ReadAllTextAsync(configPath) != conflictText)
    throw new InvalidOperationException("An unmanaged conflicting server entry was not preserved.");

await File.WriteAllTextAsync(configPath, afterDisconnect);
var reconnected = await service.ConnectAsync();
if (reconnected.State != CodexMcpConnectionState.Connected)
    throw new InvalidOperationException("Could not regenerate a final managed configuration for parser inspection.");

Console.WriteLine($"CODEX_CONFIGURATION_SMOKE_OK backup=3 conflict=preserved staged=versioned config={configPath}");

static string EscapeToml(string value) => value
    .Replace("\\", "\\\\", StringComparison.Ordinal)
    .Replace("\"", "\\\"", StringComparison.Ordinal)
    .Replace("\r", "\\r", StringComparison.Ordinal)
    .Replace("\n", "\\n", StringComparison.Ordinal)
    .Replace("\t", "\\t", StringComparison.Ordinal);
