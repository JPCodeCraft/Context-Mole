using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

using ContextMole.Core;

namespace ContextMole.Broker.Protocol;

public static class BrokerProtocol
{
    public const int MajorVersion = 1;
    public const int MinorVersion = 0;
    public const int MaximumFrameBytes = 32 * 1024 * 1024;
    public static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(5);

    public const string HealthMethod = "health";
    public const string ShutdownMethod = "shutdown";
    public const string DeploymentMismatchCode = "broker_deployment_mismatch";
    public const string DeploymentConflictCode = "broker_deployment_conflict";
    public const string EmbeddingReloadMethod = "embedding.reload";
    public const string EmbeddingStatusMethod = "embedding.status";
    public const string EmbeddingCountTokensMethod = "embedding.count_tokens";
    public const string EmbeddingPassagesMethod = "embedding.passages";
    public const string EmbeddingQueryMethod = "embedding.query";

    public static BrokerDeploymentRelation CompareDeployments(
        string clientVersion,
        string clientDeploymentId,
        string brokerVersion,
        string brokerDeploymentId)
    {
        if (string.Equals(clientDeploymentId, brokerDeploymentId, StringComparison.Ordinal))
            return BrokerDeploymentRelation.Same;
        if (!Version.TryParse(clientVersion, out var parsedClient) ||
            !Version.TryParse(brokerVersion, out var parsedBroker))
            return BrokerDeploymentRelation.Conflict;
        var comparison = parsedClient.CompareTo(parsedBroker);
        return comparison > 0 ? BrokerDeploymentRelation.ClientNewer
            : comparison < 0 ? BrokerDeploymentRelation.BrokerNewer
            : BrokerDeploymentRelation.Conflict;
    }
}

public enum BrokerDeploymentRelation { Same, ClientNewer, BrokerNewer, Conflict }

public static class BrokerToolMethods
{
    public const string ListProjects = "list_projects";
    public const string SearchProject = "search_project";
    public const string ReadPassages = "read_passages";
    public const string GetDocumentInfo = "get_document_info";
    public const string ListDocuments = "list_documents";
    public const string ListAttachments = "list_attachments";
    public const string ResolveLocalFile = "resolve_local_file";
    public const string MaterializeContent = "materialize_content";
}

public static class BrokerJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}

public sealed record BrokerHandshakeRequest(
    int ProtocolMajor,
    int ProtocolMinor,
    string AuthenticationToken,
    string DataDirectoryId,
    string ClientVersion,
    string DeploymentId);

public sealed record BrokerHandshakeResponse(
    bool Accepted,
    int ProtocolMajor,
    int ProtocolMinor,
    string BrokerVersion,
    string DeploymentId,
    IReadOnlyList<string> Capabilities,
    BrokerRpcError? Error = null);

public sealed record BrokerRpcRequest(
    Guid RequestId,
    string Method,
    JsonElement Payload,
    DateTimeOffset DeadlineUtc);

public sealed record BrokerRpcResponse(
    Guid RequestId,
    JsonElement? Result,
    BrokerRpcError? Error);

public sealed record BrokerRpcError(string Code, string Message, bool Retryable);

public sealed record BrokerHealthResponse(
    int ProcessId,
    string Version,
    int ProtocolMajor,
    int ProtocolMinor,
    DateTimeOffset StartedUtc);

public sealed record BrokerEmbeddingStatus(bool IsAvailable, string? UnavailableReason, EmbeddingPolicy? Policy);
public sealed record BrokerCountTokensRequest(string Text);
public sealed record BrokerCountTokensResponse(int Count);
public sealed record BrokerEmbedPassagesRequest(IReadOnlyList<string> Passages);
public sealed record BrokerEmbedQueryRequest(string Query);

public sealed record BrokerSearchProjectRequest(SearchRequest Request);
public sealed record BrokerReadPassagesRequest(
    Guid ProjectId,
    IReadOnlyList<Guid> PassageIds,
    int ContextBefore,
    int ContextAfter);
public sealed record BrokerGetDocumentInfoRequest(Guid ProjectId, Guid DocumentId, Guid? ContentId);
public sealed record BrokerListDocumentsRequest(
    Guid ProjectId,
    string Status,
    IReadOnlyList<string>? Extensions,
    IReadOnlyList<string>? PathPrefixes,
    string? NameQuery,
    DateTimeOffset? ModifiedFromUtc,
    DateTimeOffset? ModifiedToUtc,
    string SortBy,
    string SortDirection,
    int Limit,
    string? Cursor);
public sealed record BrokerListAttachmentsRequest(Guid ProjectId, Guid DocumentId, string? Cursor, int Limit);
public sealed record BrokerResolveLocalFileRequest(Guid ProjectId, Guid DocumentId, Guid? ContentId);
public sealed record BrokerMaterializeContentRequest(Guid ProjectId, Guid ContentId);

public sealed class BrokerRpcException : Exception
{
    public BrokerRpcException(string code, string message, bool retryable, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }
    public bool Retryable { get; }
}

public static class BrokerFrameCodec
{
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, BrokerJson.Options);
        if (bytes.Length > BrokerProtocol.MaximumFrameBytes)
            throw new BrokerRpcException("request_too_large",
                $"The broker request exceeds {BrokerProtocol.MaximumFrameBytes} bytes.", false);

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > BrokerProtocol.MaximumFrameBytes)
            throw new BrokerRpcException("invalid_frame", "The broker sent an invalid frame length.", false);

        var bytes = GC.AllocateUninitializedArray<byte>(length);
        await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, BrokerJson.Options)
                   ?? throw new BrokerRpcException("invalid_frame", "The broker sent an empty JSON frame.", false);
        }
        catch (JsonException exception)
        {
            throw new BrokerRpcException("invalid_frame", "The broker sent malformed JSON.", false, exception);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("The broker connection closed before a frame completed.");
            offset += count;
        }
    }
}
