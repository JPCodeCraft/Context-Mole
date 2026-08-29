using System.Text.Json.Serialization;

namespace ContextMole.Core;

public enum ProjectState
{
    Active,
    Paused,
    Removing
}

public enum CpuUsageProfile
{
    Light,
    Normal,
    Heavy
}

public enum EmbeddingModelChoice
{
    Granite311M,
    Granite97M
}

public enum IndexJobKind
{
    Index,
    Reindex,
    EmbeddingRefresh
}

public enum ExtractionMethod
{
    NativeText,
    Ocr,
    Html,
    Markdown,
    Email,
    Attachment,
    Unsupported
}

public enum LocationKind
{
    Document,
    Page,
    Sheet,
    Slide,
    Structure,
    EmailPart,
    ImageFrame
}

public enum AttachmentScope
{
    Any,
    RootOnly,
    AttachmentsOnly
}

public enum SearchMode
{
    Hybrid,
    Keyword,
    Semantic
}

public enum SearchClauseOccur
{
    Must,
    Should,
    MustNot
}

public enum SearchMatchKind
{
    Term,
    Phrase,
    Prefix
}

public enum SearchField
{
    Body,
    Title,
    Heading,
    Filename,
    Path,
    ContentName,
    Sheet,
    EmailSubject
}

public enum DocumentInventoryStatus
{
    Indexed,
    Pending,
    Processing,
    Paused,
    Error
}

public enum DocumentStatusFilter
{
    All,
    Indexed,
    Pending,
    Processing,
    Paused,
    Error
}

public enum DocumentSortField
{
    FileName,
    SourcePath,
    ModifiedUtc,
    LastIndexedUtc,
    Status
}

public enum DocumentSortDirection
{
    Asc,
    Desc
}

public sealed record ProjectFolderInfo(Guid Id, string Path);

public sealed record ProjectFileTypeCount(string Extension, int Count);

public enum ProjectWorkPhase
{
    Ready,
    Queued,
    RetryScheduled,
    Indexing,
    Retrying
}

/// <summary>
/// UI-facing details for unfinished indexing work. This is kept out of the public MCP payload so the
/// existing list-projects schema remains stable.
/// </summary>
public sealed record ProjectWorkSummary(
    int QueuedCount,
    int RetryScheduledCount,
    int ProcessingCount,
    int RunningRetryCount,
    DateTimeOffset? NextRetryUtc)
{
    public ProjectWorkPhase Phase => RunningRetryCount > 0 ? ProjectWorkPhase.Retrying
        : ProcessingCount > 0 ? ProjectWorkPhase.Indexing
        : QueuedCount > 0 && QueuedCount == RetryScheduledCount ? ProjectWorkPhase.RetryScheduled
        : QueuedCount > 0 ? ProjectWorkPhase.Queued
        : ProjectWorkPhase.Ready;
}

public sealed record ProjectSummary(
    Guid Id,
    string Name,
    ProjectState State,
    IReadOnlyList<ProjectFolderInfo> Folders,
    long SearchGeneration,
    int DocumentCount,
    int PendingCount,
    int IndexedCount,
    int ErrorCount,
    DateTimeOffset? LastCompletedUtc,
    string? CurrentFile = null)
{
    [JsonIgnore]
    public ProjectWorkSummary Work { get; init; } = new(
        Math.Max(0, PendingCount - (CurrentFile is null ? 0 : 1)),
        0,
        CurrentFile is null ? 0 : 1,
        0,
        null);
}

public sealed record ProjectErrorInfo(
    long Id,
    Guid ProjectId,
    Guid? DocumentId,
    string Code,
    string Message,
    bool Retryable,
    int Attempt,
    DateTimeOffset CreatedUtc,
    string? SourcePath);

public sealed record DocumentListRequest(
    Guid ProjectId,
    DocumentStatusFilter Status = DocumentStatusFilter.All,
    IReadOnlyList<string>? Extensions = null,
    IReadOnlyList<string>? PathPrefixes = null,
    string? NameQuery = null,
    DateTimeOffset? ModifiedFromUtc = null,
    DateTimeOffset? ModifiedToUtc = null,
    DocumentSortField SortBy = DocumentSortField.FileName,
    DocumentSortDirection SortDirection = DocumentSortDirection.Asc,
    int Limit = 100,
    string? Cursor = null);

public sealed record DocumentInventoryItem(
    Guid DocumentId,
    Guid FolderId,
    string SourcePath,
    string FileName,
    string FileType,
    string? MimeType,
    long SizeBytes,
    DateTimeOffset ModifiedUtc,
    DocumentInventoryStatus Status,
    int ContentCount,
    int AttachmentCount,
    int ExtractedPassageCount,
    int ErrorCount,
    string? ErrorSummary,
    string? IndexedFingerprint,
    Guid? IndexRevisionId,
    DateTimeOffset? LastIndexedUtc);

