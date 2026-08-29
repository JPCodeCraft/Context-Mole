namespace ContextMole.Core;

public interface IAppPaths
{
    string DataDirectory { get; }
    string DatabasePath { get; }
    string AssetsDirectory { get; }
    string LogsDirectory { get; }
    string TempDirectory { get; }
}

public interface ICpuUsageSettings
{
    CpuUsageProfile Profile { get; }
    int LogicalProcessorCount { get; }
    int ThreadLimit { get; }
    int MaximumThreadLimit { get; }
    event EventHandler? Changed;
    void SetProfile(CpuUsageProfile profile);
    bool RefreshFromDisk() => false;
}

public interface IEmbeddingModelSettings
{
    EmbeddingModelChoice Model { get; }
    event EventHandler? Changed;
    void SetModel(EmbeddingModelChoice model);
    bool RefreshFromDisk();
}

public interface ICpuWorkerLease : IDisposable
{
    IDisposable Activate();
}

public interface ICpuFullCapacityLease : IDisposable
{
    int ThreadCount { get; }
}

public interface IGlobalCpuBudget
{
    int MaximumWorkerCount { get; }
    ValueTask<ICpuWorkerLease> AcquireWorkerAsync(CancellationToken cancellationToken);
    ValueTask<ICpuFullCapacityLease> AcquireFullCapacityAsync(CancellationToken cancellationToken);
}

public interface IDocumentExtractor
{
    IReadOnlyCollection<string> Extensions { get; }
    Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken);
}

public interface IContentMaterializer
{
    Task<MaterializedContent> MaterializeAsync(Guid projectId, Guid contentId,
        CancellationToken cancellationToken = default);
}

public interface IOcrEngine
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    bool AreAssetsReady => IsAvailable;
    Task PrepareAssetsAsync(CancellationToken cancellationToken = default) =>
        EnsureAvailableAsync(cancellationToken);
    Task EnsureAvailableAsync(CancellationToken cancellationToken = default);
    Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken);
}

public interface IEmbeddingGenerator : IAsyncDisposable
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    EmbeddingPolicy? Policy { get; }
    Task ReloadAsync(CancellationToken cancellationToken = default);
    int CountTokens(string text);
    Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken);
    Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken);
}

public interface IVectorIndex
{
    long SearchGeneration { get; }
    IReadOnlyList<VectorMatch> Search(ReadOnlySpan<float> query, int count, SearchFilters? filters = null);
}

public interface IVectorIndexFactory
{
    IVectorIndex Create(VectorSnapshot snapshot);
}

public interface IIndexWriter
{
    Task Ready { get; }
    Task<Guid> CreateProjectAsync(CreateProjectRequest request, CancellationToken cancellationToken = default);
    Task UpdateProjectAsync(UpdateProjectRequest request, CancellationToken cancellationToken = default);
    /// <summary>
    /// Atomically changes the project's pause state. Pausing also releases and requeues the project's
    /// running jobs and discards their partial staging revisions; queued retry schedules are preserved.
    /// </summary>
    Task SetProjectPausedAsync(Guid projectId, bool paused, CancellationToken cancellationToken = default);
    Task RequestReindexAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task RequestEmbeddingRefreshAsync(Guid projectId, EmbeddingPolicy targetPolicy, bool retryFailed,
        CancellationToken cancellationToken = default);
    Task<RetryFailedFilesResult> RetryFailedFilesAsync(Guid projectId,
        CancellationToken cancellationToken = default);
    Task RemoveProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ObservationResult> ObserveFileAsync(FileObservation observation, CancellationToken cancellationToken = default);
    Task HandleRenamedAsync(Guid projectId, Guid folderId, string oldPath, string newPath, CancellationToken cancellationToken = default);
    Task HandleDeletedAsync(Guid projectId, Guid folderId, string path, CancellationToken cancellationToken = default);
    Task CompleteReconciliationAsync(Guid projectId, Guid folderId, string token, CancellationToken cancellationToken = default);
    Task<IndexJobLease?> LeaseNextJobAsync(TimeSpan leaseDuration, CancellationToken cancellationToken = default);
    Task<BeginRevisionResult> BeginRevisionAsync(IndexJobLease job, string sha256, long size, DateTimeOffset modifiedUtc, CancellationToken cancellationToken = default);
    Task<bool> CommitRevisionAsync(IndexCommitRequest request, CancellationToken cancellationToken = default);
    Task<EmbeddingRefreshSource?> LoadEmbeddingRefreshSourceAsync(IndexJobLease job,
        CancellationToken cancellationToken = default);
    Task<bool> CommitEmbeddingRefreshAsync(EmbeddingRefreshCommitRequest request,
        CancellationToken cancellationToken = default);
    Task FailJobAsync(IndexJobLease job, string code, string message, bool retryable, CancellationToken cancellationToken = default);
}

