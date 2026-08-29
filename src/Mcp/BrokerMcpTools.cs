using System.ComponentModel;

using ContextMole.Broker.Protocol;
using ContextMole.Core;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace ContextMole.Mcp;

[McpServerToolType]
public sealed class BrokerMcpTools(BrokerRpcClient broker, ILogger<BrokerMcpTools> logger)
{
    private readonly BrokerRpcClient _broker = broker;
    private readonly ILogger<BrokerMcpTools> _logger = logger;

    [McpServerTool(Name = "list_projects", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use when the project ID is unknown or to inspect available indexes. Lists every initialized project, including paused projects, with authorized folders, search generation, and document status counts; it does not search file contents.")]
    public Task<object> ListProjects(CancellationToken cancellationToken) =>
        RunAsync(BrokerToolMethods.ListProjects, new { }, cancellationToken);

    [McpServerTool(Name = "search_project", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Agent-directed search over one indexed project. Choose keyword for exact terms/phrases/prefixes, semantic for concepts, or hybrid to fuse both. Mix passage-scoped must/should/must_not clauses, target metadata fields, override lexical/fusion weights, focus returned content_ids, and control grouped previews. Results are grouped by root or nested content node and include stable IDs, unique match counts, separate per-branch inspection depths, scores, confidence, matched clauses/fields, provenance, collapsed counts, and suppressed-source summaries. Borderline semantic matches are labelled rather than hidden unless strict_semantic_threshold is enabled. When embedding coverage is mixed, compatible documents remain eligible for semantic retrieval and the response reports semantic_partial_coverage.")]
    public Task<object> SearchProject(
        [Description("Stable project ID from list_projects; exactly one project is searched per call.")] Guid project_id,
        [Description("Retrieval strategy: hybrid (default), keyword, or semantic.")] SearchMode mode = SearchMode.Hybrid,
        [Description("Natural-language conceptual query for semantic/hybrid retrieval. Required for semantic mode; omit for keyword mode.")] string? semantic_query = null,
        [Description("Structured lexical constraints. Clauses may independently be must, should, or must_not and term, phrase, or prefix. All required clause logic is evaluated within one passage, not across a document group.")] IReadOnlyList<McpSearchClause>? clauses = null,
        [Description("Required number of should clauses that must match the same passage. Defaults to 1 when there are only should clauses, otherwise 0.")] int? minimum_should_match = null,
        [Description("Optional 0-10 BM25 field-weight overrides. Omitted fields use agent-oriented defaults.")] McpSearchFieldWeights? field_weights = null,
        [Description("Optional 0-10 keyword/semantic fusion weights; hybrid mode only.")] McpSearchBranchWeights? branch_weights = null,
        [Description("Optional document/content/path/type/date/attachment filters. Use content_ids to focus a follow-up on selected groups.")] McpSearchFilters? filters = null,
        [Description("Grouped result limits, diversity, and semantic confidence behavior.")] McpSearchResultOptions? result_options = null,
        CancellationToken cancellationToken = default) => RunAsync(BrokerToolMethods.SearchProject,
        new BrokerSearchProjectRequest(new SearchRequest(project_id, mode, semantic_query,
            clauses?.Select(clause => clause.ToDomain()).ToArray(), minimum_should_match, field_weights?.ToDomain(),
            branch_weights?.ToDomain(), filters?.ToDomain(), result_options?.ToDomain())), cancellationToken);

    [McpServerTool(Name = "read_passages", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use after search_project to read the full stored text of selected passage IDs with up to three neighboring passages before and after. Every returned passage retains its own provenance; it does not open or materialize the source file.")]
    public Task<object> ReadPassages(Guid project_id, IReadOnlyList<Guid> passage_ids, int context_before = 0,
        int context_after = 0, CancellationToken cancellationToken = default) => RunAsync(
        BrokerToolMethods.ReadPassages,
        new BrokerReadPassagesRequest(project_id, passage_ids, context_before, context_after), cancellationToken);

    [McpServerTool(Name = "get_document_info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use to inspect one indexed document or content node. Returns stored metadata, fingerprint and revision, extraction counts, and recorded errors without reading passage text, opening the file, or changing the index.")]
    public Task<object> GetDocumentInfo(Guid project_id, Guid document_id, Guid? content_id = null,
        CancellationToken cancellationToken = default) => RunAsync(BrokerToolMethods.GetDocumentInfo,
        new BrokerGetDocumentInfoRequest(project_id, document_id, content_id), cancellationToken);

    [McpServerTool(Name = "list_documents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use to browse or filter a project's root-document inventory and indexing status. Returns deterministic, cursor-paginated metadata, counts, errors, and revisions only; attachments are represented by counts and extracted text is never loaded.")]
    public Task<object> ListDocuments(Guid project_id, string status = "all",
        IReadOnlyList<string>? extensions = null, IReadOnlyList<string>? path_prefixes = null,
        string? name_query = null, DateTimeOffset? modified_from_utc = null, DateTimeOffset? modified_to_utc = null,
        string sort_by = "file_name", string sort_direction = "asc", int limit = 100, string? cursor = null,
        CancellationToken cancellationToken = default) => RunAsync(BrokerToolMethods.ListDocuments,
        new BrokerListDocumentsRequest(project_id, status, extensions, path_prefixes, name_query, modified_from_utc,
            modified_to_utc, sort_by, sort_direction, limit, cursor), cancellationToken);

    [McpServerTool(Name = "list_attachments", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use to discover attachment and archive-entry content IDs and hierarchy for one indexed root document. Returns metadata in deterministic preorder; it does not read, open, or materialize content bytes.")]
    public Task<object> ListAttachments(Guid project_id, Guid document_id, string? cursor = null, int limit = 100,
        CancellationToken cancellationToken = default) => RunAsync(BrokerToolMethods.ListAttachments,
        new BrokerListAttachmentsRequest(project_id, document_id, cursor, limit), cancellationToken);

    [McpServerTool(Name = "resolve_local_file", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use when the existing original document or container path is sufficient. Resolves an indexed document or content ID to its authorized root source file with stored provenance; it never accepts arbitrary paths or extracts attachments.")]
    public Task<object> ResolveLocalFile(Guid project_id, Guid document_id, Guid? content_id = null,
        CancellationToken cancellationToken = default) => RunAsync(BrokerToolMethods.ResolveLocalFile,
        new BrokerResolveLocalFileRequest(project_id, document_id, content_id), cancellationToken);

    [McpServerTool(Name = "materialize_content", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use when another tool must open or verify an indexed root document, attachment, or ZIP/RAR entry, especially when formatting, tables, images, or structure matter. It validates project authorization and the indexed fingerprint; root content reuses its source path, while nested content extracts only the selected item to collision-safe temporary storage. It does not open or render the file.")]
    public Task<object> MaterializeContent(Guid project_id, Guid content_id,
        CancellationToken cancellationToken = default) => RunAsync(BrokerToolMethods.MaterializeContent,
        new BrokerMaterializeContentRequest(project_id, content_id), cancellationToken);

    private async Task<object> RunAsync<TRequest>(string method, TRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _broker.InvokeAsync(method, request, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerRpcException exception)
        {
            return new ErrorEnvelope(new ToolError(exception.Code, exception.Message, exception.Retryable));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unhandled MCP broker failure");
            return new ErrorEnvelope(new ToolError("broker_unavailable",
                "The shared Context Mole broker is unavailable. Retry the request.", true));
        }
    }
}
