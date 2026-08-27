using ContextMole.Infrastructure;

namespace ContextMole.Tests;

[CollectionDefinition(nameof(ProcessEnvironmentCollection), DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
}

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class CodexConfigurationCompatibilityTests
{
    [Fact]
    public async Task ConnectPreservesLegacyCodexConfigurationIdentifiers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new StorageTestPaths();
        var codexHome = Path.Combine(paths.DataDirectory, "codex-home");
        var serverDirectory = Path.Combine(paths.DataDirectory, "server");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(serverDirectory);
        var server = Path.Combine(serverDirectory,
            OperatingSystem.IsWindows() ? "MCPIndexSearch.Mcp.exe" : "MCPIndexSearch.Mcp");
        await File.WriteAllTextAsync(server, "test server", cancellationToken);

        var previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var previousCurrentOverride = Environment.GetEnvironmentVariable("CONTEXTMOLE_MCP_PATH");
        var previousLegacyOverride = Environment.GetEnvironmentVariable("MCPINDEXSEARCH_MCP_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", server);
            Environment.SetEnvironmentVariable("MCPINDEXSEARCH_MCP_PATH", null);

            var service = new CodexMcpConfigurationService(paths);
            var connected = await service.ConnectAsync(cancellationToken);
            Assert.Equal(CodexMcpConnectionState.Connected, connected.State);
            Assert.True(connected.RestartRequired);
            Assert.Equal(Path.GetFullPath(server), connected.ServerPath);

            var config = await File.ReadAllTextAsync(service.ConfigPath, cancellationToken);
            Assert.Contains("# BEGIN MCPIndexSearch managed MCP server", config, StringComparison.Ordinal);
            Assert.Contains("# END MCPIndexSearch managed MCP server", config, StringComparison.Ordinal);
            Assert.Contains("[mcp_servers.mcp-index-search]", config, StringComparison.Ordinal);
            Assert.Contains("[mcp_servers.mcp-index-search.env]", config, StringComparison.Ordinal);
            Assert.Contains("MCPINDEXSEARCH_DATA_DIR", config, StringComparison.Ordinal);
            Assert.DoesNotContain("ContextMole managed MCP server", config, StringComparison.Ordinal);
            Assert.DoesNotContain("[mcp_servers.context-mole]", config, StringComparison.Ordinal);
            Assert.DoesNotContain("CONTEXTMOLE_DATA_DIR", config, StringComparison.Ordinal);

            var disconnected = await service.DisconnectAsync(cancellationToken);
            Assert.Equal(CodexMcpConnectionState.Disconnected, disconnected.State);
            Assert.DoesNotContain("MCPIndexSearch managed MCP server",
                await File.ReadAllTextAsync(service.ConfigPath, cancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousCodexHome);
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", previousCurrentOverride);
            Environment.SetEnvironmentVariable("MCPINDEXSEARCH_MCP_PATH", previousLegacyOverride);
        }
    }
}