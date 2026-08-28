using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

using ContextMole.Core;

namespace ContextMole.Broker.Protocol;

public sealed record BrokerLaunchCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public static BrokerLaunchCommand Resolve() => ResolveFromDirectory(AppContext.BaseDirectory);

    public static BrokerLaunchCommand ResolveFromDirectory(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        baseDirectory = Path.GetFullPath(baseDirectory);
        var executableName = OperatingSystem.IsWindows() ? "ContextMole.Broker.exe" : "ContextMole.Broker";
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "broker", executableName),
            Path.Combine(baseDirectory, executableName),
            Path.Combine(baseDirectory, "mcp-server", "broker", executableName)
        };
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            return new BrokerLaunchCommand(ValidateExecutablePath(candidate), []);
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContextMole.slnx")))
            {
                var brokerBin = Path.Combine(directory.FullName, "src", "Broker", "bin");
                var developmentPath = ResolveDevelopmentAppHost(baseDirectory, brokerBin, executableName);
                if (developmentPath is not null)
                    return new BrokerLaunchCommand(ValidateExecutablePath(developmentPath), []);
            }

            directory = directory.Parent;
        }

        throw new BrokerRpcException("broker_unavailable",
            "The Context Mole broker executable could not be found in this deployment.", true);
    }

    private static string? ResolveDevelopmentAppHost(string baseDirectory, string brokerBin,
        string executableName)
    {
        if (!Directory.Exists(brokerBin)) return null;

        var buildCoordinates = TryGetBuildCoordinates(baseDirectory);
        if (buildCoordinates is not null)
        {
            var exact = Path.Combine(brokerBin, buildCoordinates.Value.Configuration,
                buildCoordinates.Value.TargetFramework,
                buildCoordinates.Value.RuntimeIdentifier is null
                    ? executableName
                    : Path.Combine(buildCoordinates.Value.RuntimeIdentifier, executableName));
            if (File.Exists(exact)) return Path.GetFullPath(exact);

            var currentRid = RuntimeInformation.RuntimeIdentifier;
            var currentRidCandidate = Path.Combine(brokerBin, buildCoordinates.Value.Configuration,
                buildCoordinates.Value.TargetFramework, currentRid, executableName);
            if (File.Exists(currentRidCandidate)) return Path.GetFullPath(currentRidCandidate);

            var frameworkDependent = Path.Combine(brokerBin, buildCoordinates.Value.Configuration,
                buildCoordinates.Value.TargetFramework, executableName);
            if (File.Exists(frameworkDependent)) return Path.GetFullPath(frameworkDependent);
        }

        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        return Directory.EnumerateFiles(brokerBin, executableName, SearchOption.AllDirectories)
            .Where(path => IsCompatibleRuntimeOutput(path, brokerBin, runtimeIdentifier))
            .OrderByDescending(path => DevelopmentCandidateScore(path, buildCoordinates, runtimeIdentifier))
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

    private static string ValidateExecutablePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The Context Mole broker executable was not found.", fullPath);
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Context Mole broker executable must not be a reparse point.");
        return fullPath;
    }

}

public sealed class BrokerRpcClient
{
    private const string DataDirectoryEnvironmentVariable = "CONTEXTMOLE_DATA_DIR";
    private static readonly TimeSpan InitialConnectTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProbeConnectTimeout = TimeSpan.FromMilliseconds(500);

    private readonly BrokerEndpoint _endpoint;
    private readonly Lazy<BrokerLaunchCommand> _launchCommand;
    private readonly object _authenticationTokenGate = new();
    private string? _authenticationToken;
    private readonly string _clientVersion;
    private readonly string _deploymentId;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _startupTimeout;
    private readonly Action<BrokerLaunchCommand> _brokerStarter;

    public BrokerRpcClient(
        string dataDirectory,
        BrokerLaunchCommand launchCommand,
        TimeProvider? timeProvider = null,
        TimeSpan? startupTimeout = null,
        string? clientVersion = null,
        string? deploymentId = null)
        : this(dataDirectory, () => launchCommand, timeProvider, startupTimeout, clientVersion, deploymentId,
            null)
    {
    }

    public BrokerRpcClient(
        string dataDirectory,
        Func<BrokerLaunchCommand> launchCommandFactory,
        TimeProvider? timeProvider = null,
        TimeSpan? startupTimeout = null,
        string? clientVersion = null,
        string? deploymentId = null)
        : this(dataDirectory, launchCommandFactory, timeProvider, startupTimeout, clientVersion, deploymentId,
            null)
    {
    }

