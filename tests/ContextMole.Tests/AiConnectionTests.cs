using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

using ContextMole.Infrastructure;

namespace ContextMole.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class AiConnectionTests
{
    [Fact]
    public async Task DevelopmentDeploymentMatchesUiBuildAndStagesMatchingBroker()
    {
        using var paths = new StorageTestPaths();
        var repository = Path.Combine(paths.RootDirectory, "repository");
        var currentRid = RuntimeInformation.RuntimeIdentifier;
        var competingRid = OperatingSystem.IsWindows() ? "linux-x64" : "win-x64";
        var appOutput = Path.Combine(repository, "src", "App.UI", "bin", "Debug", "net10.0", currentRid);
        var mcpBin = Path.Combine(repository, "src", "Mcp", "bin", "Debug", "net10.0");
        var brokerBin = Path.Combine(repository, "src", "Broker", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(appOutput);
        Directory.CreateDirectory(Path.Combine(mcpBin, currentRid));
        Directory.CreateDirectory(Path.Combine(mcpBin, competingRid));
        Directory.CreateDirectory(Path.Combine(brokerBin, currentRid));
        Directory.CreateDirectory(Path.Combine(brokerBin, competingRid));
        File.WriteAllText(Path.Combine(repository, "ContextMole.slnx"), "<Solution />");
        var executableName = OperatingSystem.IsWindows() ? "ContextMole.Mcp.exe" : "ContextMole.Mcp";
        var expected = Path.Combine(mcpBin, currentRid, executableName);
        var incompatible = Path.Combine(mcpBin, competingRid, executableName);
        var brokerExecutableName = OperatingSystem.IsWindows() ? "ContextMole.Broker.exe" : "ContextMole.Broker";
        var expectedBroker = Path.Combine(brokerBin, currentRid, brokerExecutableName);
        var incompatibleBroker = Path.Combine(brokerBin, competingRid, brokerExecutableName);
        File.WriteAllText(expected, "current RID MCP");
        File.WriteAllText(incompatible, "competing RID MCP");
        File.WriteAllText(expectedBroker, "current RID broker");
        File.WriteAllText(Path.Combine(brokerBin, currentRid, "broker-dependency.dll"), "broker dependency");
        File.WriteAllText(Path.Combine(brokerBin, currentRid, "external-native-symbols.pdb"), "not staged");
        var currentNative = Path.Combine(brokerBin, currentRid, "runtimes", currentRid, "native");
        var incompatibleNative = Path.Combine(brokerBin, currentRid, "runtimes", competingRid, "native");
        Directory.CreateDirectory(currentNative);
        Directory.CreateDirectory(incompatibleNative);
        File.WriteAllText(Path.Combine(currentNative, "current-native.bin"), "current native");
        File.WriteAllText(Path.Combine(incompatibleNative, "wrong-native.bin"), "wrong native");
        File.WriteAllText(incompatibleBroker, "competing RID broker");
        File.SetLastWriteTimeUtc(incompatible, DateTime.UtcNow.AddMinutes(2));
        File.SetLastWriteTimeUtc(incompatibleBroker, DateTime.UtcNow.AddMinutes(2));
        var previousOverride = Environment.GetEnvironmentVariable("CONTEXTMOLE_MCP_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", null);
            var candidate = new McpServerDeploymentService(paths).ResolveCandidateFromDirectory(appOutput);

            Assert.NotNull(candidate);
            Assert.Equal(Path.GetFullPath(expected), candidate.Path);
            Assert.True(candidate.RequiresStaging);
            Assert.Equal(Path.GetFullPath(expectedBroker), candidate.BrokerPath);
            var stagedExecutable = await new McpServerDeploymentService(paths).PrepareAsync(candidate,
                TestContext.Current.CancellationToken);
            Assert.True(File.Exists(stagedExecutable));
            var stagedDirectory = Path.GetDirectoryName(stagedExecutable)!;
            Assert.True(File.Exists(Path.Combine(stagedDirectory, "broker", brokerExecutableName)));
            Assert.True(File.Exists(Path.Combine(stagedDirectory, "broker", "broker-dependency.dll")));
            Assert.True(File.Exists(Path.Combine(stagedDirectory, "broker", "runtimes", currentRid, "native",
                "current-native.bin")));
            Assert.False(File.Exists(Path.Combine(stagedDirectory, "broker", "runtimes", competingRid, "native",
                "wrong-native.bin")));
            Assert.False(File.Exists(Path.Combine(stagedDirectory, "broker", "external-native-symbols.pdb")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", previousOverride);
        }
    }

    [Fact]
    public async Task JsonConnectionPreservesUnrelatedSettingsAndRemovesOnlyOwnedEntry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new StorageTestPaths();
        var configPath = Path.Combine(paths.DataDirectory, "client", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, """
            {
              "theme": "dark",
              "mcpServers": {
                "other-server": {
                  "command": "other"
                }
              }
            }
            """, cancellationToken);

        var server = await CreateServerAsync(paths, cancellationToken);
        var previousOverride = Environment.GetEnvironmentVariable("CONTEXTMOLE_MCP_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", server);
            var deployment = new McpServerDeploymentService(paths);
            var service = new JsonMcpConfigurationService(
                new AiClientDefinition("test", "Test Client", "Test client"),
                configPath, "mcpServers", paths, deployment, transportType: "stdio");

            var connected = await service.ConnectAsync(cancellationToken);
            Assert.Equal(AiConnectionState.Connected, connected.State);
            Assert.True(connected.RestartRequired);

            var root = JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken))!.AsObject();
            Assert.Equal("dark", root["theme"]!.GetValue<string>());
            Assert.Equal("other", root["mcpServers"]!["other-server"]!["command"]!.GetValue<string>());
            var entry = root["mcpServers"]!["context-mole"]!.AsObject();
            Assert.Equal("stdio", entry["type"]!.GetValue<string>());
            Assert.Equal(Path.GetFullPath(server), entry["command"]!.GetValue<string>());
            Assert.Equal(paths.DataDirectory, entry["env"]!["CONTEXTMOLE_DATA_DIR"]!.GetValue<string>());
            Assert.Equal("1", entry["env"]!["CONTEXTMOLE_MANAGED_CONNECTION"]!.GetValue<string>());
            Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(configPath)!, "*.bak"));

            var status = await service.GetStatusAsync(cancellationToken);
            Assert.Equal(AiConnectionState.Connected, status.State);

            var disconnected = await service.DisconnectAsync(cancellationToken);
            Assert.Equal(AiConnectionState.Disconnected, disconnected.State);
            root = JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken))!.AsObject();
            Assert.Null(root["mcpServers"]!["context-mole"]);
            Assert.Equal("other", root["mcpServers"]!["other-server"]!["command"]!.GetValue<string>());
            Assert.Equal(2, Directory.EnumerateFiles(Path.GetDirectoryName(configPath)!, "*.bak").Count());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", previousOverride);
        }
    }

    [Fact]
    public async Task JsonConnectionNeverOverwritesUnmanagedEntry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new StorageTestPaths();
        var configPath = Path.Combine(paths.DataDirectory, "client", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        const string existing = """
            {
              "mcpServers": {
                "context-mole": {
                  "command": "user-owned"
                }
              }
            }
            """;
        await File.WriteAllTextAsync(configPath, existing, cancellationToken);

        var server = await CreateServerAsync(paths, cancellationToken);
        var previousOverride = Environment.GetEnvironmentVariable("CONTEXTMOLE_MCP_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", server);
            var service = new JsonMcpConfigurationService(
                new AiClientDefinition("test", "Test Client", "Test client"),
                configPath, "mcpServers", paths, new McpServerDeploymentService(paths));

            Assert.Equal(AiConnectionState.Conflict, (await service.GetStatusAsync(cancellationToken)).State);
            Assert.Equal(AiConnectionState.Conflict, (await service.ConnectAsync(cancellationToken)).State);
            Assert.Equal(existing, await File.ReadAllTextAsync(configPath, cancellationToken));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(configPath)!, "*.bak"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", previousOverride);
        }
    }

    [Fact]
    public async Task JsonConnectionLeavesMalformedConfigurationUntouched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new StorageTestPaths();
        var configPath = Path.Combine(paths.DataDirectory, "client", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        const string malformed = "{ not-json";
        await File.WriteAllTextAsync(configPath, malformed, cancellationToken);

        var server = await CreateServerAsync(paths, cancellationToken);
        var previousOverride = Environment.GetEnvironmentVariable("CONTEXTMOLE_MCP_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", server);
            var service = new JsonMcpConfigurationService(
                new AiClientDefinition("test", "Test Client", "Test client"),
                configPath, "mcpServers", paths, new McpServerDeploymentService(paths));

            Assert.Equal(AiConnectionState.Conflict, (await service.ConnectAsync(cancellationToken)).State);
            Assert.Equal(malformed, await File.ReadAllTextAsync(configPath, cancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", previousOverride);
        }
    }

    [Fact]
    public async Task JsonConnectionRepairsInvalidManagedCommand()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new StorageTestPaths();
        var configPath = Path.Combine(paths.DataDirectory, "client", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["context-mole"] = new JsonObject
                {
                    ["command"] = "\0",
                    ["env"] = new JsonObject
                    {
                        ["CONTEXTMOLE_MANAGED_CONNECTION"] = "1",
                        ["CONTEXTMOLE_DATA_DIR"] = paths.DataDirectory
                    }
                }
            }
        };
        await File.WriteAllTextAsync(configPath, root.ToJsonString(), cancellationToken);

        var server = await CreateServerAsync(paths, cancellationToken);
        var previousOverride = Environment.GetEnvironmentVariable("CONTEXTMOLE_MCP_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", server);
            var service = new JsonMcpConfigurationService(
                new AiClientDefinition("test", "Test Client", "Test client"),
                configPath, "mcpServers", paths, new McpServerDeploymentService(paths));

            Assert.Equal(AiConnectionState.UpdateRequired, (await service.GetStatusAsync(cancellationToken)).State);
            Assert.Equal(AiConnectionState.Connected, (await service.ConnectAsync(cancellationToken)).State);

            root = JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken))!.AsObject();
            Assert.Equal(Path.GetFullPath(server),
                root["mcpServers"]!["context-mole"]!["command"]!.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", previousOverride);
        }
    }

    [Fact]
    public async Task JsonConnectionUpdatesManagedDataDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new StorageTestPaths();
        var configPath = Path.Combine(paths.DataDirectory, "client", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var server = await CreateServerAsync(paths, cancellationToken);
        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["context-mole"] = new JsonObject
                {
                    ["command"] = server,
                    ["env"] = new JsonObject
                    {
                        ["CONTEXTMOLE_MANAGED_CONNECTION"] = "1",
                        ["CONTEXTMOLE_DATA_DIR"] = Path.Combine(paths.DataDirectory, "old-index")
                    }
                }
            }
        };
        await File.WriteAllTextAsync(configPath, root.ToJsonString(), cancellationToken);

        var previousOverride = Environment.GetEnvironmentVariable("CONTEXTMOLE_MCP_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", server);
            var service = new JsonMcpConfigurationService(
                new AiClientDefinition("test", "Test Client", "Test client"),
                configPath, "mcpServers", paths, new McpServerDeploymentService(paths));

            Assert.Equal(AiConnectionState.UpdateRequired, (await service.GetStatusAsync(cancellationToken)).State);
            Assert.Equal(AiConnectionState.Connected, (await service.ConnectAsync(cancellationToken)).State);

            root = JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken))!.AsObject();
            Assert.Equal(paths.DataDirectory,
                root["mcpServers"]!["context-mole"]!["env"]!["CONTEXTMOLE_DATA_DIR"]!.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", previousOverride);
        }
    }

    [Fact]
    public void CatalogIncludesPopularClientsAndMarksUnstablePathsAsManual()
    {
        using var paths = new StorageTestPaths();
        var service = new AiConnectionsService(paths, new McpServerDeploymentService(paths));
        var clients = service.Clients.ToDictionary(client => client.Id, StringComparer.Ordinal);

        Assert.Contains("codex", clients.Keys);
        Assert.Contains("claude-code", clients.Keys);
        Assert.Contains("claude-desktop", clients.Keys);
        Assert.Contains("cursor", clients.Keys);
        Assert.Contains("zed", clients.Keys);
        Assert.Contains("vscode", clients.Keys);
        Assert.Contains("copilot-cli", clients.Keys);
        Assert.Contains("gemini-cli", clients.Keys);
        Assert.Contains("google-antigravity", clients.Keys);
        Assert.Contains("kiro", clients.Keys);
        Assert.Contains("jetbrains-junie", clients.Keys);
        Assert.Contains("devin", clients.Keys);
        Assert.Contains("windsurf", clients.Keys);
        Assert.Contains("cline", clients.Keys);
        Assert.Contains("roo-code", clients.Keys);
        Assert.Contains("opencode", clients.Keys);
        Assert.False(clients["vscode"].SupportsAutomaticSetup);
        Assert.False(clients["roo-code"].SupportsAutomaticSetup);
        Assert.False(clients["opencode"].SupportsAutomaticSetup);
    }

    [Fact]
    public async Task CatalogHonorsOfficialClientHomeOverrides()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new StorageTestPaths();
        var overrides = new Dictionary<string, string?>
        {
            ["CLAUDE_CONFIG_DIR"] = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
            ["COPILOT_HOME"] = Environment.GetEnvironmentVariable("COPILOT_HOME"),
            ["GEMINI_CLI_HOME"] = Environment.GetEnvironmentVariable("GEMINI_CLI_HOME"),
            ["CLINE_DATA_DIR"] = Environment.GetEnvironmentVariable("CLINE_DATA_DIR"),
            ["CLINE_DIR"] = Environment.GetEnvironmentVariable("CLINE_DIR"),
            ["CLINE_MCP_SETTINGS_PATH"] = Environment.GetEnvironmentVariable("CLINE_MCP_SETTINGS_PATH"),
            ["KIRO_HOME"] = Environment.GetEnvironmentVariable("KIRO_HOME"),
            ["JUNIE_HOME"] = Environment.GetEnvironmentVariable("JUNIE_HOME")
        };
        var clientHomes = new[]
            {
                "CLAUDE_CONFIG_DIR", "COPILOT_HOME", "GEMINI_CLI_HOME", "CLINE_DATA_DIR", "KIRO_HOME",
                "JUNIE_HOME"
            }.ToDictionary(
            name => name,
            name => Path.Combine(paths.DataDirectory, name.ToLowerInvariant()),
            StringComparer.Ordinal);
        try
        {
            Environment.SetEnvironmentVariable("CLINE_DIR", null);
            Environment.SetEnvironmentVariable("CLINE_MCP_SETTINGS_PATH", null);
            foreach (var (name, value) in clientHomes) Environment.SetEnvironmentVariable(name, value);
            var service = new AiConnectionsService(paths, new McpServerDeploymentService(paths));

            Assert.Equal(Path.Combine(clientHomes["CLAUDE_CONFIG_DIR"], ".claude.json"),
                (await service.GetStatusAsync("claude-code", cancellationToken)).ConfigPath);
            Assert.Equal(Path.Combine(clientHomes["COPILOT_HOME"], "mcp-config.json"),
                (await service.GetStatusAsync("copilot-cli", cancellationToken)).ConfigPath);
            Assert.Equal(Path.Combine(clientHomes["GEMINI_CLI_HOME"], ".gemini", "settings.json"),
                (await service.GetStatusAsync("gemini-cli", cancellationToken)).ConfigPath);
            Assert.Equal(Path.Combine(clientHomes["CLINE_DATA_DIR"], "settings", "cline_mcp_settings.json"),
                (await service.GetStatusAsync("cline", cancellationToken)).ConfigPath);
            Assert.Equal(Path.Combine(clientHomes["KIRO_HOME"], "settings", "mcp.json"),
                (await service.GetStatusAsync("kiro", cancellationToken)).ConfigPath);
            Assert.Equal(Path.Combine(clientHomes["JUNIE_HOME"], "mcp", "mcp.json"),
                (await service.GetStatusAsync("jetbrains-junie", cancellationToken)).ConfigPath);
        }
        finally
        {
            foreach (var (name, value) in overrides) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task CatalogUsesClineSettingsPathPrecedence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new StorageTestPaths();
        var variableNames = new[] { "CLINE_MCP_SETTINGS_PATH", "CLINE_DATA_DIR", "CLINE_DIR" };
        var previous = variableNames.ToDictionary(name => name, Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        var explicitSettings = Path.Combine(paths.DataDirectory, "explicit", "cline.json");
        var dataDirectory = Path.Combine(paths.DataDirectory, "data-override");
        var clineDirectory = Path.Combine(paths.DataDirectory, "cline-override");
        try
        {
            Environment.SetEnvironmentVariable("CLINE_MCP_SETTINGS_PATH", explicitSettings);
            Environment.SetEnvironmentVariable("CLINE_DATA_DIR", dataDirectory);
            Environment.SetEnvironmentVariable("CLINE_DIR", clineDirectory);
            var service = new AiConnectionsService(paths, new McpServerDeploymentService(paths));
            Assert.Equal(explicitSettings,
                (await service.GetStatusAsync("cline", cancellationToken)).ConfigPath);

            Environment.SetEnvironmentVariable("CLINE_MCP_SETTINGS_PATH", null);
            service = new AiConnectionsService(paths, new McpServerDeploymentService(paths));
            Assert.Equal(Path.Combine(dataDirectory, "settings", "cline_mcp_settings.json"),
                (await service.GetStatusAsync("cline", cancellationToken)).ConfigPath);

            Environment.SetEnvironmentVariable("CLINE_DATA_DIR", null);
            service = new AiConnectionsService(paths, new McpServerDeploymentService(paths));
            Assert.Equal(Path.Combine(clineDirectory, "data", "settings", "cline_mcp_settings.json"),
                (await service.GetStatusAsync("cline", cancellationToken)).ConfigPath);
        }
        finally
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static async Task<string> CreateServerAsync(StorageTestPaths paths, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(paths.DataDirectory, "server");
        Directory.CreateDirectory(directory);
        var server = Path.Combine(directory,
            OperatingSystem.IsWindows() ? "ContextMole.Mcp.exe" : "ContextMole.Mcp");
        await File.WriteAllTextAsync(server, "test server", cancellationToken);
        return server;
    }
}
