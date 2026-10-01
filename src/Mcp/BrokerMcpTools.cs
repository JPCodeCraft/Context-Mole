using System.ComponentModel;
using System.Text.Json;

using ContextMole.Broker.Protocol;
using ContextMole.Core;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

namespace ContextMole.Mcp;

[McpServerToolType]
public sealed class BrokerMcpTools(BrokerRpcClient broker, ILogger<BrokerMcpTools> logger)
{
    private readonly BrokerRpcClient _broker = broker;
    private readonly ILogger<BrokerMcpTools> _logger = logger;

    [McpServerTool(Name = "list_projects", OutputSchemaType = typeof(ProjectListResponse), ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use when the project ID is unknown or to inspect available indexes. Lists every initialized project, including paused projects, with authorized folders, search generation, and document status counts; it does not search file contents.")]
    public Task<CallToolResult> ListProjects(CancellationToken cancellationToken) =>
        RunAsync(BrokerToolMethods.ListProjects, new { }, cancellationToken);

    [McpServerTool(Name = "search_project", OutputSchemaType = typeof(SearchWireResponse), ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Search one indexed project by exact lexical clauses, conceptual similarity, or both. match_scope=passage applies constraints to one passage; section applies them across the complete logical section. Set retrieval_options.candidate_limit independently of output limits. Compact results contain literal anchor excerpts, field matches, provenance, and similarity; full detail adds raw ranking diagnostics. Continue with only project_id and cursor for fixed-ranked pages. Check candidate_limit_reached and warnings before treating evaluated counts as exhaustive. Similarity is not a relevance probability.")]
    public Task<CallToolResult> SearchProject(
        [Description("Stable project ID from list_projects; exactly one project is searched per call.")] Guid project_id,
        [Description("Retrieval strategy: hybrid (default), keyword, or semantic.")] SearchMode mode = SearchMode.Hybrid,
        [Description("Natural-language conceptual query for semantic/hybrid retrieval. Required for semantic mode; omit for keyword mode.")] string? semantic_query = null,
        [Description("Structured lexical constraints. Clauses may independently be must, should, or must_not and term, phrase, or prefix. Required clause logic follows match_scope; section requirements may be supported by different passages.")] IReadOnlyList<McpSearchClause>? clauses = null,
        [Description("Required number of should clauses that must match within the selected scope. Defaults to 1 when there are only should clauses, otherwise 0.")] int? minimum_should_match = null,
        [Description("Optional 0-10 BM25 field-weight overrides. Omitted fields use agent-oriented defaults.")] McpSearchFieldWeights? field_weights = null,
        [Description("Optional 0-10 keyword/semantic fusion weights; hybrid mode only.")] McpSearchBranchWeights? branch_weights = null,
        [Description("Optional document/content/path/type/date/attachment filters. Use content_ids to focus a follow-up on selected groups.")] McpSearchFilters? filters = null,
        [Description("Per-page grouped result limits, diversity, detail, and semantic similarity behavior.")] McpSearchResultOptions? result_options = null,
        [Description("passage (default) or section. Section scope combines distributed lexical evidence within one persisted logical section.")] SearchScope match_scope = SearchScope.Passage,
        [Description("Independent retrieval budget per enabled branch.")] McpSearchRetrievalOptions? retrieval_options = null,
        [Description("Continuation cursor; supply only project_id and cursor when continuing.")] string? cursor = null,
        CancellationToken cancellationToken = default) => RunAsync(BrokerToolMethods.SearchProject,
        new BrokerSearchProjectRequest(new SearchRequest(project_id, mode, semantic_query,
            clauses?.Select(clause => clause.ToDomain()).ToArray(), minimum_should_match, field_weights?.ToDomain(),
            branch_weights?.ToDomain(), filters?.ToDomain(), result_options?.ToDomain(), match_scope,
            retrieval_options?.CandidateLimit ?? 1000, result_options?.Detail ?? SearchDetail.Compact, cursor)), cancellationToken);

    [McpServerTool(Name = "read_passages", OutputSchemaType = typeof(ReadPassagesResponse), ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use after search_project to read the full stored text of selected passage IDs with up to three neighboring passages before and after. Every returned passage retains its own provenance; it does not open or materialize the source file.")]
    public Task<CallToolResult> ReadPassages(Guid project_id, IReadOnlyList<Guid> passage_ids,
        [Description("Required search_generation returned by search; prevents reading changed evidence.")] long expected_search_generation,
        int context_before = 0, int context_after = 0,
        CancellationToken cancellationToken = default) => RunAsync(
        BrokerToolMethods.ReadPassages,
        new BrokerReadPassagesRequest(project_id, passage_ids, context_before, context_after, expected_search_generation), cancellationToken);

    [McpServerTool(Name = "read_section", OutputSchemaType = typeof(SectionReadResponse), ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read a logical section's stored passages in source order. Use section_id and search_generation returned by search; generation guards prevent reading changed evidence. Pages retain individual passage IDs and locations.")]
    public Task<CallToolResult> ReadSection(Guid project_id, Guid section_id, long expected_search_generation,
        int limit = 20, string? cursor = null, CancellationToken cancellationToken = default) =>
        RunAsync(BrokerToolMethods.ReadSection, new BrokerReadSectionRequest(project_id, section_id,
            expected_search_generation, limit, cursor), cancellationToken);

    [McpServerTool(Name = "get_document_info", OutputSchemaType = typeof(DocumentInfo), ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use to inspect one indexed document or content node. Returns stored metadata, fingerprint and revision, extraction counts, and recorded errors without reading passage text, opening the file, or changing the index.")]
    public Task<CallToolResult> GetDocumentInfo(Guid project_id, Guid document_id, Guid? content_id = null,
        CancellationToken cancellationToken = default) => RunAsync(BrokerToolMethods.GetDocumentInfo,
        new BrokerGetDocumentInfoRequest(project_id, document_id, content_id), cancellationToken);

    [McpServerTool(Name = "list_documents", OutputSchemaType = typeof(DocumentListResponse), ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use to browse or filter a project's root-document inventory and indexing status. Returns deterministic, cursor-paginated metadata, counts, errors, and revisions only; attachments are represented by counts and extracted text is never loaded.")]
    public Task<CallToolResult> ListDocuments(Guid project_id, string status = "all",
        IReadOnlyList<string>? extensions = null, IReadOnlyList<string>? path_prefixes = null,
        string? name_query = null, DateTimeOffset? modified_from_utc = null, DateTimeOffset? modified_to_utc = null,
        string sort_by = "file_name", string sort_direction = "asc", int limit = 100, string? cursor = null,
        CancellationToken cancellationToken = default) => RunAsync(BrokerToolMethods.ListDocuments,
        new BrokerListDocumentsRequest(project_id, status, extensions, path_prefixes, name_query, modified_from_utc,
            modified_to_utc, sort_by, sort_direction, limit, cursor), cancellationToken);

    [McpServerTool(Name = "list_attachments", OutputSchemaType = typeof(AttachmentPage), ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use to discover attachment and archive-entry content IDs and hierarchy for one indexed root document. Returns metadata in deterministic preorder; it does not read, open, or materialize content bytes.")]
    public Task<CallToolResult> ListAttachments(Guid project_id, Guid document_id, string? cursor = null, int limit = 100,
        CancellationToken cancellationToken = default) => RunAsync(BrokerToolMethods.ListAttachments,
        new BrokerListAttachmentsRequest(project_id, document_id, cursor, limit), cancellationToken);

    [McpServerTool(Name = "resolve_local_file", OutputSchemaType = typeof(ResolvedLocalFile), ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use when the existing original document or container path is sufficient. Resolves an indexed document or content ID to its authorized root source file with stored provenance; it never accepts arbitrary paths or extracts attachments.")]
    public Task<CallToolResult> ResolveLocalFile(Guid project_id, Guid document_id, Guid? content_id = null,
        CancellationToken cancellationToken = default) => RunAsync(BrokerToolMethods.ResolveLocalFile,
        new BrokerResolveLocalFileRequest(project_id, document_id, content_id), cancellationToken);

    [McpServerTool(Name = "materialize_content", OutputSchemaType = typeof(MaterializedContent), ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use when another tool must open or verify an indexed root document, attachment, or ZIP/RAR entry, especially when formatting, tables, images, or structure matter. It validates project authorization and the indexed fingerprint; root content reuses its source path, while nested content extracts only the selected item to collision-safe temporary storage. It does not open or render the file.")]
    public Task<CallToolResult> MaterializeContent(Guid project_id, Guid content_id,
        CancellationToken cancellationToken = default) => RunAsync(BrokerToolMethods.MaterializeContent,
        new BrokerMaterializeContentRequest(project_id, content_id), cancellationToken);

    private async Task<CallToolResult> RunAsync<TRequest>(string method, TRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _broker.InvokeAsync(method, request, cancellationToken).ConfigureAwait(false);
            if (method == BrokerToolMethods.ListProjects)
                result = JsonSerializer.SerializeToElement(new ProjectListResponse(
                    result.Deserialize<IReadOnlyList<ProjectSummary>>(BrokerJson.Options) ?? []), BrokerJson.Options);
            return new CallToolResult { IsError = false, StructuredContent = result,
                Content = [new TextContentBlock { Text = result.GetRawText() }] };
        }
        catch (BrokerRpcException exception)
        {
            return ErrorResult(new ToolError(exception.Code, exception.Message, exception.Retryable));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unhandled MCP broker failure");
            return ErrorResult(new ToolError("broker_unavailable",
                "The shared Context Mole broker is unavailable. Retry the request.", true));
        }
    }
    private static CallToolResult ErrorResult(ToolError error)
    {
        var value = JsonSerializer.SerializeToElement(new ErrorEnvelope(error), BrokerJson.Options);
        return new CallToolResult { IsError = true, StructuredContent = value,
            Content = [new TextContentBlock { Text = value.GetRawText() }] };
    }

}
