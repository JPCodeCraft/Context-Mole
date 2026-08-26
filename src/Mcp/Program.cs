using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;
using MCPIndexSearch.Documents;
using MCPIndexSearch.Infrastructure;
using MCPIndexSearch.Search;
using MCPIndexSearch.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace MCPIndexSearch.Mcp;

internal static class Program
{
    internal const string ServerInstructions =
        "Use this server whenever the user asks to find, search, locate, list, summarize, compare, cross-check, or verify information in local or indexed files, emails, or attachments. If the project is unknown, call list_projects first. Search with search_project, read relevant passages with read_passages, and cite only exact returned provenance. Use materialize_content when the user requests original-file verification or when formatting, tables, images, attachments, or document structure may affect the answer.\n\n" +
        "Use list_documents for inventories, filtering, and indexing status; get_document_info for one document's metadata, revision, extraction counts, and errors; list_attachments to discover indexed attachment content IDs; and resolve_local_file when the existing root document or container path is sufficient. Search results are evidence leads, not complete documents: read enough context before summarizing or comparing. Base claims and citations only on tool output; never infer or normalize source paths, attachment chains, typed locations, file contents, or citations. If a source changed after indexing, do not claim it matches the indexed version. This server does not render, open, reindex, modify, or delete source files.";

    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose)
            .CreateLogger();
        builder.Services.AddSerilog(dispose: true);
        builder.Services.AddMcpIndexInfrastructure(includeOcr: false);
        builder.Services.AddMcpIndexDocuments();
        builder.Services.AddReadOnlyMcpIndexStorage();
        builder.Services.AddMcpIndexSearch();

        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        serializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInstructions = ServerInstructions;
            })
            .WithStdioServerTransport()
            .WithTools<McpTools>(serializerOptions);

        await builder.Build().RunAsync().ConfigureAwait(false);
    }
}