    internal BrokerRpcClient(
        string dataDirectory,
        Func<BrokerLaunchCommand> launchCommandFactory,
        TimeProvider? timeProvider,
        TimeSpan? startupTimeout,
        string? clientVersion,
        string? deploymentId,
        Action<BrokerLaunchCommand>? brokerStarter)
    {
        _endpoint = new BrokerEndpoint(dataDirectory);
        ArgumentNullException.ThrowIfNull(launchCommandFactory);
        _launchCommand = new Lazy<BrokerLaunchCommand>(launchCommandFactory,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startupTimeout = startupTimeout ?? BrokerProtocol.DefaultStartupTimeout;
        if (_startupTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        var assembly = typeof(BrokerRpcClient).Assembly;
        _clientVersion = clientVersion ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        _deploymentId = deploymentId ?? assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                            .InformationalVersion ?? _clientVersion;
        ArgumentException.ThrowIfNullOrWhiteSpace(_clientVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(_deploymentId);
        _brokerStarter = brokerStarter ?? StartBrokerProcess;
    }

    public static BrokerRpcClient CreateForCurrentProcess(string dataDirectory) =>
        new(dataDirectory, static () => BrokerLaunchCommand.Resolve());

    public Task<JsonElement> InvokeAsync<TRequest>(string method, TRequest payload,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null) =>
        InvokeElementAsync(method, JsonSerializer.SerializeToElement(payload, BrokerJson.Options),
            cancellationToken, timeout);

    public async Task<TResponse> InvokeAsync<TRequest, TResponse>(string method, TRequest payload,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var element = await InvokeAsync(method, payload, cancellationToken, timeout).ConfigureAwait(false);
        try
        {
            return element.Deserialize<TResponse>(BrokerJson.Options)
                   ?? throw new BrokerRpcException("invalid_response", "The broker returned an empty result.", true);
        }
        catch (JsonException exception)
        {
            throw new BrokerRpcException("invalid_response", "The broker returned an incompatible result.", true,
                exception);
        }
    }

    public Task<BrokerHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<object, BrokerHealthResponse>(BrokerProtocol.HealthMethod, new { }, cancellationToken,
            TimeSpan.FromSeconds(5));

    public async Task ReloadEmbeddingAsync(CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync(BrokerProtocol.EmbeddingReloadMethod, new { }, cancellationToken).ConfigureAwait(false);

    public Task<BrokerEmbeddingStatus> GetEmbeddingStatusAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<object, BrokerEmbeddingStatus>(BrokerProtocol.EmbeddingStatusMethod, new { }, cancellationToken);

    public Task<BrokerCountTokensResponse> CountTokensAsync(string text,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<BrokerCountTokensRequest, BrokerCountTokensResponse>(BrokerProtocol.EmbeddingCountTokensMethod,
            new BrokerCountTokensRequest(text), cancellationToken);

    public Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<BrokerEmbedPassagesRequest, EmbeddingBatch>(BrokerProtocol.EmbeddingPassagesMethod,
            new BrokerEmbedPassagesRequest(passages), cancellationToken);

    public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken = default) =>
        InvokeAsync<BrokerEmbedQueryRequest, QueryEmbedding>(BrokerProtocol.EmbeddingQueryMethod,
            new BrokerEmbedQueryRequest(query), cancellationToken);

    private async Task<JsonElement> InvokeElementAsync(string method, JsonElement payload,
        CancellationToken cancellationToken, TimeSpan? timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var requestTimeout = timeout ?? BrokerProtocol.DefaultRequestTimeout;
        if (requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        Exception? lastTransportFailure = null;
        var maximumAttempts = string.Equals(method, BrokerProtocol.ShutdownMethod, StringComparison.Ordinal) ? 1 : 2;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return (await InvokeConnectedAsync(method, payload, requestTimeout,
                    attempt == 0 ? InitialConnectTimeout : ProbeConnectTimeout, cancellationToken)
                    .ConfigureAwait(false)).Result;
            }
            catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
            {
                lastTransportFailure = exception;
                if (attempt == 0 && maximumAttempts > 1)
                {
                    await EnsureBrokerStartedAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            catch (BrokerRpcException exception) when (
                string.Equals(exception.Code, BrokerProtocol.DeploymentMismatchCode, StringComparison.Ordinal))
            {
                lastTransportFailure = exception;
                if (attempt == 0 && maximumAttempts > 1)
                {
                    await EnsureBrokerStartedAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
        }

        if (lastTransportFailure is BrokerRpcException { Code: BrokerProtocol.DeploymentMismatchCode } mismatch)
            throw new BrokerRpcException(BrokerProtocol.DeploymentMismatchCode,
                "The packaged Context Mole broker deployment could not replace an incompatible shared broker.",
                true, mismatch);
        throw new BrokerRpcException("broker_unavailable",
            "The shared Context Mole broker is unavailable. Retry the request.", true, lastTransportFailure);
    }

    private async Task<BrokerConnectedResult> InvokeConnectedAsync(string method, JsonElement payload,
        TimeSpan requestTimeout, TimeSpan connectTimeout, CancellationToken cancellationToken,
        BrokerDeploymentMode deploymentMode = BrokerDeploymentMode.RequireCompatible)
    {
        using var requestTimeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeoutCancellation.CancelAfter(requestTimeout);
        var token = requestTimeoutCancellation.Token;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _endpoint.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)Math.Clamp(connectTimeout.TotalMilliseconds, 1, int.MaxValue), token)
                .ConfigureAwait(false);

            await BrokerFrameCodec.WriteAsync(pipe, new BrokerHandshakeRequest(
                BrokerProtocol.MajorVersion,
                BrokerProtocol.MinorVersion,
                GetAuthenticationToken(),
                _endpoint.DataDirectoryId,
                _clientVersion,
                _deploymentId), token).ConfigureAwait(false);
            var handshake = await BrokerFrameCodec.ReadAsync<BrokerHandshakeResponse>(pipe, token).ConfigureAwait(false);
            if (!handshake.Accepted)
            {
                var error = handshake.Error ?? new BrokerRpcError("protocol_mismatch",
                    "The broker rejected the protocol handshake.", false);
                throw new BrokerRpcException(error.Code, error.Message, error.Retryable);
            }
            if (handshake.ProtocolMajor != BrokerProtocol.MajorVersion)
                throw new BrokerRpcException("protocol_mismatch",
                    "The client and shared broker use incompatible protocol versions.", false);
            var deploymentRelation = BrokerProtocol.CompareDeployments(_clientVersion, _deploymentId,
                handshake.BrokerVersion, handshake.DeploymentId);
            if (deploymentMode == BrokerDeploymentMode.ShutdownOlderOnly)
            {
                if (deploymentRelation == BrokerDeploymentRelation.Conflict)
                    throw new BrokerRpcException(BrokerProtocol.DeploymentConflictCode,
                        "The client and shared broker have different builds of the same Context Mole version.",
                        false);
                if (deploymentRelation is BrokerDeploymentRelation.Same or BrokerDeploymentRelation.BrokerNewer)
                    return new BrokerConnectedResult(default, false);
            }
            else
            {
                if (deploymentRelation == BrokerDeploymentRelation.ClientNewer)
                    throw new BrokerRpcException(BrokerProtocol.DeploymentMismatchCode,
                        "The client requires a newer Context Mole broker deployment.", true);
                if (deploymentRelation == BrokerDeploymentRelation.Conflict)
                    throw new BrokerRpcException(BrokerProtocol.DeploymentConflictCode,
                        "The client and shared broker have different builds of the same Context Mole version.",
                        false);
            }

            var request = new BrokerRpcRequest(Guid.CreateVersion7(), method, payload,
                _timeProvider.GetUtcNow().Add(requestTimeout));
            await BrokerFrameCodec.WriteAsync(pipe, request, token).ConfigureAwait(false);
            var response = await BrokerFrameCodec.ReadAsync<BrokerRpcResponse>(pipe, token).ConfigureAwait(false);
            if (response.RequestId != request.RequestId)
                throw new BrokerRpcException("invalid_response", "The broker response ID did not match the request.", true);
            if (response.Error is not null)
                throw new BrokerRpcException(response.Error.Code, response.Error.Message, response.Error.Retryable);
            return new BrokerConnectedResult(
                response.Result ?? JsonSerializer.SerializeToElement(new { }, BrokerJson.Options), true);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested &&
                                                           requestTimeoutCancellation.IsCancellationRequested)
        {
            throw new BrokerRpcException("deadline_exceeded", "The broker request deadline elapsed.", true,
                exception);
        }
    }

    private async Task EnsureBrokerStartedAsync(CancellationToken cancellationToken)
    {
        ContextMoleProcessLease launchAdmission;
        try
        {
            launchAdmission = ContextMoleProcessCoordination.AcquireLease(_endpoint.DataDirectory,
                "broker-launch");
        }
        catch (ContextMoleException exception)
        {
            throw new BrokerRpcException(exception.Code, exception.Message, exception.Retryable, exception);
        }
        using (launchAdmission)
        {
            var deadline = _timeProvider.GetUtcNow().Add(_startupTimeout);
            await using var startupLock = await AcquireStartupLockAsync(deadline, cancellationToken)
                .ConfigureAwait(false);

            var availability = await ProbeBrokerAsync(cancellationToken).ConfigureAwait(false);
            if (availability == BrokerAvailability.Compatible) return;

            // Resolve and validate the staged payload before asking a usable older broker to exit. A bad or
            // incomplete deployment must leave the existing broker online for older adapters.
            var launchCommand = ResolveLaunchCommand();
            CancellationTokenSource? recoveryCancellation = null;
            try
            {
                var startupCancellation = cancellationToken;
                if (availability == BrokerAvailability.DeploymentMismatch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = deadline - _timeProvider.GetUtcNow();
                    if (remaining <= TimeSpan.Zero)
                        throw new BrokerRpcException("broker_unavailable",
                            "The incompatible shared Context Mole broker could not be replaced in time.", true);

                    // Once shutdown might be accepted, caller cancellation must not strand every adapter without
                    // a broker. Complete the bounded handoff, then honor cancellation before retrying user work.
                    recoveryCancellation = new CancellationTokenSource(remaining, _timeProvider);
                    startupCancellation = recoveryCancellation.Token;
                    var shutdownDispatched = await RequestMismatchedBrokerShutdownAsync(startupCancellation)
                        .ConfigureAwait(false);
                    if (!shutdownDispatched)
                    {
                        // The exact shutdown connection reached the same or a newer deployment. It won the race;
                        // never stop it and do not launch another process.
                        cancellationToken.ThrowIfCancellationRequested();
                        return;
                    }

                    await WaitForBrokerInstanceReleaseAsync(deadline, startupCancellation).ConfigureAwait(false);
                }

                _ = GetAuthenticationToken();
                _brokerStarter(launchCommand);

                Exception? lastFailure = null;
                while (_timeProvider.GetUtcNow() < deadline)
                {
                    startupCancellation.ThrowIfCancellationRequested();
                    try
                    {
                        availability = await ProbeBrokerAsync(startupCancellation).ConfigureAwait(false);
                        if (availability == BrokerAvailability.Compatible)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return;
                        }
                        if (availability == BrokerAvailability.DeploymentMismatch)
                            throw new BrokerRpcException(BrokerProtocol.DeploymentMismatchCode,
                                "The packaged broker executable does not match this Context Mole deployment.",
                                false);
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                                       IsTransportFailure(exception, startupCancellation))
                    {
                        lastFailure = exception;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, startupCancellation)
                        .ConfigureAwait(false);
                }

                throw new BrokerRpcException("broker_unavailable",
                    "The shared Context Mole broker did not become ready before the startup deadline.", true,
                    lastFailure);
            }
            catch (OperationCanceledException exception) when (
                recoveryCancellation?.IsCancellationRequested == true)
            {
                throw new BrokerRpcException("broker_unavailable",
                    "The shared Context Mole broker replacement did not complete before the startup deadline.",
                    true, exception);
            }
            finally
            {
                recoveryCancellation?.Dispose();
            }
        }
    }

    private async Task<BrokerAvailability> ProbeBrokerAsync(CancellationToken cancellationToken)
    {
        // The broker publishes this file only after its pipe accept loop is listening. Its absence is the
        // normal stopped/starting state and should not be discovered by repeatedly throwing pipe timeouts.
        if (!File.Exists(_endpoint.InstanceMetadataPath)) return BrokerAvailability.Unavailable;
        try
        {
            _ = await InvokeConnectedAsync(BrokerProtocol.HealthMethod,
                JsonSerializer.SerializeToElement(new { }, BrokerJson.Options), TimeSpan.FromSeconds(2),
                ProbeConnectTimeout, cancellationToken).ConfigureAwait(false);
            return BrokerAvailability.Compatible;
        }
        catch (BrokerRpcException exception) when (
            string.Equals(exception.Code, BrokerProtocol.DeploymentMismatchCode, StringComparison.Ordinal))
        {
            return BrokerAvailability.DeploymentMismatch;
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            return BrokerAvailability.Unavailable;
        }
    }

    private async Task<bool> RequestMismatchedBrokerShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await InvokeConnectedAsync(BrokerProtocol.ShutdownMethod,
                JsonSerializer.SerializeToElement(new { }, BrokerJson.Options), TimeSpan.FromSeconds(2),
                ProbeConnectTimeout, cancellationToken, BrokerDeploymentMode.ShutdownOlderOnly)
                .ConfigureAwait(false);
            return result.RequestDispatched;
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            // The broker may close its pipe before the shutdown acknowledgement is read. The instance-lock
            // handoff below is the authoritative indication that the old deployment has stopped.
            return true;
        }
        catch (BrokerRpcException exception) when (
            string.Equals(exception.Code, "deadline_exceeded", StringComparison.Ordinal))
        {
            // A shutdown can be accepted even when its acknowledgement misses the request deadline. Treat it as
            // destructive and complete recovery under the internal handoff token.
            return true;
        }
    }

    private async Task WaitForBrokerInstanceReleaseAsync(DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CanAcquireInstanceLock()) return;
            await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        throw new BrokerRpcException("broker_unavailable",
            "The incompatible shared Context Mole broker did not stop before restart.", true);
    }