public sealed record DocumentListResponse(
    Guid ProjectId,
    long SearchGeneration,
    int ReturnedCount,
    IReadOnlyList<DocumentInventoryItem> Documents,
    string? NextCursor);

public sealed record CreateProjectRequest(string Name, IReadOnlyList<string> Folders);

public sealed record UpdateProjectRequest(Guid ProjectId, string Name, IReadOnlyList<string> Folders);

public sealed record FileObservation(
    Guid ProjectId,
    Guid FolderId,
    string Path,
    long Size,
    DateTimeOffset ModifiedUtc,
    string? ReconciliationToken = null,
    bool Force = false);

public sealed record ObservationResult(Guid DocumentId, long ObservationEpoch, bool Queued);

public sealed record RetryFailedFilesResult(int QueuedCount, int AlreadyPendingCount);

public sealed record IndexJobLease(
    Guid JobId,
    Guid ProjectId,
    Guid DocumentId,
    Guid FolderId,
    string SourcePath,
    string Extension,
    long ExpectedObservationEpoch,
    IndexJobKind Kind,
    int Attempt);

public sealed record BeginRevisionResult(
    bool ShouldExtract,
    bool IsStale,
    Guid? RevisionId,
    string? Reason = null);

public sealed record SourceLocation(
    LocationKind Kind,
    int? Page = null,
    string? Sheet = null,
    string? CellRange = null,
    int? Slide = null,
    string? StructurePath = null,
    string? EmailPart = null,
    int? ImageFrame = null);

public sealed record ExtractedSection(
    string Text,
    SourceLocation Location,
    ExtractionMethod Method,
    double? OcrConfidence = null,
    string? Heading = null);

public sealed record ExtractedNode(
    string Name,
    string? MimeType,
    string Relationship,
    IReadOnlyList<ExtractedSection> Sections,
    IReadOnlyList<ExtractedNode> Attachments,
    string Status = "indexed",
    string? Title = null)
{
    public static ExtractedNode Empty(string name, string relationship = "root") =>
        new(name, null, relationship, [], []);
}

public sealed record ExtractionError(
    string Code,
    string Message,
    bool Retryable,
    string? ItemName = null);

public sealed record ExtractionResult(
    ExtractedNode Root,
    IReadOnlyList<ExtractionError> Errors)
{
    public static ExtractionResult Failure(string fileName, string code, string message, bool retryable = false) =>
        new(ExtractedNode.Empty(fileName), [new ExtractionError(code, message, retryable, fileName)]);
}

public sealed record ExtractionRequest(
    string SourcePath,
    int MaxDepth = 5,
    int MaxAttachments = 1000,
    long MaxAttachmentBytes = 250L * 1024 * 1024,
    long MaxAggregateBytes = 1024L * 1024 * 1024);

public sealed record OcrRequest(
    ReadOnlyMemory<byte> ImageBytes,
    string Extension,
    TimeSpan Timeout);

public sealed record OcrResult(string Text, double? Confidence, bool TimedOut = false);

public sealed record EmbeddingPolicy(
    string ModelId,
    string Revision,
    string ModelSha256,
    string TokenizerSha256,
    string Precision,
    int SourceDimensions,
    int Dimensions,
    string Pooling,
    string Normalization)
{
    public string Key => string.Join(':', ModelId, Revision, ModelSha256, TokenizerSha256, Precision,
        SourceDimensions, Dimensions, Pooling, Normalization);
}

public sealed record EmbeddingBatch(
    IReadOnlyList<float[]> Vectors,
    EmbeddingPolicy Policy);

public sealed record QueryEmbedding(
    float[] Vector,
    EmbeddingPolicy Policy);

public sealed record ContentNodeDraft(
    Guid Id,
    Guid? ParentId,
    int Ordinal,
    string Name,
    string? MimeType,
    string Relationship,
    int Depth,
    string Status = "indexed");

public sealed record PassageDraft(
    Guid Id,
    Guid ContentId,
    int Ordinal,
    string DisplayText,
    string SearchText,
    SourceLocation Location,
    ExtractionMethod ExtractionMethod,
    double? OcrConfidence,
    float[]? Embedding,
    string? BodySearchText = null,
    string? Title = null,
    string? Heading = null,
    string? FileName = null,
    string? SourcePath = null,
    string? ContentName = null,
    string? EmailSubject = null);

