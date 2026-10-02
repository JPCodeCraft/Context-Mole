using System.ComponentModel;
using System.Runtime.InteropServices;
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
        cancellationToken.ThrowIfCancellationRequested();
        var targetPath = ResolveTargetPath(configPath, description);
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException($"The {description} directory could not be resolved.");
        Directory.CreateDirectory(directory);
        var targetExisted = File.Exists(targetPath);

        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.partial";
        try
        {
            await WritePrivateTemporaryFileAsync(temporaryPath, targetPath, updated, cancellationToken)
                .ConfigureAwait(false);
            await EnsureUnchangedAsync(targetPath, expected, targetExisted, description, cancellationToken)
                .ConfigureAwait(false);
            await CommitPreparedFileAsync(temporaryPath, targetPath, expected, targetExisted, description,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
    }

    // Separate the commit from preparation so its final-read race can be tested deterministically.
    internal static async Task CommitPreparedFileAsync(
        string temporaryPath,
        string targetPath,
        string expected,
        bool targetExisted,
        string description,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!targetExisted)
        {
            // File.Move(overwrite: false) is a check-then-rename on Unix, not an atomic create-if-absent.
            MoveWithoutReplacing(temporaryPath, targetPath);
            return;
        }

        if (Directory.Exists(targetPath) || new FileInfo(targetPath).LinkTarget is not null)
            throw new IOException($"The {description} target changed while it was being updated. Try again.");

        var backupPath = CreateBackupPath(targetPath);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Delegate replacement-with-backup to Windows ReplaceFile. Native concurrent-writer
                // and ACL behavior still needs validation; this is not a content CAS.
                cancellationToken.ThrowIfCancellationRequested();
                File.Replace(temporaryPath, targetPath, backupPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // ReplaceFile can fail after moving the original to its backup. Never remove that backup.
                if (File.Exists(backupPath))
                    throw RecoveryRequired(description, backupPath, exception);
                throw;
            }
        }
        else
        {
            // Stage at the recovery pathname first: an exchange leaves the displaced file directly there.
            MoveWithoutReplacing(temporaryPath, backupPath);
            var captured = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ExchangeFiles(backupPath, targetPath);
                captured = true;
            }
            finally
            {
                if (!captured) DeleteTemporaryFile(backupPath);
            }
        }

        // The commit is the cancellation boundary. A late cancellation must not mask a competing save.
        string displaced;
        try
        {
            if (Directory.Exists(backupPath) || new FileInfo(backupPath).LinkTarget is not null)
                throw new IOException("The displaced target is no longer a regular file.");
            displaced = await File.ReadAllTextAsync(backupPath, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw RecoveryRequired(description, backupPath, exception);
        }

        if (ContentsEqual(expected, displaced)) return;

        // There is no cross-process content CAS. Restoring blindly could overwrite yet another save.
        // Keep the actual displaced file, and also retain the caller's original text when possible.
        var originalPath = CreateBackupPath(targetPath, "expected-");
        var originalTemporaryPath = $"{originalPath}.partial";
        var originalRecovery = string.Empty;
        try
        {
            await WritePrivateTemporaryFileAsync(originalTemporaryPath, targetPath, expected, CancellationToken.None,
                privateMode: true).ConfigureAwait(false);
            MoveWithoutReplacing(originalTemporaryPath, originalPath);
            originalRecovery = $" The previously read configuration text is preserved at '{originalPath}'.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            originalRecovery = " A separate copy of the previously read configuration text could not be saved.";
        }
        finally
        {
            DeleteTemporaryFile(originalTemporaryPath);
        }

        throw new IOException($"The {description} changed while it was being updated. " +
                              $"The competing save is preserved at '{backupPath}'. " +
                              "Context Mole's update may already be active; review the configuration and backup " +
                              "before trying again." + originalRecovery);
    }

    private static IOException RecoveryRequired(string description, string backupPath, Exception exception)
        => new($"The {description} update could not be verified. The displaced configuration is preserved at " +
               $"'{backupPath}'. Context Mole's update may already be active; review the configuration and backup " +
               "before trying again.", exception);

    private static string CreateBackupPath(string targetPath, string kind = "")
        => $"{targetPath}.contextmole-{kind}{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.bak";

    private static string ResolveTargetPath(string configPath, string description)
    {
        var info = new FileInfo(configPath);
        if (info.LinkTarget is null) return Path.GetFullPath(configPath);

        var target = info.ResolveLinkTarget(returnFinalTarget: true)
            ?? throw new IOException($"The {description} symbolic link does not resolve to a file.");
        return Path.GetFullPath(target.FullName);
    }

    private static async Task WritePrivateTemporaryFileAsync(
        string temporaryPath,
        string targetPath,
        string content,
        CancellationToken cancellationToken,
        bool privateMode = false)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 81920,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = !privateMode && File.Exists(targetPath)
                ? File.GetUnixFileMode(targetPath)
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var stream = new FileStream(temporaryPath, options);
        if (!OperatingSystem.IsWindows())
        {
            // Apply the original mode even when the process umask is more restrictive.
            File.SetUnixFileMode(temporaryPath, options.UnixCreateMode!.Value);
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureUnchangedAsync(
        string path,
        string expected,
        bool expectedToExist,
        string description,
        CancellationToken cancellationToken)
    {
        var exists = File.Exists(path);
        var current = exists
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        if (exists != expectedToExist || !ContentsEqual(expected, current))
            throw new IOException($"The {description} changed while it was being updated. Try again.");
    }

    private static bool ContentsEqual(string expected, string current)
        => CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(current)));

    private static void MoveWithoutReplacing(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Move(source, destination, overwrite: false);
            return;
        }
        RenameUnix(source, destination, exchange: false);
    }

    private static void ExchangeFiles(string source, string destination)
        => RenameUnix(source, destination, exchange: true);

    private static void RenameUnix(string source, string destination, bool exchange)
    {
        int result;
        try
        {
            if (OperatingSystem.IsLinux())
                result = RenameAt2(-100, source, -100, destination, exchange ? 2u : 1u);
            else if (OperatingSystem.IsMacOS())
                result = RenameX(source, destination, exchange ? 2u : 4u);
            else
                throw new IOException("This platform does not support safe configuration replacement.");
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            throw new IOException("This platform does not support safe configuration replacement.", exception);
        }

        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            // Fail closed; never fall back to a check-then-overwrite operation.
            throw new IOException("The configuration could not be committed safely. " +
                                  "It may have changed, or the file system may not support atomic replacement.",
                new Win32Exception(error));
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only uncommitted, uniquely named staging files reach this cleanup.
        }
    }

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameX([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);
}
