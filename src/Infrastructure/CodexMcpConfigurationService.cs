using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MCPIndexSearch.Core;

namespace MCPIndexSearch.Infrastructure;

public enum CodexMcpConnectionState
{
    Disconnected,
    Connected,
    UpdateRequired,
    Conflict,
    ServerUnavailable
}

public sealed record CodexMcpConnectionStatus(
    CodexMcpConnectionState State,
    string Message,
    string ConfigPath,
    string? ServerPath = null,
    bool RestartRequired = false);

public sealed partial class CodexMcpConfigurationService(IAppPaths appPaths)
{
    private const string ServerName = "mcp-index-search";
    private const string BeginMarker = "# BEGIN MCPIndexSearch managed MCP server";
    private const string EndMarker = "# END MCPIndexSearch managed MCP server";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly IAppPaths _appPaths = appPaths;

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

    public async Task<CodexMcpConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var config = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
        if (TryGetManagedBlock(config, out var start, out var end))
        {
            var serverPath = TryGetManagedServerPath(config, start, end);
            if (serverPath is not null && IsRepositoryBuildOutput(serverPath))
            {
                return new(CodexMcpConnectionState.UpdateRequired,
                    "The Codex connection uses mutable development output. Update it to stage an isolated MCP server, then restart Codex.",
                    ConfigPath, serverPath);
            }

            var candidate = ResolveServerCandidate();
            if (serverPath is not null && candidate is not null &&
                !string.Equals(serverPath, GetRegistrationPath(candidate), PathComparison))
            {
                return new(CodexMcpConnectionState.UpdateRequired,
                    "A different local MCP server build is available. Update the Codex connection, then restart Codex.",
                    ConfigPath, serverPath);
            }

            return serverPath is null || !File.Exists(serverPath)
                ? new(CodexMcpConnectionState.Connected,
                    "Codex is configured, but the local MCP server executable is missing. Disconnect or reinstall the application bundle.", ConfigPath)
                : new(CodexMcpConnectionState.Connected,
                    "Connected. Restart Codex after changing this setting.", ConfigPath, serverPath);
        }

        if (UnmanagedServerHeaderRegex().IsMatch(config))
        {
            return new(CodexMcpConnectionState.Conflict,
                "An existing mcp-index-search entry is already present in the Codex configuration. It was left unchanged.", ConfigPath);
        }

