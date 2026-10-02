using ContextMole.Infrastructure;

using Tomlyn;
using Tomlyn.Model;

namespace ContextMole.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class CodexTomlSafetyTests
{
    private const string Begin = "# BEGIN Context Mole managed MCP server";
    private const string End = "# END Context Mole managed MCP server";

    [Theory]
    [InlineData("mcp_servers = { context-mole = { command = 'custom' } }\n")]
    [InlineData("mcp_servers.context-mole.command = 'custom'\n")]
    [InlineData("[ mcp_servers . context-mole ]\ncommand = 'custom'\n")]
    [InlineData("[\"mcp_servers\".\"context-mole\"]\ncommand = 'custom'\n")]
    [InlineData("[mcp_servers.\"context\\u002Dmole\"]\ncommand = 'custom'\n")]
    [InlineData("[mcp_servers]\ncontext-mole = { command = 'custom' }\n")]
    [InlineData("[mcp_servers.context-mole.env]\nCUSTOM = 'keep'\n")]
    [InlineData("\"mcp_servers\".\"context-mole\" = { command = 'custom' }\n")]
    [InlineData("[[mcp_servers.context-mole]]\ncommand = 'custom'\n")]
    public async Task ExistingUnmanagedEntriesAreDetectedSemanticallyAndNeverChanged(string config)
    {
        Assert.NotNull(TomlSerializer.Deserialize<TomlTable>(config));
        using var fixture = new Fixture();
        await fixture.WriteAsync(config);
        await AssertConflictsWithoutMutationAsync(fixture, config);
    }

