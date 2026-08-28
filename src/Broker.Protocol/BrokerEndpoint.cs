using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using ContextMole.Core;

namespace ContextMole.Broker.Protocol;

public sealed class BrokerEndpoint
{
    private const string BrokerDirectoryName = ".broker";
    private const string TokenFileName = "auth-token-v1";
    private static readonly ConcurrentDictionary<string, object> AdmissionGates =
        new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    public BrokerEndpoint(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        DataDirectoryId = ComputeId(DataDirectory);
        PipeName = $"context-mole-v{BrokerProtocol.MajorVersion}-{DataDirectoryId}";
        BrokerDirectory = Path.Combine(DataDirectory, BrokerDirectoryName);
        AuthenticationTokenPath = Path.Combine(BrokerDirectory, TokenFileName);
        StartupLockPath = Path.Combine(BrokerDirectory, $"start-v{BrokerProtocol.MajorVersion}.lock");
        InstanceLockPath = Path.Combine(BrokerDirectory, $"instance-v{BrokerProtocol.MajorVersion}.lock");
        InstanceMetadataPath = Path.Combine(BrokerDirectory, $"instance-v{BrokerProtocol.MajorVersion}.json");
    }

    public string DataDirectory { get; }
    public string DataDirectoryId { get; }
    public string PipeName { get; }
    public string BrokerDirectory { get; }
    public string AuthenticationTokenPath { get; }
    public string StartupLockPath { get; }
    public string InstanceLockPath { get; }
    public string InstanceMetadataPath { get; }

    public string GetOrCreateAuthenticationToken()
    {
        lock (AdmissionGates.GetOrAdd(DataDirectory, static _ => new object()))
        {
            using var temporaryAdmission = ContextMoleProcessCoordination.AcquireLease(DataDirectory,
                "broker-token");
            return CreateOrReadTokenAfterAdmission();
        }
    }

    private string CreateOrReadTokenAfterAdmission()
    {
        if (ContextMoleProcessCoordination.IsShutdownRequested(DataDirectory))
            throw new ContextMoleException("application_shutting_down",
                "Context Mole is being uninstalled. The shared broker will not start.", false);
        EnsurePrivateBrokerDirectory();
        var temporaryPath = Path.Combine(BrokerDirectory, $".{TokenFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(token);
                writer.Flush();
                stream.Flush(true);
            }
            ApplyPrivateFilePermissions(temporaryPath);
            try
            {
                File.Move(temporaryPath, AuthenticationTokenPath, overwrite: false);
                return token;
            }
            catch (IOException) when (File.Exists(AuthenticationTokenPath))
            {
                return ReadAuthenticationToken();
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void EnsurePrivateBrokerDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BrokerDirectory);
        var attributes = File.GetAttributes(BrokerDirectory);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Context Mole broker directory must not be a reparse point.");
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(BrokerDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException("The Context Mole broker directory could not be made private.", exception);
            }
        }
    }

    private static string ComputeId(string dataDirectory)
    {
        var path = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? dataDirectory.ToUpperInvariant()
            : dataDirectory;
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}\0{path}"));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    private static void ApplyPrivateFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private string ReadAuthenticationToken()
    {
        var existing = File.ReadAllText(AuthenticationTokenPath, Encoding.ASCII).Trim();
        if (existing.Length == 64 && existing.All(Uri.IsHexDigit)) return existing;
        throw new IOException("The Context Mole broker authentication token is invalid.");
    }

}