        var resolved = ResolveServerCandidate();
        return resolved is null
            ? new(CodexMcpConnectionState.ServerUnavailable,
                "The MCP server executable was not found. Publish the application bundle, then try again.", ConfigPath)
            : new(CodexMcpConnectionState.Disconnected,
                "Connect once, then restart Codex to search these projects.", ConfigPath, resolved.Path);
    }

    public async Task<CodexMcpConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidate = ResolveServerCandidate();
            if (candidate is null)
            {
                return new(CodexMcpConnectionState.ServerUnavailable,
                    "The MCP server executable was not found. Publish the application bundle, then try again.", ConfigPath);
            }

            var serverPath = candidate.RequiresStaging
                ? await StageServerAsync(candidate.Path, cancellationToken).ConfigureAwait(false)
                : candidate.Path;

            var config = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
            if (!TryGetManagedBlock(config, out var start, out var end) && UnmanagedServerHeaderRegex().IsMatch(config))
            {
                return new(CodexMcpConnectionState.Conflict,
                    "An existing mcp-index-search entry is already present in the Codex configuration. It was left unchanged.",
                    ConfigPath, serverPath);
            }

            var managedBlock = BuildManagedBlock(serverPath);
            var updated = start >= 0
                ? string.Concat(config.AsSpan(0, start), managedBlock, config.AsSpan(end))
                : AppendBlock(config, managedBlock);
            if (string.Equals(config, updated, StringComparison.Ordinal))
            {
                return new(CodexMcpConnectionState.Connected,
                    "Already connected. Restart Codex if the server is not visible yet.", ConfigPath, serverPath);
            }

            await WriteConfigSafelyAsync(config, updated, cancellationToken).ConfigureAwait(false);
            return new(CodexMcpConnectionState.Connected,
                "Connected successfully. Restart Codex to load MCPIndexSearch.", ConfigPath, serverPath, true);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<CodexMcpConnectionStatus> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
            if (!TryGetManagedBlock(config, out var start, out var end))
            {
                return UnmanagedServerHeaderRegex().IsMatch(config)
                    ? new(CodexMcpConnectionState.Conflict,
                        "The existing mcp-index-search entry is not managed by this application and was left unchanged.", ConfigPath)
                    : new(CodexMcpConnectionState.Disconnected, "Already disconnected.", ConfigPath);
            }

            var updated = string.Concat(config.AsSpan(0, start), config.AsSpan(end)).TrimEnd() + Environment.NewLine;
            await WriteConfigSafelyAsync(config, updated, cancellationToken).ConfigureAwait(false);
            return new(CodexMcpConnectionState.Disconnected,
                "Disconnected successfully. Restart Codex to remove MCPIndexSearch from the current session.", ConfigPath,
                RestartRequired: true);
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
        MCPINDEXSEARCH_DATA_DIR = "{{EscapeToml(_appPaths.DataDirectory)}}"
        {{EndMarker}}
        """ + Environment.NewLine;

    private ServerCandidate? ResolveServerCandidate()
    {
        var overridePath = Environment.GetEnvironmentVariable("MCPINDEXSEARCH_MCP_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var fullOverride = Path.GetFullPath(overridePath);
            if (File.Exists(fullOverride)) return new(fullOverride, IsRepositoryBuildOutput(fullOverride));
        }

        var executable = OperatingSystem.IsWindows() ? "MCPIndexSearch.Mcp.exe" : "MCPIndexSearch.Mcp";
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, executable),
                     Path.Combine(AppContext.BaseDirectory, "mcp-server", executable)
                 })
        {
            if (File.Exists(candidate))
            {
                var fullCandidate = Path.GetFullPath(candidate);
                return new(fullCandidate, IsRepositoryBuildOutput(fullCandidate));
            }
        }

        // Development builds keep the two executables in separate project output folders.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MCPIndexSearch.slnx")))
            {
                var bin = Path.Combine(directory.FullName, "src", "Mcp", "bin");
                if (!Directory.Exists(bin)) return null;
                var developmentPath = Directory.EnumerateFiles(bin, executable, SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Select(Path.GetFullPath)
                    .FirstOrDefault();
                return developmentPath is null ? null : new(developmentPath, RequiresStaging: true);
            }
            directory = directory.Parent;
        }

        return null;
    }

    private async Task<string> StageServerAsync(string sourceExecutable, CancellationToken cancellationToken)
    {
        var sourceDirectory = Path.GetDirectoryName(sourceExecutable)
            ?? throw new IOException("The MCP server output directory could not be resolved.");
        var deploymentsDirectory = Path.GetFullPath(Path.Combine(_appPaths.DataDirectory, "mcp-server", "deployments"));
        Directory.CreateDirectory(deploymentsDirectory);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fingerprint = ComputeDeploymentFingerprint(sourceDirectory);
            var deploymentDirectory = Path.Combine(deploymentsDirectory, fingerprint);
            var deployedExecutable = GetRegistrationPath(sourceExecutable, fingerprint);
            if (File.Exists(deployedExecutable)) return deployedExecutable;
            if (Directory.Exists(deploymentDirectory))
                throw new IOException($"The staged MCP server deployment is incomplete: {deploymentDirectory}");

            var temporaryDirectory = Path.Combine(deploymentsDirectory, $".{fingerprint}.{Guid.NewGuid():N}.partial");
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                await CopyDirectoryAsync(sourceDirectory, temporaryDirectory, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(fingerprint, ComputeDeploymentFingerprint(sourceDirectory), StringComparison.Ordinal))
                {
                    TryDeleteDirectory(temporaryDirectory);
                    if (attempt == 0) continue;
                    throw new IOException("The MCP server build output changed while it was being staged. Build again, then retry.");
                }

                try
                {
                    Directory.Move(temporaryDirectory, deploymentDirectory);
                }
                catch (IOException) when (File.Exists(deployedExecutable))
                {
                    TryDeleteDirectory(temporaryDirectory);
                }

                if (!File.Exists(deployedExecutable))
                    throw new IOException("The staged MCP server executable is missing after deployment.");
                return deployedExecutable;
            }
            finally
            {
                TryDeleteDirectory(temporaryDirectory);
            }
        }

        throw new IOException("The MCP server build output could not be staged.");
    }

    private string GetRegistrationPath(ServerCandidate candidate)
    {
        if (!candidate.RequiresStaging) return candidate.Path;
        var sourceDirectory = Path.GetDirectoryName(candidate.Path)
            ?? throw new IOException("The MCP server output directory could not be resolved.");
        return GetRegistrationPath(candidate.Path, ComputeDeploymentFingerprint(sourceDirectory));
    }

    private string GetRegistrationPath(string sourceExecutable, string fingerprint) =>
        Path.GetFullPath(Path.Combine(_appPaths.DataDirectory, "mcp-server", "deployments", fingerprint,
            Path.GetFileName(sourceExecutable)));

    private static async Task CopyDirectoryAsync(string sourceDirectory, string destinationDirectory,
        CancellationToken cancellationToken)
    {
        foreach (var sourcePath in EnumerateServerFiles(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete, 81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(sourcePath));
        }
    }

    private static string ComputeDeploymentFingerprint(string sourceDirectory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in EnumerateServerFiles(sourceDirectory))
        {
            var info = new FileInfo(path);
            var relativePath = Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/');
            var entry = $"{relativePath}\0{info.Length}\0{info.LastWriteTimeUtc.Ticks}\0";
            hash.AppendData(Encoding.UTF8.GetBytes(entry));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..16];
    }

    private static IEnumerable<string> EnumerateServerFiles(string sourceDirectory) =>
        Directory.EnumerateFiles(sourceDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            })
            .Order(StringComparer.Ordinal);

    private static bool IsRepositoryBuildOutput(string executablePath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(executablePath))!);
        while (directory is not null)
        {
            if (string.Equals(directory.Name, "bin", StringComparison.OrdinalIgnoreCase))
            {
                var projectDirectory = directory.Parent;
                var sourceDirectory = projectDirectory?.Parent;
                var repositoryDirectory = sourceDirectory?.Parent;
                if (sourceDirectory is not null && repositoryDirectory is not null &&
                    string.Equals(sourceDirectory.Name, "src", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(repositoryDirectory.FullName, "MCPIndexSearch.slnx")))
                    return true;
            }

            directory = directory.Parent;
        }

        return false;
    }

    private static string? TryGetManagedServerPath(string config, int start, int end)
    {
        var block = config[start..end];
        var match = ManagedCommandRegex().Match(block);
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A failed partial deployment is never selected or registered.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed partial deployment is never selected or registered.
        }
    }

    private async Task<string> ReadConfigAsync(CancellationToken cancellationToken)
    {
        return File.Exists(ConfigPath)
            ? await File.ReadAllTextAsync(ConfigPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
    }

    private async Task WriteConfigSafelyAsync(string expected, string updated, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(ConfigPath)
            ?? throw new InvalidOperationException("The Codex configuration directory could not be resolved.");
        Directory.CreateDirectory(directory);

        var current = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                SHA256.HashData(Encoding.UTF8.GetBytes(current))))
        {
            throw new IOException("Codex configuration changed while it was being updated. Try again.");
        }

        if (File.Exists(ConfigPath))
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
            File.Copy(ConfigPath, $"{ConfigPath}.mcpindexsearch-{timestamp}.bak", overwrite: false);
        }

        var temporaryPath = $"{ConfigPath}.{Guid.NewGuid():N}.partial";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, updated, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows() && File.Exists(ConfigPath))
                File.SetUnixFileMode(temporaryPath, File.GetUnixFileMode(ConfigPath));
            File.Move(temporaryPath, ConfigPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
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

    private static string AppendBlock(string config, string block)
    {
        if (string.IsNullOrWhiteSpace(config)) return block;
        return config.TrimEnd() + Environment.NewLine + Environment.NewLine + block;
    }

    private static string EscapeToml(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    [GeneratedRegex("""(?m)^\s*\[\s*mcp_servers\.(?:mcp-index-search|"mcp-index-search"|'mcp-index-search')\s*\]\s*(?:#.*)?$""")]
    private static partial Regex UnmanagedServerHeaderRegex();

    [GeneratedRegex("""(?m)^\s*command\s*=\s*"(?<value>(?:\\.|[^"\\])*)"\s*$""")]
    private static partial Regex ManagedCommandRegex();

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record ServerCandidate(string Path, bool RequiresStaging);
}
