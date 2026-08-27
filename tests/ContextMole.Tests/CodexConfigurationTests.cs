using ContextMole.Infrastructure;

namespace ContextMole.Tests;

[CollectionDefinition(nameof(ProcessEnvironmentCollection), DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
}

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class CodexConfigurationTests
{
    [Fact]
    public async Task ConnectPreservesUnrelatedCodexSettings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new StorageTestPaths();
        var codexHome = Path.Combine(paths.DataDirectory, "codex-home");
        var serverDirectory = Path.Combine(paths.DataDirectory, "server");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(serverDirectory);
        var server = Path.Combine(serverDirectory,
            OperatingSystem.IsWindows() ? "ContextMole.Mcp.exe" : "ContextMole.Mcp");
        await File.WriteAllTextAsync(server, "test server", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(codexHome, "config.toml"), "model = \"gpt-test\"\n",
            cancellationToken);

        var previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var previousOverride = Environment.GetEnvironmentVariable("CONTEXTMOLE_MCP_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", server);

            var service = new CodexMcpConfigurationService(paths);
            var connected = await service.ConnectAsync(cancellationToken);
            Assert.Equal(AiConnectionState.Connected, connected.State);
            Assert.True(connected.RestartRequired);
            Assert.Equal(Path.GetFullPath(server), connected.ServerPath);

            var config = await File.ReadAllTextAsync(service.ConfigPath, cancellationToken);
            Assert.Contains("model = \"gpt-test\"", config, StringComparison.Ordinal);
            Assert.Contains("# BEGIN Context Mole managed MCP server", config, StringComparison.Ordinal);
            Assert.Contains("# END Context Mole managed MCP server", config, StringComparison.Ordinal);
            Assert.Contains("[mcp_servers.context-mole]", config, StringComparison.Ordinal);
            Assert.Contains("[mcp_servers.context-mole.env]", config, StringComparison.Ordinal);
            Assert.Contains("CONTEXTMOLE_DATA_DIR", config, StringComparison.Ordinal);

            var disconnected = await service.DisconnectAsync(cancellationToken);
            Assert.Equal(AiConnectionState.Disconnected, disconnected.State);
            var remaining = await File.ReadAllTextAsync(service.ConfigPath, cancellationToken);
            Assert.Contains("model = \"gpt-test\"", remaining, StringComparison.Ordinal);
            Assert.DoesNotContain("Context Mole managed MCP server", remaining, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousCodexHome);
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", previousOverride);
        }
    }

    [Fact]
    public async Task ConnectRepairsInvalidManagedCodexCommand()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new StorageTestPaths();
        var codexHome = Path.Combine(paths.DataDirectory, "codex-home");
        var serverDirectory = Path.Combine(paths.DataDirectory, "server");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(serverDirectory);
        var server = Path.Combine(serverDirectory,
            OperatingSystem.IsWindows() ? "ContextMole.Mcp.exe" : "ContextMole.Mcp");
        await File.WriteAllTextAsync(server, "test server", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(codexHome, "config.toml"), """
            # BEGIN Context Mole managed MCP server
            [mcp_servers.context-mole]
            command = "\q"

            [mcp_servers.context-mole.env]
            CONTEXTMOLE_DATA_DIR = "{{DATA_DIRECTORY}}"
            # END Context Mole managed MCP server
            """.Replace("{{DATA_DIRECTORY}}", EscapeToml(paths.DataDirectory), StringComparison.Ordinal),
            cancellationToken);

        var previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var previousOverride = Environment.GetEnvironmentVariable("CONTEXTMOLE_MCP_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", server);

            var service = new CodexMcpConfigurationService(paths);
            Assert.Equal(AiConnectionState.UpdateRequired, (await service.GetStatusAsync(cancellationToken)).State);
            Assert.Equal(AiConnectionState.Connected, (await service.ConnectAsync(cancellationToken)).State);
            Assert.Contains(Path.GetFileName(server), await File.ReadAllTextAsync(service.ConfigPath, cancellationToken),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousCodexHome);
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", previousOverride);
        }
    }

    [Fact]
    public async Task ConnectUpdatesManagedCodexDataDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new StorageTestPaths();
        var codexHome = Path.Combine(paths.DataDirectory, "codex-home");
        var serverDirectory = Path.Combine(paths.DataDirectory, "server");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(serverDirectory);
        var server = Path.Combine(serverDirectory,
            OperatingSystem.IsWindows() ? "ContextMole.Mcp.exe" : "ContextMole.Mcp");
        await File.WriteAllTextAsync(server, "test server", cancellationToken);
        var config = $$"""
            # BEGIN Context Mole managed MCP server
            [mcp_servers.context-mole]
            command = "{{EscapeToml(server)}}"

            [mcp_servers.context-mole.env]
            CONTEXTMOLE_DATA_DIR = "{{EscapeToml(Path.Combine(paths.DataDirectory, "old-index"))}}"
            # END Context Mole managed MCP server
            """;
        await File.WriteAllTextAsync(Path.Combine(codexHome, "config.toml"), config, cancellationToken);

        var previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var previousOverride = Environment.GetEnvironmentVariable("CONTEXTMOLE_MCP_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", server);

            var service = new CodexMcpConfigurationService(paths);
            Assert.Equal(AiConnectionState.UpdateRequired, (await service.GetStatusAsync(cancellationToken)).State);
            Assert.Equal(AiConnectionState.Connected, (await service.ConnectAsync(cancellationToken)).State);
            Assert.Contains(EscapeToml(paths.DataDirectory),
                await File.ReadAllTextAsync(service.ConfigPath, cancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousCodexHome);
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", previousOverride);
        }
    }

    private static string EscapeToml(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}