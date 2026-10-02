using System.Text.Json.Nodes;

using ContextMole.Core;
using ContextMole.Infrastructure;

namespace ContextMole.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class JsonMcpConfigurationRegressionTests
{
    [Fact]
    public async Task DisabledManagedConnectionIsNotReportedConnectedOrSilentlyEnabled()
    {
        using var fixture = new JsonConnectionFixture();
        var entry = fixture.ManagedEntry();
        entry["disabled"] = true;
        entry["command"] = "old-command";
        entry["env"]!["CONTEXTMOLE_DATA_DIR"] = "old-index";
        var original = await fixture.WriteEntryAsync(entry);

        await AssertConflictWithoutWriteAsync(fixture, original, "disabled");

        var removed = await fixture.Service.DisconnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AiConnectionState.Disconnected, removed.State);
        Assert.Null((await fixture.ReadRootAsync())["mcpServers"]!["context-mole"]);
        Assert.Equal("other", (await fixture.ReadRootAsync())["mcpServers"]!["other-server"]!["command"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    public async Task InvalidDisabledValueIsAnActionableConflict(string value)
    {
        using var fixture = new JsonConnectionFixture();
        var entry = fixture.ManagedEntry();
        entry["disabled"] = JsonNode.Parse(value);
        var original = await fixture.WriteEntryAsync(entry);

        await AssertConflictWithoutWriteAsync(fixture, original, "boolean");
    }

    [Theory]
    [InlineData("type", "\"sse\"")]
    [InlineData("type", "\"streamableHttp\"")]
    [InlineData("type", "\"http\"")]
    [InlineData("type", "\"local\"")]
    [InlineData("type", "null")]
    [InlineData("type", "42")]
    [InlineData("transportType", "\"sse\"")]
    [InlineData("transportType", "\"http\"")]
    [InlineData("transportType", "null")]
    [InlineData("url", "\"https://example.invalid/mcp\"")]
    [InlineData("url", "null")]
    [InlineData("transport", "{\"type\":\"sse\",\"url\":\"https://example.invalid/mcp\"}")]
    [InlineData("transport", "{\"type\":\"stdio\",\"command\":\"another-command\"}")]
    public async Task IncompatibleOrAmbiguousTransportCannotLookConfiguredOrBeOverwritten(string property, string value)
    {
        using var fixture = new JsonConnectionFixture();
        var entry = fixture.ManagedEntry();
        entry[property] = JsonNode.Parse(value);
        var original = await fixture.WriteEntryAsync(entry);

        await AssertConflictWithoutWriteAsync(fixture, original, "local");
    }

    [Theory]
    [InlineData(null, "stdio")]
    [InlineData("stdio", "stdio")]
    [InlineData("local", "local")]
    [InlineData("local", "stdio")]
    public async Task SupportedExplicitTransportRemainsConfigured(string? requiredTransport, string configuredTransport)
    {
        using var fixture = new JsonConnectionFixture(requiredTransport);
        var entry = fixture.ManagedEntry();
        entry["type"] = configuredTransport;
        await fixture.WriteEntryAsync(entry);

        Assert.Equal(AiConnectionState.Connected,
            (await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken)).State);
        Assert.Equal(AiConnectionState.Connected,
            (await fixture.Service.ConnectAsync(TestContext.Current.CancellationToken)).State);
        Assert.Equal(configuredTransport, (await fixture.ReadEntryAsync())["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task StdioClientDoesNotAcceptCopilotLocalAlias()
    {
        using var fixture = new JsonConnectionFixture("stdio");
        var entry = fixture.ManagedEntry();
        entry["type"] = "local";
        var original = await fixture.WriteEntryAsync(entry);

        await AssertConflictWithoutWriteAsync(fixture, original, "stdio");
    }

    [Fact]
    public async Task StdioLegacyTransportTypeRemainsConfigured()
    {
        using var fixture = new JsonConnectionFixture();
        var entry = fixture.ManagedEntry();
        entry["transportType"] = "stdio";
        await fixture.WriteEntryAsync(entry);

        Assert.Equal(AiConnectionState.Connected,
            (await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken)).State);
        Assert.Equal(AiConnectionState.Connected,
            (await fixture.Service.ConnectAsync(TestContext.Current.CancellationToken)).State);
        Assert.Equal("stdio", (await fixture.ReadEntryAsync())["transportType"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("CUSTOM_FLAG", "1")]
    [InlineData("CUSTOM_FLAG", "true")]
    [InlineData("CUSTOM_FLAG", "null")]
    [InlineData("CUSTOM_FLAG", "{}")]
    [InlineData("CUSTOM_FLAG", "[]")]
    [InlineData("CONTEXTMOLE_DATA_DIR", "1")]
    public async Task NonStringEnvironmentValuesConflictWithoutDroppingUserValues(string name, string value)
    {
        using var fixture = new JsonConnectionFixture();
        var entry = fixture.ManagedEntry();
        entry["env"]![name] = JsonNode.Parse(value);
        var original = await fixture.WriteEntryAsync(entry);

        await AssertConflictWithoutWriteAsync(fixture, original, name);
    }

    [Theory]
    [InlineData("{\"theme\":\"dark\",\"theme\":\"light\"}")]
    [InlineData("{\"mcpServers\":{},\"mcpServers\":{}}")]
    [InlineData("{\"mcpServers\":{},\"\\u006dcpServers\":{}}")]
    [InlineData("{\"mcpServers\":{\"other\":{},\"other\":{}}}")]
    [InlineData("{\"mcpServers\":{\"context-mole\":{},\"context-mole\":{}}}")]
    [InlineData("{\"mcpServers\":{\"context-mole\":{\"command\":\"a\",\"command\":\"b\"}}}")]
    [InlineData("{\"mcpServers\":{\"context-mole\":{\"env\":{\"CONTEXTMOLE_MANAGED_CONNECTION\":\"1\",\"CONTEXTMOLE_MANAGED_CONNECTION\":\"0\"}}}}")]
    [InlineData("{\"unrelated\":{\"nested\":{\"value\":1,\"value\":2}}}")]
    [InlineData("{\"unrelated\":[{\"value\":1,\"value\":2}]}")]
    public async Task DuplicatePropertiesAtAnyDepthConflictForEveryOperation(string original)
    {
        using var fixture = new JsonConnectionFixture();
        await File.WriteAllTextAsync(fixture.ConfigPath, original, TestContext.Current.CancellationToken);

        await AssertConflictWithoutWriteAsync(fixture, original, "duplicate JSON property");
        var removed = await fixture.Service.DisconnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AiConnectionState.Conflict, removed.State);
        Assert.Contains("duplicate JSON property", removed.Message);
        await fixture.AssertUnchangedAsync(original);
    }

    [Fact]
    public async Task NullServerMapIsNotSilentlyReplaced()
    {
        using var fixture = new JsonConnectionFixture();
        const string original = "{\"mcpServers\":null}";
        await File.WriteAllTextAsync(fixture.ConfigPath, original, TestContext.Current.CancellationToken);

        await AssertConflictWithoutWriteAsync(fixture, original, "JSON object");
        Assert.Equal(AiConnectionState.Conflict,
            (await fixture.Service.DisconnectAsync(TestContext.Current.CancellationToken)).State);
        await fixture.AssertUnchangedAsync(original);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("CONTEXTMOLE_DATA_DIR")]
    public async Task RelativeOwnedPathsRequireRepairEvenWhenTheyResolveToCurrentPaths(string property)
    {
        var currentDirectory = Environment.CurrentDirectory;
        // Windows TEMP and the checkout can be on different drives. Keep these targets
        // under the current directory so GetRelativePath cannot return an absolute path.
        using var fixture = new JsonConnectionFixture(parentDirectory: currentDirectory);
        var entry = fixture.ManagedEntry();
        entry["disabledTools"] = new JsonArray("read_passage");
        entry["env"]!["CUSTOM_FLAG"] = "retained-value";
        var absolute = property == "command" ? fixture.ServerPath : fixture.Paths.DataDirectory;
        var relative = Path.GetRelativePath(currentDirectory, absolute);
        Assert.False(Path.IsPathFullyQualified(relative));
        Assert.Equal(absolute, Path.GetFullPath(relative, currentDirectory));
        Assert.True(property == "command" ? File.Exists(relative) : Directory.Exists(relative));
        if (property == "command") entry[property] = relative;
        else entry["env"]![property] = relative;
        await fixture.WriteEntryAsync(entry);

        var status = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AiConnectionState.UpdateRequired, status.State);
        Assert.Equal(AiConnectionState.Connected,
            (await fixture.Service.ConnectAsync(TestContext.Current.CancellationToken)).State);

        var expected = entry.DeepClone().AsObject();
        expected["command"] = fixture.ServerPath;
        expected["env"]!["CONTEXTMOLE_DATA_DIR"] = fixture.Paths.DataDirectory;
        var updated = await fixture.ReadEntryAsync();
        Assert.True(JsonNode.DeepEquals(expected, updated));
        Assert.True(Path.IsPathFullyQualified(updated["command"]!.GetValue<string>()));
        Assert.True(Path.IsPathFullyQualified(updated["env"]!["CONTEXTMOLE_DATA_DIR"]!.GetValue<string>()));
        Assert.Equal(AiConnectionState.Connected,
            (await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken)).State);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ManagedLaunchUpdatesPreserveUserFieldsAndRestrictions(bool changeExecutable, bool changeDataDirectory)
    {
        using var fixture = new JsonConnectionFixture("stdio");
        var entry = fixture.ManagedEntry();
        entry["type"] = "stdio";
        entry["disabled"] = false;
        entry["disabledTools"] = new JsonArray("read_passage", "materialize_attachment");
        entry["autoApprove"] = new JsonArray("list_projects");
        entry["timeout"] = 180;
        entry["cwd"] = fixture.Paths.SourceDirectory;
        entry["custom"] = new JsonObject { ["values"] = new JsonArray("retained", 7, false) };
        entry["env"]!["CUSTOM_FLAG"] = "retained-value";
        if (changeExecutable) entry["command"] = Path.Combine(fixture.Paths.RootDirectory, "old-server");
        if (changeDataDirectory) entry["env"]!["CONTEXTMOLE_DATA_DIR"] = Path.Combine(fixture.Paths.RootDirectory, "old-index");
        await fixture.WriteEntryAsync(entry);

        Assert.Equal(AiConnectionState.UpdateRequired,
            (await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken)).State);
        var updated = await fixture.Service.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AiConnectionState.Connected, updated.State);
        Assert.True(updated.RestartRequired);

        var actual = await fixture.ReadEntryAsync();
        var expected = entry.DeepClone().AsObject();
        expected["command"] = fixture.ServerPath;
        expected["env"]!["CONTEXTMOLE_DATA_DIR"] = fixture.Paths.DataDirectory;
        Assert.True(JsonNode.DeepEquals(expected, actual));
        Assert.Equal(AiConnectionState.Connected,
            (await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken)).State);

        var unchanged = await File.ReadAllTextAsync(fixture.ConfigPath, TestContext.Current.CancellationToken);
        var repeat = await fixture.Service.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AiConnectionState.Connected, repeat.State);
        Assert.False(repeat.RestartRequired);
        Assert.Equal(unchanged, await File.ReadAllTextAsync(fixture.ConfigPath, TestContext.Current.CancellationToken));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(fixture.ConfigPath)!, "*.bak"));
    }

    [Fact]
    public async Task RepairingOwnedLaunchFieldsPreservesCustomFields()
    {
        using var fixture = new JsonConnectionFixture("stdio");
        var entry = fixture.ManagedEntry();
        entry["args"] = new JsonArray("--unsupported");
        entry["timeout"] = 180;
        entry["disabledTools"] = new JsonArray("read_passage");
        entry["env"]!["CUSTOM_FLAG"] = "retained-value";
        await fixture.WriteEntryAsync(entry);

        Assert.Equal(AiConnectionState.UpdateRequired,
            (await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken)).State);
        Assert.Equal(AiConnectionState.Connected,
            (await fixture.Service.ConnectAsync(TestContext.Current.CancellationToken)).State);
        var actual = await fixture.ReadEntryAsync();
        var expected = entry.DeepClone().AsObject();
        expected["args"] = new JsonArray();
        expected["type"] = "stdio";
        Assert.True(JsonNode.DeepEquals(expected, actual));
    }

    [Theory]
    [InlineData("local", "[\"list_projects\"]")]
    [InlineData("local", "[]")]
    [InlineData("stdio", "[\"list_projects\"]")]
    [InlineData("stdio", "[]")]
    public async Task CopilotToolRestrictionsAreNotExpandedByAnUpdate(string transport, string tools)
    {
        using var fixture = new JsonConnectionFixture("local", includeAllTools: true);
        var entry = fixture.ManagedEntry();
        entry["type"] = transport;
        entry["tools"] = JsonNode.Parse(tools);
        await fixture.WriteEntryAsync(entry);
        Assert.Equal(AiConnectionState.Connected,
            (await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken)).State);

        entry["env"]!["CONTEXTMOLE_DATA_DIR"] = "old-index";
        await fixture.WriteEntryAsync(entry);
        Assert.Equal(AiConnectionState.Connected,
            (await fixture.Service.ConnectAsync(TestContext.Current.CancellationToken)).State);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(tools), (await fixture.ReadEntryAsync())["tools"]));
        Assert.Equal(transport, (await fixture.ReadEntryAsync())["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"*\"")]
    [InlineData("[1]")]
    public async Task InvalidCopilotToolListConflictsInsteadOfExpandingAccess(string tools)
    {
        using var fixture = new JsonConnectionFixture("local", includeAllTools: true);
        var entry = fixture.ManagedEntry();
        entry["type"] = "local";
        entry["tools"] = JsonNode.Parse(tools);
        var original = await fixture.WriteEntryAsync(entry);

        await AssertConflictWithoutWriteAsync(fixture, original, "array of strings");
    }

    [Fact]
    public async Task CommentsAndTrailingCommasStillAllowSafeConfiguration()
    {
        using var fixture = new JsonConnectionFixture();
        await File.WriteAllTextAsync(fixture.ConfigPath,
            "{/*comment*/\"theme\":\"dark\",\"mcpServers\":{\"other-server\":{\"command\":\"other\"},},}",
            TestContext.Current.CancellationToken);

        Assert.Equal(AiConnectionState.Connected,
            (await fixture.Service.ConnectAsync(TestContext.Current.CancellationToken)).State);
        var root = await fixture.ReadRootAsync();
        Assert.Equal("dark", root["theme"]!.GetValue<string>());
        Assert.Equal("other", root["mcpServers"]!["other-server"]!["command"]!.GetValue<string>());
    }

    private static async Task AssertConflictWithoutWriteAsync(JsonConnectionFixture fixture, string original, string reason)
    {
        var status = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AiConnectionState.Conflict, status.State);
        Assert.Contains(reason, status.Message);
        var connected = await fixture.Service.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AiConnectionState.Conflict, connected.State);
        Assert.Contains(reason, connected.Message);
        Assert.False(connected.RestartRequired);
        await fixture.AssertUnchangedAsync(original);
    }

    private sealed class JsonConnectionFixture : IDisposable
    {
        private readonly string? _previousServerOverride;

        public JsonConnectionFixture(string? transportType = null, bool includeAllTools = false,
            string? parentDirectory = null)
        {
            Paths = new JsonConnectionTestPaths(parentDirectory ?? Path.GetTempPath());
            ConfigPath = Path.Combine(Paths.DataDirectory, "isolated-client", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            ServerPath = Path.Combine(Paths.DataDirectory,
                OperatingSystem.IsWindows() ? "ContextMole.Mcp.exe" : "ContextMole.Mcp");
            File.WriteAllText(ServerPath, "test server marker, never executed");
            _previousServerOverride = Environment.GetEnvironmentVariable("CONTEXTMOLE_MCP_PATH");
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", ServerPath);
            Service = new JsonMcpConfigurationService(
                new AiClientDefinition("test-json", "Test JSON Client", "Isolated JSON test client"),
                ConfigPath, "mcpServers", Paths, new McpServerDeploymentService(Paths), transportType, includeAllTools);
        }

        public JsonConnectionTestPaths Paths { get; }
        public string ConfigPath { get; }
        public string ServerPath { get; }
        public JsonMcpConfigurationService Service { get; }

        public JsonObject ManagedEntry() => new()
        {
            ["command"] = ServerPath,
            ["args"] = new JsonArray(),
            ["env"] = new JsonObject
            {
                ["CONTEXTMOLE_DATA_DIR"] = Paths.DataDirectory,
                ["CONTEXTMOLE_MANAGED_CONNECTION"] = "1"
            }
        };

        public async Task<string> WriteEntryAsync(JsonObject entry)
        {
            var root = new JsonObject
            {
                ["theme"] = "dark",
                ["mcpServers"] = new JsonObject
                {
                    ["other-server"] = new JsonObject { ["command"] = "other" },
                    ["context-mole"] = entry.DeepClone()
                }
            };
            var original = root.ToJsonString();
            await File.WriteAllTextAsync(ConfigPath, original, TestContext.Current.CancellationToken);
            return original;
        }

        public async Task<JsonObject> ReadRootAsync() =>
            JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath, TestContext.Current.CancellationToken))!.AsObject();

        public async Task<JsonObject> ReadEntryAsync() =>
            (await ReadRootAsync())["mcpServers"]!["context-mole"]!.AsObject();

        public async Task AssertUnchangedAsync(string original)
        {
            Assert.Equal(original, await File.ReadAllTextAsync(ConfigPath, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(ConfigPath)!, "*.bak"));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(ConfigPath)!, "*.partial"));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", _previousServerOverride);
            Paths.Dispose();
        }
    }

    private sealed class JsonConnectionTestPaths : IAppPaths, IDisposable
    {
        private readonly string _parentDirectory;

        public JsonConnectionTestPaths(string parentDirectory)
        {
            _parentDirectory = Path.GetFullPath(parentDirectory);
            RootDirectory = Path.Combine(_parentDirectory, $"ContextMole-json-tests-{Guid.NewGuid():N}");
            DataDirectory = Path.Combine(RootDirectory, "data");
            SourceDirectory = Path.Combine(RootDirectory, "source");
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(SourceDirectory);
        }

        public string RootDirectory { get; }
        public string SourceDirectory { get; }
        public string DataDirectory { get; }
        public string DatabasePath => Path.Combine(DataDirectory, "index.db");
        public string AssetsDirectory => Path.Combine(DataDirectory, "assets");
        public string LogsDirectory => Path.Combine(DataDirectory, "logs");
        public string TempDirectory => Path.Combine(DataDirectory, "temp");

        public void Dispose()
        {
            var relative = Path.GetRelativePath(_parentDirectory, RootDirectory);
            if (Path.IsPathRooted(relative) || relative == "." || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("The test directory escaped its owned fixture root.");

            if (Directory.Exists(RootDirectory)) Directory.Delete(RootDirectory, recursive: true);
        }
    }
}
