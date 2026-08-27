using ContextMole.Core;

namespace ContextMole.Infrastructure;

public enum AiConnectionState
{
    Disconnected,
    Connected,
    UpdateRequired,
    Conflict,
    ServerUnavailable,
    Broken,
    ManualSetup
}

public sealed record AiClientDefinition(
    string Id,
    string DisplayName,
    string Description,
    bool SupportsAutomaticSetup = true);

public sealed record AiConnectionStatus(
    AiClientDefinition Client,
    AiConnectionState State,
    string Message,
    string? ConfigPath = null,
    string? ServerPath = null,
    bool RestartRequired = false);

public interface IAiClientConnection
{
    AiClientDefinition Client { get; }
    string? ConfigPath { get; }
    Task<AiConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<AiConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default);
    Task<AiConnectionStatus> DisconnectAsync(CancellationToken cancellationToken = default);
}

public static class AiClientCatalog
{
    public static AiClientDefinition Codex { get; } = new(
        "codex", "OpenAI Codex / ChatGPT", "Codex CLI, IDE extension, and ChatGPT desktop");

    public static AiClientDefinition ClaudeCode { get; } = new(
        "claude-code", "Claude Code", "Anthropic's terminal coding agent");

    public static AiClientDefinition ClaudeDesktop { get; } = new(
        "claude-desktop", "Claude Desktop", "Anthropic's desktop assistant");

    public static AiClientDefinition Cursor { get; } = new(
        "cursor", "Cursor", "Cursor editor and command-line agent");

    public static AiClientDefinition Zed { get; } = new(
        "zed", "Zed", "Zed editor and its native AI agent");

    public static AiClientDefinition VisualStudioCode { get; } = new(
        "vscode", "Visual Studio Code", "VS Code agent mode and GitHub Copilot Chat", SupportsAutomaticSetup: false);

    public static AiClientDefinition GitHubCopilotCli { get; } = new(
        "copilot-cli", "GitHub Copilot CLI", "GitHub's command-line coding agent");

    public static AiClientDefinition Windsurf { get; } = new(
        "windsurf", "Windsurf Cascade (legacy)", "Legacy Windsurf editor MCP configuration");

    public static AiClientDefinition GeminiCli { get; } = new(
        "gemini-cli", "Gemini CLI", "Google's terminal coding agent");

    public static AiClientDefinition GoogleAntigravity { get; } = new(
        "google-antigravity", "Google Antigravity", "Google's agent-first development environment");

    public static AiClientDefinition Kiro { get; } = new(
        "kiro", "Kiro", "AWS's IDE and command-line coding agent");

    public static AiClientDefinition JetBrainsJunie { get; } = new(
        "jetbrains-junie", "JetBrains Junie", "Junie in JetBrains IDEs and the command line");

    public static AiClientDefinition Devin { get; } = new(
        "devin", "Devin CLI / Desktop", "Cognition's current local coding agent");

    public static AiClientDefinition Cline { get; } = new(
        "cline", "Cline", "Cline extension for VS Code-compatible editors");

    public static AiClientDefinition RooCode { get; } = new(
        "roo-code", "Roo Code", "Roo Code extension for VS Code-compatible editors", SupportsAutomaticSetup: false);

    public static AiClientDefinition OpenCode { get; } = new(
        "opencode", "OpenCode", "Open-source terminal coding agent", SupportsAutomaticSetup: false);
}

public sealed class AiConnectionsService
{
    private readonly IReadOnlyList<IAiClientConnection> _connections;
    private readonly IReadOnlyDictionary<string, IAiClientConnection> _connectionsById;

