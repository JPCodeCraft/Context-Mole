using System.Security.Cryptography;
using System.Text;

namespace ContextMole.Infrastructure;

internal static class SafeConfigurationFile
{
    public static async Task WriteAsync(
        string configPath,
        string expected,
        string updated,
        string description,
        CancellationToken cancellationToken)
    {
        var targetPath = ResolveTargetPath(configPath, description);
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException($"The {description} directory could not be resolved.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.partial";
        try
        {
            await WritePrivateTemporaryFileAsync(temporaryPath, targetPath, updated, cancellationToken)
                .ConfigureAwait(false);
            await EnsureUnchangedAsync(targetPath, expected, description, cancellationToken).ConfigureAwait(false);

            if (File.Exists(targetPath))
            {
                var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
                File.Copy(targetPath, $"{targetPath}.contextmole-{timestamp}-{Guid.NewGuid():N}.bak",
                    overwrite: false);
            }

            await EnsureUnchangedAsync(targetPath, expected, description, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A unique partial file is never read as configuration and can be cleaned up later.
            }
        }
    }

    private static string ResolveTargetPath(string configPath, string description)
    {
        var info = new FileInfo(configPath);
        if (info.LinkTarget is null) return configPath;

        var target = info.ResolveLinkTarget(returnFinalTarget: true)
            ?? throw new IOException($"The {description} symbolic link does not resolve to a file.");
        return Path.GetFullPath(target.FullName);
    }

    private static async Task WritePrivateTemporaryFileAsync(
        string temporaryPath,
        string targetPath,
        string content,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.Exists(targetPath)
                ? File.GetUnixFileMode(targetPath)
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(temporaryPath, mode);
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureUnchangedAsync(
        string path,
        string expected,
        string description,
        CancellationToken cancellationToken)
    {
        var current = File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                SHA256.HashData(Encoding.UTF8.GetBytes(current))))
        {
            throw new IOException($"The {description} changed while it was being updated. Try again.");
        }
    }
}