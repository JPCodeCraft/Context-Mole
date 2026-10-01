using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using ContextMole.Broker.Protocol;
using ContextMole.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextMole.Mcp;

internal static class Program
{
    internal const string ServerInstructions =
        "Use this server to search and inspect local indexed files, emails, attachments, and archives. If the project is unknown, call list_projects. Choose keyword for exact terms, phrases, prefixes, and metadata; semantic for concepts; hybrid for both. Build lexical logic from independent must, should, and must_not clauses. match_scope defaults to passage; choose section when requirements may be distributed across passages in one logical section. A section exclusion applies to the entire section. Start with default ranking weights.\n\n" +
        "retrieval_options.candidate_limit defaults to 1000 per active branch (maximum 10000), independently of result_options limits. Semantic candidate budgets bound retained and hydrated matches; the flat vector index still scans eligible vectors. Compact results contain literal anchor excerpts and UTF-16 excerpt/match offsets referring to read_passages.text, matched metadata fields, typed locations, source provenance, and semantic_similarity. result_options.detail=full adds raw ranking and extraction diagnostics. Similarity is a cosine comparison, not confidence or a probability of relevance. below_similarity_threshold is null when no semantic similarity exists. Strict threshold filtering is opt-in. Check warnings and candidate_limit_reached before treating evaluated counts as exhaustive.\n\n" +
        "Continue a search with only project_id and next_cursor supplied as cursor. Search sessions preserve ranking and result settings for ten minutes and may be evicted under the memory limit. Per-page document diversity defers remaining groups to later pages. On cursor_expired, index_changed, or semantic_model_changed, run the search again. semantic_partial_coverage means only the compatible vector subset was searched; keyword still covers the full index. Semantic-only unavailable requests return warnings without changing mode.\n\n" +
        "Read selected passage IDs with expected_search_generation from search to prevent changed evidence from being read; read_section pages a persisted logical section in source order. Use list_documents for inventory, get_document_info for accurate root or selected-content metadata, list_attachments for hierarchy, and resolve_local_file or materialize_content when another tool needs original files. Excerpts are leads: read enough source context before making claims. Base citations on exact returned provenance. This server does not open, render, modify, or delete source files.";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await WindowsMcpDesktopTransport.RunAsync(args, RunServerAsync).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Context Mole MCP startup failed: {exception}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task RunServerAsync(string[] args, Stream? input, Stream? output)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton<IAppPaths, McpAppPaths>();
        builder.Services.AddSingleton<IHostedService, McpProcessLifetimeService>();
        builder.Services.AddSingleton(provider => new BrokerRpcClient(
            provider.GetRequiredService<IAppPaths>().DataDirectory,
            static () => BrokerLaunchCommand.Resolve()));

        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        serializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        var server = builder.Services.AddMcpServer(options =>
            {
                options.ServerInstructions = ServerInstructions;
            });
        if (input is not null && output is not null)
            server.WithStreamServerTransport(input, output);
        else
            server.WithStdioServerTransport();
        server.WithTools<BrokerMcpTools>(serializerOptions);

        using var host = builder.Build();
        var paths = host.Services.GetRequiredService<IAppPaths>();
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ContextMole.Mcp.Startup")
            .LogInformation("MCP {Version} starting. Data directory: {DataDirectory}. Database: {DatabasePath}",
                typeof(Program).Assembly.GetCustomAttributes(false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
                paths.DataDirectory, paths.DatabasePath);
        await host.RunAsync().ConfigureAwait(false);
    }
}
