using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace ContextMole.Mcp;

internal static class WindowsMcpDesktopTransport
{
    internal const string WorkerArgument = "--context-mole-desktop-transport";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    public static async Task<int> RunAsync(string[] args, Func<string[], Stream?, Stream?, Task> runServer)
    {
        if (args.Length > 0 && args[0] == WorkerArgument)
        {
            if (!OperatingSystem.IsWindows() || args.Length < 2 || !IsValidPipeName(args[1]))
                throw new ArgumentException("Invalid Context Mole desktop transport arguments.");
            return await RunWorkerAsync(args[1], args[2..], runServer).ConfigureAwait(false);
        }

        var access = WindowsDataDirectoryAccess.Probe(new McpAppPaths().DataDirectory);
        await Console.Error.WriteLineAsync($"Context Mole MCP data directory: {access.RequestedDirectory}; physical directory: {access.PhysicalDirectory}").ConfigureAwait(false);
        if (!access.IsRedirected)
        {
            await runServer(args, null, null).ConfigureAwait(false);
            return 0;
        }

        await Console.Error.WriteLineAsync("Windows redirected the MCP data directory into an MSIX private cache. Launching the MCP in the desktop runtime.").ConfigureAwait(false);
        return await RunRelayAsync(args).ConfigureAwait(false);
    }

    private static async Task<int> RunWorkerAsync(string pipeName, string[] args,
        Func<string[], Stream?, Stream?, Task> runServer)
    {
        using var input = new NamedPipeClientStream(".", pipeName + "-in", PipeDirection.In, PipeOptions.Asynchronous);
        using var output = new NamedPipeClientStream(".", pipeName + "-out", PipeDirection.Out, PipeOptions.Asynchronous);
        using var errors = new NamedPipeClientStream(".", pipeName + "-err", PipeDirection.Out, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(ConnectTimeout);
        await Task.WhenAll(input.ConnectAsync(timeout.Token), output.ConnectAsync(timeout.Token), errors.ConnectAsync(timeout.Token)).ConfigureAwait(false);
        var originalError = Console.Error;
        using var errorWriter = new StreamWriter(errors, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        Console.SetError(TextWriter.Synchronized(errorWriter));
        try
        {
            var access = WindowsDataDirectoryAccess.Probe(new McpAppPaths().DataDirectory);
            await Console.Error.WriteLineAsync($"Context Mole desktop MCP data directory: {access.RequestedDirectory}; physical directory: {access.PhysicalDirectory}").ConfigureAwait(false);
            if (access.IsRedirected)
                throw new InvalidOperationException("Windows still redirects the desktop MCP data directory. Cannot safely use the shared index.");
            await runServer(args, input, output).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Context Mole desktop MCP failed: {exception}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            await errorWriter.FlushAsync().ConfigureAwait(false);
            Console.SetError(originalError);
        }
    }

    private static async Task<int> RunRelayAsync(string[] args)
    {
        var pipeName = "context-mole-desktop-" + Guid.NewGuid().ToString("N");
        using var input = CreatePipe(pipeName + "-in", PipeDirection.Out);
        using var output = CreatePipe(pipeName + "-out", PipeDirection.In);
        using var errors = CreatePipe(pipeName + "-err", PipeDirection.In);
        var (executable, arguments) = GetWorkerCommand(pipeName, args);
        using var process = WindowsDesktopProcessLauncher.Start(executable, arguments);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            timeout.CancelAfter(ConnectTimeout);
            var connected = Task.WhenAll(input.WaitForConnectionAsync(timeout.Token),
                output.WaitForConnectionAsync(timeout.Token), errors.WaitForConnectionAsync(timeout.Token));
            var exited = process.WaitForExitAsync(cancellation.Token);
            if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited)
                throw new InvalidOperationException($"Desktop MCP exited before connecting (exit code {process.ExitCode}).");
            await connected.ConfigureAwait(false);

            var inputPump = Console.OpenStandardInput().CopyToAsync(input, cancellation.Token);
            var outputPump = output.CopyToAsync(Console.OpenStandardOutput(), cancellation.Token);
            var errorPump = errors.CopyToAsync(Console.OpenStandardError(), cancellation.Token);
            var first = await Task.WhenAny(inputPump, outputPump, exited).ConfigureAwait(false);
            if (first == inputPump)
            {
                await inputPump.ConfigureAwait(false);
                input.Dispose(); // EOF makes the MCP worker stop and release its lifecycle lease.
            }
            await exited.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token).ConfigureAwait(false);
            await Task.WhenAll(outputPump, errorPump).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            await cancellation.CancelAsync().ConfigureAwait(false);
            if (!process.HasExited)
            {
                // The broker can be shared with the desktop app; terminate only this worker.
                process.Kill();
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe(string name, PipeDirection direction) =>
        new(name, direction, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    internal static bool IsValidPipeName(string name) => name.StartsWith("context-mole-desktop-", StringComparison.Ordinal)
        && Guid.TryParseExact(name["context-mole-desktop-".Length..], "N", out _);

    internal static (string Executable, string[] Arguments) GetWorkerCommand(string pipeName, string[] args)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the MCP executable.");
        var prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { typeof(Program).Assembly.Location } : Array.Empty<string>();
        if (prefix.Any(string.IsNullOrEmpty)) throw new InvalidOperationException("Cannot locate the MCP assembly.");
        return (executable, [.. prefix, WorkerArgument, pipeName, .. args]);
    }
}
