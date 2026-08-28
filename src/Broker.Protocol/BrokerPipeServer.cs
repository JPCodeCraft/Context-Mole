using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using ContextMole.Core;

namespace ContextMole.Broker.Protocol;

public sealed class BrokerPipeServer : IAsyncDisposable
{
    private static readonly IReadOnlyList<string> Capabilities =
    [
        "tools",
        "embeddings",
        "cancellation",
        "idle_lifecycle"
    ];

    private readonly BrokerEndpoint _endpoint;
    private readonly string _authenticationToken;
    private readonly string _brokerVersion;
    private readonly string _deploymentId;
    private readonly DateTimeOffset _startedUtc;
    private readonly Func<BrokerRpcRequest, CancellationToken, Task<JsonElement>> _handler;
    private readonly Action<Exception>? _unhandledException;
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly SemaphoreSlim _connectionSlots = new(16, 16);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private long _nextConnectionId;

    public BrokerPipeServer(
        BrokerEndpoint endpoint,
        string authenticationToken,
        string brokerVersion,
        string deploymentId,
        DateTimeOffset startedUtc,
        Func<BrokerRpcRequest, CancellationToken, Task<JsonElement>> handler,
        Action<Exception>? unhandledException = null)
    {
        _endpoint = endpoint;
        _authenticationToken = authenticationToken;
        _brokerVersion = brokerVersion;
        _deploymentId = deploymentId;
        _startedUtc = startedUtc;
        _handler = handler;
        _unhandledException = unhandledException;
    }

