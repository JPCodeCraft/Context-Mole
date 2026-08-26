using System.ComponentModel;
using MCPIndexSearch.Core;
using MCPIndexSearch.Search;
using ModelContextProtocol.Server;

namespace MCPIndexSearch.Mcp;

[McpServerToolType]
public sealed class McpTools(ISearchStore store, HybridSearchService search, IContentMaterializer materializer, IAppPaths paths)
{
    private readonly ISearchStore _store = store;
    private readonly HybridSearchService _search = search;
    private readonly IContentMaterializer _materializer = materializer;
    private readonly IAppPaths _paths = paths;

    [McpServerTool(Name = "list_projects", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use when the project ID is unknown or to inspect available indexes. Lists every initialized project, including paused projects, with authorized folders, search generation, and document status counts; it does not search file contents.")]
    public Task<object> ListProjects(CancellationToken cancellationToken) => RunAsync(async () =>
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _store.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
    });

    [McpServerTool(Name = "search_project", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use to find evidence in one known project. Runs ranked keyword and local-semantic search over indexed passages, including ZIP/RAR entries, and returns excerpts with IDs plus exact stored source, attachment-chain, and typed-location provenance; it does not return complete documents.")]
    public Task<object> SearchProject(Guid project_id, string query, int limit = 10, McpSearchFilters? filters = null,
        CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _search.SearchAsync(new SearchRequest(project_id, query, limit, filters?.ToDomain()), cancellationToken).ConfigureAwait(false);
    });

    [McpServerTool(Name = "read_passages", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use after search_project to read the full stored text of selected passage IDs with up to three neighboring passages before and after. Every returned passage retains its own provenance; it does not open or materialize the source file.")]
    public Task<object> ReadPassages(Guid project_id, IReadOnlyList<Guid> passage_ids, int context_before = 0,
        int context_after = 0, CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (passage_ids.Count is < 1 or > 50)
            throw new McpIndexException("invalid_request", "passage_ids must contain between 1 and 50 IDs.");
        if (context_before is < 0 or > 3 || context_after is < 0 or > 3)
            throw new McpIndexException("invalid_request", "context_before and context_after must be between 0 and 3.");
        return await _store.ReadPassagesAsync(project_id, passage_ids, context_before, context_after, cancellationToken).ConfigureAwait(false);
    });

    [McpServerTool(Name = "get_document_info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use to inspect one indexed document or content node. Returns stored metadata, fingerprint and revision, extraction counts, and recorded errors without reading passage text, opening the file, or changing the index.")]
    public Task<object> GetDocumentInfo(Guid project_id, Guid document_id, Guid? content_id = null,
        CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _store.GetDocumentInfoAsync(project_id, document_id, content_id, cancellationToken).ConfigureAwait(false)
            ?? throw new McpIndexException("document_not_found", "The indexed document or content node was not found.");
    });

    [McpServerTool(Name = "list_documents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use to browse or filter a project's root-document inventory and indexing status. Returns deterministic, cursor-paginated metadata, counts, errors, and revisions only; attachments are represented by counts and extracted text is never loaded.")]
    public Task<object> ListDocuments(Guid project_id, string status = "all",
        IReadOnlyList<string>? extensions = null, IReadOnlyList<string>? path_prefixes = null,
        string? name_query = null, DateTimeOffset? modified_from_utc = null, DateTimeOffset? modified_to_utc = null,
        string sort_by = "file_name", string sort_direction = "asc", int limit = 100, string? cursor = null,
        CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        try
        {
            return await _store.ListDocumentsAsync(new DocumentListRequest(project_id,
                ParseDocumentStatus(status), extensions, path_prefixes, name_query, modified_from_utc, modified_to_utc,
                ParseDocumentSortField(sort_by), ParseDocumentSortDirection(sort_direction), limit, cursor), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (McpIndexException exception) when (exception.Code is "not_initialized" or "schema_incompatible")
        {
            throw new McpIndexException("index_unavailable", "The local document index is unavailable or incompatible.", true);
        }
    });

    [McpServerTool(Name = "list_attachments", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use to discover attachment and archive-entry content IDs and hierarchy for one indexed root document. Returns metadata in deterministic preorder; it does not read, open, or materialize content bytes.")]
    public Task<object> ListAttachments(Guid project_id, Guid document_id, string? cursor = null, int limit = 100,
        CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (limit is < 1 or > 500)
            throw new McpIndexException("invalid_request", "limit must be between 1 and 500.");
        return await _store.ListAttachmentsAsync(project_id, document_id, cursor, limit, cancellationToken).ConfigureAwait(false);
    });

    [McpServerTool(Name = "resolve_local_file", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use when the existing original document or container path is sufficient. Resolves an indexed document or content ID to its authorized root source file with stored provenance; it never accepts arbitrary paths or extracts attachments.")]
    public Task<object> ResolveLocalFile(Guid project_id, Guid document_id, Guid? content_id = null,
        CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _store.ResolveLocalFileAsync(project_id, document_id, content_id, cancellationToken).ConfigureAwait(false)
            ?? throw new McpIndexException("document_not_found", "The indexed document or content node was not found.");
    });

    [McpServerTool(Name = "materialize_content", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use when another tool must open or verify an indexed root document, attachment, or ZIP/RAR entry, especially when formatting, tables, images, or structure matter. It validates project authorization and the indexed fingerprint; root content reuses its source path, while nested content extracts only the selected item to collision-safe temporary storage. It does not open or render the file.")]
    public Task<object> MaterializeContent(Guid project_id, Guid content_id,
        CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _materializer.MaterializeAsync(project_id, content_id, cancellationToken).ConfigureAwait(false);
    });

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!await _store.IsInitializedAsync(cancellationToken).ConfigureAwait(false))
            throw File.Exists(_paths.DatabasePath)
                ? new McpIndexException("schema_incompatible", "The local index database schema is incompatible with this MCP server.")
                : new McpIndexException("not_initialized", "The local index database has not been initialized by the desktop application.");
    }

    private static DocumentStatusFilter ParseDocumentStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "all" => DocumentStatusFilter.All,
        "indexed" => DocumentStatusFilter.Indexed,
        "pending" => DocumentStatusFilter.Pending,
        "processing" => DocumentStatusFilter.Processing,
        "paused" => DocumentStatusFilter.Paused,
        "error" => DocumentStatusFilter.Error,
        _ => throw new McpIndexException("invalid_filter", "status must be all, indexed, pending, processing, paused, or error.")
    };

    private static DocumentSortField ParseDocumentSortField(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "file_name" => DocumentSortField.FileName,
        "source_path" => DocumentSortField.SourcePath,
        "modified_utc" => DocumentSortField.ModifiedUtc,
        "last_indexed_utc" => DocumentSortField.LastIndexedUtc,
        "status" => DocumentSortField.Status,
        _ => throw new McpIndexException("invalid_filter", "sort_by must be file_name, source_path, modified_utc, last_indexed_utc, or status.")
    };

    private static DocumentSortDirection ParseDocumentSortDirection(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "asc" => DocumentSortDirection.Asc,
        "desc" => DocumentSortDirection.Desc,
        _ => throw new McpIndexException("invalid_filter", "sort_direction must be asc or desc.")
    };

    private static async Task<object> RunAsync(Func<Task<object>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (McpIndexException exception)
        {
            return new ErrorEnvelope(new ToolError(exception.Code, exception.Message, exception.Retryable));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ErrorEnvelope(new ToolError("internal_error", exception.Message, false));
        }
    }
}

public sealed record McpSearchFilters(
    IReadOnlyList<Guid>? DocumentIds = null,
    IReadOnlyList<string>? PathPrefixes = null,
    IReadOnlyList<string>? Extensions = null,
    DateTimeOffset? ModifiedFromUtc = null,
    DateTimeOffset? ModifiedToUtc = null,
    AttachmentScope AttachmentScope = AttachmentScope.Any)
{
    public SearchFilters ToDomain() => new(DocumentIds, PathPrefixes, Extensions, ModifiedFromUtc, ModifiedToUtc, AttachmentScope);
}

public sealed record ToolError(string Code, string Message, bool Retryable);
public sealed record ErrorEnvelope(ToolError Error);
