using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

using ContextMole.Core;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextMole.Indexing;

public sealed class IndexingCoordinator(
    IIndexWriter writer,
    ISearchStore searchStore,
    IAppPaths paths,
    IDocumentExtractor extractor,
    IEmbeddingGenerator embeddings,
    IndexingActivityTracker activities,
    EmbeddingPolicyRefreshTracker policyRefreshes,
    IGlobalCpuBudget cpuBudget,
    ILogger<IndexingCoordinator> logger) : BackgroundService, IProjectIndexingControl
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(1);
    private const int ChunkTargetTokens = 384;
    private const int ChunkMaximumTokens = 512;
    private const int ChunkOverlapTokens = 64;
    private const int TokenProbeWordCount = 32;
    private readonly IIndexWriter _writer = writer;
    private readonly ISearchStore _searchStore = searchStore;
    private readonly string _dataDirectory = NormalizeDirectoryPath(paths.DataDirectory);
    private readonly IDocumentExtractor _extractor = extractor;
    private readonly IEmbeddingGenerator _embeddings = embeddings;
    private readonly IndexingActivityTracker _activities = activities;
    private readonly EmbeddingPolicyRefreshTracker _policyRefreshes = policyRefreshes;
    private readonly IGlobalCpuBudget _cpuBudget = cpuBudget;
    private readonly ILogger<IndexingCoordinator> _logger = logger;
    private readonly Channel<WatchChange> _watchChanges = Channel.CreateUnbounded<WatchChange>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _watchersGate = new();
    private readonly object _projectWorkGate = new();
    private readonly SemaphoreSlim _reconciliationGate = new(1, 1);
    private readonly Dictionary<Guid, FolderWatcher> _watchers = [];
    private readonly Dictionary<Guid, HashSet<ProjectJobOperation>> _projectOperations = [];
    private readonly Dictionary<Guid, ProjectPauseGate> _projectPauseGates = [];
    private readonly HashSet<LeaseClaim> _leaseClaims = [];
    private bool _stopping;

    public void BeginPause(Guid projectId)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project ID is required.", nameof(projectId));

        ProjectPauseGate pauseGate;
        ProjectJobOperation[] operations;
        lock (_projectWorkGate)
        {
            if (_projectPauseGates.ContainsKey(projectId)) return;
            pauseGate = new ProjectPauseGate();
            _projectPauseGates.Add(projectId, pauseGate);
            operations = _projectOperations.TryGetValue(projectId, out var active)
                ? active.ToArray()
                : [];
        }

        CancelForPause(projectId, pauseGate, operations);
        _logger.LogInformation("Indexing pause requested for project {ProjectId}", projectId);
    }

    public async Task DrainPausedAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project ID is required.", nameof(projectId));

        ProjectPauseGate pauseGate;
        ProjectJobOperation[] operations;
        Task[] claims;
        lock (_projectWorkGate)
        {
            if (!_projectPauseGates.TryGetValue(projectId, out var currentPauseGate))
                throw new InvalidOperationException("BeginPause must be called before paused indexing work is drained.");
            pauseGate = currentPauseGate!;
            pauseGate.Resolve(ProjectPauseResolution.DurablyPaused);
            operations = _projectOperations.TryGetValue(projectId, out var active)
                ? active.ToArray()
                : [];
            claims = _leaseClaims.Select(claim => claim.Completion).ToArray();
        }

        CancelForPause(projectId, pauseGate, operations);
        var cleanup = operations.Select(operation => operation.Completion).Concat(claims).ToArray();
        await Task.WhenAll(cleanup).WaitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Indexing paused and drained for project {ProjectId}", projectId);
    }

    public void Resume(Guid projectId)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project ID is required.", nameof(projectId));

        lock (_projectWorkGate)
        {
            if (_projectPauseGates.Remove(projectId, out var pauseGate))
                pauseGate.Resolve(ProjectPauseResolution.Resumed);
        }
        _logger.LogInformation("Indexing admission resumed for project {ProjectId}", projectId);
    }

    private void CancelForPause(Guid projectId, ProjectPauseGate pauseGate,
        IEnumerable<ProjectJobOperation> operations)
    {
        foreach (var operation in operations)
        {
            try
            {
                operation.CancelForPause(pauseGate);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "A cancellation callback failed while pausing indexing for project {ProjectId}", projectId);
            }
        }
    }

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
            if (IsAppDataPath(item.Folder.Path))
            {
                lock (_watchersGate) _watchers.Remove(item.Folder.Id, out existing);
                existing?.Dispose();
                continue;
            }
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

    private void Queue(Guid projectId, Guid folderId, string path, WatchChangeKind kind, string? oldPath = null)
    {
        if (kind != WatchChangeKind.Reconcile && IsAppDataPath(path) &&
            (kind != WatchChangeKind.Rename || oldPath is null || IsAppDataPath(oldPath))) return;
        _watchChanges.Writer.TryWrite(new WatchChange(projectId, folderId, path, kind, oldPath, DateTimeOffset.UtcNow));
    }

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

        var pathIsAppData = IsAppDataPath(change.Path);
        var oldPathIsAppData = change.OldPath is not null && IsAppDataPath(change.OldPath);

        if (change.Kind == WatchChangeKind.Rename && change.OldPath is not null)
        {
            if (pathIsAppData)
            {
                if (!oldPathIsAppData && SupportedContent.IsSupported(change.OldPath))
                    await _writer.HandleDeletedAsync(change.ProjectId, change.FolderId, change.OldPath,
                        cancellationToken).ConfigureAwait(false);
                return;
            }
            if (oldPathIsAppData)
            {
                if (File.Exists(change.Path) && SupportedContent.IsSupported(change.Path))
                    await ObservePathAsync(change.ProjectId, change.FolderId, change.Path, null, true,
                        cancellationToken).ConfigureAwait(false);
                return;
            }
            if (File.Exists(change.Path) && SupportedContent.IsSupported(change.Path))
            {
                await _writer.HandleRenamedAsync(change.ProjectId, change.FolderId, change.OldPath, change.Path,
                    cancellationToken).ConfigureAwait(false);
                await ObservePathAsync(change.ProjectId, change.FolderId, change.Path, null, true,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (SupportedContent.IsSupported(change.OldPath) &&
                     await IsFolderAvailableAsync(change.ProjectId, change.FolderId, cancellationToken).ConfigureAwait(false))
            {
                await _writer.HandleDeletedAsync(change.ProjectId, change.FolderId, change.OldPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await ReconcileFolderIfAvailableAsync(change.ProjectId, change.FolderId, cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }


        if (pathIsAppData) return;

        if (change.Kind == WatchChangeKind.Delete)
        {
            if (File.Exists(change.Path))
            {
                if (SupportedContent.IsSupported(change.Path))
                    await ObservePathAsync(change.ProjectId, change.FolderId, change.Path, null, true,
                        cancellationToken).ConfigureAwait(false);
            }
            else if (SupportedContent.IsSupported(change.Path))
            {
                if (await IsFolderAvailableAsync(change.ProjectId, change.FolderId, cancellationToken).ConfigureAwait(false))
                    await _writer.HandleDeletedAsync(change.ProjectId, change.FolderId, change.Path, cancellationToken)
                        .ConfigureAwait(false);
            }
            else
            {
                await ReconcileFolderIfAvailableAsync(change.ProjectId, change.FolderId, cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }

        if (!File.Exists(change.Path)) return;

        if (SupportedContent.IsSupported(change.Path))
            await ObservePathAsync(change.ProjectId, change.FolderId, change.Path, null, true, cancellationToken).ConfigureAwait(false);
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

                    var firstRequestForPolicy = _policyRefreshes.TryBeginRefresh(project.Id, policy.Key);
                    if (firstRequestForPolicy)
                        _logger.LogInformation("Queueing project {Project} for embedding policy refresh", project.Name);
                    await _writer.RequestEmbeddingRefreshAsync(project.Id, policy, retryFailed: firstRequestForPolicy,
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
                if (PathsOverlap(root, _dataDirectory))
                    await RemoveExcludedDocumentsAsync(projectId, folderId, root, cancellationToken)
                        .ConfigureAwait(false);
                if (!Directory.Exists(root))
                {
                    _logger.LogInformation("Retaining index state because folder became unavailable: {Folder}", root);
                    return;
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
        if (IsAppDataPath(path)) return;
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
            IndexJobLease? job = null;
            ProjectJobOperation? operation = null;
            Exception? leaseFailure = null;
            using (BeginLeaseClaim())
            {
                try
                {
                    job = await _writer.LeaseNextJobAsync(TimeSpan.FromMinutes(20), cancellationToken)
                        .ConfigureAwait(false);
                    if (job is not null)
                        operation = await BeginProjectOperationAsync(job.ProjectId, cancellationToken)
                            .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    leaseFailure = exception;
                }
            }

            if (leaseFailure is not null)
            {
                _logger.LogWarning(leaseFailure, "An indexing worker could not lease a job; it will retry");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (job is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (operation is null)
            {
                _logger.LogDebug("Skipped a late indexing lease for paused project {ProjectId}", job.ProjectId);
                continue;
            }

            while (operation is not null)
            {
                var retryAfterPauseRollback = false;
                using (operation)
                using (var activity = _activities.Start(job))
                {
                    var jobCancellationToken = operation.CancellationToken;
                    ICpuWorkerLease? capacity = null;
                    try
                    {
                        PreparedIndexSource? source = null;
                        if (job.Kind != IndexJobKind.EmbeddingRefresh)
                        {
                            source = await PrepareIndexSourceAsync(job, activity, jobCancellationToken)
                                .ConfigureAwait(false);
                            if (source is null)
                            {
                                activity.Complete(false);
                                break;
                            }
                        }

                        activity.SetStage(IndexingPipelineStage.WaitingForCpu);
                        capacity = await _cpuBudget.AcquireWorkerAsync(jobCancellationToken).ConfigureAwait(false);
                        using (capacity.Activate())
                        {
                            var indexed = await ProcessJobAsync(job, source, activity, jobCancellationToken)
                                .ConfigureAwait(false);
                            activity.Complete(indexed);
                        }
                    }
                    catch (Exception exception) when (operation.IsPauseCancellationRequested)
                    {
                        if (exception is OperationCanceledException)
                            _logger.LogInformation("Indexing canceled for paused project {ProjectId}: {Path}",
                                job.ProjectId, job.SourcePath);
                        else
                            _logger.LogInformation(exception,
                                "Indexing stopped during pause cleanup for project {ProjectId}: {Path}",
                                job.ProjectId, job.SourcePath);

                        capacity?.Dispose();
                        capacity = null;
                        activity.Complete(false);
                        try
                        {
                            retryAfterPauseRollback = await operation.GetPauseResolution()
                                .WaitAsync(cancellationToken).ConfigureAwait(false) == ProjectPauseResolution.Resumed;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
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
                            var code = job.Kind == IndexJobKind.EmbeddingRefresh && exception is not ContextMoleException
                                ? "embedding_refresh_failed"
                                : ErrorCode(exception);
                            await _writer.FailJobAsync(job, code, exception.Message, IsTemporary(exception),
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception recordingException)
                        {
                            _logger.LogError(recordingException,
                                "The indexing failure for {Path} could not be recorded", job.SourcePath);
                        }
                        activity.Complete(false);
                    }
                    finally
                    {
                        capacity?.Dispose();
                    }
                }

                if (!retryAfterPauseRollback) break;
                operation = await BeginProjectOperationAsync(job.ProjectId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<PreparedIndexSource?> PrepareIndexSourceAsync(IndexJobLease job,
        IndexingActivityHandle activity, CancellationToken cancellationToken)
    {
        activity.SetStage(IndexingPipelineStage.InspectingSource);
        var before = new FileInfo(job.SourcePath);
        if (!before.Exists)
        {
            if (await IsFolderAvailableAsync(job.ProjectId, job.FolderId, cancellationToken).ConfigureAwait(false))
            {
                await _writer.HandleDeletedAsync(job.ProjectId, job.FolderId, job.SourcePath, cancellationToken)
                    .ConfigureAwait(false);
                await _writer.FailJobAsync(job, "source_changed",
                    "The source path changed or disappeared while it was being indexed.", true,
                    cancellationToken).ConfigureAwait(false);
            }
            else
                await _writer.FailJobAsync(job, "folder_unavailable",
                    "The source folder is unavailable; the last successful index revision was retained.", true,
                    cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (IsAppDataPath(before.FullName))
        {
            await _writer.HandleDeletedAsync(job.ProjectId, job.FolderId, job.SourcePath, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        if (!await IsFolderAvailableAsync(job.ProjectId, job.FolderId, cancellationToken).ConfigureAwait(false))
        {
            await _writer.FailJobAsync(job, "folder_unavailable",
                "The source folder is unavailable; the last successful index revision was retained.", true,
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (!await IsAuthorizedSourceAsync(job, before, cancellationToken).ConfigureAwait(false))
        {
            await _writer.FailJobAsync(job, "source_not_authorized",
                "The source path became a file-system link or left its authorized project folder.", false,
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (IsCloudPlaceholder(before.Attributes))
        {
            await _writer.FailJobAsync(job, "cloud_placeholder", "The file is an unavailable cloud-storage placeholder; it was not hydrated.", true, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return new PreparedIndexSource(before.Length,
            new DateTimeOffset(before.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private async Task<bool> ProcessJobAsync(IndexJobLease job, PreparedIndexSource? source,
        IndexingActivityHandle activity, CancellationToken cancellationToken)
    {
        if (job.Kind == IndexJobKind.EmbeddingRefresh)
            return await ProcessEmbeddingRefreshAsync(job, activity, cancellationToken).ConfigureAwait(false);

        var prepared = source ?? throw new InvalidOperationException("An index job requires source preflight.");
        var initialLength = prepared.Length;
        var initialModified = prepared.ModifiedUtc;
        activity.SetStage(IndexingPipelineStage.Hashing);
        var sha256 = await HashAsync(job.SourcePath, cancellationToken).ConfigureAwait(false);
        var afterHash = new FileInfo(job.SourcePath);
        if (!afterHash.Exists || afterHash.Length != initialLength || new DateTimeOffset(afterHash.LastWriteTimeUtc, TimeSpan.Zero) != initialModified)
        {
            await RequeueChangedSourceAsync(job, cancellationToken).ConfigureAwait(false);
            return false;
        }

        activity.SetStage(IndexingPipelineStage.PreparingRevision);
        var begin = await _writer.BeginRevisionAsync(job, sha256, initialLength, initialModified, cancellationToken).ConfigureAwait(false);
        if (!begin.ShouldExtract || begin.RevisionId is null)
            return false;

        activity.SetStage(IndexingPipelineStage.ExtractingContent);
        var extraction = await _extractor.ExtractAsync(new ExtractionRequest(job.SourcePath), cancellationToken).ConfigureAwait(false);
        activity.SetStage(IndexingPipelineStage.ChunkingText);
        var (contentNodes, passageSeeds) = FlattenAndChunk(extraction.Root, job.SourcePath, job.DocumentId);
        if (passageSeeds.Count == 0 && extraction.Errors.Count > 0)
        {
            var failure = extraction.Errors.FirstOrDefault(error => error.Retryable) ?? extraction.Errors[0];
            throw new ContextMoleException(failure.Code,
                failure.ItemName is null ? failure.Message : $"{failure.ItemName}: {failure.Message}",
                failure.Retryable);
        }

        var indexingErrors = extraction.Errors.ToList();
        IReadOnlyList<float[]> vectors = [];
        EmbeddingPolicy? embeddingPolicy = null;
        if (_embeddings.IsAvailable && passageSeeds.Count > 0)
        {
            try
            {
                activity.SetStage(IndexingPipelineStage.GeneratingEmbeddings);
                var embeddingBatch = await _embeddings.EmbedPassagesAsync(
                    passageSeeds.Select(seed => seed.SearchText).ToArray(), cancellationToken).ConfigureAwait(false);
                if (embeddingBatch.Policy.Dimensions != 384 || embeddingBatch.Vectors.Count != passageSeeds.Count ||
                    embeddingBatch.Vectors.Any(vector => vector.Length != 384))
                    throw new ContextMoleException("model_output_invalid",
                        "The embedding model returned an invalid passage vector set.");
                vectors = embeddingBatch.Vectors;
                embeddingPolicy = embeddingBatch.Policy;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "Semantic embedding failed for {Path}; committing keyword-search content", job.SourcePath);
                indexingErrors.Add(new ExtractionError("embedding_refresh_failed",
                    $"Semantic embeddings could not be generated: {exception.Message}",
                    IsTemporary(exception) || exception is ContextMoleException { Code: "model_unavailable" },
                    Path.GetFileName(job.SourcePath)));
                vectors = [];
                embeddingPolicy = null;
            }
        }

        activity.SetStage(IndexingPipelineStage.VerifyingSource);
        var finalInfo = new FileInfo(job.SourcePath);
        if (!finalInfo.Exists || finalInfo.Length != initialLength ||
            new DateTimeOffset(finalInfo.LastWriteTimeUtc, TimeSpan.Zero) != initialModified ||
            !await IsAuthorizedSourceAsync(job, finalInfo, cancellationToken).ConfigureAwait(false))
        {
            await RequeueChangedSourceAsync(job, cancellationToken).ConfigureAwait(false);
            return false;
        }
        var finalSha256 = await HashAsync(job.SourcePath, cancellationToken).ConfigureAwait(false);
        finalInfo = new FileInfo(job.SourcePath);
        if (!finalInfo.Exists || finalInfo.Length != initialLength ||
            new DateTimeOffset(finalInfo.LastWriteTimeUtc, TimeSpan.Zero) != initialModified ||
            !string.Equals(finalSha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            await RequeueChangedSourceAsync(job, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var passages = passageSeeds.Select((seed, index) => seed with
        {
            Embedding = vectors.Count == passageSeeds.Count ? vectors[index] : null
        }).ToArray();
        activity.SetStage(IndexingPipelineStage.WritingIndex);
        return await _writer.CommitRevisionAsync(new IndexCommitRequest(job.JobId, job.ProjectId, job.DocumentId, begin.RevisionId.Value,
            job.ExpectedObservationEpoch, sha256, initialLength, initialModified, contentNodes, passages,
            vectors.Count == passageSeeds.Count ? embeddingPolicy : null, indexingErrors), cancellationToken).ConfigureAwait(false);
    }

    private async Task RequeueChangedSourceAsync(IndexJobLease job, CancellationToken cancellationToken)
    {
        var current = new FileInfo(job.SourcePath);
        if (!current.Exists)
        {
            if (await IsFolderAvailableAsync(job.ProjectId, job.FolderId, cancellationToken).ConfigureAwait(false))
            {
                await _writer.HandleDeletedAsync(job.ProjectId, job.FolderId, job.SourcePath, cancellationToken)
                    .ConfigureAwait(false);
                await _writer.FailJobAsync(job, "source_changed",
                    "The source path changed or disappeared while it was being indexed.", true,
                    cancellationToken).ConfigureAwait(false);
            }
            else
                await _writer.FailJobAsync(job, "folder_unavailable",
                    "The source folder became unavailable; the last successful index revision was retained.", true,
                    cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await IsFolderAvailableAsync(job.ProjectId, job.FolderId, cancellationToken).ConfigureAwait(false))
        {
            await _writer.FailJobAsync(job, "folder_unavailable",
                "The source folder became unavailable; the last successful index revision was retained.", true,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await IsAuthorizedSourceAsync(job, current, cancellationToken).ConfigureAwait(false))
        {
            await _writer.FailJobAsync(job, "source_not_authorized",
                "The source path became a file-system link or left its authorized project folder.", false,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await ObservePathAsync(job.ProjectId, job.FolderId, job.SourcePath, null, true, cancellationToken)
            .ConfigureAwait(false);
        await _writer.FailJobAsync(job, "source_changed",
            "The source changed while it was being indexed; indexing was restarted.", true, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> IsAuthorizedSourceAsync(IndexJobLease job, FileInfo source,
        CancellationToken cancellationToken)
    {
        if (IsFileSystemLink(source) || IsAppDataPath(source.FullName)) return false;
        var root = await GetFolderRootAsync(job.ProjectId, job.FolderId, cancellationToken).ConfigureAwait(false);
        if (root is null) return false;

        var fullRoot = Path.GetFullPath(root);
        var fullSource = Path.GetFullPath(source.FullName);
        var relative = Path.GetRelativePath(fullRoot, fullSource);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            return false;

        for (var directory = source.Directory; directory is not null; directory = directory.Parent)
        {
            if (IsFileSystemLink(directory)) return false;
            if (string.Equals(Path.GetFullPath(directory.FullName), fullRoot, PathComparison())) return true;
        }
        return false;
    }

    private async Task ReconcileFolderIfAvailableAsync(Guid projectId, Guid folderId,
        CancellationToken cancellationToken)
    {
        var root = await GetFolderRootAsync(projectId, folderId, cancellationToken).ConfigureAwait(false);
        if (root is not null && Directory.Exists(root))
            await ReconcileFolderAsync(projectId, folderId, root, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsFolderAvailableAsync(Guid projectId, Guid folderId,
        CancellationToken cancellationToken) =>
        await GetFolderRootAsync(projectId, folderId, cancellationToken).ConfigureAwait(false) is { } root &&
        Directory.Exists(root);

    private async Task<string?> GetFolderRootAsync(Guid projectId, Guid folderId,
        CancellationToken cancellationToken) =>
        (await _searchStore.ListProjectsAsync(cancellationToken).ConfigureAwait(false))
        .FirstOrDefault(project => project.Id == projectId)?.Folders
        .FirstOrDefault(folder => folder.Id == folderId)?.Path;

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
            throw new ContextMoleException("embedding_unavailable",
                _embeddings.UnavailableReason ?? "The selected embedding model is unavailable.", true);

        var refreshed = source.Passages.Select((passage, index) =>
            new PassageEmbedding(passage.PassageId, vectors[index])).ToArray();
        activity.SetStage(IndexingPipelineStage.WritingIndex);
        return await _writer.CommitEmbeddingRefreshAsync(new EmbeddingRefreshCommitRequest(job.JobId,
            job.ProjectId, job.DocumentId, source.RevisionId, job.ExpectedObservationEpoch, refreshed, policy),
            cancellationToken).ConfigureAwait(false);
    }

    private (List<ContentNodeDraft> Nodes, List<PassageDraft> Passages) FlattenAndChunk(
        ExtractedNode root, string sourcePath, Guid documentId)
    {
        var nodes = new List<ContentNodeDraft>();
        var passages = new List<PassageDraft>();
        var fileName = Path.GetFileName(sourcePath);
        AddNode(root, null, 0, 0, "root");
        return (nodes, passages);

        void AddNode(ExtractedNode node, Guid? parentId, int ordinal, int depth, string structuralPath)
        {
            var contentId = DeterministicId(documentId, "content", structuralPath);
            nodes.Add(new ContentNodeDraft(contentId, parentId, ordinal, node.Name, node.MimeType, node.Relationship, depth, node.Status));
            var passageOrdinal = 0;
            var title = IndependentTitle(node.Title, node.Name, depth == 0 ? fileName : null);
            var emailSubject = ExtractEmailSubject(node.Sections);
            foreach (var prepared in PrepareSections(node.Sections))
            {
                var chunkOrdinal = 0;
                foreach (var chunk in Chunk(prepared.Text))
                {
                    var section = prepared.Section;
                    var body = TextNormalization.ForSearch(chunk,
                        section.Method == ExtractionMethod.NativeText && section.Location.Kind == LocationKind.Page);
                    var semanticText = BuildSemanticText(body, title, fileName, sourcePath, node.Name,
                        section.Heading, section.Location.Sheet, emailSubject, prepared.TableHeader);
                    var passageId = DeterministicId(contentId, "passage",
                        $"{prepared.FirstSectionOrdinal}:{prepared.LastSectionOrdinal}:{LocationIdentity(section.Location)}:{chunkOrdinal++}");
                    passages.Add(new PassageDraft(passageId, contentId, passageOrdinal++, chunk,
                        semanticText, section.Location, section.Method, section.OcrConfidence, null, body, title,
                        section.Heading, fileName, sourcePath, node.Name, emailSubject));
                }
            }
            var siblingOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < node.Attachments.Count; index++)
            {
                var child = node.Attachments[index];
                var siblingKey = $"{TextNormalization.NameKey(child.Relationship)}\u001f{TextNormalization.NameKey(child.Name)}";
                siblingOccurrences.TryGetValue(siblingKey, out var occurrence);
                siblingOccurrences[siblingKey] = occurrence + 1;
                AddNode(child, contentId, index, depth + 1,
                    $"{structuralPath}/{siblingKey}\u001f{occurrence}");
            }
        }
    }

    private static IReadOnlyList<PreparedSection> PrepareSections(IReadOnlyList<ExtractedSection> sections)
    {
        var normalized = sections.Select((section, ordinal) => new
            {
                Section = section,
                Ordinal = ordinal,
                Text = TextNormalization.ForDisplay(section.Text)
            })
            .Where(item => item.Text.Length > 0)
            .ToArray();
        var tableHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in normalized)
        {
            var table = TableBoundary(item.Section.Location);
            if (table is null || tableHeaders.ContainsKey(table)) continue;
            var header = NormalizeTableHeader(item.Text);
            if (header is not null) tableHeaders[table] = header;
        }

        var result = new List<PreparedSection>();
        foreach (var item in normalized)
        {
            var section = item.Section;
            var boundary = StructuralBoundary(section);
            var table = TableBoundary(section.Location);
            var tableHeader = table is not null && tableHeaders.TryGetValue(table, out var header) ? header : null;
            if (result.Count > 0)
            {
                var previous = result[^1];
                var combinedWordCount = CountWords(previous.Text) + CountWords(item.Text);
                if (CanMerge(section) && combinedWordCount <= 256 && previous.Boundary == boundary &&
                    previous.Section.Method == section.Method &&
                    string.Equals(previous.Section.Heading, section.Heading, StringComparison.Ordinal))
                {
                    result[^1] = previous with
                    {
                        Section = previous.Section with
                        {
                            Location = MergeLocations(previous.Section.Location, section.Location)
                        },
                        Text = $"{previous.Text}\n{item.Text}",
                        LastSectionOrdinal = item.Ordinal
                    };
                    continue;
                }
            }
            result.Add(new PreparedSection(section, item.Text, item.Ordinal, item.Ordinal, boundary, tableHeader));
        }
        return result;

        static int CountWords(string value) =>
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static bool CanMerge(ExtractedSection section) => section.Location.Kind switch
    {
        LocationKind.Sheet => false,
        LocationKind.Structure when IsStandaloneStructure(section.Location.StructurePath) => false,
        _ => true
    };

    private static bool IsStandaloneStructure(string? path) =>
        path is not null && !path.StartsWith("document/paragraph[", StringComparison.Ordinal);

    private static string StructuralBoundary(ExtractedSection section)
    {
        var location = section.Location;
        var locationBoundary = location.Kind switch
        {
            LocationKind.Page => $"page:{location.Page}:part:{location.StructurePath}",
            LocationKind.Slide => $"slide:{location.Slide}:part:{location.StructurePath}",
            LocationKind.Sheet => $"sheet:{location.Sheet}:{location.StructurePath}:{location.CellRange}",
            LocationKind.EmailPart => $"email:{location.EmailPart}:part:{location.StructurePath}",
            LocationKind.ImageFrame => $"image:{location.Page}:{location.ImageFrame}:part:{location.StructurePath}",
            LocationKind.Structure when location.StructurePath?.StartsWith("document/paragraph[",
                StringComparison.Ordinal) == true => "structure:document-body",
            LocationKind.Structure => $"structure:{location.StructurePath}",
            _ => "document"
        };
        return $"{locationBoundary}\u001fheading:{section.Heading}";
    }

    private static SourceLocation MergeLocations(SourceLocation first, SourceLocation last)
    {
        if (first == last) return first;
        if (first.Kind == LocationKind.Structure && last.Kind == LocationKind.Structure &&
            first.StructurePath is { } firstPath && last.StructurePath is { } lastPath)
            return first with { StructurePath = $"{firstPath}..{lastPath}" };
        return first;
    }

    private static string BuildSemanticText(string body, string title, string fileName, string sourcePath,
        string contentName, string? heading, string? sheet, string? emailSubject, string? tableHeader)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(title)) lines.Add($"Title: {title}");
        lines.Add($"Filename: {fileName}");
        lines.Add($"Path: {sourcePath}");
        lines.Add($"Content: {contentName}");
        if (!string.IsNullOrWhiteSpace(heading)) lines.Add($"Heading: {heading}");
        if (!string.IsNullOrWhiteSpace(sheet)) lines.Add($"Sheet: {sheet}");
        if (!string.IsNullOrWhiteSpace(emailSubject)) lines.Add($"Email subject: {emailSubject}");
        if (!string.IsNullOrWhiteSpace(tableHeader)) lines.Add($"Table headers: {tableHeader}");
        lines.Add($"Body: {body}");
        return TextNormalization.ForSearch(string.Join('\n', lines));
    }

    private static string IndependentTitle(string? extractedTitle, string contentName, string? rootFileName)
    {
        var title = TextNormalization.ForDisplay(extractedTitle);
        if (title.Length == 0) return string.Empty;
        var titleKey = TextNormalization.NameKey(title);
        var duplicateNames = new[] { contentName, Path.GetFileNameWithoutExtension(contentName), rootFileName,
                rootFileName is null ? null : Path.GetFileNameWithoutExtension(rootFileName) }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => TextNormalization.NameKey(value!));
        return duplicateNames.Contains(titleKey, StringComparer.Ordinal) ? string.Empty : title;
    }

    private static string? ExtractEmailSubject(IReadOnlyList<ExtractedSection> sections)
    {
        foreach (var line in sections.Where(section => section.Location.Kind == LocationKind.EmailPart)
                     .SelectMany(section => TextNormalization.ForDisplay(section.Text).Split('\n')))
        {
            if (line.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
            {
                var subject = line["Subject:".Length..].Trim();
                if (subject.Length > 0) return subject;
            }
        }
        return null;
    }

    private static string? TableBoundary(SourceLocation location)
    {
        if (location.Kind == LocationKind.Sheet && !string.IsNullOrWhiteSpace(location.CellRange) &&
            string.IsNullOrWhiteSpace(location.StructurePath))
            return $"{location.Sheet}\u001fgrid";
        if (location.Kind == LocationKind.Structure && IsTableStructure(location.StructurePath))
            return location.StructurePath;
        return null;
    }

    private static string? NormalizeTableHeader(string text)
    {
        var firstRow = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstRow is null) return null;
        var cells = firstRow.Split('\t', StringSplitOptions.RemoveEmptyEntries)
            .Select(cell =>
            {
                var separator = cell.LastIndexOf(": ", StringComparison.Ordinal);
                return (separator >= 0 ? cell[(separator + 2)..] : cell).Trim();
            })
            .Where(cell => cell.Length > 0)
            .ToArray();
        if (cells.Length == 0) return null;
        var header = string.Join(" | ", cells);
        return header.Length <= 500 ? header : header[..500];
    }

    private static bool IsTableStructure(string? path) =>
        path?.Contains("/table[", StringComparison.OrdinalIgnoreCase) == true ||
        path?.StartsWith("table[", StringComparison.OrdinalIgnoreCase) == true;

    private static string LocationIdentity(SourceLocation location) => string.Join('\u001f',
        (int)location.Kind, location.Page, location.Sheet, location.CellRange, location.Slide,
        location.StructurePath, location.EmailPart, location.ImageFrame);

    private static Guid DeterministicId(Guid scopeId, string kind, string structuralIdentity)
    {
        var bytes = Encoding.UTF8.GetBytes($"{scopeId:N}\u001f{kind}\u001f{structuralIdentity}");
        var hash = SHA256.HashData(bytes);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed record PreparedSection(
        ExtractedSection Section,
        string Text,
        int FirstSectionOrdinal,
        int LastSectionOrdinal,
        string Boundary,
        string? TableHeader);

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

    private IEnumerable<string> EnumerateFilesWithoutFollowingLinks(string root)
    {
        if (IsAppDataPath(root)) yield break;
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
                if (IsFileSystemLink(entry) || IsAppDataPath(entry.FullName)) continue;
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                    pending.Push(new DirectoryInfo(entry.FullName));
                else
                    yield return entry.FullName;
            }
        }
    }

    private static bool IsFileSystemLink(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0 && !string.IsNullOrEmpty(info.LinkTarget);

    private async Task RemoveExcludedDocumentsAsync(Guid projectId, Guid folderId, string folderRoot,
        CancellationToken cancellationToken)
    {
        var excluded = new List<string>();
        string? cursor = null;
        var prefix = IsAppDataPath(folderRoot) ? NormalizeDirectoryPath(folderRoot) : _dataDirectory;
        do
        {
            var page = await _searchStore.ListDocumentsAsync(new DocumentListRequest(projectId,
                PathPrefixes: [prefix], Limit: 100, Cursor: cursor), cancellationToken).ConfigureAwait(false);
            excluded.AddRange(page.Documents.Where(document => document.FolderId == folderId &&
                IsAppDataPath(document.SourcePath)).Select(document => document.SourcePath));
            cursor = page.NextCursor;
        } while (cursor is not null);

        foreach (var path in excluded)
            await _writer.HandleDeletedAsync(projectId, folderId, path, cancellationToken).ConfigureAwait(false);
    }

    private bool IsAppDataPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(_dataDirectory, fullPath);
            return relative == "." || !Path.IsPathRooted(relative) && relative != ".." &&
                   !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                   !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return root is not null && string.Equals(fullPath, root, PathComparison())
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool PathsOverlap(string left, string right) =>
        IsSameOrChildPath(left, right) || IsSameOrChildPath(right, left);

    private static bool IsSameOrChildPath(string candidate, string root)
    {
        var relative = Path.GetRelativePath(NormalizeDirectoryPath(root), NormalizeDirectoryPath(candidate));
        return relative == "." || !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string ErrorCode(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "access_denied",
        IOException => "io_error",
        ContextMoleException mcp => mcp.Code,
        _ => "indexing_failed"
    };

    private static bool IsTemporary(Exception exception) => exception is IOException or UnauthorizedAccessException ||
        exception is ContextMoleException { Retryable: true } ||
        exception is ContextMoleException { Code: "application_shutting_down" };
    private static StringComparer PathComparer() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private LeaseClaim BeginLeaseClaim()
    {
        var claim = new LeaseClaim(this);
        lock (_projectWorkGate) _leaseClaims.Add(claim);
        return claim;
    }

    private async ValueTask<ProjectJobOperation?> BeginProjectOperationAsync(Guid projectId,
        CancellationToken workerCancellationToken)
    {
        while (true)
        {
            Task<ProjectPauseResolution>? pauseResolution = null;
            lock (_projectWorkGate)
            {
                if (_projectPauseGates.TryGetValue(projectId, out var pauseGate))
                {
                    if (pauseGate.Resolution.IsCompletedSuccessfully &&
                        pauseGate.Resolution.Result == ProjectPauseResolution.DurablyPaused)
                        return null;
                    pauseResolution = pauseGate.Resolution;
                }
                else
                {
                    var operation = new ProjectJobOperation(this, projectId, workerCancellationToken);
                    if (!_projectOperations.TryGetValue(projectId, out var operations))
                    {
                        operations = [];
                        _projectOperations.Add(projectId, operations);
                    }
                    operations.Add(operation);
                    return operation;
                }
            }

            var resolution = await pauseResolution.WaitAsync(workerCancellationToken).ConfigureAwait(false);
            if (resolution == ProjectPauseResolution.DurablyPaused) return null;
        }
    }

    private void CompleteLeaseClaim(LeaseClaim claim)
    {
        lock (_projectWorkGate)
        {
            if (!_leaseClaims.Remove(claim)) return;
            claim.Complete();
        }
    }

    private void CompleteProjectOperation(ProjectJobOperation operation)
    {
        operation.Complete();
        lock (_projectWorkGate)
        {
            if (!_projectOperations.TryGetValue(operation.ProjectId, out var operations) ||
                !operations.Remove(operation))
                return;
            if (operations.Count == 0) _projectOperations.Remove(operation.ProjectId);
        }
    }

    private sealed record WatchChange(Guid ProjectId, Guid FolderId, string Path, WatchChangeKind Kind, string? OldPath, DateTimeOffset ObservedUtc);
    private sealed record PreparedIndexSource(long Length, DateTimeOffset ModifiedUtc);
    private enum WatchChangeKind { Upsert, Delete, Rename, Reconcile }
    private enum ProjectPauseResolution { DurablyPaused, Resumed }
    private sealed record FolderWatcher(string Path, FileSystemWatcher Watcher) : IDisposable
    {
        public void Dispose() => Watcher.Dispose();
    }

    private sealed class LeaseClaim(IndexingCoordinator owner) : IDisposable
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public Task Completion => _completion.Task;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.CompleteLeaseClaim(this);
        }

        public void Complete() => _completion.TrySetResult();
    }

    private sealed class ProjectPauseGate
    {
        private readonly TaskCompletionSource<ProjectPauseResolution> _resolution =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProjectPauseResolution> Resolution => _resolution.Task;
        public void Resolve(ProjectPauseResolution resolution) => _resolution.TrySetResult(resolution);
    }

    private sealed class ProjectJobOperation(
        IndexingCoordinator owner,
        Guid projectId,
        CancellationToken workerCancellationToken) : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(workerCancellationToken);
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _completed;
        private int _pauseCancellationRequested;
        private Task<ProjectPauseResolution>? _pauseResolution;

        public Guid ProjectId { get; } = projectId;
        public CancellationToken CancellationToken => _cancellation.Token;
        public bool IsPauseCancellationRequested => Volatile.Read(ref _pauseCancellationRequested) != 0;
        public Task Completion => _completion.Task;

        public void CancelForPause(ProjectPauseGate pauseGate)
        {
            Interlocked.Exchange(ref _pauseCancellationRequested, 1);
            lock (_gate)
            {
                _pauseResolution ??= pauseGate.Resolution;
                if (!_completed) _cancellation.Cancel();
            }
        }

        public Task<ProjectPauseResolution> GetPauseResolution()
        {
            lock (_gate)
                return _pauseResolution ?? throw new InvalidOperationException(
                    "The project operation was not canceled by a pause.");
        }

        public void Dispose() => owner.CompleteProjectOperation(this);

        public void Complete()
        {
            lock (_gate)
            {
                if (_completed) return;
                _completed = true;
                _cancellation.Dispose();
            }
            _completion.TrySetResult();
        }
    }

}
