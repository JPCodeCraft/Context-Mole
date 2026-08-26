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
    ILogger<IndexingCoordinator> logger) : BackgroundService
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(1);
    private readonly IIndexWriter _writer = writer;
    private readonly ISearchStore _searchStore = searchStore;
    private readonly IDocumentExtractor _extractor = extractor;
    private readonly IEmbeddingGenerator _embeddings = embeddings;
    private readonly IndexingActivityTracker _activities = activities;
    private readonly ILogger<IndexingCoordinator> _logger = logger;
    private readonly Channel<WatchChange> _watchChanges = Channel.CreateUnbounded<WatchChange>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Dictionary<Guid, FolderWatcher> _watchers = [];
    private readonly HashSet<(Guid ProjectId, string Policy)> _policyRefreshQueued = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _writer.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
            await RefreshWatchersAsync(stoppingToken).ConfigureAwait(false);
            await ReconcileAllAsync(stoppingToken).ConfigureAwait(false);
            await QueueEmbeddingPolicyRefreshAsync(stoppingToken).ConfigureAwait(false);

            var watcherLoop = DrainWatcherChangesAsync(stoppingToken);
            var refreshLoop = RefreshLoopAsync(stoppingToken);
            var reconciliationLoop = ReconciliationLoopAsync(stoppingToken);
            var workers = Enumerable.Range(0, 2).Select(_ => IndexWorkerLoopAsync(stoppingToken)).ToArray();
            await Task.WhenAll(workers.Prepend(watcherLoop).Append(refreshLoop).Append(reconciliationLoop)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _watchChanges.Writer.TryComplete();
        foreach (var watcher in _watchers.Values)
            watcher.Dispose();
        _watchers.Clear();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await RefreshWatchersAsync(cancellationToken).ConfigureAwait(false);
            await QueueEmbeddingPolicyRefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconciliationLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ReconciliationInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await ReconcileAllAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshWatchersAsync(CancellationToken cancellationToken)
    {
        var projects = await _searchStore.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
        var desired = projects.SelectMany(project => project.Folders.Select(folder => (project.Id, Folder: folder)))
            .ToDictionary(item => item.Folder.Id);

        foreach (var removed in _watchers.Keys.Where(id => !desired.ContainsKey(id)).ToArray())
        {
            _watchers.Remove(removed, out var watcher);
            watcher?.Dispose();
        }

        foreach (var item in desired.Values)
        {
            if (_watchers.TryGetValue(item.Folder.Id, out var existing) &&
                string.Equals(existing.Path, item.Folder.Path, PathComparison()))
                continue;
            existing?.Dispose();
            try
            {
                if (Directory.Exists(item.Folder.Path))
                {
                    _watchers[item.Folder.Id] = CreateWatcher(item.Id, item.Folder);
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
            if (_watchers.TryGetValue(change.FolderId, out var watcher))
                await ReconcileFolderAsync(change.ProjectId, change.FolderId, watcher.Path, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (change.Kind == WatchChangeKind.Rename && change.OldPath is not null && File.Exists(change.Path) && SupportedContent.IsSupported(change.Path))
        {
            await _writer.HandleRenamedAsync(change.ProjectId, change.FolderId, change.OldPath, change.Path, cancellationToken).ConfigureAwait(false);
            await ObservePathAsync(change.ProjectId, change.FolderId, change.Path, null, false, cancellationToken).ConfigureAwait(false);
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
        if (!_embeddings.IsAvailable || _embeddings.Policy is null) return;
        foreach (var project in await _searchStore.ListProjectsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (project.IndexedCount == 0 || project.State == ProjectState.Paused) continue;
            if (_policyRefreshQueued.Contains((project.Id, _embeddings.Policy.Key))) continue;
            var snapshot = await _searchStore.LoadVectorSnapshotAsync(project.Id, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(snapshot.Policy?.Key, _embeddings.Policy.Key, StringComparison.Ordinal))
            {
                _logger.LogInformation("Queueing project {Project} for embedding policy refresh", project.Name);
                await _writer.RequestReindexAsync(project.Id, cancellationToken).ConfigureAwait(false);
                _policyRefreshQueued.Add((project.Id, _embeddings.Policy.Key));
            }
        }
    }

    private async Task ReconcileFolderAsync(Guid projectId, Guid folderId, string root, CancellationToken cancellationToken)
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
            var job = await _writer.LeaseNextJobAsync(TimeSpan.FromMinutes(20), cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                continue;
            }

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
                await _writer.FailJobAsync(job, ErrorCode(exception), exception.Message, IsTemporary(exception), CancellationToken.None)
                    .ConfigureAwait(false);
                activity.Complete(false);
            }
        }
    }

    private async Task<bool> ProcessJobAsync(IndexJobLease job, IndexingActivityHandle activity, CancellationToken cancellationToken)
    {
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
        if (_embeddings.IsAvailable && passageSeeds.Count > 0)
        {
            activity.SetStage(IndexingPipelineStage.GeneratingEmbeddings);
            vectors = await _embeddings.EmbedPassagesAsync(passageSeeds.Select(seed => seed.SearchText).ToArray(), cancellationToken).ConfigureAwait(false);
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
            vectors.Count == passageSeeds.Count ? _embeddings.Policy : null, extraction.Errors), cancellationToken).ConfigureAwait(false);
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
                var display = TextNormalization.ForDisplay(chunk);
                if (string.IsNullOrWhiteSpace(display)) continue;
                passages.Add(new PassageDraft(Guid.CreateVersion7(), contentId, passageOrdinal++, display,
                    TextNormalization.ForSearch(display, section.Method == ExtractionMethod.NativeText && section.Location.Kind == LocationKind.Page),
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
                var candidate = string.Join(' ', words[start..(end + 1)]);
                var count = _embeddings.CountTokens(candidate);
                if (count > 512) break;
                bestEnd = end + 1;
                end++;
                if (count >= 384) break;
            }
            yield return string.Join(' ', words[start..bestEnd]);
            if (bestEnd >= words.Length) yield break;

            var overlapStart = bestEnd;
            while (overlapStart > start)
            {
                var candidate = string.Join(' ', words[(overlapStart - 1)..bestEnd]);
                if (_embeddings.CountTokens(candidate) > 64) break;
                overlapStart--;
            }
            start = overlapStart == start ? bestEnd : overlapStart;
        }
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

    private static bool IsTemporary(Exception exception) => exception is IOException or UnauthorizedAccessException;
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
