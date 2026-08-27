using System.Security.Cryptography;
using System.Text;

using ContextMole.Core;

namespace ContextMole.Infrastructure;

public sealed record McpServerCandidate(string Path, bool RequiresStaging);

public sealed class McpServerDeploymentService(IAppPaths appPaths)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly IAppPaths _appPaths = appPaths;

    public McpServerCandidate? ResolveCandidate()
    {
        var overridePath = Environment.GetEnvironmentVariable("CONTEXTMOLE_MCP_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var fullOverride = Path.GetFullPath(overridePath);
            if (File.Exists(fullOverride)) return new(fullOverride, IsRepositoryBuildOutput(fullOverride));
        }

        var executable = OperatingSystem.IsWindows() ? "ContextMole.Mcp.exe" : "ContextMole.Mcp";
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, executable),
                     Path.Combine(AppContext.BaseDirectory, "mcp-server", executable)
                 })
        {
            if (!File.Exists(candidate)) continue;
            var fullCandidate = Path.GetFullPath(candidate);
            return new(fullCandidate, IsRepositoryBuildOutput(fullCandidate));
        }

        // Development builds keep the UI and MCP executables in separate project output folders.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContextMole.slnx")))
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

    public string GetRegistrationPath(McpServerCandidate candidate)
    {
        if (!candidate.RequiresStaging) return candidate.Path;
        var sourceDirectory = Path.GetDirectoryName(candidate.Path)
            ?? throw new IOException("The MCP server output directory could not be resolved.");
        return GetRegistrationPath(candidate.Path, ComputeDeploymentFingerprint(sourceDirectory));
    }

    public async Task<string> PrepareAsync(McpServerCandidate candidate, CancellationToken cancellationToken = default)
    {
        if (!candidate.RequiresStaging) return candidate.Path;

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StageServerAsync(candidate.Path, cancellationToken).ConfigureAwait(false);
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
        }).Order(StringComparer.Ordinal);

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