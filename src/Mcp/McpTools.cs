using System.ComponentModel;

using ContextMole.Core;
using ContextMole.Search;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace ContextMole.Mcp;

[McpServerToolType]
public sealed class McpTools(
    ISearchStore store,
    HybridSearchService search,
    IContentMaterializer materializer,
    IAppPaths paths,
    ILogger<McpTools> logger)
{
    private readonly ISearchStore _store = store;
    private readonly HybridSearchService _search = search;
    private readonly IContentMaterializer _materializer = materializer;
    private readonly IAppPaths _paths = paths;
    private readonly ILogger<McpTools> _logger = logger;

    [McpServerTool(Name = "list_projects", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use when the project ID is unknown or to inspect available indexes. Lists every initialized project, including paused projects, with authorized folders, search generation, and document status counts; it does not search file contents.")]
    public Task<object> ListProjects(CancellationToken cancellationToken) => RunAsync(async () =>
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _store.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
    });

    [McpServerTool(Name = "search_project", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Agent-directed search over one indexed project. Choose keyword for exact terms/phrases/prefixes, semantic for concepts, or hybrid to fuse both. Mix passage-scoped must/should/must_not clauses, target metadata fields, override lexical/fusion weights, focus returned content_ids, and control grouped previews. Results are grouped by root or nested content node and include stable IDs, unique match counts, separate per-branch inspection depths, scores, confidence, matched clauses/fields, provenance, collapsed counts, and suppressed-source summaries. Borderline semantic matches are labelled rather than hidden unless strict_semantic_threshold is enabled.")]
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
        CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _search.SearchAsync(new SearchRequest(project_id, mode, semantic_query,
            clauses?.Select(clause => clause.ToDomain()).ToArray(), minimum_should_match, field_weights?.ToDomain(),
            branch_weights?.ToDomain(), filters?.ToDomain(), result_options?.ToDomain()), cancellationToken).ConfigureAwait(false);
    });

    [McpServerTool(Name = "read_passages", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use after search_project to read the full stored text of selected passage IDs with up to three neighboring passages before and after. Every returned passage retains its own provenance; it does not open or materialize the source file.")]
    public Task<object> ReadPassages(Guid project_id, IReadOnlyList<Guid> passage_ids, int context_before = 0,
        int context_after = 0, CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (passage_ids.Count is < 1 or > 50)
            throw new ContextMoleException("invalid_request", "passage_ids must contain between 1 and 50 IDs.");
        if (context_before is < 0 or > 3 || context_after is < 0 or > 3)
            throw new ContextMoleException("invalid_request", "context_before and context_after must be between 0 and 3.");
        return await _store.ReadPassagesAsync(project_id, passage_ids, context_before, context_after, cancellationToken).ConfigureAwait(false);
    });

    [McpServerTool(Name = "get_document_info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use to inspect one indexed document or content node. Returns stored metadata, fingerprint and revision, extraction counts, and recorded errors without reading passage text, opening the file, or changing the index.")]
    public Task<object> GetDocumentInfo(Guid project_id, Guid document_id, Guid? content_id = null,
        CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _store.GetDocumentInfoAsync(project_id, document_id, content_id, cancellationToken).ConfigureAwait(false)
            ?? throw new ContextMoleException("document_not_found", "The indexed document or content node was not found.");
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
        catch (ContextMoleException exception) when (exception.Code is "not_initialized" or "schema_incompatible")
        {
            throw new ContextMoleException("index_unavailable", "The local document index is unavailable or incompatible.", true);
        }
    });

    [McpServerTool(Name = "list_attachments", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use to discover attachment and archive-entry content IDs and hierarchy for one indexed root document. Returns metadata in deterministic preorder; it does not read, open, or materialize content bytes.")]
    public Task<object> ListAttachments(Guid project_id, Guid document_id, string? cursor = null, int limit = 100,
        CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (limit is < 1 or > 500)
            throw new ContextMoleException("invalid_request", "limit must be between 1 and 500.");
        return await _store.ListAttachmentsAsync(project_id, document_id, cursor, limit, cancellationToken).ConfigureAwait(false);
    });

    [McpServerTool(Name = "resolve_local_file", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use when the existing original document or container path is sufficient. Resolves an indexed document or content ID to its authorized root source file with stored provenance; it never accepts arbitrary paths or extracts attachments.")]
    public Task<object> ResolveLocalFile(Guid project_id, Guid document_id, Guid? content_id = null,
        CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _store.ResolveLocalFileAsync(project_id, document_id, content_id, cancellationToken).ConfigureAwait(false)
            ?? throw new ContextMoleException("document_not_found", "The indexed document or content node was not found.");
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
                ? new ContextMoleException("schema_incompatible", "The local index database schema is incompatible with this MCP server.")
                : new ContextMoleException("not_initialized", "The local index database has not been initialized by the desktop application.");
    }

    private static DocumentStatusFilter ParseDocumentStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "all" => DocumentStatusFilter.All,
        "indexed" => DocumentStatusFilter.Indexed,
        "pending" => DocumentStatusFilter.Pending,
        "processing" => DocumentStatusFilter.Processing,
        "paused" => DocumentStatusFilter.Paused,
        "error" => DocumentStatusFilter.Error,
        _ => throw new ContextMoleException("invalid_filter", "status must be all, indexed, pending, processing, paused, or error.")
    };

    private static DocumentSortField ParseDocumentSortField(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "file_name" => DocumentSortField.FileName,
        "source_path" => DocumentSortField.SourcePath,
        "modified_utc" => DocumentSortField.ModifiedUtc,
        "last_indexed_utc" => DocumentSortField.LastIndexedUtc,
        "status" => DocumentSortField.Status,
        _ => throw new ContextMoleException("invalid_filter", "sort_by must be file_name, source_path, modified_utc, last_indexed_utc, or status.")
    };

    private static DocumentSortDirection ParseDocumentSortDirection(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "asc" => DocumentSortDirection.Asc,
        "desc" => DocumentSortDirection.Desc,
        _ => throw new ContextMoleException("invalid_filter", "sort_direction must be asc or desc.")
    };

    private async Task<object> RunAsync(Func<Task<object>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ContextMoleException exception)
        {
            return new ErrorEnvelope(new ToolError(exception.Code, exception.Message, exception.Retryable));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unhandled MCP tool failure");
            return new ErrorEnvelope(new ToolError("internal_error", "The local index request failed unexpectedly.", false));
        }
    }

}

public sealed record McpSearchClause(
    [property: Description("Stable caller-defined ID echoed in matched_clause_ids (1-64 ASCII letters, numbers, dot, underscore, or hyphen).")] string Id,
    [property: Description("Text to match: one token for term/prefix, or one-or-more tokens for phrase.")] string Text,
    [property: Description("Boolean role: must, should, or must_not.")] SearchClauseOccur Occur = SearchClauseOccur.Should,
    [property: Description("Match behavior: exact term, ordered phrase, or token prefix.")] SearchMatchKind Match = SearchMatchKind.Term,
    [property: Description("Optional target fields; omission searches body, title, heading, filename, path, content_name, sheet, and email_subject.")] IReadOnlyList<SearchField>? Fields = null)
{
    public SearchClause ToDomain() => new(Id, Text, Occur, Match, Fields);
}

public sealed record McpSearchFieldWeights(
    [property: Description("Body text weight, default 1.0.")] double Body = 1.0,
    [property: Description("Document title weight, default 3.0.")] double Title = 3.0,
    [property: Description("Section heading weight, default 2.0.")] double Heading = 2.0,
    [property: Description("Root filename weight, default 2.5.")] double Filename = 2.5,
    [property: Description("Authorized source path weight, default 0.5.")] double Path = 0.5,
    [property: Description("Root/attachment/archive-entry name weight, default 2.5.")] double ContentName = 2.5,
    [property: Description("Worksheet name weight, default 1.5.")] double Sheet = 1.5,
    [property: Description("Email subject weight, default 3.0.")] double EmailSubject = 3.0)
{
    public SearchFieldWeights ToDomain() => new(Body, Title, Heading, Filename, Path, ContentName, Sheet, EmailSubject);
}

public sealed record McpSearchBranchWeights(
    [property: Description("Keyword reciprocal-rank-fusion weight, default 1.0.")] double Keyword = 1.0,
    [property: Description("Semantic reciprocal-rank-fusion weight, default 1.0.")] double Semantic = 1.0)
{
    public SearchBranchWeights ToDomain() => new(Keyword, Semantic);
}

public sealed record McpSearchFilters(
    [property: Description("Optional stable root document IDs.")] IReadOnlyList<Guid>? DocumentIds = null,
    [property: Description("Optional stable content IDs returned by search/list_attachments; use for focused follow-ups.")] IReadOnlyList<Guid>? ContentIds = null,
    [property: Description("Optional authorized source-directory prefixes.")] IReadOnlyList<string>? PathPrefixes = null,
    [property: Description("Optional root source extensions such as .msg or pdf.")] IReadOnlyList<string>? RootExtensions = null,
    [property: Description("Optional nested/root content-name extensions; e.g. pdf finds a PDF attachment inside an email or archive.")] IReadOnlyList<string>? ContentExtensions = null,
    [property: Description("Optional inclusive source modified-time lower bound.")] DateTimeOffset? ModifiedFromUtc = null,
    [property: Description("Optional inclusive source modified-time upper bound.")] DateTimeOffset? ModifiedToUtc = null,
    [property: Description("any, root_only, or attachments_only.")] AttachmentScope AttachmentScope = AttachmentScope.Any)
{
    public SearchFilters ToDomain() => new(DocumentIds, ContentIds, PathPrefixes, RootExtensions, ContentExtensions,
        ModifiedFromUtc, ModifiedToUtc, AttachmentScope);
}

public sealed record McpSearchResultOptions(
    [property: Description("Maximum content groups returned, 1-50; default 10.")] int GroupLimit = 10,
    [property: Description("Maximum consolidated passage previews per content group, 1-10; default 1.")] int PreviewsPerGroup = 1,
    [property: Description("Diversity cap per root document, 1-50; default 2.")] int MaxGroupsPerDocument = 2,
    [property: Description("Cosine score below which any preview with a semantic score is marked low_confidence; default 0.25, allowed -1 to 1.")] double SemanticConfidenceThreshold = 0.25,
    [property: Description("False by default so borderline semantic leads remain visible. True hides semantic-only matches below the threshold.")] bool StrictSemanticThreshold = false)
{
    public SearchResultOptions ToDomain() => new(GroupLimit, PreviewsPerGroup, MaxGroupsPerDocument,
        SemanticConfidenceThreshold, StrictSemanticThreshold);
}

public sealed record ToolError(string Code, string Message, bool Retryable);
public sealed record ErrorEnvelope(ToolError Error);
