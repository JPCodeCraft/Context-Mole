using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

using ContextMole.Broker;
using ContextMole.Broker.Protocol;
using ContextMole.Core;
using ContextMole.Infrastructure;
using ContextMole.Mcp;
using ContextMole.Search;

using Microsoft.Extensions.Logging.Abstractions;

namespace ContextMole.Tests;

public sealed class BrokerProtocolTests
{
    [Fact]
    public async Task ConcurrentClientsCreateOneCompleteAuthenticationToken()
    {
        using var paths = new StorageTestPaths();
        using var processLease = ContextMoleProcessCoordination.AcquireLease(paths.DataDirectory, "broker-test");
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            return new BrokerEndpoint(paths.DataDirectory).GetOrCreateAuthenticationToken();
        }, TestContext.Current.CancellationToken)).ToArray();

        start.Set();
        var tokens = await Task.WhenAll(tasks);

        Assert.Single(tokens.Distinct(StringComparer.Ordinal));
        Assert.Equal(64, tokens[0].Length);
        Assert.Equal(tokens[0], File.ReadAllText(new BrokerEndpoint(paths.DataDirectory)
            .AuthenticationTokenPath).Trim());
    }

    [Fact]
    public void TokenCreationRefusesActiveShutdownWithoutCreatingBrokerDirectory()
    {
        using var paths = new StorageTestPaths();
        _ = ContextMoleProcessCoordination.RequestShutdown(paths.DataDirectory, TimeSpan.FromMinutes(1));
        var endpoint = new BrokerEndpoint(paths.DataDirectory);

        var exception = Assert.Throws<ContextMoleException>(() => endpoint.GetOrCreateAuthenticationToken());

        Assert.Equal("application_shutting_down", exception.Code);
        Assert.False(Directory.Exists(endpoint.BrokerDirectory));
    }

    [Fact]
    public async Task FrameCodecRejectsOversizedLengthBeforeAllocatingPayload()
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, BrokerProtocol.MaximumFrameBytes + 1);
        await using var stream = new MemoryStream(bytes);

        var exception = await Assert.ThrowsAsync<BrokerRpcException>(() =>
            BrokerFrameCodec.ReadAsync<JsonElement>(stream, TestContext.Current.CancellationToken));

        Assert.Equal("invalid_frame", exception.Code);
    }

    [Fact]
    public async Task AuthenticatedClientInvokesOneRequestOverPipe()
    {
        using var paths = new StorageTestPaths();
        var endpoint = new BrokerEndpoint(paths.DataDirectory);
        var token = endpoint.GetOrCreateAuthenticationToken();
        using var stop = new CancellationTokenSource();
        await using var server = new BrokerPipeServer(endpoint, token, "1.0", "tests",
            DateTimeOffset.UtcNow, (request, _) => Task.FromResult(request.Payload));
        var serverTask = server.RunAsync(stop.Token);
        var client = new BrokerRpcClient(paths.DataDirectory,
            new BrokerLaunchCommand("unused-in-running-server-test", []), deploymentId: "tests");

        try
        {
            var response = await client.InvokeAsync("echo", new { value = 42 },
                TestContext.Current.CancellationToken);
            Assert.Equal(42, response.GetProperty("value").GetInt32());
        }
        finally
        {
            await stop.CancelAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task ServerRejectsIncorrectAuthenticationToken()
    {
        using var paths = new StorageTestPaths();
        var endpoint = new BrokerEndpoint(paths.DataDirectory);
        var token = endpoint.GetOrCreateAuthenticationToken();
        using var stop = new CancellationTokenSource();
        await using var server = new BrokerPipeServer(endpoint, token, "1.0", "tests",
            DateTimeOffset.UtcNow, (_, _) => throw new InvalidOperationException("Must not dispatch"));
        var serverTask = server.RunAsync(stop.Token);

        try
        {
            await using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await BrokerFrameCodec.WriteAsync(pipe, new BrokerHandshakeRequest(BrokerProtocol.MajorVersion,
                BrokerProtocol.MinorVersion, new string('0', 64), endpoint.DataDirectoryId, "tests", "tests"),
                TestContext.Current.CancellationToken);
            var response = await BrokerFrameCodec.ReadAsync<BrokerHandshakeResponse>(pipe,
                TestContext.Current.CancellationToken);

            Assert.False(response.Accepted);
            Assert.Equal("authentication_failed", response.Error?.Code);
        }
        finally
        {
            await stop.CancelAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task ServerRejectsMismatchedApplicationRequestButAllowsAuthenticatedShutdown()
    {
        using var paths = new StorageTestPaths();
        var endpoint = new BrokerEndpoint(paths.DataDirectory);
        var token = endpoint.GetOrCreateAuthenticationToken();
        var dispatchCount = 0;
        using var stop = new CancellationTokenSource();
        await using var server = new BrokerPipeServer(endpoint, token, "2.0", "new-deployment",
            DateTimeOffset.UtcNow, (request, _) =>
            {
                Interlocked.Increment(ref dispatchCount);
                return Task.FromResult(request.Payload);
            });
        var serverTask = server.RunAsync(stop.Token);

        try
        {
            var rejected = await InvokeRawAsync(endpoint, token, "3.0", "old-deployment", "echo");
            Assert.Equal(BrokerProtocol.DeploymentMismatchCode, rejected.Error?.Code);
            Assert.True(rejected.Error?.Retryable);
            Assert.Equal(0, Volatile.Read(ref dispatchCount));

            var shutdown = await InvokeRawAsync(endpoint, token, "3.0", "old-deployment",
                BrokerProtocol.ShutdownMethod);
            Assert.Null(shutdown.Error);
            Assert.Equal(1, Volatile.Read(ref dispatchCount));
        }
        finally
        {
            await stop.CancelAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task OlderClientUsesNewerBrokerWithoutReplacement()
    {
        using var paths = new StorageTestPaths();
        var endpoint = new BrokerEndpoint(paths.DataDirectory);
        var token = endpoint.GetOrCreateAuthenticationToken();
        var launchAttempts = 0;
        using var stop = new CancellationTokenSource();
        await using var server = new BrokerPipeServer(endpoint, token, "2.0", "new-deployment",
            DateTimeOffset.UtcNow, (request, _) => Task.FromResult(request.Payload));
        var serverTask = server.RunAsync(stop.Token);
        var client = new BrokerRpcClient(paths.DataDirectory, () =>
        {
            Interlocked.Increment(ref launchAttempts);
            throw new InvalidOperationException("A newer compatible broker must not be replaced.");
        }, clientVersion: "1.0", deploymentId: "old-deployment");

        try
        {
            var response = await client.InvokeAsync("echo", new { value = 42 },
                TestContext.Current.CancellationToken);
            Assert.Equal(42, response.GetProperty("value").GetInt32());
            Assert.Equal(0, Volatile.Read(ref launchAttempts));
        }
        finally
        {
            await stop.CancelAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task EqualVersionDeploymentConflictIsRejectedWithoutDispatchOrReplacement()
    {
        using var paths = new StorageTestPaths();
        var endpoint = new BrokerEndpoint(paths.DataDirectory);
        var token = endpoint.GetOrCreateAuthenticationToken();
        var dispatchCount = 0;
        var launchAttempts = 0;
        using var stop = new CancellationTokenSource();
        await using var server = new BrokerPipeServer(endpoint, token, "2.0", "build-a",
            DateTimeOffset.UtcNow, (request, _) =>
            {
                Interlocked.Increment(ref dispatchCount);
                return Task.FromResult(request.Payload);
            });
        var serverTask = server.RunAsync(stop.Token);
        var client = new BrokerRpcClient(paths.DataDirectory, () =>
        {
            Interlocked.Increment(ref launchAttempts);
            throw new InvalidOperationException("An unordered deployment conflict must not launch a replacement.");
        }, clientVersion: "2.0", deploymentId: "build-b");

        try
        {
            var exception = await Assert.ThrowsAsync<BrokerRpcException>(() => client.InvokeAsync("echo", new { },
                TestContext.Current.CancellationToken));
            Assert.Equal(BrokerProtocol.DeploymentConflictCode, exception.Code);
            Assert.False(exception.Retryable);
            Assert.Equal(0, Volatile.Read(ref dispatchCount));
            Assert.Equal(0, Volatile.Read(ref launchAttempts));
        }
        finally
        {
            await stop.CancelAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task NewerClientStopsOlderBrokerAndWaitsForInstanceReleaseBeforeReplacement()
    {
        using var paths = new StorageTestPaths();
        var endpoint = new BrokerEndpoint(paths.DataDirectory);
        var token = endpoint.GetOrCreateAuthenticationToken();
        endpoint.EnsurePrivateBrokerDirectory();
        var instanceLock = new FileStream(endpoint.InstanceLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.Asynchronous);
        await File.WriteAllTextAsync(endpoint.InstanceMetadataPath, "{}", TestContext.Current.CancellationToken);
        var shutdownReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applicationDispatches = 0;
        var launchAttempts = 0;
        Task shutdownCleanup = Task.CompletedTask;
        using var stop = new CancellationTokenSource();
        Task serverTask = Task.CompletedTask;
        await using var server = new BrokerPipeServer(endpoint, token, "1.0", "old-deployment",
            DateTimeOffset.UtcNow, (request, _) =>
            {
                if (!string.Equals(request.Method, BrokerProtocol.ShutdownMethod, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref applicationDispatches);
                    return Task.FromResult(request.Payload);
                }

                shutdownReceived.TrySetResult();
                shutdownCleanup = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    await stop.CancelAsync();
                    await serverTask;
                    File.Delete(endpoint.InstanceMetadataPath);
                    instanceLock.Dispose();
                });
                return Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
            });
        serverTask = server.RunAsync(stop.Token);
        var client = new BrokerRpcClient(paths.DataDirectory,
            () => new BrokerLaunchCommand("test-replacement", []),
            timeProvider: null, startupTimeout: TimeSpan.FromSeconds(5), clientVersion: "2.0",
            deploymentId: "new-deployment", brokerStarter: _ =>
            {
                using var releasedLock = new FileStream(endpoint.InstanceLockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                Interlocked.Increment(ref launchAttempts);
                throw new BrokerRpcException("launch_blocked",
                    "The replacement launch was observed after the instance lock was released.", false);
            });

        try
        {
            var exception = await Assert.ThrowsAsync<BrokerRpcException>(() => client.InvokeAsync("echo", new { },
                TestContext.Current.CancellationToken));
            Assert.Equal("launch_blocked", exception.Code);
            await shutdownReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await shutdownCleanup.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.Equal(0, Volatile.Read(ref applicationDispatches));
            Assert.Equal(1, Volatile.Read(ref launchAttempts));
        }
        finally
        {
            instanceLock.Dispose();
            await stop.CancelAsync();
            await serverTask;
            await shutdownCleanup;
        }
    }

    [Fact]
    public async Task InvalidReplacementPayloadLeavesOlderBrokerRunning()
    {
        using var paths = new StorageTestPaths();
        var endpoint = new BrokerEndpoint(paths.DataDirectory);
        var token = endpoint.GetOrCreateAuthenticationToken();
        endpoint.EnsurePrivateBrokerDirectory();
        await using var instanceLock = new FileStream(endpoint.InstanceLockPath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
        await File.WriteAllTextAsync(endpoint.InstanceMetadataPath, "{}", TestContext.Current.CancellationToken);
        var shutdownDispatches = 0;
        var applicationDispatches = 0;
        var resolutionAttempts = 0;
        using var stop = new CancellationTokenSource();
        await using var server = new BrokerPipeServer(endpoint, token, "1.0", "old-deployment",
            DateTimeOffset.UtcNow, (request, _) =>
            {
                if (string.Equals(request.Method, BrokerProtocol.ShutdownMethod, StringComparison.Ordinal))
                    Interlocked.Increment(ref shutdownDispatches);
                else
                    Interlocked.Increment(ref applicationDispatches);
                return Task.FromResult(request.Payload);
            });
        var serverTask = server.RunAsync(stop.Token);
        var client = new BrokerRpcClient(paths.DataDirectory, () =>
        {
            Interlocked.Increment(ref resolutionAttempts);
            throw new BrokerRpcException("payload_missing", "The staged broker payload is incomplete.", false);
        }, clientVersion: "2.0", deploymentId: "new-deployment");

        try
        {
            var exception = await Assert.ThrowsAsync<BrokerRpcException>(() => client.InvokeAsync("echo", new { },
                TestContext.Current.CancellationToken));
            Assert.Equal("payload_missing", exception.Code);
            Assert.Equal(1, Volatile.Read(ref resolutionAttempts));
            Assert.Equal(0, Volatile.Read(ref shutdownDispatches));

            var response = await InvokeRawAsync(endpoint, token, "1.0", "old-deployment", "echo");
            Assert.Null(response.Error);
            Assert.Equal(1, Volatile.Read(ref applicationDispatches));
        }
        finally
        {
            await stop.CancelAsync();
            await serverTask;
            File.Delete(endpoint.InstanceMetadataPath);
        }
    }

    [Fact]
    public async Task ShutdownConnectionDoesNotStopRacedInCurrentBroker()
    {
        using var paths = new StorageTestPaths();
        var endpoint = new BrokerEndpoint(paths.DataDirectory);
        var token = endpoint.GetOrCreateAuthenticationToken();
        endpoint.EnsurePrivateBrokerDirectory();
        await File.WriteAllTextAsync(endpoint.InstanceMetadataPath, "{}", TestContext.Current.CancellationToken);
        var receivedMethods = new List<string>();
        using var stop = new CancellationTokenSource();
        var serverTask = RunDeploymentSequenceAsync(endpoint, token,
            [("1.0", "old-deployment"), ("1.0", "old-deployment"),
             ("2.0", "new-deployment"), ("2.0", "new-deployment")], receivedMethods, stop.Token);
        var launchAttempts = 0;
        var client = new BrokerRpcClient(paths.DataDirectory,
            () => new BrokerLaunchCommand("test-replacement", []),
            timeProvider: null, startupTimeout: TimeSpan.FromSeconds(5), clientVersion: "2.0",
            deploymentId: "new-deployment", brokerStarter: _ => Interlocked.Increment(ref launchAttempts));

        try
        {
            var response = await client.InvokeAsync("echo", new { value = 42 },
                TestContext.Current.CancellationToken);
            Assert.Equal(42, response.GetProperty("value").GetInt32());
            await serverTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.Equal(["echo"], receivedMethods);
            Assert.Equal(0, Volatile.Read(ref launchAttempts));
        }
        finally
        {
            await stop.CancelAsync();
            await serverTask;
            File.Delete(endpoint.InstanceMetadataPath);
        }
    }

    [Fact]
    public async Task CancellationAfterShutdownStillStartsReplacementBroker()
    {
        using var paths = new StorageTestPaths();
        var endpoint = new BrokerEndpoint(paths.DataDirectory);
        var token = endpoint.GetOrCreateAuthenticationToken();
        endpoint.EnsurePrivateBrokerDirectory();
        var oldInstanceLock = new FileStream(endpoint.InstanceLockPath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
        await File.WriteAllTextAsync(endpoint.InstanceMetadataPath, "{}", TestContext.Current.CancellationToken);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using var oldStop = new CancellationTokenSource();
        Task oldServerTask = Task.CompletedTask;
        Task oldShutdownCleanup = Task.CompletedTask;
        await using var oldServer = new BrokerPipeServer(endpoint, token, "1.0", "old-deployment",
            DateTimeOffset.UtcNow, (request, _) =>
            {
                if (!string.Equals(request.Method, BrokerProtocol.ShutdownMethod, StringComparison.Ordinal))
                    throw new InvalidOperationException("The original application request must not reach the old broker.");
                oldShutdownCleanup = Task.Run(async () =>
                {
                    await requestCancellation.CancelAsync();
                    await oldStop.CancelAsync();
                    await oldServerTask;
                    File.Delete(endpoint.InstanceMetadataPath);
                    oldInstanceLock.Dispose();
                });
                return Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
            });
        oldServerTask = oldServer.RunAsync(oldStop.Token);

        FileStream? replacementInstanceLock = null;
        BrokerPipeServer? replacementServer = null;
        using var replacementStop = new CancellationTokenSource();
        Task replacementServerTask = Task.CompletedTask;
        var replacementLaunches = 0;
        var replacementApplicationDispatches = 0;
        var client = new BrokerRpcClient(paths.DataDirectory,
            () => new BrokerLaunchCommand("test-replacement", []),
            timeProvider: null, startupTimeout: TimeSpan.FromSeconds(5), clientVersion: "2.0",
            deploymentId: "new-deployment", brokerStarter: _ =>
            {
                replacementInstanceLock = new FileStream(endpoint.InstanceLockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
                Interlocked.Increment(ref replacementLaunches);
                replacementServer = new BrokerPipeServer(endpoint, token, "2.0", "new-deployment",
                    DateTimeOffset.UtcNow, (request, _) =>
                    {
                        if (!string.Equals(request.Method, BrokerProtocol.HealthMethod, StringComparison.Ordinal))
                            Interlocked.Increment(ref replacementApplicationDispatches);
                        return Task.FromResult(request.Payload);
                    });
                replacementServerTask = replacementServer.RunAsync(replacementStop.Token,
                    () => File.WriteAllText(endpoint.InstanceMetadataPath, "{}"));
            });

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.InvokeAsync("echo", new { },
                requestCancellation.Token));
            await oldShutdownCleanup.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref replacementLaunches));
            Assert.Equal(0, Volatile.Read(ref replacementApplicationDispatches));

            var response = await InvokeRawAsync(endpoint, token, "2.0", "new-deployment", "echo");
            Assert.Null(response.Error);
            Assert.Equal(1, Volatile.Read(ref replacementApplicationDispatches));
        }
        finally
        {
            await oldStop.CancelAsync();
            await oldServerTask;
            await oldShutdownCleanup;
            await replacementStop.CancelAsync();
            await replacementServerTask;
            if (replacementServer is not null) await replacementServer.DisposeAsync();
            replacementInstanceLock?.Dispose();
            oldInstanceLock.Dispose();
            if (File.Exists(endpoint.InstanceMetadataPath)) File.Delete(endpoint.InstanceMetadataPath);
        }
    }

    [Fact]
    public async Task DisconnectForwardsCancellationToBrokerHandler()
    {
        using var paths = new StorageTestPaths();
        var endpoint = new BrokerEndpoint(paths.DataDirectory);
        var token = endpoint.GetOrCreateAuthenticationToken();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        await using var server = new BrokerPipeServer(endpoint, token, "1.0", "tests", DateTimeOffset.UtcNow,
            async (_, cancellationToken) =>
            {
                handlerStarted.TrySetResult();
                using var registration = cancellationToken.Register(() => handlerCancelled.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonSerializer.SerializeToElement(new { });
            });
        var serverTask = server.RunAsync(stop.Token);
        var client = new BrokerRpcClient(paths.DataDirectory,
            new BrokerLaunchCommand("unused-in-running-server-test", []), deploymentId: "tests");
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        try
        {
            var request = client.InvokeAsync("wait", new { }, requestCancellation.Token);
            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await requestCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            await handlerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally
        {
            await stop.CancelAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task DeadlineDoesNotRetryOrRestartRequest()
    {
        using var paths = new StorageTestPaths();
        var endpoint = new BrokerEndpoint(paths.DataDirectory);
        var token = endpoint.GetOrCreateAuthenticationToken();
        var dispatchCount = 0;
        using var stop = new CancellationTokenSource();
        await using var server = new BrokerPipeServer(endpoint, token, "1.0", "tests", DateTimeOffset.UtcNow,
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref dispatchCount);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonSerializer.SerializeToElement(new { });
            });
        var serverTask = server.RunAsync(stop.Token);
        var launchAttempts = 0;
        var client = new BrokerRpcClient(paths.DataDirectory,
            () => new BrokerLaunchCommand("must-not-launch-after-deadline", []),
            timeProvider: null, startupTimeout: null, clientVersion: null, deploymentId: "tests",
            brokerStarter: _ => Interlocked.Increment(ref launchAttempts));

        try
        {
            var exception = await Assert.ThrowsAsync<BrokerRpcException>(() => client.InvokeAsync("wait", new { },
                TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(100)));
            Assert.Equal("deadline_exceeded", exception.Code);
            Assert.InRange(Volatile.Read(ref dispatchCount), 0, 1);
            Assert.Equal(0, Volatile.Read(ref launchAttempts));
        }
        finally
        {
            await stop.CancelAsync();
            await serverTask;
        }
    }

    [Fact]
    public void ActivityTrackerRequiresNoActiveRequestsAndFullIdleDuration()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));
        var tracker = new BrokerActivityTracker(time);
        using (tracker.BeginRequest())
        {
            time.Advance(TimeSpan.FromMinutes(20));
            Assert.False(tracker.IsIdle(TimeSpan.FromMinutes(10)));
        }

        Assert.False(tracker.IsIdle(TimeSpan.FromMinutes(10)));
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(tracker.IsIdle(TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public async Task IdleUnloadFinishesRuntimeRetirementBeforeCreatingReplacement()
    {
        using var paths = new StorageTestPaths();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));
        var cpuSettings = new SignalingCpuUsageSettings();
        using var cpuBudget = new GlobalCpuBudget(cpuSettings);
        var retiring = new TestBrokerSearchRuntime(blockDisposal: true);
        var replacement = new TestBrokerSearchRuntime(blockDisposal: false);
        var creationCount = 0;
        await using var manager = new BrokerSearchRuntimeManager(paths, cpuSettings,
            new FixedEmbeddingModelSettings(), cpuBudget, null!, null!, time, () =>
            {
                return Interlocked.Increment(ref creationCount) switch
                {
                    1 => retiring,
                    2 => replacement,
                    _ => throw new InvalidOperationException("The runtime was recreated more than once.")
                };
            });

        Assert.True(await manager.CountTokensAsync("first request", TestContext.Current.CancellationToken) > 0);
        time.Advance(BrokerSearchRuntimeManager.SemanticIdleTimeout);
        var unload = manager.UnloadIfIdleAsync(TestContext.Current.CancellationToken);
        await retiring.DisposalStarted.WaitAsync(TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);

        var replacementRequest = manager.CountTokensAsync("request during retirement",
            TestContext.Current.CancellationToken);
        try
        {
            await cpuSettings.SecondRefreshStarted.WaitAsync(TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref creationCount));
            Assert.False(replacementRequest.IsCompleted);
        }
        finally
        {
            retiring.AllowDisposal();
        }

        Assert.True(await unload);
        Assert.True(await replacementRequest > 0);
        Assert.Equal(2, Volatile.Read(ref creationCount));
    }

    [Fact]
    public async Task MemoryPressureStillClearsTheDisposableVectorCache()
    {
        using var paths = new StorageTestPaths();
        var cpuSettings = new SignalingCpuUsageSettings();
        using var cpuBudget = new GlobalCpuBudget(cpuSettings);
        var runtime = new TestBrokerSearchRuntime(blockDisposal: false);
        runtime.SeedCache();
        await using var manager = new BrokerSearchRuntimeManager(paths, cpuSettings,
            new FixedEmbeddingModelSettings(), cpuBudget, null!,
            new FixedMemorySnapshots(new SystemMemorySnapshot(
                16L * 1024 * 1024 * 1024, 1L * 1024 * 1024 * 1024, 512L * 1024 * 1024)),
            TimeProvider.System, () => runtime);

        Assert.True(await manager.CountTokensAsync("load runtime", TestContext.Current.CancellationToken) > 0);
        Assert.True(runtime.Cache.CurrentBytes > 0);

        Assert.True(await manager.ClearVectorCacheUnderPressureAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(0, runtime.Cache.CurrentBytes);
        Assert.Equal(0, runtime.Cache.Count);
    }

    [Fact]
    public async Task LocalEmbeddingMetadataReloadDoesNotResolveOrWakeBroker()
    {
        using var paths = new StorageTestPaths();
        var launchResolutions = 0;
        var client = new BrokerRpcClient(paths.DataDirectory, () =>
        {
            Interlocked.Increment(ref launchResolutions);
            throw new InvalidOperationException("A metadata refresh must not resolve the broker executable.");
        });
        await using var embeddings = new BrokerEmbeddingGenerator(client, paths,
            new FixedEmbeddingModelSettings());

        await embeddings.ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, Volatile.Read(ref launchResolutions));
        Assert.True(embeddings.CountTokens("metadata only") > 0);
        Assert.Equal(0, Volatile.Read(ref launchResolutions));
        Assert.False(embeddings.IsAvailable);
        Assert.NotNull(embeddings.Policy);
    }

    [Fact]
    public void DevelopmentUiOutputResolvesRepositoryBrokerBuild()
    {
        using var paths = new StorageTestPaths();
        var repository = Path.Combine(paths.RootDirectory, "repository");
        var appOutput = Path.Combine(repository, "src", "App.UI", "bin", "Debug", "net10.0");
        var brokerOutput = Path.Combine(repository, "src", "Broker", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(appOutput);
        Directory.CreateDirectory(brokerOutput);
        File.WriteAllText(Path.Combine(repository, "ContextMole.slnx"), "<Solution />");
        var executable = Path.Combine(brokerOutput,
            OperatingSystem.IsWindows() ? "ContextMole.Broker.exe" : "ContextMole.Broker");
        File.WriteAllText(executable, "test broker apphost");
        var incompatibleRid = OperatingSystem.IsWindows() ? "linux-x64" : "win-x64";
        var incompatibleOutput = Path.Combine(brokerOutput, incompatibleRid);
        Directory.CreateDirectory(incompatibleOutput);
        var incompatible = Path.Combine(incompatibleOutput,
            OperatingSystem.IsWindows() ? "ContextMole.Broker.exe" : "ContextMole.Broker");
        File.WriteAllText(incompatible, "newer incompatible broker apphost");
        File.SetLastWriteTimeUtc(incompatible, DateTime.UtcNow.AddMinutes(1));
        var mcpOutput = Path.Combine(repository, "src", "Mcp", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(mcpOutput);
        var mcp = Path.Combine(mcpOutput,
            OperatingSystem.IsWindows() ? "ContextMole.Mcp.exe" : "ContextMole.Mcp");
        File.WriteAllText(mcp, "newer MCP apphost that cannot host the broker");
        File.SetLastWriteTimeUtc(mcp, DateTime.UtcNow.AddMinutes(2));

        var command = BrokerLaunchCommand.ResolveFromDirectory(appOutput);

        Assert.Equal(Path.GetFullPath(executable), command.FileName);
        Assert.Empty(command.Arguments);
    }

    [Fact]
    public void RepositoryBrokerBuildIsStagedAsImmutableContentSnapshot()
    {
        using var paths = new StorageTestPaths();
        var repository = Path.Combine(paths.RootDirectory, "repository");
        var brokerOutput = Path.Combine(repository, "src", "Broker", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(brokerOutput);
        File.WriteAllText(Path.Combine(repository, "ContextMole.slnx"), "<Solution />");
        var executableName = OperatingSystem.IsWindows() ? "ContextMole.Broker.exe" : "ContextMole.Broker";
        var executable = Path.Combine(brokerOutput, executableName);
        var dependency = Path.Combine(brokerOutput, "ContextMole.Core.dll");
        File.WriteAllText(executable, "test broker apphost");
        File.WriteAllText(dependency, "dependency revision one");
        File.WriteAllText(Path.Combine(brokerOutput, "ContextMole.Core.pdb"), "debug symbols");
        var incompatibleRid = OperatingSystem.IsWindows() ? "linux-x64" : "win-x64";
        var incompatibleNativeDirectory = Path.Combine(brokerOutput, "runtimes", incompatibleRid, "native");
        Directory.CreateDirectory(incompatibleNativeDirectory);
        File.WriteAllText(Path.Combine(incompatibleNativeDirectory, "foreign-native-library"), "incompatible");
        var endpoint = new BrokerEndpoint(paths.DataDirectory);
        var sourceCommand = new BrokerLaunchCommand(executable, []);

        var first = BrokerDevelopmentDeployment.StageIfRepositoryBuild(endpoint, sourceCommand);

        Assert.NotEqual(Path.GetFullPath(executable), first.FileName);
        AssertPathIsWithin(endpoint.BrokerDirectory, first.FileName);
        var firstDirectory = Path.GetDirectoryName(first.FileName)!;
        Assert.Equal("test broker apphost", File.ReadAllText(first.FileName));
        Assert.Equal("dependency revision one", File.ReadAllText(Path.Combine(firstDirectory,
            "ContextMole.Core.dll")));
        Assert.False(File.Exists(Path.Combine(firstDirectory, "ContextMole.Core.pdb")));
        Assert.False(Directory.Exists(Path.Combine(firstDirectory, "runtimes", incompatibleRid)));

        File.WriteAllText(dependency, "dependency revision two");
        var second = BrokerDevelopmentDeployment.StageIfRepositoryBuild(endpoint, sourceCommand);
        var secondAgain = BrokerDevelopmentDeployment.StageIfRepositoryBuild(endpoint, sourceCommand);

        Assert.NotEqual(first.FileName, second.FileName);
        Assert.Equal(second.FileName, secondAgain.FileName);
        Assert.Equal("dependency revision one", File.ReadAllText(Path.Combine(firstDirectory,
            "ContextMole.Core.dll")));
        Assert.Equal("dependency revision two", File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(second.FileName)!, "ContextMole.Core.dll")));
    }

    [Fact]
    public void DevelopmentRidOutputNeverSelectsACompetingBrokerRid()
    {
        using var paths = new StorageTestPaths();
        var repository = Path.Combine(paths.RootDirectory, "repository");
        var currentRid = RuntimeInformation.RuntimeIdentifier;
        var competingRid = OperatingSystem.IsWindows() ? "linux-x64" : "win-x64";
        var appOutput = Path.Combine(repository, "src", "App.UI", "bin", "Release", "net10.0",
            currentRid, "publish");
        var brokerBin = Path.Combine(repository, "src", "Broker", "bin", "Release", "net10.0");
        Directory.CreateDirectory(appOutput);
        Directory.CreateDirectory(Path.Combine(brokerBin, currentRid));
        Directory.CreateDirectory(Path.Combine(brokerBin, competingRid));
        File.WriteAllText(Path.Combine(repository, "ContextMole.slnx"), "<Solution />");
        var executableName = OperatingSystem.IsWindows() ? "ContextMole.Broker.exe" : "ContextMole.Broker";
        var expected = Path.Combine(brokerBin, currentRid, executableName);
        var incompatible = Path.Combine(brokerBin, competingRid, executableName);
        File.WriteAllText(expected, "current RID broker");
        File.WriteAllText(incompatible, "competing RID broker");
        File.SetLastWriteTimeUtc(incompatible, DateTime.UtcNow.AddMinutes(2));

        var command = BrokerLaunchCommand.ResolveFromDirectory(appOutput);

        Assert.Equal(Path.GetFullPath(expected), command.FileName);
        Assert.Empty(command.Arguments);
    }

    [Fact]
    public async Task ConcurrentClientsLaunchExactlyOneBrokerProcess()
    {
        var paths = new StorageTestPaths();
        var brokerProcessIds = new HashSet<int>();
        try
        {
            using var processLease = ContextMoleProcessCoordination.AcquireLease(paths.DataDirectory, "broker-test");
            var sourceCommand = ResolveRepositoryBrokerLaunchCommand();
            Assert.Contains($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}Broker" +
                            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                sourceCommand.FileName, StringComparison.OrdinalIgnoreCase);
            var clients = Enumerable.Range(0, 2)
                .Select(_ => new BrokerRpcClient(paths.DataDirectory, sourceCommand))
                .ToArray();
            var launched = await clients[0].GetHealthAsync(TestContext.Current.CancellationToken);
            var health = await Task.WhenAll(clients.Select(client =>
                client.GetHealthAsync(TestContext.Current.CancellationToken)));
            brokerProcessIds.UnionWith(health.Select(item => item.ProcessId));
            var brokerProcessId = Assert.Single(brokerProcessIds);
            Assert.Equal(launched.ProcessId, brokerProcessId);
            Assert.NotEqual(Environment.ProcessId, brokerProcessId);
            Assert.All(health, item => Assert.Equal(launched.StartedUtc, item.StartedUtc));
            var endpoint = new BrokerEndpoint(paths.DataDirectory);
            var executableName = Path.GetFileName(sourceCommand.FileName);
            var stagedExecutable = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(endpoint.BrokerDirectory, "deployments"), executableName,
                SearchOption.AllDirectories));
            Assert.NotEqual(Path.GetFullPath(sourceCommand.FileName), Path.GetFullPath(stagedExecutable));
            if (OperatingSystem.IsWindows())
            {
                var mutableDependency = Path.Combine(Path.GetDirectoryName(sourceCommand.FileName)!,
                    "ContextMole.Core.dll");
                Assert.True(File.Exists(mutableDependency));
                using var exclusive = new FileStream(mutableDependency, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None);
            }
            var tools = new BrokerMcpTools(clients[0], NullLogger<BrokerMcpTools>.Instance);
            var unavailable = Assert.IsType<ErrorEnvelope>(await tools.ListProjects(
                TestContext.Current.CancellationToken));
            Assert.Equal("not_initialized", unavailable.Error.Code);

            _ = await clients[0].InvokeAsync(BrokerProtocol.ShutdownMethod, new { },
                TestContext.Current.CancellationToken, TimeSpan.FromSeconds(5));
            await WaitForProcessExitAsync(brokerProcessId, TestContext.Current.CancellationToken);
        }
        finally
        {
            TryAddPublishedBrokerProcessId(paths.DataDirectory, brokerProcessIds);
            foreach (var processId in brokerProcessIds)
                await StopOwnedTestProcessAsync(processId);
            paths.Dispose();
        }
    }

    private static void AssertPathIsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        Assert.False(Path.IsPathRooted(relative));
        Assert.NotEqual("..", relative);
        Assert.False(relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        Assert.False(relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static BrokerLaunchCommand ResolveRepositoryBrokerLaunchCommand()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "ContextMole.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);

        DirectoryInfo? testOutput = new(AppContext.BaseDirectory);
        while (testOutput is not null && !testOutput.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
            testOutput = testOutput.Parent;
        Assert.NotNull(testOutput);
        var buildCoordinates = Path.GetRelativePath(testOutput.FullName, AppContext.BaseDirectory);
        var brokerOutput = Path.Combine(repository.FullName, "src", "Broker", "bin", buildCoordinates);
        return BrokerLaunchCommand.ResolveFromDirectory(brokerOutput);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }

    private static async Task RunDeploymentSequenceAsync(BrokerEndpoint endpoint, string token,
        IReadOnlyList<(string Version, string DeploymentId)> identities, IList<string> receivedMethods,
        CancellationToken cancellationToken)
    {
        foreach (var identity in identities)
        {
            await using var pipe = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken);
            var handshake = await BrokerFrameCodec.ReadAsync<BrokerHandshakeRequest>(pipe, cancellationToken);
            Assert.Equal(token, handshake.AuthenticationToken);
            await BrokerFrameCodec.WriteAsync(pipe, new BrokerHandshakeResponse(true,
                BrokerProtocol.MajorVersion, BrokerProtocol.MinorVersion, identity.Version,
                identity.DeploymentId, []), cancellationToken);

            BrokerRpcRequest request;
            try
            {
                request = await BrokerFrameCodec.ReadAsync<BrokerRpcRequest>(pipe, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or EndOfStreamException)
            {
                continue;
            }

            receivedMethods.Add(request.Method);
            await BrokerFrameCodec.WriteAsync(pipe,
                new BrokerRpcResponse(request.RequestId, request.Payload, null), cancellationToken);
        }
    }

    private static async Task<BrokerRpcResponse> InvokeRawAsync(BrokerEndpoint endpoint, string token,
        string clientVersion, string deploymentId, string method)
    {
        await using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await BrokerFrameCodec.WriteAsync(pipe, new BrokerHandshakeRequest(BrokerProtocol.MajorVersion,
            BrokerProtocol.MinorVersion, token, endpoint.DataDirectoryId, clientVersion, deploymentId),
            TestContext.Current.CancellationToken);
        var handshake = await BrokerFrameCodec.ReadAsync<BrokerHandshakeResponse>(pipe,
            TestContext.Current.CancellationToken);
        Assert.True(handshake.Accepted);
        var request = new BrokerRpcRequest(Guid.CreateVersion7(), method,
            JsonSerializer.SerializeToElement(new { value = 42 }), DateTimeOffset.UtcNow.AddSeconds(5));
        await BrokerFrameCodec.WriteAsync(pipe, request, TestContext.Current.CancellationToken);
        return await BrokerFrameCodec.ReadAsync<BrokerRpcResponse>(pipe,
            TestContext.Current.CancellationToken);
    }

    private sealed class SignalingCpuUsageSettings : ICpuUsageSettings
    {
        private readonly TaskCompletionSource<bool> _secondRefreshStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _refreshCount;

        public CpuUsageProfile Profile => CpuUsageProfile.Normal;
        public int LogicalProcessorCount => 1;
        public int ThreadLimit => 1;
        public int MaximumThreadLimit => 1;
        public Task SecondRefreshStarted => _secondRefreshStarted.Task;
        public event EventHandler? Changed { add { } remove { } }
        public void SetProfile(CpuUsageProfile profile) => throw new NotSupportedException();

        public bool RefreshFromDisk()
        {
            if (Interlocked.Increment(ref _refreshCount) == 2)
                _secondRefreshStarted.TrySetResult(true);
            return false;
        }
    }

    private sealed class TestBrokerSearchRuntime(bool blockDisposal) : IBrokerSearchRuntime
    {
        private readonly TaskCompletionSource<bool> _disposalStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _disposalAllowed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public HybridSearchService Search => throw new NotSupportedException();
        public IEmbeddingGenerator Embeddings { get; } = new StorageUnavailableEmbeddings();
        public VectorIndexCache Cache { get; } = new(1024);
        public Task DisposalStarted => _disposalStarted.Task;

        public void SeedCache()
        {
            var policy = new EmbeddingPolicy("test", "1", "model", "tokenizer", "fp32",
                384, 384, "cls", "l2");
            var entry = new VectorEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "cached.txt", ".txt",
                DateTimeOffset.UnixEpoch, false, new float[16]);
            Cache.GetOrCreate(Guid.NewGuid(), new VectorSnapshot(1, policy, [entry]),
                new FlatVectorIndexFactory());
        }

        public void AllowDisposal() => _disposalAllowed.TrySetResult(true);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _disposalStarted.TrySetResult(true);
            if (blockDisposal) await _disposalAllowed.Task.ConfigureAwait(false);
            await Embeddings.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class FixedEmbeddingModelSettings : IEmbeddingModelSettings
    {
        public EmbeddingModelChoice Model => EmbeddingModelChoice.Granite97M;
        public event EventHandler? Changed { add { } remove { } }
        public void SetModel(EmbeddingModelChoice model) => throw new NotSupportedException();
        public bool RefreshFromDisk() => false;
    }

    private sealed class FixedMemorySnapshots(SystemMemorySnapshot snapshot)
        : ISystemMemorySnapshotProvider
    {
        public SystemMemorySnapshot Capture() => snapshot;
    }

    private static async Task WaitForProcessExitAsync(int processId, CancellationToken cancellationToken)
    {
        using var process = System.Diagnostics.Process.GetProcessById(processId);
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    private static async Task StopOwnedTestProcessAsync(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
        }
    }

    private static void TryAddPublishedBrokerProcessId(string dataDirectory, ISet<int> processIds)
    {
        try
        {
            var metadataPath = new BrokerEndpoint(dataDirectory).InstanceMetadataPath;
            if (!File.Exists(metadataPath)) return;
            using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
            if (metadata.RootElement.TryGetProperty("process_id", out var value) && value.TryGetInt32(out var processId))
                processIds.Add(processId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }
}
