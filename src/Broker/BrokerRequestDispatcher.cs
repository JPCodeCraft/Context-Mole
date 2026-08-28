using System.Text.Json;

using ContextMole.Broker.Protocol;
using ContextMole.Core;

using Microsoft.Extensions.Hosting;

namespace ContextMole.Broker;

public sealed class BrokerRequestDispatcher(
    ISearchStore store,
    IContentMaterializer materializer,
    IAppPaths paths,
    BrokerSearchRuntimeManager searchRuntime,
    BrokerActivityTracker activity,
    IHostApplicationLifetime applicationLifetime)
{
    private static readonly DateTimeOffset ProcessStartedUtc = ReadProcessStartedUtc();
    private readonly ISearchStore _store = store;
    private readonly IContentMaterializer _materializer = materializer;
    private readonly IAppPaths _paths = paths;
    private readonly BrokerSearchRuntimeManager _searchRuntime = searchRuntime;
    private readonly BrokerActivityTracker _activity = activity;
    private readonly IHostApplicationLifetime _applicationLifetime = applicationLifetime;
    private readonly SemaphoreSlim _ordinaryReads = new(8, 8);
    private readonly SemaphoreSlim _materializations = new(2, 2);

    public async Task<JsonElement> DispatchAsync(BrokerRpcRequest request, CancellationToken cancellationToken)
    {
        using var activity = _activity.BeginRequest();
        if (request.Method == BrokerProtocol.HealthMethod)
            return Serialize(new BrokerHealthResponse(Environment.ProcessId,
                typeof(BrokerRequestDispatcher).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                BrokerProtocol.MajorVersion, BrokerProtocol.MinorVersion,
                ProcessStartedUtc));
        if (request.Method == BrokerProtocol.ShutdownMethod)
        {
            _ = StopAfterResponseAsync();
            return Serialize(new { accepted = true });
        }

        var gate = request.Method == BrokerToolMethods.MaterializeContent
            ? _materializations
            : _ordinaryReads;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return request.Method switch
            {
                BrokerToolMethods.ListProjects => Serialize(await ListProjectsAsync(cancellationToken)
                    .ConfigureAwait(false)),
                BrokerToolMethods.SearchProject => Serialize(await SearchProjectAsync(
                    Deserialize<BrokerSearchProjectRequest>(request.Payload), cancellationToken).ConfigureAwait(false)),
                BrokerToolMethods.ReadPassages => Serialize(await ReadPassagesAsync(
                    Deserialize<BrokerReadPassagesRequest>(request.Payload), cancellationToken).ConfigureAwait(false)),
                BrokerToolMethods.GetDocumentInfo => Serialize(await GetDocumentInfoAsync(
                    Deserialize<BrokerGetDocumentInfoRequest>(request.Payload), cancellationToken).ConfigureAwait(false)),
                BrokerToolMethods.ListDocuments => Serialize(await ListDocumentsAsync(
                    Deserialize<BrokerListDocumentsRequest>(request.Payload), cancellationToken).ConfigureAwait(false)),
                BrokerToolMethods.ListAttachments => Serialize(await ListAttachmentsAsync(
                    Deserialize<BrokerListAttachmentsRequest>(request.Payload), cancellationToken).ConfigureAwait(false)),
                BrokerToolMethods.ResolveLocalFile => Serialize(await ResolveLocalFileAsync(
                    Deserialize<BrokerResolveLocalFileRequest>(request.Payload), cancellationToken).ConfigureAwait(false)),
                BrokerToolMethods.MaterializeContent => Serialize(await MaterializeContentAsync(
                    Deserialize<BrokerMaterializeContentRequest>(request.Payload), cancellationToken).ConfigureAwait(false)),
                BrokerProtocol.EmbeddingReloadMethod => await ReloadEmbeddingAsync(cancellationToken)
                    .ConfigureAwait(false),
                BrokerProtocol.EmbeddingStatusMethod => Serialize(await _searchRuntime
                    .GetEmbeddingStatusAsync(cancellationToken).ConfigureAwait(false)),
                BrokerProtocol.EmbeddingCountTokensMethod => Serialize(await CountTokensAsync(
                    Deserialize<BrokerCountTokensRequest>(request.Payload), cancellationToken).ConfigureAwait(false)),
                BrokerProtocol.EmbeddingPassagesMethod => Serialize(await EmbedPassagesAsync(
                    Deserialize<BrokerEmbedPassagesRequest>(request.Payload), cancellationToken).ConfigureAwait(false)),
                BrokerProtocol.EmbeddingQueryMethod => Serialize(await EmbedQueryAsync(
                    Deserialize<BrokerEmbedQueryRequest>(request.Payload), cancellationToken).ConfigureAwait(false)),
                _ => throw new ContextMoleException("unknown_method",
                    $"The broker method '{request.Method}' is not supported.", false)
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _store.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SearchResponse> SearchProjectAsync(BrokerSearchProjectRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _searchRuntime.SearchAsync(request.Request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PassageInfo>> ReadPassagesAsync(BrokerReadPassagesRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (request.PassageIds.Count is < 1 or > 50)
            throw new ContextMoleException("invalid_request", "passage_ids must contain between 1 and 50 IDs.");
        if (request.ContextBefore is < 0 or > 3 || request.ContextAfter is < 0 or > 3)
            throw new ContextMoleException("invalid_request",
                "context_before and context_after must be between 0 and 3.");
        return await _store.ReadPassagesAsync(request.ProjectId, request.PassageIds, request.ContextBefore,
            request.ContextAfter, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DocumentInfo> GetDocumentInfoAsync(BrokerGetDocumentInfoRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _store.GetDocumentInfoAsync(request.ProjectId, request.DocumentId, request.ContentId,
                   cancellationToken).ConfigureAwait(false)
               ?? throw new ContextMoleException("document_not_found",
                   "The indexed document or content node was not found.");
    }

    private async Task<DocumentListResponse> ListDocumentsAsync(BrokerListDocumentsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _store.ListDocumentsAsync(new DocumentListRequest(request.ProjectId,
                ParseDocumentStatus(request.Status), request.Extensions, request.PathPrefixes, request.NameQuery,
                request.ModifiedFromUtc, request.ModifiedToUtc, ParseDocumentSortField(request.SortBy),
                ParseDocumentSortDirection(request.SortDirection), request.Limit, request.Cursor), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ContextMoleException exception) when (exception.Code is "not_initialized" or "schema_incompatible")
        {
            throw new ContextMoleException("index_unavailable",
                "The local document index is unavailable or incompatible.", true);
        }
    }

    private async Task<AttachmentPage> ListAttachmentsAsync(BrokerListAttachmentsRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (request.Limit is < 1 or > 500)
            throw new ContextMoleException("invalid_request", "limit must be between 1 and 500.");
        return await _store.ListAttachmentsAsync(request.ProjectId, request.DocumentId, request.Cursor,
            request.Limit, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResolvedLocalFile> ResolveLocalFileAsync(BrokerResolveLocalFileRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _store.ResolveLocalFileAsync(request.ProjectId, request.DocumentId, request.ContentId,
                   cancellationToken).ConfigureAwait(false)
               ?? throw new ContextMoleException("document_not_found",
                   "The indexed document or content node was not found.");
    }

    private async Task<MaterializedContent> MaterializeContentAsync(BrokerMaterializeContentRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _materializer.MaterializeAsync(request.ProjectId, request.ContentId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonElement> ReloadEmbeddingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _searchRuntime.RefreshEmbeddingMetadata();
        return Serialize(new { refreshed = true });
    }

    private async Task<BrokerCountTokensResponse> CountTokensAsync(BrokerCountTokensRequest request,
        CancellationToken cancellationToken) => new(await _searchRuntime.CountTokensAsync(request.Text,
        cancellationToken).ConfigureAwait(false));

    private Task<EmbeddingBatch> EmbedPassagesAsync(BrokerEmbedPassagesRequest request,
        CancellationToken cancellationToken) =>
        _searchRuntime.EmbedPassagesAsync(request.Passages, cancellationToken);

    private Task<QueryEmbedding> EmbedQueryAsync(BrokerEmbedQueryRequest request,
        CancellationToken cancellationToken) =>
        _searchRuntime.EmbedQueryAsync(request.Query, cancellationToken);

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!await _store.IsInitializedAsync(cancellationToken).ConfigureAwait(false))
            throw File.Exists(_paths.DatabasePath)
                ? new ContextMoleException("schema_incompatible",
                    "The local index database schema is incompatible with this MCP server.")
                : new ContextMoleException("not_initialized",
                    "The local index database has not been initialized by the desktop application.");
    }

    private async Task StopAfterResponseAsync()
    {
        await Task.Delay(100).ConfigureAwait(false);
        _applicationLifetime.StopApplication();
    }

    private static T Deserialize<T>(JsonElement payload)
    {
        try
        {
            return payload.Deserialize<T>(BrokerJson.Options)
                   ?? throw new ContextMoleException("invalid_request", "The broker request payload is required.");
        }
        catch (JsonException exception)
        {
            throw new ContextMoleException("invalid_request",
                $"The broker request payload is invalid: {exception.Message}");
        }
    }

    private static JsonElement Serialize<T>(T value) => JsonSerializer.SerializeToElement(value, BrokerJson.Options);

    private static DateTimeOffset ReadProcessStartedUtc()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }

    private static DocumentStatusFilter ParseDocumentStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "all" => DocumentStatusFilter.All,
        "indexed" => DocumentStatusFilter.Indexed,
        "pending" => DocumentStatusFilter.Pending,
        "processing" => DocumentStatusFilter.Processing,
        "paused" => DocumentStatusFilter.Paused,
        "error" => DocumentStatusFilter.Error,
        _ => throw new ContextMoleException("invalid_filter",
            "status must be all, indexed, pending, processing, paused, or error.")
    };

    private static DocumentSortField ParseDocumentSortField(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "file_name" => DocumentSortField.FileName,
        "source_path" => DocumentSortField.SourcePath,
        "modified_utc" => DocumentSortField.ModifiedUtc,
        "last_indexed_utc" => DocumentSortField.LastIndexedUtc,
        "status" => DocumentSortField.Status,
        _ => throw new ContextMoleException("invalid_filter",
            "sort_by must be file_name, source_path, modified_utc, last_indexed_utc, or status.")
    };

    private static DocumentSortDirection ParseDocumentSortDirection(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "asc" => DocumentSortDirection.Asc,
            "desc" => DocumentSortDirection.Desc,
            _ => throw new ContextMoleException("invalid_filter", "sort_direction must be asc or desc.")
        };
}