public sealed record IndexCommitRequest(
    Guid JobId,
    Guid ProjectId,
    Guid DocumentId,
    Guid RevisionId,
    long ExpectedObservationEpoch,
    string Sha256,
    long Size,
    DateTimeOffset ModifiedUtc,
    IReadOnlyList<ContentNodeDraft> ContentNodes,
    IReadOnlyList<PassageDraft> Passages,
    EmbeddingPolicy? EmbeddingPolicy,
    IReadOnlyList<ExtractionError> Errors);

public sealed record EmbeddingRefreshPassage(
    Guid PassageId,
    string SearchText);

public sealed record EmbeddingRefreshSource(
    Guid RevisionId,
    IReadOnlyList<EmbeddingRefreshPassage> Passages);

public sealed record PassageEmbedding(
    Guid PassageId,
    float[] Vector);

public sealed record EmbeddingRefreshCommitRequest(
    Guid JobId,
    Guid ProjectId,
    Guid DocumentId,
    Guid RevisionId,
    long ExpectedObservationEpoch,
    IReadOnlyList<PassageEmbedding> Embeddings,
    EmbeddingPolicy Policy);

public sealed record SearchClause(
    string Id,
    string Text,
    SearchClauseOccur Occur = SearchClauseOccur.Should,
    SearchMatchKind Match = SearchMatchKind.Term,
    IReadOnlyList<SearchField>? Fields = null);

public sealed record SearchFieldWeights(
    double Body = 1.0,
    double Title = 3.0,
    double Heading = 2.0,
    double Filename = 2.5,
    double Path = 0.5,
    double ContentName = 2.5,
    double Sheet = 1.5,
    double EmailSubject = 3.0);

public sealed record SearchBranchWeights(double Keyword = 1.0, double Semantic = 1.0);

public sealed record SearchFilters(
    IReadOnlyList<Guid>? DocumentIds = null,
    IReadOnlyList<Guid>? ContentIds = null,
    IReadOnlyList<string>? PathPrefixes = null,
    IReadOnlyList<string>? RootExtensions = null,
    IReadOnlyList<string>? ContentExtensions = null,
    DateTimeOffset? ModifiedFromUtc = null,
    DateTimeOffset? ModifiedToUtc = null,
    AttachmentScope AttachmentScope = AttachmentScope.Any);

public sealed record SearchResultOptions(
    int GroupLimit = 10,
    int PreviewsPerGroup = 1,
    int MaxGroupsPerDocument = 2,
    double SemanticConfidenceThreshold = 0.25,
    bool StrictSemanticThreshold = false);

public sealed record SearchRequest(
    Guid ProjectId,
    SearchMode Mode = SearchMode.Hybrid,
    string? SemanticQuery = null,
    IReadOnlyList<SearchClause>? Clauses = null,
    int? MinimumShouldMatch = null,
    SearchFieldWeights? FieldWeights = null,
    SearchBranchWeights? BranchWeights = null,
    SearchFilters? Filters = null,
    SearchResultOptions? ResultOptions = null);

public sealed record SearchCandidate(
    Guid PassageId,
    Guid DocumentId,
    Guid ContentId,
    string DisplayText,
    string SourcePath,
    string FileName,
    string FileType,
    DateTimeOffset ModifiedUtc,
    SourceLocation Location,
    IReadOnlyList<string> AttachmentChain,
    ExtractionMethod ExtractionMethod,
    double? OcrConfidence,
    double? KeywordScore = null,
    double? SemanticScore = null,
    int? KeywordRank = null,
    int? SemanticRank = null,
    int Ordinal = 0,
    string? BodySearchText = null,
    string? Title = null,
    string? Heading = null,
    string? ContentName = null,
    string? ContentMimeType = null,
    string? ContentExtension = null,
    string? EmailSubject = null);

public sealed record SearchResultItem(
    Guid PassageId,
    Guid DocumentId,
    Guid ContentId,
    string Excerpt,
    bool Truncated,
    string SourcePath,
    string FileName,
    string FileType,
    DateTimeOffset ModifiedUtc,
    SourceLocation Location,
    IReadOnlyList<string> AttachmentChain,
    ExtractionMethod ExtractionMethod,
    double? OcrConfidence,
    double FusedScore,
    double? KeywordScore,
    double? SemanticScore,
    int? KeywordRank,
    int? SemanticRank,
    bool LowConfidence,
    IReadOnlyList<string> MatchedClauseIds,
    IReadOnlyList<SearchField> MatchedFields,
    IReadOnlyList<Guid> ConsolidatedPassageIds);

