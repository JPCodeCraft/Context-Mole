using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using MCPIndexSearch.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MCPIndexSearch.Indexing;

public sealed class IndexingCoordinator(
    IIndexWriter writer,
    ISearchStore searchStore,
    IDocumentExtractor extractor,
    IEmbeddingGenerator embeddings,
    IndexingActivityTracker activities,
    EmbeddingPolicyRefreshTracker policyRefreshes,
    IGlobalCpuBudget cpuBudget,
    ILogger<IndexingCoordinator> logger) : BackgroundService
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(1);
    private const int ChunkTargetTokens = 384;
    private const int ChunkMaximumTokens = 512;
    private const int ChunkOverlapTokens = 64;
    private const int TokenProbeWordCount = 32;
    private readonly IIndexWriter _writer = writer;
    private readonly ISearchStore _searchStore = searchStore;
    private readonly IDocumentExtractor _extractor = extractor;
    private readonly IEmbeddingGenerator _embeddings = embeddings;
    private readonly IndexingActivityTracker _activities = activities;
    private readonly EmbeddingPolicyRefreshTracker _policyRefreshes = policyRefreshes;
    private readonly IGlobalCpuBudget _cpuBudget = cpuBudget;
    private readonly ILogger<IndexingCoordinator> _logger = logger;
    private readonly Channel<WatchChange> _watchChanges = Channel.CreateUnbounded<WatchChange>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _watchersGate = new();
    private readonly SemaphoreSlim _reconciliationGate = new(1, 1);
    private readonly Dictionary<Guid, FolderWatcher> _watchers = [];
    private bool _stopping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _writer.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
            await RefreshWatchersAsync(stoppingToken, queueReconciliation: false).ConfigureAwait(false);
            await ReconcileAllAsync(stoppingToken).ConfigureAwait(false);
            await QueueEmbeddingPolicyRefreshAsync(stoppingToken).ConfigureAwait(false);

            var watcherLoop = DrainWatcherChangesAsync(stoppingToken);
            var refreshLoop = RefreshLoopAsync(stoppingToken);
            var reconciliationLoop = ReconciliationLoopAsync(stoppingToken);
            var workers = Enumerable.Range(0, _cpuBudget.MaximumWorkerCount)
                .Select(_ => IndexWorkerLoopAsync(stoppingToken)).ToArray();
            await Task.WhenAll(workers.Prepend(watcherLoop).Append(refreshLoop).Append(reconciliationLoop)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_watchersGate) _stopping = true;
        _watchChanges.Writer.TryComplete();
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            FolderWatcher[] watchers;
            lock (_watchersGate)
            {
                watchers = _watchers.Values.ToArray();
                _watchers.Clear();
            }
            foreach (var watcher in watchers) watcher.Dispose();
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await RefreshWatchersAsync(cancellationToken).ConfigureAwait(false);
                await QueueEmbeddingPolicyRefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Periodic project refresh failed; it will be retried");
            }
        }
    }

    private async Task ReconciliationLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ReconciliationInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await ReconcileAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Periodic folder reconciliation failed; it will be retried");
            }
        }
    }

    private async Task RefreshWatchersAsync(CancellationToken cancellationToken, bool queueReconciliation = true)
    {
        var projects = await _searchStore.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
        var desired = projects.SelectMany(project => project.Folders.Select(folder => (project.Id, Folder: folder)))
            .ToDictionary(item => item.Folder.Id);

        Guid[] existingIds;
        lock (_watchersGate) existingIds = _watchers.Keys.ToArray();
        foreach (var removed in existingIds.Where(id => !desired.ContainsKey(id)))
        {
            FolderWatcher? watcher;
            lock (_watchersGate) _watchers.Remove(removed, out watcher);
            watcher?.Dispose();
        }

        foreach (var item in desired.Values)
        {
            FolderWatcher? existing;
            lock (_watchersGate)
            {
                if (_watchers.TryGetValue(item.Folder.Id, out existing) &&
                    string.Equals(existing.Path, item.Folder.Path, PathComparison()))
                    continue;
                _watchers.Remove(item.Folder.Id);
            }
            existing?.Dispose();
            try
            {
                if (Directory.Exists(item.Folder.Path))
                {
                    var created = CreateWatcher(item.Id, item.Folder);
                    var registered = false;
                    lock (_watchersGate)
                    {
                        if (!_stopping)
                        {
                            _watchers[item.Folder.Id] = created;
                            registered = true;
                        }
                        else
                            created.Dispose();
                    }
                    if (queueReconciliation && registered)
                        Queue(item.Id, item.Folder.Id, item.Folder.Path, WatchChangeKind.Reconcile);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Unable to watch project folder {Folder}", item.Folder.Path);
            }
        }
    }

    private FolderWatcher CreateWatcher(Guid projectId, ProjectFolderInfo folder)
    {
        var watcher = new FileSystemWatcher(folder.Path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = false
        };
        watcher.Created += (_, args) => Queue(projectId, folder.Id, args.FullPath, WatchChangeKind.Upsert);
        watcher.Changed += (_, args) => Queue(projectId, folder.Id, args.FullPath, WatchChangeKind.Upsert);
        watcher.Deleted += (_, args) => Queue(projectId, folder.Id, args.FullPath, WatchChangeKind.Delete);
        watcher.Renamed += (_, args) => Queue(projectId, folder.Id, args.FullPath, WatchChangeKind.Rename, args.OldFullPath);
        watcher.Error += (_, _) => Queue(projectId, folder.Id, folder.Path, WatchChangeKind.Reconcile);
        watcher.EnableRaisingEvents = true;
        return new FolderWatcher(folder.Path, watcher);
    }

    private void Queue(Guid projectId, Guid folderId, string path, WatchChangeKind kind, string? oldPath = null) =>
        _watchChanges.Writer.TryWrite(new WatchChange(projectId, folderId, path, kind, oldPath, DateTimeOffset.UtcNow));

    private async Task DrainWatcherChangesAsync(CancellationToken cancellationToken)
    {
        var pending = new Dictionary<string, WatchChange>(PathComparer());
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (!cancellationToken.IsCancellationRequested)
        {
            while (_watchChanges.Reader.TryRead(out var change))
                pending[$"{change.ProjectId:N}|{change.Path}"] = change;

            var now = DateTimeOffset.UtcNow;
            foreach (var pair in pending.Where(pair => now - pair.Value.ObservedUtc >= DebounceInterval).ToArray())
            {
                pending.Remove(pair.Key);
                try
                {
                    await ApplyWatchChangeAsync(pair.Value, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "Unable to apply filesystem event for {Path}", pair.Value.Path);
                }
            }

            await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyWatchChangeAsync(WatchChange change, CancellationToken cancellationToken)
    {
        if (change.Kind == WatchChangeKind.Reconcile)
        {
            string? root = null;
            lock (_watchersGate)
            {
                if (_watchers.TryGetValue(change.FolderId, out var watcher)) root = watcher.Path;
            }
            if (root is not null)
                await ReconcileFolderAsync(change.ProjectId, change.FolderId, root, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (change.Kind == WatchChangeKind.Rename && change.OldPath is not null && File.Exists(change.Path) && SupportedContent.IsSupported(change.Path))
        {
            await _writer.HandleRenamedAsync(change.ProjectId, change.FolderId, change.OldPath, change.Path, cancellationToken).ConfigureAwait(false);
            await ObservePathAsync(change.ProjectId, change.FolderId, change.Path, null, true, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (change.Kind == WatchChangeKind.Delete || !File.Exists(change.Path))
        {
            if (SupportedContent.IsSupported(change.Path))
                await _writer.HandleDeletedAsync(change.ProjectId, change.FolderId, change.Path, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (SupportedContent.IsSupported(change.Path))
            await ObservePathAsync(change.ProjectId, change.FolderId, change.Path, null, false, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileAllAsync(CancellationToken cancellationToken)
    {
        var projects = await _searchStore.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var project in projects)
        foreach (var folder in project.Folders)
            await ReconcileFolderAsync(project.Id, folder.Id, folder.Path, cancellationToken).ConfigureAwait(false);
    }

    private async Task QueueEmbeddingPolicyRefreshAsync(CancellationToken cancellationToken)
    {
        await _policyRefreshes.RunExclusiveAsync(async () =>
        {
            try
            {
                await _embeddings.ReloadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _policyRefreshes.Clear();
                throw;
            }
            var policy = _embeddings.Policy;
            if (!_embeddings.IsAvailable || policy is null)
            {
                _policyRefreshes.Clear();
                return;
            }
            foreach (var project in await _searchStore.ListProjectsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (project.IndexedCount == 0 || project.State == ProjectState.Paused) continue;
                try
                {
                    var metadata = await _searchStore.LoadVectorSnapshotMetadataAsync(project.Id, cancellationToken)
                        .ConfigureAwait(false);
                    if (metadata.IsComplete && string.Equals(metadata.Policy?.Key, policy.Key, StringComparison.Ordinal))
                    {
                        _policyRefreshes.CancelRefresh(project.Id, policy.Key);
                        continue;
                    }

                    if (_policyRefreshes.IsRefreshPending(project.Id, policy.Key)) continue;
                    if (!_policyRefreshes.TryBeginRefresh(project.Id, policy.Key)) continue;

                    _logger.LogInformation("Queueing project {Project} for embedding policy refresh", project.Name);
                    await _writer.RequestEmbeddingRefreshAsync(project.Id, policy, retryFailed: false,
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    _policyRefreshes.CancelRefresh(project.Id, policy.Key);
                    throw;
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileFolderAsync(Guid projectId, Guid folderId, string root, CancellationToken cancellationToken)
    {
        await _reconciliationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(root))
            {
                _logger.LogInformation("Retaining index state because folder is unavailable: {Folder}", root);
                return;
            }

            var token = Guid.CreateVersion7().ToString("N");
            try
            {
                foreach (var path in EnumerateFilesWithoutFollowingLinks(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (SupportedContent.IsSupported(path))
                        await ObservePathAsync(projectId, folderId, path, token, false, cancellationToken).ConfigureAwait(false);
                }
                await _writer.CompleteReconciliationAsync(projectId, folderId, token, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Folder reconciliation was incomplete; no deletions were inferred for {Folder}", root);
            }
        }
        finally
        {
            _reconciliationGate.Release();
        }
    }

    private async Task ObservePathAsync(Guid projectId, Guid folderId, string path, string? token, bool force,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || IsFileSystemLink(info))
            return;
        await _writer.ObserveFileAsync(new FileObservation(projectId, folderId, info.FullName, info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), token, force), cancellationToken).ConfigureAwait(false);
    }

    private async Task IndexWorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ICpuWorkerLease? capacity = null;
            IndexJobLease? job;
            try
            {
                capacity = await _cpuBudget.AcquireWorkerAsync(cancellationToken).ConfigureAwait(false);
                job = await _writer.LeaseNextJobAsync(TimeSpan.FromMinutes(20), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                capacity?.Dispose();
                return;
            }
            catch (Exception exception)
            {
                capacity?.Dispose();
                _logger.LogWarning(exception, "An indexing worker could not lease a job; it will retry");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (job is null)
            {
                capacity.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (capacity)
            using (capacity.Activate())
            {
                using var activity = _activities.Start(job);
                try
                {
                    var indexed = await ProcessJobAsync(job, activity, cancellationToken).ConfigureAwait(false);
                    activity.Complete(indexed);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    activity.Complete(false);
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Indexing failed for {Path}", job.SourcePath);
                    activity.SetStage(IndexingPipelineStage.RecordingError);
                    try
                    {
                        var code = job.Kind == IndexJobKind.EmbeddingRefresh
                            ? "embedding_refresh_failed"
                            : ErrorCode(exception);
                        await _writer.FailJobAsync(job, code, exception.Message, IsTemporary(exception), CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception recordingException)
                    {
                        _logger.LogError(recordingException, "The indexing failure for {Path} could not be recorded", job.SourcePath);
                    }
                    activity.Complete(false);
                }
            }
        }
    }

    private async Task<bool> ProcessJobAsync(IndexJobLease job, IndexingActivityHandle activity, CancellationToken cancellationToken)
    {
        if (job.Kind == IndexJobKind.EmbeddingRefresh)
            return await ProcessEmbeddingRefreshAsync(job, activity, cancellationToken).ConfigureAwait(false);

        activity.SetStage(IndexingPipelineStage.InspectingSource);
        var before = new FileInfo(job.SourcePath);
        if (!before.Exists)
        {
            await _writer.HandleDeletedAsync(job.ProjectId, job.FolderId, job.SourcePath, cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (IsCloudPlaceholder(before.Attributes))
        {
            await _writer.FailJobAsync(job, "cloud_placeholder", "The file is an unavailable cloud-storage placeholder; it was not hydrated.", true, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var initialLength = before.Length;
        var initialModified = new DateTimeOffset(before.LastWriteTimeUtc, TimeSpan.Zero);
        activity.SetStage(IndexingPipelineStage.Hashing);
        var sha256 = await HashAsync(job.SourcePath, cancellationToken).ConfigureAwait(false);
        var afterHash = new FileInfo(job.SourcePath);
        if (!afterHash.Exists || afterHash.Length != initialLength || new DateTimeOffset(afterHash.LastWriteTimeUtc, TimeSpan.Zero) != initialModified)
        {
            if (afterHash.Exists)
                await ObservePathAsync(job.ProjectId, job.FolderId, job.SourcePath, null, false, cancellationToken).ConfigureAwait(false);
            return false;
        }

        activity.SetStage(IndexingPipelineStage.PreparingRevision);
        var begin = await _writer.BeginRevisionAsync(job, sha256, initialLength, initialModified, cancellationToken).ConfigureAwait(false);
        if (!begin.ShouldExtract || begin.RevisionId is null)
            return false;

        activity.SetStage(IndexingPipelineStage.ExtractingContent);
        var extraction = await _extractor.ExtractAsync(new ExtractionRequest(job.SourcePath), cancellationToken).ConfigureAwait(false);
        activity.SetStage(IndexingPipelineStage.ChunkingText);
        var (contentNodes, passageSeeds) = FlattenAndChunk(extraction.Root);
        IReadOnlyList<float[]> vectors = [];
        EmbeddingPolicy? embeddingPolicy = null;
        if (_embeddings.IsAvailable && passageSeeds.Count > 0)
        {
            activity.SetStage(IndexingPipelineStage.GeneratingEmbeddings);
            var embeddingBatch = await _embeddings.EmbedPassagesAsync(
                passageSeeds.Select(seed => seed.SearchText).ToArray(), cancellationToken).ConfigureAwait(false);
            vectors = embeddingBatch.Vectors;
            embeddingPolicy = embeddingBatch.Policy;
        }

        activity.SetStage(IndexingPipelineStage.VerifyingSource);
        var finalInfo = new FileInfo(job.SourcePath);
        if (!finalInfo.Exists || finalInfo.Length != initialLength || new DateTimeOffset(finalInfo.LastWriteTimeUtc, TimeSpan.Zero) != initialModified)
        {
            if (finalInfo.Exists)
                await ObservePathAsync(job.ProjectId, job.FolderId, job.SourcePath, null, false, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var passages = passageSeeds.Select((seed, index) => seed with
        {
            Embedding = vectors.Count == passageSeeds.Count ? vectors[index] : null
        }).ToArray();
        activity.SetStage(IndexingPipelineStage.WritingIndex);
        return await _writer.CommitRevisionAsync(new IndexCommitRequest(job.JobId, job.ProjectId, job.DocumentId, begin.RevisionId.Value,
            job.ExpectedObservationEpoch, sha256, initialLength, initialModified, contentNodes, passages,
            vectors.Count == passageSeeds.Count ? embeddingPolicy : null, extraction.Errors), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ProcessEmbeddingRefreshAsync(IndexJobLease job, IndexingActivityHandle activity,
        CancellationToken cancellationToken)
    {
        activity.SetStage(IndexingPipelineStage.PreparingRevision);
        var source = await _writer.LoadEmbeddingRefreshSourceAsync(job, cancellationToken).ConfigureAwait(false);
        if (source is null) return false;

        IReadOnlyList<float[]> vectors = [];
        EmbeddingPolicy? policy = _embeddings.Policy;
        if (source.Passages.Count > 0)
        {
            activity.SetStage(IndexingPipelineStage.GeneratingEmbeddings);
            var batch = await _embeddings.EmbedPassagesAsync(
                source.Passages.Select(passage => passage.SearchText).ToArray(), cancellationToken)
                .ConfigureAwait(false);
            vectors = batch.Vectors;
            policy = batch.Policy;
        }

        if (policy is null || vectors.Count != source.Passages.Count)
            throw new McpIndexException("embedding_unavailable",
                _embeddings.UnavailableReason ?? "The selected embedding model is unavailable.", true);

        var refreshed = source.Passages.Select((passage, index) =>
            new PassageEmbedding(passage.PassageId, vectors[index])).ToArray();
        activity.SetStage(IndexingPipelineStage.WritingIndex);
        return await _writer.CommitEmbeddingRefreshAsync(new EmbeddingRefreshCommitRequest(job.JobId,
            job.ProjectId, job.DocumentId, source.RevisionId, job.ExpectedObservationEpoch, refreshed, policy),
            cancellationToken).ConfigureAwait(false);
    }

    private (List<ContentNodeDraft> Nodes, List<PassageDraft> Passages) FlattenAndChunk(ExtractedNode root)
    {
        var nodes = new List<ContentNodeDraft>();
        var passages = new List<PassageDraft>();
        AddNode(root, null, 0, 0);
        return (nodes, passages);

        void AddNode(ExtractedNode node, Guid? parentId, int ordinal, int depth)
        {
            var contentId = Guid.CreateVersion7();
            nodes.Add(new ContentNodeDraft(contentId, parentId, ordinal, node.Name, node.MimeType, node.Relationship, depth, node.Status));
            var passageOrdinal = 0;
            foreach (var section in node.Sections)
            foreach (var chunk in Chunk(section.Text))
            {
                passages.Add(new PassageDraft(Guid.CreateVersion7(), contentId, passageOrdinal++, chunk,
                    TextNormalization.ForSearch(chunk, section.Method == ExtractionMethod.NativeText && section.Location.Kind == LocationKind.Page),
                    section.Location, section.Method, section.OcrConfidence, null));
            }
            for (var index = 0; index < node.Attachments.Count; index++)
                AddNode(node.Attachments[index], contentId, index, depth + 1);
        }
    }

    private IEnumerable<string> Chunk(string source)
    {
        var normalized = TextNormalization.ForDisplay(source);
        if (string.IsNullOrWhiteSpace(normalized)) yield break;
        var words = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var start = 0;
        while (start < words.Length)
        {
            var end = start;
            var bestEnd = start + 1;
            while (end < words.Length)
            {
                var probeEnd = Math.Min(words.Length, end + TokenProbeWordCount);
                if (CountTokens(start, probeEnd) < ChunkTargetTokens)
                {
                    bestEnd = probeEnd;
                    end = probeEnd;
                    continue;
                }

                while (end < probeEnd)
                {
                    end++;
                    var count = CountTokens(start, end);
                    if (count > ChunkMaximumTokens) break;
                    bestEnd = end;
                    if (count >= ChunkTargetTokens) break;
                }
                break;
            }
            yield return JoinWords(start, bestEnd);
            if (bestEnd >= words.Length) yield break;

            var overlapStart = bestEnd;
            while (overlapStart > start)
            {
                var probeStart = Math.Max(start, overlapStart - TokenProbeWordCount);
                if (CountTokens(probeStart, bestEnd) <= ChunkOverlapTokens)
                {
                    overlapStart = probeStart;
                    continue;
                }

                while (overlapStart > probeStart)
                {
                    if (CountTokens(overlapStart - 1, bestEnd) > ChunkOverlapTokens) break;
                    overlapStart--;
                }
                break;
            }
            start = overlapStart == start ? bestEnd : overlapStart;
        }

        int CountTokens(int first, int end) => _embeddings.CountTokens(JoinWords(first, end));
        string JoinWords(int first, int end) => string.Join(" ", words, first, end - first);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static bool IsCloudPlaceholder(FileAttributes attributes)
    {
        const FileAttributes recallOnOpen = (FileAttributes)0x00040000;
        const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;
        return (attributes & (FileAttributes.Offline | recallOnOpen | recallOnDataAccess)) != 0;
    }

    private static IEnumerable<string> EnumerateFilesWithoutFollowingLinks(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };

        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos("*", options))
            {
                if (IsFileSystemLink(entry)) continue;
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                    pending.Push(new DirectoryInfo(entry.FullName));
                else
                    yield return entry.FullName;
            }
        }
    }

    private static bool IsFileSystemLink(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0 && !string.IsNullOrEmpty(info.LinkTarget);

    private static string ErrorCode(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "access_denied",
        IOException => "io_error",
        McpIndexException mcp => mcp.Code,
        _ => "indexing_failed"
    };

    private static bool IsTemporary(Exception exception) => exception is IOException or UnauthorizedAccessException ||
        exception is McpIndexException { Retryable: true };
    private static StringComparer PathComparer() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record WatchChange(Guid ProjectId, Guid FolderId, string Path, WatchChangeKind Kind, string? OldPath, DateTimeOffset ObservedUtc);
    private enum WatchChangeKind { Upsert, Delete, Rename, Reconcile }
    private sealed record FolderWatcher(string Path, FileSystemWatcher Watcher) : IDisposable
    {
        public void Dispose() => Watcher.Dispose();
    }
}