    public AiConnectionsService(IAppPaths appPaths, McpServerDeploymentService deployment)
    {
        _connections =
        [
            new CodexMcpConfigurationService(appPaths, deployment),
            CreateJsonConnection(
                AiClientCatalog.ClaudeCode,
                ResolveClaudeCodeConfigPath,
                "mcpServers", appPaths, deployment, transportType: "stdio"),
            CreateJsonConnection(
                AiClientCatalog.ClaudeDesktop,
                ResolveClaudeDesktopConfigPath,
                "mcpServers", appPaths, deployment),
            CreateJsonConnection(
                AiClientCatalog.Cursor,
                () => Path.Combine(UserHome(), ".cursor", "mcp.json"),
                "mcpServers", appPaths, deployment, transportType: "stdio"),
            CreateJsonConnection(
                AiClientCatalog.Zed,
                ResolveZedConfigPath,
                "context_servers", appPaths, deployment),
            new ManualAiClientConnection(AiClientCatalog.VisualStudioCode,
                "In VS Code, run MCP: Add Server and choose the user profile. The README contains the exact configuration."),
            CreateJsonConnection(
                AiClientCatalog.GitHubCopilotCli,
                () => Path.Combine(EnvironmentDirectory("COPILOT_HOME", Path.Combine(UserHome(), ".copilot")),
                    "mcp-config.json"),
                "mcpServers", appPaths, deployment, transportType: "local", includeAllTools: true),
            CreateJsonConnection(
                AiClientCatalog.GeminiCli,
                () => Path.Combine(EnvironmentDirectory("GEMINI_CLI_HOME", UserHome()), ".gemini", "settings.json"),
                "mcpServers", appPaths, deployment),
            CreateJsonConnection(
                AiClientCatalog.GoogleAntigravity,
                () => Path.Combine(UserHome(), ".gemini", "config", "mcp_config.json"),
                "mcpServers", appPaths, deployment),
            CreateJsonConnection(
                AiClientCatalog.Kiro,
                () => Path.Combine(EnvironmentDirectory("KIRO_HOME", Path.Combine(UserHome(), ".kiro")),
                    "settings", "mcp.json"),
                "mcpServers", appPaths, deployment),
            CreateJsonConnection(
                AiClientCatalog.JetBrainsJunie,
                () => Path.Combine(EnvironmentDirectory("JUNIE_HOME", Path.Combine(UserHome(), ".junie")),
                    "mcp", "mcp.json"),
                "mcpServers", appPaths, deployment),
            CreateJsonConnection(
                AiClientCatalog.Devin,
                ResolveDevinConfigPath,
                "mcpServers", appPaths, deployment),
            CreateJsonConnection(
                AiClientCatalog.Windsurf,
                () => Path.Combine(UserHome(), ".codeium", "windsurf", "mcp_config.json"),
                "mcpServers", appPaths, deployment),
            CreateJsonConnection(
                AiClientCatalog.Cline,
                ResolveClineConfigPath,
                "mcpServers", appPaths, deployment),
            new ManualAiClientConnection(AiClientCatalog.RooCode,
                "Open Roo Code's MCP view, choose Edit Global MCP, and paste the manual configuration from the README."),
            new ManualAiClientConnection(AiClientCatalog.OpenCode,
                "OpenCode uses a different configuration shape. Use its specific local-server example in the README.")
        ];
        _connectionsById = _connections.ToDictionary(connection => connection.Client.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<AiClientDefinition> Clients => _connections.Select(connection => connection.Client).ToArray();

    public Task<AiConnectionStatus> GetStatusAsync(string clientId, CancellationToken cancellationToken = default) =>
        GetConnection(clientId).GetStatusAsync(cancellationToken);

    public Task<AiConnectionStatus> ConnectAsync(string clientId, CancellationToken cancellationToken = default) =>
        GetConnection(clientId).ConnectAsync(cancellationToken);

    public Task<AiConnectionStatus> DisconnectAsync(string clientId, CancellationToken cancellationToken = default) =>
        GetConnection(clientId).DisconnectAsync(cancellationToken);

    private IAiClientConnection GetConnection(string clientId) =>
        _connectionsById.TryGetValue(clientId, out var connection)
            ? connection
            : throw new ArgumentException($"Unknown AI client: {clientId}", nameof(clientId));

    private static IAiClientConnection CreateJsonConnection(
        AiClientDefinition client,
        Func<string> resolveConfigPath,
        string rootProperty,
        IAppPaths appPaths,
        McpServerDeploymentService deployment,
        string? transportType = null,
        bool includeAllTools = false)
    {
        try
        {
            return new JsonMcpConfigurationService(client, resolveConfigPath(), rootProperty, appPaths, deployment,
                transportType, includeAllTools);
        }
        catch (PlatformNotSupportedException exception)
        {
            return new InvalidAiClientConnection(client, exception.Message, AiConnectionState.ServerUnavailable);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new InvalidAiClientConnection(client,
                $"The configured {client.DisplayName} settings path is invalid: {exception.Message}");
        }
    }

    private static string UserHome() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string EnvironmentDirectory(string variableName, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(variableName);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? fallback : configured);
    }

    private static string ResolveClaudeCodeConfigPath()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(UserHome(), ".claude.json")
            : Path.Combine(Path.GetFullPath(configured), ".claude.json");
    }

    private static string ResolveClineConfigPath()
    {
        var settingsPath = Environment.GetEnvironmentVariable("CLINE_MCP_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(settingsPath)) return Path.GetFullPath(settingsPath);

        var dataDirectory = Environment.GetEnvironmentVariable("CLINE_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            var clineDirectory = EnvironmentDirectory("CLINE_DIR", Path.Combine(UserHome(), ".cline"));
            dataDirectory = Path.Combine(clineDirectory, "data");
        }

        return Path.Combine(Path.GetFullPath(dataDirectory), "settings", "cline_mcp_settings.json");
    }

    private static string ResolveClaudeDesktopConfigPath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude",
                "claude_desktop_config.json");
        if (OperatingSystem.IsMacOS())
            return Path.Combine(UserHome(), "Library", "Application Support", "Claude",
                "claude_desktop_config.json");
        throw new PlatformNotSupportedException("Claude Desktop is available only on Windows and macOS.");
    }

    private static string ResolveDevinConfigPath() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "devin", "mcp_config.json")
        : Path.Combine(UserHome(), ".config", "devin", "mcp_config.json");

    private static string ResolveZedConfigPath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zed",
                "settings.json");
        if (OperatingSystem.IsMacOS())
            return Path.Combine(UserHome(), "Library", "Application Support", "Zed", "settings.json");
        return Path.Combine(UserHome(), ".config", "zed", "settings.json");
    }
}

internal sealed class ManualAiClientConnection(AiClientDefinition client, string instructions) : IAiClientConnection
{
    public AiClientDefinition Client { get; } = client;
    public string? ConfigPath => null;

    public Task<AiConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Status());

    public Task<AiConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Status());

    public Task<AiConnectionStatus> DisconnectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Status());

    private AiConnectionStatus Status() => new(Client, AiConnectionState.ManualSetup, instructions);
}

internal sealed class InvalidAiClientConnection(
    AiClientDefinition client,
    string error,
    AiConnectionState state = AiConnectionState.Conflict) : IAiClientConnection
{
    public AiClientDefinition Client { get; } = client;
    public string? ConfigPath => null;

    public Task<AiConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Status());

    public Task<AiConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Status());

    public Task<AiConnectionStatus> DisconnectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Status());

    private AiConnectionStatus Status() => new(Client, state, error);
}