    [Theory]
    [InlineData("model = \"unterminated\n")]
    [InlineData("model = 'one'\nmodel = 'two'\n")]
    [InlineData("mcp_servers = 12\n")]
    [InlineData(Begin + "\n[mcp_servers.context-mole]\ncommand = 'old'\n")]
    [InlineData(End + "\n")]
    [InlineData(End + "\n" + Begin + "\n")]
    [InlineData(Begin + "\n" + Begin + "\n[mcp_servers.context-mole]\ncommand='old'\n" + End + "\n")]
    [InlineData(Begin + "\n[mcp_servers.context-mole]\ncommand='old'\n" + End + "\n" + End + "\n")]
    [InlineData(Begin + "\n[unrelated]\nvalue='keep'\n" + End + "\n")]
    [InlineData(Begin + "\n[mcp_servers.context-mole]\ncommand='old'\n[unrelated]\nvalue='keep'\n" + End + "\n")]
    [InlineData(Begin + "\n[mcp_servers.context-mole]\ncommand='old'\n" + End + "\nargs=['outside']\n")]
    [InlineData(Begin + "\n[mcp_servers.context-mole]\ncommand='old'\n" + End + "\n[mcp_servers.context-mole.env]\nCUSTOM='outside'\n")]
    [InlineData("model='keep' # BEGIN Context Mole managed MCP server\n[mcp_servers.context-mole]\ncommand='old'\n" + End + "\n")]
    public async Task MalformedOrAmbiguousOwnershipFailsClosed(string config)
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync(config);
        await AssertConflictsWithoutMutationAsync(fixture, config);
    }

    [Theory]
    [InlineData("'''")]
    [InlineData("\"\"\"")]
    public async Task MarkerAndHeaderTextInsideMultilineStringsIsNotOwnership(string delimiter)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        var original = $"# user comments\ntext = {delimiter}\n{Begin}\n[mcp_servers.context-mole]\ncommand = 'string content'\n{End}\n{delimiter}\n\n";
        await fixture.WriteAsync(original);
        Assert.Equal(AiConnectionState.Disconnected, (await fixture.Service.GetStatusAsync(cancellationToken)).State);
        Assert.Equal(AiConnectionState.Connected, (await fixture.Service.ConnectAsync(cancellationToken)).State);
        var configured = await fixture.ReadAsync();
        Assert.StartsWith(original, configured, StringComparison.Ordinal);
        Assert.NotNull(TomlSerializer.Deserialize<TomlTable>(configured));
        Assert.Equal(AiConnectionState.Disconnected, (await fixture.Service.DisconnectAsync(cancellationToken)).State);
        Assert.StartsWith(original, await fixture.ReadAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaunchUpdatesPreserveCustomSettingsAndComments(bool inline)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        const string prefix = "# user comments\nmodel='keep'\n\n";
        var suffix = "\n# unrelated trailing comments\n[mcp_servers.other]\ncommand='other'\n\n";
        var owned = inline
            ? $"{Begin}\nmcp_servers = {{ context-mole = {{ command = 'old', args=['--custom'], enabled_tools=['search'], disabled_tools=['delete'], startup_timeout_sec=120, default_tools_approval_mode='never', env={{ CONTEXTMOLE_DATA_DIR='old', CUSTOM='keep' }} }} }}\n{End}\n"
            : $"{Begin}\n[mcp_servers.context-mole]\ncommand = 'old' # command comment\nargs=['--custom']\nenabled_tools=['search']\ndisabled_tools=['delete']\nstartup_timeout_sec=120\ndefault_tools_approval_mode='never'\n[mcp_servers.context-mole.env]\nCONTEXTMOLE_DATA_DIR='old' # data comment\nCUSTOM='keep'\n{End}\n";
        // Inline tables close their parents, so use an unrelated top-level table in the suffix.
        if (inline) suffix = "\n# unrelated trailing comments\n[other]\ncommand='other'\n\n";
        await fixture.WriteAsync(prefix + owned + suffix);
        Assert.Equal(AiConnectionState.Connected, (await fixture.Service.ConnectAsync(cancellationToken)).State);
        var configured = await fixture.ReadAsync();
        Assert.StartsWith(prefix, configured, StringComparison.Ordinal);
        Assert.EndsWith(suffix, configured, StringComparison.Ordinal);
        Assert.Equal(owned.Replace("'old'", "\"" + Escape(fixture.ServerPath) + "\"", StringComparison.Ordinal)
            .Replace("CONTEXTMOLE_DATA_DIR=\"" + Escape(fixture.ServerPath) + "\"",
                "CONTEXTMOLE_DATA_DIR=\"" + Escape(fixture.Paths.DataDirectory) + "\"", StringComparison.Ordinal),
            configured[prefix.Length..^suffix.Length]);
        var server = GetServer(configured);
        Assert.Equal(fixture.ServerPath, server["command"]);
        Assert.Equal("--custom", Assert.IsType<TomlArray>(server["args"])[0]);
        Assert.Equal("search", Assert.IsType<TomlArray>(server["enabled_tools"])[0]);
        Assert.Equal("delete", Assert.IsType<TomlArray>(server["disabled_tools"])[0]);
        Assert.Equal(120L, server["startup_timeout_sec"]);
        Assert.Equal("never", server["default_tools_approval_mode"]);
        var env = Assert.IsType<TomlTable>(server["env"]);
        Assert.Equal("keep", env["CUSTOM"]);
        Assert.Equal(fixture.Paths.DataDirectory, env["CONTEXTMOLE_DATA_DIR"]);
        Assert.Equal(AiConnectionState.Connected, (await fixture.Service.GetStatusAsync(cancellationToken)).State);
        Assert.False((await fixture.Service.ConnectAsync(cancellationToken)).RestartRequired);
        Assert.Equal(configured, await fixture.ReadAsync());
        Assert.Equal(AiConnectionState.Disconnected, (await fixture.Service.DisconnectAsync(cancellationToken)).State);
        Assert.Equal(prefix + suffix, await fixture.ReadAsync());
    }

    [Theory]
    [InlineData("[mcp_servers.context-mole]\ncommand='old'\n")]
    [InlineData("[mcp_servers.context-mole]\nargs=['--custom']\n")]
    [InlineData("mcp_servers={context-mole={args=['--custom']}}\n")]
    [InlineData("[mcp_servers]\ncontext-mole={command='old', env={CUSTOM='keep'}}\n")]
    public async Task MissingLaunchSettingsAreAddedWithoutDroppingCustomFields(string owned)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        await fixture.WriteAsync($"{Begin}\n{owned}{End}\n");
        Assert.Equal(AiConnectionState.Connected, (await fixture.Service.ConnectAsync(cancellationToken)).State);
        var server = GetServer(await fixture.ReadAsync());
        Assert.Equal(fixture.ServerPath, server["command"]);
        Assert.Equal(fixture.Paths.DataDirectory, Assert.IsType<TomlTable>(server["env"])["CONTEXTMOLE_DATA_DIR"]);
        if (owned.Contains("args", StringComparison.Ordinal)) Assert.Equal("--custom", Assert.IsType<TomlArray>(server["args"])[0]);
        if (owned.Contains("CUSTOM", StringComparison.Ordinal)) Assert.Equal("keep", Assert.IsType<TomlTable>(server["env"])["CUSTOM"]);
    }

    [Theory]
    [InlineData("enabled = false\n")]
    [InlineData("enabled = 'false'\n")]
    [InlineData("args = [42]\n")]
    [InlineData("args = '--bad-type'\n")]
    [InlineData("env = 'bad-type'\n")]
    [InlineData("env = { CUSTOM=42 }\n")]
    [InlineData("url = 'https://example.invalid/mcp'\n")]
    public async Task DisabledOrInvalidManagedLaunchSettingsAreNotReportedReady(string restriction)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        var config = $"{Begin}\n[mcp_servers.context-mole]\ncommand='old'\n{restriction}{End}\n";
        await fixture.WriteAsync(config);
        Assert.Equal(AiConnectionState.Conflict, (await fixture.Service.GetStatusAsync(cancellationToken)).State);
        Assert.Equal(AiConnectionState.Conflict, (await fixture.Service.ConnectAsync(cancellationToken)).State);
        Assert.Equal(config, await fixture.ReadAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(fixture.Service.ConfigPath)!, "*.bak"));
        Assert.Equal(AiConnectionState.Disconnected, (await fixture.Service.DisconnectAsync(cancellationToken)).State);
    }

    [Fact]
    public async Task ValidLiteralAndUnicodeEscapedPathsAreReadSemantically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        var server = Escape(fixture.ServerPath).Replace("ContextMole", "Context\\u004Dole", StringComparison.Ordinal);
        var data = Escape(fixture.Paths.DataDirectory);
        var config = $"{Begin}\n[\"mcp_servers\".\"context\\u002Dmole\"]\ncommand=\"{server}\" # path comment\n[\"mcp_servers\".\"context-mole\".\"env\"]\nCONTEXTMOLE_DATA_DIR=\"{data}\" # data comment\n{End}\n";
        await fixture.WriteAsync(config);
        Assert.Equal(AiConnectionState.Connected, (await fixture.Service.GetStatusAsync(cancellationToken)).State);
        Assert.False((await fixture.Service.ConnectAsync(cancellationToken)).RestartRequired);
        Assert.Equal(config, await fixture.ReadAsync());
        // A valid literal path should also be recognized without normalizing its representation.
        config = $"{Begin}\n[mcp_servers.context-mole]\ncommand='{fixture.ServerPath}'\n[mcp_servers.context-mole.env]\nCONTEXTMOLE_DATA_DIR='{fixture.Paths.DataDirectory}'\n{End}\n";
        await fixture.WriteAsync(config);
        Assert.Equal(AiConnectionState.Connected, (await fixture.Service.GetStatusAsync(cancellationToken)).State);
        Assert.Equal(config, await fixture.ReadAsync());
        Assert.False((await fixture.Service.ConnectAsync(cancellationToken)).RestartRequired);
        Assert.Equal(config, await fixture.ReadAsync());
    }

    [Fact]
    public async Task DisconnectPreservesExactUnrelatedWhitespaceAndLineEndings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        const string prefix = "# keep\r\nmodel = 'test'  \r\n\r\n";
        var suffix = "\r\n\r\n# keep trailing spaces  \r\n";
        await fixture.WriteAsync($"{prefix}  {Begin}  \r\n[mcp_servers.context-mole]\r\ncommand='old'\r\n  {End}  \r\n{suffix}");
        Assert.Equal(AiConnectionState.Disconnected, (await fixture.Service.DisconnectAsync(cancellationToken)).State);
        Assert.Equal(prefix + suffix, await fixture.ReadAsync());
    }

    private static async Task AssertConflictsWithoutMutationAsync(Fixture fixture, string config)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.Equal(AiConnectionState.Conflict, (await fixture.Service.GetStatusAsync(cancellationToken)).State);
        Assert.Equal(AiConnectionState.Conflict, (await fixture.Service.ConnectAsync(cancellationToken)).State);
        Assert.Equal(AiConnectionState.Conflict, (await fixture.Service.DisconnectAsync(cancellationToken)).State);
        Assert.Equal(config, await fixture.ReadAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(fixture.Service.ConfigPath)!, "*.bak"));
    }

    private static TomlTable GetServer(string config) => Assert.IsType<TomlTable>(
        Assert.IsType<TomlTable>(TomlSerializer.Deserialize<TomlTable>(config)!["mcp_servers"])["context-mole"]);

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed class Fixture : IDisposable
    {
        private readonly string? _previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        private readonly string? _previousServer = Environment.GetEnvironmentVariable("CONTEXTMOLE_MCP_PATH");
        public StorageTestPaths Paths { get; } = new();
        public string ServerPath { get; }
        public CodexMcpConfigurationService Service { get; }

        public Fixture()
        {
            var home = Path.Combine(Paths.DataDirectory, "codex-home");
            Directory.CreateDirectory(home);
            var serverDirectory = Path.Combine(Paths.DataDirectory, "server");
            Directory.CreateDirectory(serverDirectory);
            ServerPath = Path.Combine(serverDirectory, OperatingSystem.IsWindows() ? "ContextMole.Mcp.exe" : "ContextMole.Mcp");
            File.WriteAllText(ServerPath, "test server");
            Environment.SetEnvironmentVariable("CODEX_HOME", home);
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", ServerPath);
            Service = new(Paths);
        }

        public Task WriteAsync(string config) => File.WriteAllTextAsync(Service.ConfigPath, config, TestContext.Current.CancellationToken);
        public Task<string> ReadAsync() => File.ReadAllTextAsync(Service.ConfigPath, TestContext.Current.CancellationToken);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", _previousCodexHome);
            Environment.SetEnvironmentVariable("CONTEXTMOLE_MCP_PATH", _previousServer);
            Paths.Dispose();
        }
    }
}
