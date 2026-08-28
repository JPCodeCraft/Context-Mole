using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;

using ContextMole.Broker.Protocol;
using ContextMole.Core;

namespace ContextMole.Infrastructure;

public sealed record McpServerCandidate(string Path, bool RequiresStaging, string? BrokerPath = null);

public sealed class McpServerDeploymentService(IAppPaths appPaths)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly IAppPaths _appPaths = appPaths;

    public McpServerCandidate? ResolveCandidate() => ResolveCandidateFromDirectory(AppContext.BaseDirectory);

    public McpServerCandidate? ResolveCandidateFromDirectory(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        baseDirectory = Path.GetFullPath(baseDirectory);
        var overridePath = Environment.GetEnvironmentVariable("CONTEXTMOLE_MCP_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var fullOverride = Path.GetFullPath(overridePath);
            if (File.Exists(fullOverride))
            {
                var requiresStaging = IsRepositoryBuildOutput(fullOverride);
                return new(fullOverride, requiresStaging,
                    requiresStaging ? ResolveSeparateDevelopmentBroker(fullOverride) : null);
            }
        }

        var executable = OperatingSystem.IsWindows() ? "ContextMole.Mcp.exe" : "ContextMole.Mcp";
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDirectory, executable),
                     Path.Combine(baseDirectory, "mcp-server", executable)
                 })
        {
            if (!File.Exists(candidate)) continue;
            var fullCandidate = Path.GetFullPath(candidate);
            var requiresStaging = IsRepositoryBuildOutput(fullCandidate);
            return new(fullCandidate, requiresStaging,
                requiresStaging ? ResolveSeparateDevelopmentBroker(fullCandidate) : null);
        }

        // Development builds keep the UI and MCP executables in separate project output folders.
        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContextMole.slnx")))
            {
                var bin = Path.Combine(directory.FullName, "src", "Mcp", "bin");
                if (!Directory.Exists(bin)) return null;
                var developmentPath = ResolveDevelopmentAppHost(baseDirectory, bin, executable);
                return developmentPath is null ? null : new(developmentPath, RequiresStaging: true,
                    ResolveSeparateDevelopmentBroker(developmentPath));
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? ResolveDevelopmentAppHost(string baseDirectory, string mcpBin,
        string executableName)
    {
        var coordinates = TryGetBuildCoordinates(baseDirectory);
        if (coordinates is not null)
        {
            var exactDirectory = Path.Combine(mcpBin, coordinates.Value.Configuration,
                coordinates.Value.TargetFramework);
            if (coordinates.Value.RuntimeIdentifier is not null)
                exactDirectory = Path.Combine(exactDirectory, coordinates.Value.RuntimeIdentifier);
            var exact = Path.Combine(exactDirectory, executableName);
            if (File.Exists(exact)) return Path.GetFullPath(exact);

            var currentRidCandidate = Path.Combine(mcpBin, coordinates.Value.Configuration,
                coordinates.Value.TargetFramework, RuntimeInformation.RuntimeIdentifier, executableName);
            if (File.Exists(currentRidCandidate)) return Path.GetFullPath(currentRidCandidate);

            var frameworkDependent = Path.Combine(mcpBin, coordinates.Value.Configuration,
                coordinates.Value.TargetFramework, executableName);
            if (File.Exists(frameworkDependent)) return Path.GetFullPath(frameworkDependent);
        }

        var currentRid = RuntimeInformation.RuntimeIdentifier;
        return Directory.EnumerateFiles(mcpBin, executableName, SearchOption.AllDirectories)
            .Where(path => IsCompatibleRuntimeOutput(path, mcpBin, currentRid))
            .OrderByDescending(path => DevelopmentCandidateScore(path, coordinates, currentRid))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .Select(Path.GetFullPath)
            .FirstOrDefault();
    }

    private static (string Configuration, string TargetFramework, string? RuntimeIdentifier)?
        TryGetBuildCoordinates(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null && !directory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
            directory = directory.Parent;
        if (directory is null) return null;

        var relative = Path.GetRelativePath(directory.FullName, baseDirectory);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;
        var rid = segments.Length >= 3 && IsRuntimeIdentifier(segments[2]) ? segments[2] : null;
        return (segments[0], segments[1], rid);
    }

    private static bool IsCompatibleRuntimeOutput(string path, string binDirectory, string currentRid)
    {
        var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(binDirectory, path));
        if (string.IsNullOrWhiteSpace(relativeDirectory)) return true;
        var segments = relativeDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var candidateRid = segments.FirstOrDefault(IsRuntimeIdentifier);
        return candidateRid is null || candidateRid.Equals(currentRid, StringComparison.OrdinalIgnoreCase);
    }

    private static int DevelopmentCandidateScore(string path,
        (string Configuration, string TargetFramework, string? RuntimeIdentifier)? coordinates,
        string currentRid)
    {
        var segments = Path.GetDirectoryName(path)!.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var score = segments.Any(segment => segment.Equals(currentRid, StringComparison.OrdinalIgnoreCase)) ? 2 : 1;
        if (coordinates is null) return score;
        if (segments.Any(segment => segment.Equals(coordinates.Value.Configuration,
                StringComparison.OrdinalIgnoreCase))) score += 4;
        if (segments.Any(segment => segment.Equals(coordinates.Value.TargetFramework,
                StringComparison.OrdinalIgnoreCase))) score += 8;
        return score;
    }

    private static bool IsRuntimeIdentifier(string value) =>
        value.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("linux-", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("osx-", StringComparison.OrdinalIgnoreCase);

    public string GetRegistrationPath(McpServerCandidate candidate)
    {
        if (!candidate.RequiresStaging) return candidate.Path;
        var sourceDirectory = Path.GetDirectoryName(candidate.Path)
            ?? throw new IOException("The MCP server output directory could not be resolved.");
        return GetRegistrationPath(candidate.Path, ComputeDeploymentFingerprint(sourceDirectory,
            GetSeparateBrokerDirectory(candidate)));
    }

    public async Task<string> PrepareAsync(McpServerCandidate candidate, CancellationToken cancellationToken = default)
    {
        if (!candidate.RequiresStaging) return candidate.Path;

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StageServerAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static bool IsRepositoryBuildOutput(string executablePath)
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
                    File.Exists(Path.Combine(repositoryDirectory.FullName, "ContextMole.slnx")))
                    return true;
            }

            directory = directory.Parent;
        }

        return false;
    }

    private async Task<string> StageServerAsync(McpServerCandidate candidate,
        CancellationToken cancellationToken)
    {
        var sourceExecutable = candidate.Path;
        var sourceDirectory = Path.GetDirectoryName(sourceExecutable)
            ?? throw new IOException("The MCP server output directory could not be resolved.");
        var brokerDirectory = GetSeparateBrokerDirectory(candidate);
        var deploymentsDirectory = Path.GetFullPath(Path.Combine(_appPaths.DataDirectory, "mcp-server", "deployments"));
        Directory.CreateDirectory(deploymentsDirectory);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fingerprint = ComputeDeploymentFingerprint(sourceDirectory, brokerDirectory);
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
                if (brokerDirectory is not null)
                    await CopyDirectoryAsync(brokerDirectory, Path.Combine(temporaryDirectory, "broker"),
                        cancellationToken, brokerPayload: true).ConfigureAwait(false);
                if (!string.Equals(fingerprint, ComputeDeploymentFingerprint(sourceDirectory, brokerDirectory),
                        StringComparison.Ordinal))
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

    private string GetRegistrationPath(string sourceExecutable, string fingerprint) =>
        Path.GetFullPath(Path.Combine(_appPaths.DataDirectory, "mcp-server", "deployments", fingerprint,
            Path.GetFileName(sourceExecutable)));

    private static async Task CopyDirectoryAsync(string sourceDirectory, string destinationDirectory,
        CancellationToken cancellationToken, bool brokerPayload = false)
    {
        var files = brokerPayload ? EnumerateDevelopmentBrokerFiles(sourceDirectory) :
            EnumerateServerFiles(sourceDirectory);
        foreach (var sourcePath in files)
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

    private static string ComputeDeploymentFingerprint(string sourceDirectory, string? brokerDirectory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDirectory(sourceDirectory, "adapter", brokerPayload: false);
        if (brokerDirectory is not null) AppendDirectory(brokerDirectory, "broker", brokerPayload: true);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..16];

        void AppendDirectory(string directory, string prefix, bool brokerPayload)
        {
            var files = brokerPayload ? EnumerateDevelopmentBrokerFiles(directory) :
                EnumerateServerFiles(directory);
            foreach (var path in files)
            {
                var info = new FileInfo(path);
                var relativePath = Path.GetRelativePath(directory, path).Replace('\\', '/');
                var entry = $"{prefix}/{relativePath}\0{info.Length}\0{info.LastWriteTimeUtc.Ticks}\0";
                hash.AppendData(Encoding.UTF8.GetBytes(entry));
            }
        }
    }

    private static string? ResolveSeparateDevelopmentBroker(string mcpExecutable)
    {
        var mcpDirectory = Path.GetDirectoryName(mcpExecutable)!;
        try
        {
            var brokerPath = BrokerLaunchCommand.ResolveFromDirectory(mcpDirectory).FileName;
            var relative = Path.GetRelativePath(mcpDirectory, brokerPath);
            if (!relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !string.Equals(relative, "..", StringComparison.Ordinal))
                return null;
            return brokerPath;
        }
        catch (Exception exception) when (exception is BrokerRpcException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? GetSeparateBrokerDirectory(McpServerCandidate candidate) =>
        candidate.BrokerPath is null
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(candidate.BrokerPath));

    private static IEnumerable<string> EnumerateServerFiles(string sourceDirectory) =>
        Directory.EnumerateFiles(sourceDirectory, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        }).Order(StringComparer.Ordinal);

    private static IEnumerable<string> EnumerateDevelopmentBrokerFiles(string sourceDirectory)
    {
        var currentRid = RuntimeInformation.RuntimeIdentifier;
        string[] platformFallbacks = OperatingSystem.IsWindows() ? ["win"] :
            OperatingSystem.IsMacOS() ? ["osx", "unix"] : ["linux", "unix"];
        foreach (var path in EnumerateServerFiles(sourceDirectory))
        {
            if (Path.GetExtension(path).Equals(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/');
            if (!relative.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
                continue;
            }

            var separator = relative.IndexOf('/', "runtimes/".Length);
            if (separator < 0) continue;
            var rid = relative["runtimes/".Length..separator];
            if (rid.Equals(currentRid, StringComparison.OrdinalIgnoreCase) ||
                platformFallbacks.Any(fallback => rid.Equals(fallback, StringComparison.OrdinalIgnoreCase)))
                yield return path;
        }
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
}