public interface ISearchStore
{
    Task<bool> IsInitializedAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectFileTypeCount>> ListProjectFileTypeCountsAsync(Guid projectId,
        CancellationToken cancellationToken = default);
    Task<DocumentListResponse> ListDocumentsAsync(DocumentListRequest request,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectErrorInfo>> ListProjectErrorsAsync(Guid projectId, int limit, CancellationToken cancellationToken = default);
    Task<KeywordSearchPage> KeywordSearchAsync(Guid projectId, string ftsQuery, int count, SearchFilters? filters, CancellationToken cancellationToken = default);
    Task<KeywordSearchPage> KeywordSearchAsync(Guid projectId, string ftsQuery, int count, SearchFilters? filters,
        SearchFieldWeights fieldWeights, CancellationToken cancellationToken = default) =>
        KeywordSearchAsync(projectId, ftsQuery, count, filters, cancellationToken);
    Task<KeywordSearchPage> KeywordSearchAsync(Guid projectId, string ftsQuery, int count, int offset,
        SearchFilters? filters, SearchFieldWeights fieldWeights, CancellationToken cancellationToken = default) =>
        offset == 0
            ? KeywordSearchAsync(projectId, ftsQuery, count, filters, fieldWeights, cancellationToken)
            : Task.FromResult(new KeywordSearchPage(0, []));
    Task<VectorSnapshotMetadata> LoadVectorSnapshotMetadataAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<VectorSnapshotMetadata> LoadVectorSnapshotMetadataAsync(Guid projectId, EmbeddingPolicy targetPolicy,
        CancellationToken cancellationToken = default) =>
        LoadVectorSnapshotMetadataAsync(projectId, cancellationToken);
    Task<VectorSnapshot> LoadVectorSnapshotAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<VectorSnapshot> LoadVectorSnapshotAsync(Guid projectId, EmbeddingPolicy targetPolicy,
        CancellationToken cancellationToken = default) =>
        LoadVectorSnapshotAsync(projectId, cancellationToken);
    IAsyncEnumerable<VectorEntry> StreamVectorEntriesAsync(Guid projectId, long expectedGeneration, SearchFilters? filters,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<VectorEntry> StreamVectorEntriesAsync(Guid projectId, long expectedGeneration,
        EmbeddingPolicy targetPolicy,
        SearchFilters? filters, CancellationToken cancellationToken = default) =>
        StreamVectorEntriesAsync(projectId, expectedGeneration, filters, cancellationToken);
    Task<IReadOnlyList<SearchCandidate>> LoadCandidatesAsync(Guid projectId, IReadOnlyCollection<Guid> passageIds,
        long expectedGeneration, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PassageInfo>> ReadPassagesAsync(Guid projectId, IReadOnlyCollection<Guid> passageIds, int contextBefore, int contextAfter, CancellationToken cancellationToken = default);
    Task<DocumentInfo?> GetDocumentInfoAsync(Guid projectId, Guid documentId, Guid? contentId, CancellationToken cancellationToken = default);
    Task<AttachmentPage> ListAttachmentsAsync(Guid projectId, Guid documentId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<ResolvedLocalFile?> ResolveLocalFileAsync(Guid projectId, Guid documentId, Guid? contentId, CancellationToken cancellationToken = default);
    Task<IndexedContentMaterialization?> GetContentMaterializationAsync(Guid projectId, Guid contentId,
        CancellationToken cancellationToken = default);
}