    private bool CanAcquireInstanceLock()
    {
        try
        {
            using var probe = new FileStream(_endpoint.InstanceLockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<FileStream> AcquireStartupLockAsync(DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        _endpoint.EnsurePrivateBrokerDirectory();
        Exception? lastFailure = null;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(_endpoint.StartupLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 4096, FileOptions.Asynchronous);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastFailure = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        throw new BrokerRpcException("broker_unavailable", "Context Mole broker startup is busy.", true,
            lastFailure);
    }

    private BrokerLaunchCommand ResolveLaunchCommand()
    {
        BrokerLaunchCommand command;
        try
        {
            command = _launchCommand.Value ?? throw new InvalidOperationException(
                "The broker launch command factory returned no command.");
            ArgumentException.ThrowIfNullOrWhiteSpace(command.FileName);
            ArgumentNullException.ThrowIfNull(command.Arguments);
            if (Path.IsPathFullyQualified(command.FileName))
            {
                var fullPath = Path.GetFullPath(command.FileName);
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException("The Context Mole broker executable was not found.", fullPath);
                if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The Context Mole broker executable must not be a reparse point.");
                command = command with { FileName = fullPath };
            }
            return command;
        }
        catch (BrokerRpcException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or
                                          UnauthorizedAccessException)
        {
            throw new BrokerRpcException("broker_unavailable",
                "The staged Context Mole broker executable is unavailable or invalid.", true, exception);
        }
    }

    private void StartBrokerProcess(BrokerLaunchCommand launchCommand)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launchCommand.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = GetLaunchWorkingDirectory(launchCommand.FileName),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in launchCommand.Arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment[DataDirectoryEnvironmentVariable] = _endpoint.DataDirectory;
        try
        {
            var process = Process.Start(startInfo)
                          ?? throw new IOException("The broker process could not be started.");
            process.StandardInput.Close();
            _ = ObserveBrokerProcessAsync(process);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new BrokerRpcException("broker_unavailable", "The shared Context Mole broker could not start.",
                true, exception);
        }
    }

    private static string GetLaunchWorkingDirectory(string fileName)
    {
        if (Path.IsPathFullyQualified(fileName)) return Path.GetDirectoryName(fileName)!;
        return AppContext.BaseDirectory;
    }

    private static async Task ObserveBrokerProcessAsync(Process process)
    {
        try
        {
            var standardOutput = DrainAsync(process.StandardOutput, forwardToStandardError: false);
            var standardError = DrainAsync(process.StandardError, forwardToStandardError: true);
            await Task.WhenAll(standardOutput, standardError, process.WaitForExitAsync()).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task DrainAsync(StreamReader reader, bool forwardToStandardError)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (forwardToStandardError) Console.Error.WriteLine(line);
        }
    }

    private static bool IsTransportFailure(Exception exception, CancellationToken callerCancellation) =>
        !callerCancellation.IsCancellationRequested && exception is IOException or EndOfStreamException or TimeoutException
            or OperationCanceledException;

    private enum BrokerAvailability { Unavailable, Compatible, DeploymentMismatch }

    private enum BrokerDeploymentMode { RequireCompatible, ShutdownOlderOnly }

    private readonly record struct BrokerConnectedResult(JsonElement Result, bool RequestDispatched);

    private string GetAuthenticationToken()
    {
        try
        {
            lock (_authenticationTokenGate)
                return _authenticationToken ??= _endpoint.GetOrCreateAuthenticationToken();
        }
        catch (ContextMoleException exception)
        {
            throw new BrokerRpcException(exception.Code, exception.Message, exception.Retryable, exception);
        }
    }
}
