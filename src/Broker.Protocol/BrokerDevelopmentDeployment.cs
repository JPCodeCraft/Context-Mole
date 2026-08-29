using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ContextMole.Broker.Protocol;

internal static class BrokerDevelopmentDeployment
{
    private const int MaximumAttempts = 2;
    private const int CopyBufferSize = 81920;
    private static readonly object Gate = new();

    public static BrokerLaunchCommand StageIfRepositoryBuild(BrokerEndpoint endpoint, BrokerLaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(command);
        if (!TryGetRepositoryBuildDirectory(command.FileName, out var sourceDirectory)) return command;

        lock (Gate)
        {
            var stagedExecutable = Stage(endpoint, sourceDirectory, Path.GetFileName(command.FileName));
            return command with { FileName = stagedExecutable };
        }
    }

    private static string Stage(BrokerEndpoint endpoint, string sourceDirectory, string executableName)
    {
        endpoint.EnsurePrivateBrokerDirectory();
        var deploymentsDirectory = Path.Combine(endpoint.BrokerDirectory, "deployments");
        EnsurePlainDirectory(deploymentsDirectory);

        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var before = CaptureSnapshot(sourceDirectory);
            if (!before.Any(file => string.Equals(file.RelativePath, executableName,
                    PathComparison)))
                throw new FileNotFoundException("The development broker executable is missing from its build output.",
                    Path.Combine(sourceDirectory, executableName));

            var temporaryDirectory = Path.Combine(deploymentsDirectory,
                $".staging-{Environment.ProcessId}-{Guid.NewGuid():N}.partial");
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                var fingerprint = CopySnapshot(before, temporaryDirectory);
                var after = CaptureSnapshot(sourceDirectory);
                if (!SnapshotsMatch(before, after))
                {
                    TryDeleteDirectory(temporaryDirectory);
                    if (attempt + 1 < MaximumAttempts) continue;
                    throw new IOException(
                        "The development broker build output changed while it was being staged. Build again, then retry.");
                }

                var deploymentDirectory = Path.Combine(deploymentsDirectory, fingerprint);
                var stagedExecutable = Path.Combine(deploymentDirectory, executableName);
                if (File.Exists(stagedExecutable))
                {
                    EnsurePlainDeployment(deploymentDirectory, stagedExecutable);
                    return stagedExecutable;
                }

                if (Directory.Exists(deploymentDirectory))
                    throw new IOException($"The staged development broker deployment is incomplete: {deploymentDirectory}");

                try
                {
                    Directory.Move(temporaryDirectory, deploymentDirectory);
                }
                catch (IOException) when (File.Exists(stagedExecutable))
                {
                    // Another process atomically published the same content while this copy was in progress.
                }

                EnsurePlainDeployment(deploymentDirectory, stagedExecutable);
                return stagedExecutable;
            }
            finally
            {
                TryDeleteDirectory(temporaryDirectory);
            }
        }

        throw new IOException("The development broker build output could not be staged.");
    }

    private static SourceFile[] CaptureSnapshot(string sourceDirectory) =>
        EnumeratePayloadFiles(sourceDirectory)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new SourceFile(
                    info.FullName,
                    Path.GetRelativePath(sourceDirectory, info.FullName),
                    info.Length,
                    info.LastWriteTimeUtc.Ticks);
            })
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();

    private static string CopySnapshot(IReadOnlyList<SourceFile> snapshot, string destinationDirectory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferSize];
        foreach (var sourceFile in snapshot)
        {
            var normalizedRelativePath = sourceFile.RelativePath.Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(
                $"{normalizedRelativePath}\0{sourceFile.Length}\0"));

            var destinationPath = Path.Combine(destinationDirectory, sourceFile.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using (var source = new FileStream(sourceFile.FullPath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, CopyBufferSize, FileOptions.SequentialScan))
            using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, CopyBufferSize, FileOptions.SequentialScan))
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer.AsSpan(0, read));
                    destination.Write(buffer, 0, read);
                }
            }

            File.SetLastWriteTimeUtc(destinationPath, new DateTime(sourceFile.LastWriteTimeUtcTicks, DateTimeKind.Utc));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(sourceFile.FullPath));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..24];
    }

    private static bool SnapshotsMatch(IReadOnlyList<SourceFile> left, IReadOnlyList<SourceFile> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].RelativePath, right[index].RelativePath, PathComparison) ||
                left[index].Length != right[index].Length ||
                left[index].LastWriteTimeUtcTicks != right[index].LastWriteTimeUtcTicks)
                return false;
        }

        return true;
    }

    private static IEnumerable<string> EnumeratePayloadFiles(string sourceDirectory)
    {
        var currentRid = RuntimeInformation.RuntimeIdentifier;
        string[] platformFallbacks = OperatingSystem.IsWindows() ? ["win"] :
            OperatingSystem.IsMacOS() ? ["osx", "unix"] : ["linux", "unix"];
        foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = false,
                     AttributesToSkip = FileAttributes.ReparsePoint
                 }))
        {
            if (Path.GetExtension(path).Equals(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/');
            var topLevelSeparator = relative.IndexOf('/');
            if (topLevelSeparator > 0)
            {
                var topLevelDirectory = relative[..topLevelSeparator];
                if (IsRuntimeIdentifier(topLevelDirectory) ||
                    topLevelDirectory.Equals("publish", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
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

    private static bool IsRuntimeIdentifier(string value) =>
        value.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("linux-", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("osx-", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetRepositoryBuildDirectory(string executablePath, out string sourceDirectory)
    {
        sourceDirectory = string.Empty;
        if (!Path.IsPathFullyQualified(executablePath)) return false;

        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath)) return false;
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The development broker executable must not be a reparse point.");

        sourceDirectory = Path.GetDirectoryName(fullPath)!;
        var directory = new DirectoryInfo(sourceDirectory);
        while (directory is not null)
        {
            if (directory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                var projectDirectory = directory.Parent;
                var sourceRoot = projectDirectory?.Parent;
                var repositoryRoot = sourceRoot?.Parent;
                if (sourceRoot?.Name.Equals("src", StringComparison.OrdinalIgnoreCase) == true &&
                    repositoryRoot is not null &&
                    File.Exists(Path.Combine(repositoryRoot.FullName, "ContextMole.slnx")))
                    return true;
            }
            directory = directory.Parent;
        }

        sourceDirectory = string.Empty;
        return false;
    }

    private static void EnsurePlainDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The development broker deployments directory must not be a reparse point.");
    }

    private static void EnsurePlainDeployment(string directory, string executable)
    {
        if (!Directory.Exists(directory) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The staged development broker deployment is unavailable or invalid.");
        if (!File.Exists(executable) ||
            (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The staged development broker executable is unavailable or invalid.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A partial directory is never selected as a deployment.
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record SourceFile(
        string FullPath,
        string RelativePath,
        long Length,
        long LastWriteTimeUtcTicks);
}
