using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;

using ContextMole.Broker.Protocol;
using ContextMole.Core;
using ContextMole.Mcp;
using ContextMole.Storage;

using Microsoft.Data.Sqlite;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class WindowsMcpDesktopTests
{
    [Theory]
    [InlineData(@"C:\Users\test\AppData\Local\ContextMole", @"C:\Users\test\AppData\Local\Packages\Client_123\LocalCache\Local\ContextMole", true)]
    [InlineData(@"C:\Users\test\AppData\Roaming\Custom", @"C:\Users\test\AppData\Local\Packages\Client_123\LocalCache\Roaming\Custom", true)]
    [InlineData(@"C:\Users\test\AppData\Local\ContextMole", @"c:\users\TEST\appdata\local\contextmole", false)]
    [InlineData(@"C:\shared", @"D:\junction-target", false)]
    [InlineData(@"C:\Users\test\AppData\Local\Packages\Client_123\LocalCache\Local\ContextMole", @"C:\Users\test\AppData\Local\Packages\Client_123\LocalCache\Local\ContextMole", false)]
    public void DetectsPackageRedirectionWithoutRejectingCustomPaths(string requested, string physical, bool expected)
        => Assert.Equal(expected, WindowsDataDirectoryAccess.IsPackageRedirectedPath(requested, physical));

    [Fact]
    public void WorkerArgumentsRejectUnrelatedPipeNames()
    {
        Assert.True(WindowsMcpDesktopTransport.IsValidPipeName("context-mole-desktop-" + Guid.NewGuid().ToString("N")));
        Assert.False(WindowsMcpDesktopTransport.IsValidPipeName("other-server"));
        Assert.False(WindowsMcpDesktopTransport.IsValidPipeName("context-mole-desktop-../file"));
        Assert.False(WindowsMcpDesktopTransport.IsValidPipeName("context-mole-desktop-"));
    }

    [Fact]
    public async Task DesktopWorkerPreservesEnvironmentServesSharedIndexAndReleasesLeaseOnEof()
    {
        Assert.SkipUnless(WindowsDesktopProcessLauncher.HasDesktopShell, "This integration test needs an interactive Windows desktop.");
        var token = TestContext.Current.CancellationToken;
        using var paths = new DesktopTestPaths();
        Guid projectId;
        using (var writer = new DatabaseWriterService(paths))
        {
            await writer.StartAsync(token);
            projectId = await writer.CreateProjectAsync(new CreateProjectRequest("Shared desktop index", [paths.Source]), token);
            await writer.StopAsync(token);
        }

        var pipeName = "context-mole-desktop-" + Guid.NewGuid().ToString("N");
        using var input = CreatePipe(pipeName + "-in", PipeDirection.Out);
        using var output = CreatePipe(pipeName + "-out", PipeDirection.In);
        using var errors = CreatePipe(pipeName + "-err", PipeDirection.In);
        // A stale v1 broker must not intercept the new shared-runtime connection.
        using var legacy = CreatePipe("context-mole-v1-" + new BrokerEndpoint(paths.DataDirectory).DataDirectoryId, PipeDirection.InOut);
        var previous = Environment.GetEnvironmentVariable(ContextMoleLocalData.DataDirectoryEnvironmentVariable);
        Process? worker = null;
        try
        {
            Environment.SetEnvironmentVariable(ContextMoleLocalData.DataDirectoryEnvironmentVariable, paths.DataDirectory);
            worker = WindowsDesktopProcessLauncher.Start(FindMcpExecutable(), [WindowsMcpDesktopTransport.WorkerArgument, pipeName]);
            await Task.WhenAll(input.WaitForConnectionAsync(token), output.WaitForConnectionAsync(token),
                errors.WaitForConnectionAsync(token)).WaitAsync(TimeSpan.FromSeconds(20), token);
            using var errorReader = new StreamReader(errors);
            var logTask = errorReader.ReadToEndAsync(token);
            using var reader = new StreamReader(output);
            using var sender = new StreamWriter(input, new UTF8Encoding(false)) { AutoFlush = true };
            await sender.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"desktop-regression","version":"1"}}}""");
            using var initialized = await ReadResponseAsync(reader, 1, token);
            Assert.False(initialized.RootElement.TryGetProperty("error", out _));
            await sender.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await sender.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_projects","arguments":{}}}""");
            using var response = await ReadResponseAsync(reader, 2, token);
            var projects = response.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("result");
            var project = Assert.Single(projects.EnumerateArray());
            Assert.Equal(projectId, project.GetProperty("id").GetGuid());
            Assert.Equal("Shared desktop index", project.GetProperty("name").GetString());
            sender.Dispose();
            await worker.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Equal(0, worker.ExitCode);
            var log = await logTask.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Contains($"physical directory: {paths.DataDirectory}", log);
            Assert.Contains("MCP", log);
            Assert.Contains("starting. Data directory:", log);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(paths.DataDirectory, ".lifecycle", "leases"), "mcp-*.lease"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContextMoleLocalData.DataDirectoryEnvironmentVariable, previous);
            if (worker is not null)
            {
                if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(token); }
                worker.Dispose();
            }
            // Stop only the test's broker before removing the owned fixture directory.
            var endpoint = new BrokerEndpoint(paths.DataDirectory);
            if (File.Exists(endpoint.InstanceMetadataPath))
            {
                var client = new BrokerRpcClient(paths.DataDirectory, () => throw new InvalidOperationException("The test broker must already be running."));
                var health = await client.GetHealthAsync(token);
                using var broker = Process.GetProcessById(health.ProcessId);
                await client.InvokeAsync(BrokerProtocol.ShutdownMethod, new { }, token);
                await broker.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (File.Exists(endpoint.InstanceMetadataPath) && DateTime.UtcNow < deadline)
                    await Task.Delay(50, token);
                Assert.False(File.Exists(endpoint.InstanceMetadataPath));
            }
            SqliteConnection.ClearAllPools();
        }
    }

    private static NamedPipeServerStream CreatePipe(string name, PipeDirection direction) =>
        new(name, direction, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task<JsonDocument> ReadResponseAsync(StreamReader reader, int id, CancellationToken token)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(20), token);
            Assert.NotNull(line);
            var response = JsonDocument.Parse(line);
            if (response.RootElement.TryGetProperty("id", out var actual) && actual.GetInt32() == id) return response;
            response.Dispose();
        }
    }

    private static string FindMcpExecutable()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "ContextMole.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var configuration = typeof(BrokerMcpTools).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var executable = Path.Combine(repository.FullName, "src", "Mcp", "bin", configuration, "net10.0", "ContextMole.Mcp.exe");
        Assert.True(File.Exists(executable), executable);
        return executable;
    }

    private sealed class DesktopTestPaths : IAppPaths, IDisposable
    {
        private readonly string _root = Path.Combine(AppContext.BaseDirectory, "desktop mcp test " + Guid.NewGuid().ToString("N"));
        public DesktopTestPaths() { Directory.CreateDirectory(DataDirectory); Directory.CreateDirectory(Source); }
        public string DataDirectory => Path.Combine(_root, "data with spaces");
        public string DatabasePath => Path.Combine(DataDirectory, "index.db");
        public string AssetsDirectory => Path.Combine(DataDirectory, "assets");
        public string LogsDirectory => Path.Combine(DataDirectory, "logs");
        public string TempDirectory => Path.Combine(DataDirectory, "temp");
        public string Source => Path.Combine(_root, "source");
        public void Dispose()
        {
            var relative = Path.GetRelativePath(AppContext.BaseDirectory, Path.GetFullPath(_root));
            if (Path.IsPathRooted(relative) || !relative.StartsWith("desktop mcp test ", StringComparison.Ordinal) || relative.Contains(Path.DirectorySeparatorChar))
                throw new InvalidOperationException("The desktop test root escaped the owned output directory.");
            Directory.Delete(_root, recursive: true);
        }
    }
}
