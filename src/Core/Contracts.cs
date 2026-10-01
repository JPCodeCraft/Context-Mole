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
    /// <summary>
    /// Prepares a rendered image only after OCR admission, so queued documents need not retain
    /// full-resolution page rasters. Engines without admission can prepare it immediately.
    /// </summary>
    async Task<OcrResult> RecognizeAsync(Func<CancellationToken, Task<OcrRequest>> prepareRequest,
        CancellationToken cancellationToken) =>
        await RecognizeAsync(await prepareRequest(cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
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
    /// <summary>Returns false only when the database is absent; incompatible or unreadable indexes throw.</summary>
    Task<bool> IsInitializedAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(CancellationToken cancellationToken = default);
    async Task<long> GetSearchGenerationAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        (await KeywordSearchAsync(projectId, string.Empty, 1, null, cancellationToken).ConfigureAwait(false)).SearchGeneration;
    async Task<string?> GetProjectFolderPathAsync(Guid projectId, Guid folderId,
        CancellationToken cancellationToken = default) =>
        (await ListProjectsAsync(cancellationToken).ConfigureAwait(false))
        .FirstOrDefault(project => project.Id == projectId)?.Folders
        .FirstOrDefault(folder => folder.Id == folderId)?.Path;
    Task<IReadOnlyList<ProjectFileTypeCount>> ListProjectFileTypeCountsAsync(Guid projectId,
        CancellationToken cancellationToken = default);
    Task<DocumentListResponse> ListDocumentsAsync(DocumentListRequest request,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectErrorInfo>> ListProjectErrorsAsync(Guid projectId, int limit, CancellationToken cancellationToken = default, int offset = 0);
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
    async Task<KeywordBranchSnapshot> LoadKeywordBranchesAsync(Guid projectId, string mainQuery,
        string optionalQuery, int candidateLimit, SearchFilters? filters, SearchFieldWeights weights,
        SearchScope scope, CancellationToken cancellationToken = default)
    {
        var generation = 0L;
        async Task<(IReadOnlyList<SearchCandidate> Candidates, bool Capped)> Load(string query)
        {
            if (query.Length == 0) return ([], false);
            var rows = new List<SearchCandidate>();
            while (rows.Count <= candidateLimit)
            {
                var count = Math.Min(1000, candidateLimit + 1 - rows.Count);
                var page = await KeywordSearchAsync(projectId, query, count, rows.Count, filters, weights,
                    cancellationToken).ConfigureAwait(false);
                if (generation == 0) generation = page.SearchGeneration;
                else if (generation != page.SearchGeneration)
                    throw new ContextMoleException("index_changed", "The index changed during candidate retrieval.", true);
                rows.AddRange(page.Candidates);
                if (page.Candidates.Count < count) break;
            }
            return (rows.Take(candidateLimit).ToArray(), rows.Count > candidateLimit);
        }
        var main = await Load(mainQuery).ConfigureAwait(false);
        var optional = await Load(optionalQuery).ConfigureAwait(false);
        return new KeywordBranchSnapshot(generation, main.Candidates, optional.Candidates, main.Capped, optional.Capped);
    }
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
    async Task<IReadOnlyList<SearchCandidate>> LoadCandidatesAsync(Guid projectId, IReadOnlyCollection<Guid> passageIds,
        long expectedGeneration, SearchScope scope, CancellationToken cancellationToken = default)
    {
        var result = new List<SearchCandidate>();
        foreach (var batch in passageIds.Chunk(500))
            result.AddRange(await LoadCandidatesAsync(projectId, batch, expectedGeneration, cancellationToken).ConfigureAwait(false));
        return result;
    }
    Task<IReadOnlyList<PassageInfo>> ReadPassagesAsync(Guid projectId, IReadOnlyCollection<Guid> passageIds, int contextBefore, int contextAfter, CancellationToken cancellationToken = default);
    async Task<IReadOnlyList<PassageInfo>> ReadPassagesAsync(Guid projectId, IReadOnlyCollection<Guid> passageIds,
        int contextBefore, int contextAfter, long expectedGeneration, CancellationToken cancellationToken = default)
    {
        if (await GetSearchGenerationAsync(projectId, cancellationToken).ConfigureAwait(false) != expectedGeneration)
            throw new ContextMoleException("index_changed", "The index changed after search. Repeat the search.", true);
        return await ReadPassagesAsync(projectId, passageIds, contextBefore, contextAfter, cancellationToken).ConfigureAwait(false);
    }
    Task<SectionReadResponse> ReadSectionAsync(Guid projectId, Guid sectionId, long expectedGeneration,
        int limit = 20, string? cursor = null, CancellationToken cancellationToken = default) =>
        throw new ContextMoleException("section_unavailable", "Section reading is unavailable in this store.");
    Task<DocumentInfo?> GetDocumentInfoAsync(Guid projectId, Guid documentId, Guid? contentId, CancellationToken cancellationToken = default);
    Task<AttachmentPage> ListAttachmentsAsync(Guid projectId, Guid documentId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<ResolvedLocalFile?> ResolveLocalFileAsync(Guid projectId, Guid documentId, Guid? contentId, CancellationToken cancellationToken = default);
    Task<IndexedContentMaterialization?> GetContentMaterializationAsync(Guid projectId, Guid contentId,
        CancellationToken cancellationToken = default);
}
