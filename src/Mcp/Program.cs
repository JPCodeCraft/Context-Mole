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
        "Use this server whenever the user asks to find, search, locate, list, summarize, compare, cross-check, or verify information in local indexed files, emails, attachments, or archives. If the project is unknown, call list_projects first. search_project is deliberately agent-directed: choose keyword for exact terms, phrases, filenames, paths, headings, sheet names, or email subjects; semantic for conceptual recall; and hybrid when both signals help. Build lexical logic from separate must, should, and must_not clauses, mixing term, phrase, and prefix matching as needed. Clause logic is passage-scoped: every must and the requested minimum_should_match must hold in the same passage. If evidence may span sections, run separate or content-focused searches, then use read_passages or materialize_content. Start with the default field and branch weights, but override them when the task gives a field or retrieval branch unusual importance.\n\n" +
        "Semantic confidence is permissive by default: inspect raw semantic scores and low_confidence rather than assuming borderline leads were removed. Use strict_semantic_threshold only when false positives cost more than recall. If hybrid reports fallback_keyword, continue with those results or retry semantic later; semantic mode intentionally returns no results with a structured semantic_unavailable warning rather than silently changing modes. Results are grouped by stable content_id. candidate_match_count is the unique evaluated match count; inspected_candidate_depths reports keyword, optional boost, and semantic branch depths separately because branches can overlap. Inspect candidate_limit_reached before treating candidate, collapsed, or suppressed counts as exhaustive; when it is true, raise the group limits or focus a later search_project call with filters.content_ids. Use read_passages with returned passage IDs for stored neighboring text. Use materialize_content when original-file verification, formatting, tables, images, attachments, archive entries, or document structure may affect the answer.\n\n" +
        "Use list_documents for inventories, filtering, and indexing status; get_document_info for one document's metadata, revision, extraction counts, and errors; list_attachments to discover content IDs; and resolve_local_file when the existing root document or container path is sufficient. Search excerpts are evidence leads, not complete documents: read enough context before summarizing or comparing. Base claims and citations only on exact returned provenance; never infer or normalize source paths, attachment chains, typed locations, contents, or citations. If a source changed after indexing, do not claim it matches the indexed version. This server does not render, open, reindex, modify, or delete source files.";

    public static async Task Main(string[] args)
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
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInstructions = ServerInstructions;
            })
            .WithStdioServerTransport()
            .WithTools<BrokerMcpTools>(serializerOptions);

        await builder.Build().RunAsync().ConfigureAwait(false);
    }
}