    public async Task RunAsync(CancellationToken cancellationToken, Action? listening = null)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _disposeCancellation.Token);
        var token = linkedCancellation.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _connectionSlots.WaitAsync(token).ConfigureAwait(false);
                var pipe = CreateServerPipe();
                try
                {
                    var connection = pipe.WaitForConnectionAsync(token);
                    listening?.Invoke();
                    listening = null;
                    await connection.ConfigureAwait(false);
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    _connectionSlots.Release();
                    throw;
                }

                var connectionId = Interlocked.Increment(ref _nextConnectionId);
                var task = HandleConnectionAsync(pipe, token);
                _connections[connectionId] = task;
                _ = ObserveConnectionAsync(connectionId, task);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            var pending = _connections.Values.ToArray();
            if (pending.Length > 0)
            {
                try
                {
                    await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is TimeoutException or IOException or OperationCanceledException)
                {
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeCancellation.CancelAsync().ConfigureAwait(false);
        _disposeCancellation.Dispose();
    }

    private NamedPipeServerStream CreateServerPipe() => new(
        _endpoint.PipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        64 * 1024,
        64 * 1024);

    private async Task ObserveConnectionAsync(long connectionId, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _unhandledException?.Invoke(exception);
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            _connectionSlots.Release();
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken serverCancellation)
    {
        await using (pipe.ConfigureAwait(false))
        {
            BrokerHandshakeRequest handshake;
            try
            {
                handshake = await BrokerFrameCodec.ReadAsync<BrokerHandshakeRequest>(pipe, serverCancellation)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or EndOfStreamException or BrokerRpcException)
            {
                return;
            }

            var handshakeResponse = ValidateHandshake(handshake);
            await BrokerFrameCodec.WriteAsync(pipe, handshakeResponse, serverCancellation).ConfigureAwait(false);
            if (!handshakeResponse.Accepted) return;

            BrokerRpcRequest request;
            try
            {
                request = await BrokerFrameCodec.ReadAsync<BrokerRpcRequest>(pipe, serverCancellation)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or EndOfStreamException or BrokerRpcException)
            {
                return;
            }

            if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Method))
            {
                await WriteErrorAsync(pipe, request.RequestId, "invalid_request",
                    "The broker request ID and method are required.", false, serverCancellation).ConfigureAwait(false);
                return;
            }

            var deploymentRelation = BrokerProtocol.CompareDeployments(handshake.ClientVersion,
                handshake.DeploymentId, _brokerVersion, _deploymentId);
            if ((deploymentRelation is BrokerDeploymentRelation.ClientNewer or BrokerDeploymentRelation.Conflict) &&
                !string.Equals(request.Method, BrokerProtocol.ShutdownMethod, StringComparison.Ordinal))
            {
                var conflict = deploymentRelation == BrokerDeploymentRelation.Conflict;
                await WriteErrorAsync(pipe, request.RequestId,
                    conflict ? BrokerProtocol.DeploymentConflictCode : BrokerProtocol.DeploymentMismatchCode,
                    conflict
                        ? "The client and shared broker have different builds of the same Context Mole version."
                        : "The client requires a newer Context Mole broker deployment.", !conflict,
                    serverCancellation).ConfigureAwait(false);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (request.DeadlineUtc <= now)
            {
                await WriteErrorAsync(pipe, request.RequestId, "deadline_exceeded",
                    "The broker request deadline elapsed before execution.", true, serverCancellation)
                    .ConfigureAwait(false);
                return;
            }

            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
            requestCancellation.CancelAfter(request.DeadlineUtc - now);
            using var disconnectMonitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
            var disconnectMonitor = MonitorDisconnectAsync(pipe, requestCancellation,
                disconnectMonitorCancellation.Token);
            BrokerRpcResponse response;
            try
            {
                var result = await _handler(request, requestCancellation.Token).ConfigureAwait(false);
                response = new BrokerRpcResponse(request.RequestId, result, null);
            }
            catch (ContextMoleException exception)
            {
                response = new BrokerRpcResponse(request.RequestId, null,
                    new BrokerRpcError(exception.Code, exception.Message, exception.Retryable));
            }
            catch (BrokerRpcException exception)
            {
                response = new BrokerRpcResponse(request.RequestId, null,
                    new BrokerRpcError(exception.Code, exception.Message, exception.Retryable));
            }
            catch (OperationCanceledException)
            {
                response = new BrokerRpcResponse(request.RequestId, null,
                    new BrokerRpcError(request.DeadlineUtc <= DateTimeOffset.UtcNow
                        ? "deadline_exceeded"
                        : "request_cancelled", "The broker request was cancelled.", true));
            }
            catch (Exception exception)
            {
                _unhandledException?.Invoke(exception);
                response = new BrokerRpcResponse(request.RequestId, null,
                    new BrokerRpcError("internal_error", "The local index request failed unexpectedly.", false));
            }
            finally
            {
                await disconnectMonitorCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await disconnectMonitor.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (!pipe.IsConnected) return;
            try
            {
                await BrokerFrameCodec.WriteAsync(pipe, response, serverCancellation).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or EndOfStreamException or OperationCanceledException)
            {
            }
        }
    }

    private BrokerHandshakeResponse ValidateHandshake(BrokerHandshakeRequest request)
    {
        if (request.ProtocolMajor != BrokerProtocol.MajorVersion)
            return Rejected("protocol_mismatch", "The client and broker protocol major versions differ.");
        if (!string.Equals(request.DataDirectoryId, _endpoint.DataDirectoryId, StringComparison.Ordinal))
            return Rejected("data_directory_mismatch", "The client and broker data directories differ.");
        if (!TokensEqual(request.AuthenticationToken, _authenticationToken))
            return Rejected("authentication_failed", "The broker authentication token was rejected.");
        return new BrokerHandshakeResponse(true, BrokerProtocol.MajorVersion, BrokerProtocol.MinorVersion,
            _brokerVersion, _deploymentId, Capabilities);
    }

    private BrokerHandshakeResponse Rejected(string code, string message) => new(false,
        BrokerProtocol.MajorVersion, BrokerProtocol.MinorVersion, _brokerVersion, _deploymentId, Capabilities,
        new BrokerRpcError(code, message, false));

    private static bool TokensEqual(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static async Task MonitorDisconnectAsync(NamedPipeServerStream pipe,
        CancellationTokenSource requestCancellation, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        try
        {
            var read = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) await requestCancellation.CancelAsync().ConfigureAwait(false);
            else await requestCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            await requestCancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private static Task WriteErrorAsync(Stream pipe, Guid requestId, string code, string message, bool retryable,
        CancellationToken cancellationToken) => BrokerFrameCodec.WriteAsync(pipe,
        new BrokerRpcResponse(requestId, null, new BrokerRpcError(code, message, retryable)), cancellationToken);
}
