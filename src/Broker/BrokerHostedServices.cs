using System.Reflection;
using System.Text.Json;

using ContextMole.Broker.Protocol;
using ContextMole.Core;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextMole.Broker;

public sealed class BrokerPipeHostedService(
    IAppPaths paths,
    BrokerRequestDispatcher dispatcher,
    IHostApplicationLifetime applicationLifetime,
    ILogger<BrokerPipeHostedService> logger) : BackgroundService
{
    private readonly IAppPaths _paths = paths;
    private readonly BrokerRequestDispatcher _dispatcher = dispatcher;
    private readonly IHostApplicationLifetime _applicationLifetime = applicationLifetime;
    private readonly ILogger<BrokerPipeHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoint = new BrokerEndpoint(_paths.DataDirectory);
        endpoint.EnsurePrivateBrokerDirectory();
        await using var instanceLock = TryAcquireInstanceLock(endpoint);
        if (instanceLock is null)
        {
            _logger.LogInformation("A compatible Context Mole broker is already running");
            _applicationLifetime.StopApplication();
            return;
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var assembly = typeof(BrokerProgram).Assembly;
        var version = assembly.GetName().Version?.ToString() ?? "0.0.0";
        var deploymentId = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                               .InformationalVersion ?? version;
        var authenticationToken = endpoint.GetOrCreateAuthenticationToken();
        try
        {
            await using var server = new BrokerPipeServer(endpoint, authenticationToken, version, deploymentId,
                startedUtc, _dispatcher.DispatchAsync,
                exception => _logger.LogError(exception, "Broker connection failed"));
            await server.RunAsync(stoppingToken, () =>
            {
                WriteInstanceMetadata(endpoint, new BrokerInstanceMetadata(Environment.ProcessId, startedUtc,
                    version, deploymentId, BrokerProtocol.MajorVersion, BrokerProtocol.MinorVersion));
                _logger.LogInformation("Context Mole broker {Version} listening for local requests", version);
            }).ConfigureAwait(false);
        }
        finally
        {
            DeleteOwnedInstanceMetadata(endpoint);
        }
    }

    private static FileStream? TryAcquireInstanceLock(BrokerEndpoint endpoint)
    {
        try
        {
            var stream = new FileStream(endpoint.InstanceLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(Environment.ProcessId);
            writer.Flush();
            stream.Flush(true);
            return stream;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void WriteInstanceMetadata(BrokerEndpoint endpoint, BrokerInstanceMetadata metadata)
    {
        var temporaryPath = endpoint.InstanceMetadataPath + $".{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(metadata, BrokerJson.Options));
            File.Move(temporaryPath, endpoint.InstanceMetadataPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void DeleteOwnedInstanceMetadata(BrokerEndpoint endpoint)
    {
        try
        {
            if (!File.Exists(endpoint.InstanceMetadataPath)) return;
            var metadata = JsonSerializer.Deserialize<BrokerInstanceMetadata>(
                File.ReadAllText(endpoint.InstanceMetadataPath), BrokerJson.Options);
            if (metadata?.ProcessId == Environment.ProcessId) File.Delete(endpoint.InstanceMetadataPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record BrokerInstanceMetadata(
        int ProcessId,
        DateTimeOffset StartedUtc,
        string Version,
        string DeploymentId,
        int ProtocolMajor,
        int ProtocolMinor);
}

public sealed class BrokerIdleHostedService(
    BrokerActivityTracker activity,
    BrokerSearchRuntimeManager searchRuntime,
    IHostApplicationLifetime applicationLifetime,
    TimeProvider timeProvider,
    ILogger<BrokerIdleHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan BrokerIdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly BrokerActivityTracker _activity = activity;
    private readonly BrokerSearchRuntimeManager _searchRuntime = searchRuntime;
    private readonly IHostApplicationLifetime _applicationLifetime = applicationLifetime;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<BrokerIdleHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (await _searchRuntime.ClearVectorCacheUnderPressureAsync(stoppingToken).ConfigureAwait(false))
                    _logger.LogInformation("Cleared the vector cache because system memory is under pressure");
                if (await _searchRuntime.UnloadIfIdleAsync(stoppingToken).ConfigureAwait(false))
                    _logger.LogInformation("Unloaded idle semantic model and vector cache");
                if (!_activity.IsIdle(BrokerIdleTimeout)) continue;
                _logger.LogInformation("Stopping the Context Mole broker after ten minutes idle");
                _applicationLifetime.StopApplication();
                return;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