public sealed record SearchResultGroup(
    Guid DocumentId,
    Guid ContentId,
    string SourcePath,
    string FileName,
    string RootExtension,
    string ContentName,
    string? ContentMimeType,
    string? ContentExtension,
    IReadOnlyList<string> AttachmentChain,
    double Score,
    int TotalMatchCount,
    int CollapsedMatchCount,
    IReadOnlyList<SearchResultItem> Previews);

public sealed record SearchSuppressedSource(
    Guid DocumentId,
    string SourcePath,
    string FileName,
    int MatchedContentGroups,
    int ReturnedContentGroups,
    int SuppressedContentGroups);

public sealed record SearchWarning(string Code, string Message);

public sealed record SearchBranchCandidateDepths(
    int Keyword,
    int OptionalKeywordBoost,
    int Semantic);

public sealed record SearchResponse(
    SearchMode RequestedMode,
    string ActualMode,
    IReadOnlyList<SearchWarning> Warnings,
    long SearchGeneration,
    int CandidateMatchCount,
    SearchBranchCandidateDepths InspectedCandidateDepths,
    bool CandidateLimitReached,
    int ReturnedGroupCount,
    int SuppressedGroupCount,
    IReadOnlyList<SearchSuppressedSource> SuppressedSources,
    IReadOnlyList<SearchResultGroup> Results);

public sealed record PassageInfo(
    Guid PassageId,
    Guid DocumentId,
    Guid ContentId,
    int Ordinal,
    string Text,
    string SourcePath,
    string FileName,
    string FileType,
    DateTimeOffset ModifiedUtc,
    SourceLocation Location,
    IReadOnlyList<string> AttachmentChain,
    ExtractionMethod ExtractionMethod,
    double? OcrConfidence,
    bool Requested,
    string? ErrorCode = null);

public sealed record DocumentInfo(
    Guid DocumentId,
    Guid ProjectId,
    string SourcePath,
    string FileName,
    string FileType,
    long Size,
    DateTimeOffset ModifiedUtc,
    string? Sha256,
    bool Searchable,
    bool Available,
    Guid? ActiveRevisionId,
    int PassageCount,
    int AttachmentCount,
    IReadOnlyDictionary<ExtractionMethod, int> ExtractionSummary,
    IReadOnlyList<ProjectErrorInfo> Errors);

public sealed record AttachmentInfo(
    Guid ContentId,
    Guid? ParentContentId,
    int Depth,
    int Ordinal,
    string Name,
    string? MimeType,
    string Relationship,
    string Status);

public sealed record AttachmentPage(IReadOnlyList<AttachmentInfo> Items, string? NextCursor);

public sealed record ResolvedLocalFile(
    Guid DocumentId,
    Guid? ContentId,
    string SourcePath,
    bool Available,
    bool Resident,
    IReadOnlyList<string> AttachmentChain);

public sealed record IndexedMaterializationNode(
    Guid ContentId,
    Guid? ParentContentId,
    int Ordinal,
    string Name,
    string? MimeType,
    string Relationship,
    int Depth,
    string Status);

public sealed record IndexedContentMaterialization(
    Guid ProjectId,
    Guid DocumentId,
    Guid ContentId,
    string SourcePath,
    string ProjectFolderPath,
    long IndexedSizeBytes,
    DateTimeOffset IndexedModifiedUtc,
    string IndexFingerprint,
    Guid IndexRevisionId,
    IReadOnlyList<IndexedMaterializationNode> ContentChain);

public sealed record MaterializedContent(
    string LocalPath,
    string SourcePath,
    IReadOnlyList<string> AttachmentChain,
    string? MimeType,
    long SizeBytes,
    string? Sha256,
    bool Temporary,
    Guid IndexRevisionId,
    string IndexFingerprint);

public sealed record VectorEntry(
    Guid PassageId,
    Guid DocumentId,
    Guid ContentId,
    string SourcePath,
    string Extension,
    DateTimeOffset ModifiedUtc,
    bool IsAttachment,
    float[] Vector,
    string? ContentExtension = null);

public sealed record VectorSnapshot(
    long SearchGeneration,
    EmbeddingPolicy? Policy,
    IReadOnlyList<VectorEntry> Entries,
    bool RequiresStreaming = false,
    string? Warning = null);

public sealed record VectorSnapshotMetadata(
    long SearchGeneration,
    EmbeddingPolicy? Policy,
    long EntryCount,
    bool RequiresStreaming = false,
    string? Warning = null,
    bool IsComplete = true);

public sealed record KeywordSearchPage(long SearchGeneration, IReadOnlyList<SearchCandidate> Candidates);

public sealed record VectorMatch(Guid PassageId, double Score, int Rank);

public sealed class ContextMoleException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
