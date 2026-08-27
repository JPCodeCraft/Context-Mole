using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using MCPIndexSearch.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace MCPIndexSearch.Storage;

public sealed class DatabaseWriterService : BackgroundService, IIndexWriter
{
    private static readonly JsonSerializerOptions StorageJsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private readonly IAppPaths _paths;
    private readonly Channel<Func<SqliteConnection, Task>> _commands = Channel.CreateBounded<Func<SqliteConnection, Task>>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DatabaseWriterService(IAppPaths paths) => _paths = paths;

    public Task Ready => _ready.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(stoppingToken).ConfigureAwait(false);
            await Schema.MigrateAsync(connection, stoppingToken).ConfigureAwait(false);
            _ready.TrySetResult();

            await foreach (var command in _commands.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                await command(connection).ConfigureAwait(false);
            }

            await using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _commands.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<Guid> CreateProjectAsync(CreateProjectRequest request, CancellationToken cancellationToken = default) =>
        EnqueueAsync(async (connection, token) =>
        {
            var name = ValidateName(request.Name);
            var folders = ValidateFolders(request.Folders);
            var projectId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow.ToString("O");
            using var transaction = connection.BeginTransaction();
            try
            {
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO projects(id,name,name_key,state,created_utc,updated_utc) VALUES($id,$name,$key,$state,$now,$now);";
                    command.Parameters.AddWithValue("$id", projectId.ToString());
                    command.Parameters.AddWithValue("$name", name);
                    command.Parameters.AddWithValue("$key", TextNormalization.NameKey(name));
                    command.Parameters.AddWithValue("$state", (int)ProjectState.Active);
                    command.Parameters.AddWithValue("$now", now);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                foreach (var folder in folders)
                {
                    await InsertFolderAsync(connection, transaction, projectId, folder, now, token).ConfigureAwait(false);
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return projectId;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new McpIndexException("duplicate_project", "A project with this name or folder already exists.");
            }
        }, cancellationToken);

    public Task UpdateProjectAsync(UpdateProjectRequest request, CancellationToken cancellationToken = default) =>
        EnqueueAsync<object?>(async (connection, token) =>
        {
            var name = ValidateName(request.Name);
            var folders = ValidateFolders(request.Folders);
            var now = DateTimeOffset.UtcNow.ToString("O");
            using var transaction = connection.BeginTransaction();
            await EnsureProjectExistsAsync(connection, transaction, request.ProjectId, token).ConfigureAwait(false);

            var existing = new Dictionary<string, Guid>(StringComparer.Ordinal);
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT id,path_key FROM project_folders WHERE project_id=$project;";
                select.Parameters.AddWithValue("$project", request.ProjectId.ToString());
                await using var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    existing[reader.GetString(1)] = Guid.Parse(reader.GetString(0));
                }
            }

            var requested = folders.ToDictionary(PathKey, StringComparer.Ordinal);
            var removed = existing.Where(pair => !requested.ContainsKey(pair.Key)).Select(pair => pair.Value).ToArray();
            foreach (var folderId in removed)
            {
                var revisions = await GetActiveRevisionIdsAsync(connection, transaction, "folder_id", folderId, token).ConfigureAwait(false);
                foreach (var revision in revisions)
                {
                    await DeleteFtsRevisionAsync(connection, transaction, revision, token).ConfigureAwait(false);
                }

                await ExecuteAsync(connection, transaction,
                    "DELETE FROM project_folders WHERE id=$id AND project_id=$project;",
                    [new("$id", folderId.ToString()), new("$project", request.ProjectId.ToString())], token).ConfigureAwait(false);
            }

            foreach (var folder in folders.Where(folder => !existing.ContainsKey(PathKey(folder))))
            {
                await InsertFolderAsync(connection, transaction, request.ProjectId, folder, now, token).ConfigureAwait(false);
            }

            await ExecuteAsync(connection, transaction,
                "UPDATE projects SET name=$name,name_key=$key,updated_utc=$now,search_generation=search_generation+$generation WHERE id=$id;",
                [new("$name", name), new("$key", TextNormalization.NameKey(name)), new("$now", now),
                 new("$generation", removed.Length > 0 ? 1 : 0), new("$id", request.ProjectId.ToString())], token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    public Task SetProjectPausedAsync(Guid projectId, bool paused, CancellationToken cancellationToken = default) =>
        EnqueueAsync<object?>(async (connection, token) =>
        {
            var changed = await ExecuteAsync(connection, null,
                "UPDATE projects SET state=$state,updated_utc=$now WHERE id=$id;",
                [new("$state", (int)(paused ? ProjectState.Paused : ProjectState.Active)),
                 new("$now", DateTimeOffset.UtcNow.ToString("O")), new("$id", projectId.ToString())], token).ConfigureAwait(false);
            if (changed == 0)
            {
                throw new McpIndexException("project_not_found", "The project does not exist.");
            }

            return null;
        }, cancellationToken);

    public Task RequestReindexAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        QueueReindexAsync(projectId, cancellationToken);

    private Task QueueReindexAsync(Guid projectId, CancellationToken cancellationToken) =>
        EnqueueAsync<object?>(async (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            var state = await GetProjectStateAsync(connection, transaction, projectId, token).ConfigureAwait(false);
            if (state == ProjectState.Paused)
            {
                throw new McpIndexException("project_paused", "Resume the project before requesting a reindex.");
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            await ExecuteAsync(connection, transaction,
                "UPDATE documents SET observation_epoch=observation_epoch+1,updated_utc=$now WHERE project_id=$project AND tombstoned=0;",
                [new("$now", now), new("$project", projectId.ToString())], token).ConfigureAwait(false);
            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = "SELECT id,observation_epoch FROM documents WHERE project_id=$project AND tombstoned=0;";
            select.Parameters.AddWithValue("$project", projectId.ToString());
            var documents = new List<(Guid Id, long Epoch)>();
            await using (var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    documents.Add((Guid.Parse(reader.GetString(0)), reader.GetInt64(1)));
                }
            }

            foreach (var document in documents)
            {
                await UpsertOpenJobAsync(connection, transaction, projectId, document.Id, document.Epoch,
                    IndexJobKind.Reindex, now, token).ConfigureAwait(false);
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    public Task RequestEmbeddingRefreshAsync(Guid projectId, EmbeddingPolicy targetPolicy, bool retryFailed,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync<object?>(async (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            var state = await GetProjectStateAsync(connection, transaction, projectId, token).ConfigureAwait(false);
            if (state == ProjectState.Paused)
            {
                throw new McpIndexException("project_paused", "Resume the project before requesting an embedding refresh.");
            }

            var policyJson = JsonSerializer.Serialize(targetPolicy, StorageJsonOptions);
            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT d.id,d.observation_epoch,r.id
                FROM documents d
                JOIN document_revisions r ON r.id=d.active_revision_id AND r.status='active'
                WHERE d.project_id=$project AND d.tombstoned=0
                  AND ($retry_failed=1 OR COALESCE((
                    SELECT CASE WHEN j.kind=$embedding_refresh AND j.state='failed' THEN 1 ELSE 0 END
                    FROM index_jobs j
                    WHERE j.project_id=$project AND j.document_id=d.id
                    ORDER BY j.updated_utc DESC,j.id DESC LIMIT 1
                  ),0)=0)
                  AND (
                    r.embedding_policy_json IS NULL OR r.embedding_policy_json<>$policy
                    OR EXISTS(
                      SELECT 1 FROM embeddings e
                      WHERE e.revision_id=r.id AND e.policy_key<>$policy_key
                    )
                    OR (SELECT COUNT(*) FROM passages p WHERE p.revision_id=r.id)<>
                       (SELECT COUNT(*) FROM embeddings e WHERE e.revision_id=r.id AND e.policy_key=$policy_key)
                  )
                ORDER BY d.path_key,d.id;
                """;
            select.Parameters.AddWithValue("$project", projectId.ToString());
            select.Parameters.AddWithValue("$policy", policyJson);
            select.Parameters.AddWithValue("$policy_key", targetPolicy.Key);
            select.Parameters.AddWithValue("$retry_failed", retryFailed ? 1 : 0);
            select.Parameters.AddWithValue("$embedding_refresh", (int)IndexJobKind.EmbeddingRefresh);
            var documents = new List<(Guid Id, long Epoch, Guid RevisionId)>();
            await using (var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    documents.Add((Guid.Parse(reader.GetString(0)), reader.GetInt64(1),
                        Guid.Parse(reader.GetString(2))));
                }
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            var cleared = 0;
            foreach (var document in documents)
            {
                cleared += await ExecuteAsync(connection, transaction,
                    "DELETE FROM embeddings WHERE revision_id=$revision;",
                    [new("$revision", document.RevisionId.ToString())], token).ConfigureAwait(false);
                await UpsertOpenJobAsync(connection, transaction, projectId, document.Id, document.Epoch,
                    IndexJobKind.EmbeddingRefresh, now, token).ConfigureAwait(false);
            }

            if (cleared > 0)
            {
                await ExecuteAsync(connection, transaction,
                    "UPDATE projects SET search_generation=search_generation+1,updated_utc=$now WHERE id=$project;",
                    [new("$now", now), new("$project", projectId.ToString())], token).ConfigureAwait(false);
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    public Task<int> RetryFailedFilesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        EnqueueAsync(async (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            var state = await GetProjectStateAsync(connection, transaction, projectId, token).ConfigureAwait(false);
            if (state == ProjectState.Paused)
            {
                throw new McpIndexException("project_paused", "Resume the project before retrying failed files.");
            }

            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText =
                """
                WITH ranked_jobs AS (
                  SELECT document_id,state,kind,
                    ROW_NUMBER() OVER (PARTITION BY document_id ORDER BY updated_utc DESC,id DESC) AS job_rank
                  FROM index_jobs
                  WHERE project_id=$project
                ),
                latest_jobs AS (
                  SELECT document_id,state,kind FROM ranked_jobs WHERE job_rank=1
                )
                SELECT d.id,d.observation_epoch,
                  CASE WHEN latest.state='failed' AND latest.kind=$embedding_refresh
                    THEN $embedding_refresh ELSE $reindex END
                FROM documents d
                LEFT JOIN latest_jobs latest ON latest.document_id=d.id
                WHERE d.project_id=$project AND d.tombstoned=0
                  AND NOT EXISTS(
                    SELECT 1 FROM index_jobs open_job
                    WHERE open_job.project_id=$project AND open_job.document_id=d.id
                      AND open_job.state IN ('queued','retry_wait','running')
                  )
                  AND (
                  EXISTS(SELECT 1 FROM project_errors e WHERE e.project_id=$project AND e.document_id=d.id)
                  OR COALESCE(latest.state,'')='failed'
                )
                ORDER BY d.path_key,d.id;
                """;
            select.Parameters.AddWithValue("$project", projectId.ToString());
            select.Parameters.AddWithValue("$embedding_refresh", (int)IndexJobKind.EmbeddingRefresh);
            select.Parameters.AddWithValue("$reindex", (int)IndexJobKind.Reindex);
            var documents = new List<(Guid Id, long Epoch, IndexJobKind Kind)>();
            await using (var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    documents.Add((Guid.Parse(reader.GetString(0)), reader.GetInt64(1),
                        (IndexJobKind)reader.GetInt32(2)));
                }
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            foreach (var document in documents)
            {
                var nextEpoch = document.Kind == IndexJobKind.EmbeddingRefresh
                    ? document.Epoch
                    : document.Epoch + 1;
                if (nextEpoch != document.Epoch)
                {
                    await ExecuteAsync(connection, transaction,
                        "UPDATE documents SET observation_epoch=$epoch,updated_utc=$now WHERE id=$document;",
                        [new("$epoch", nextEpoch), new("$now", now), new("$document", document.Id.ToString())], token)
                        .ConfigureAwait(false);
                }
                await UpsertOpenJobAsync(connection, transaction, projectId, document.Id, nextEpoch,
                    document.Kind, now, token).ConfigureAwait(false);
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return documents.Count;
        }, cancellationToken);

    public Task RemoveProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        EnqueueAsync<object?>(async (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            var revisions = await GetActiveRevisionIdsAsync(connection, transaction, "project_id", projectId, token).ConfigureAwait(false);
            foreach (var revision in revisions)
            {
                await DeleteFtsRevisionAsync(connection, transaction, revision, token).ConfigureAwait(false);
            }

            var changed = await ExecuteAsync(connection, transaction, "DELETE FROM projects WHERE id=$id;",
                [new("$id", projectId.ToString())], token).ConfigureAwait(false);
            if (changed == 0)
            {
                throw new McpIndexException("project_not_found", "The project does not exist.");
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    public Task<ObservationResult> ObserveFileAsync(FileObservation observation, CancellationToken cancellationToken = default) =>
        EnqueueAsync(async (connection, token) =>
        {
            var path = CanonicalPath(observation.Path);
            var pathKey = PathKey(path);
            var now = DateTimeOffset.UtcNow.ToString("O");
            using var transaction = connection.BeginTransaction();
            await EnsureFolderBelongsToProjectAsync(connection, transaction, observation.ProjectId, observation.FolderId, token).ConfigureAwait(false);

            Guid documentId;
            long epoch;
            long priorSize = -1;
            DateTimeOffset? priorModified = null;
            var tombstoned = false;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT id,size,modified_utc,observation_epoch,tombstoned FROM documents WHERE project_id=$project AND path_key=$path;";
                select.Parameters.AddWithValue("$project", observation.ProjectId.ToString());
                select.Parameters.AddWithValue("$path", pathKey);
                await using var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    documentId = Guid.Parse(reader.GetString(0));
                    priorSize = reader.GetInt64(1);
                    priorModified = DateTimeOffset.Parse(reader.GetString(2));
                    epoch = reader.GetInt64(3);
                    tombstoned = reader.GetInt64(4) != 0;
                }
                else
                {
                    documentId = Guid.CreateVersion7();
                    epoch = 0;
                }
            }

            var changed = observation.Force || tombstoned || epoch == 0 || priorSize != observation.Size || priorModified != observation.ModifiedUtc;
            if (epoch == 0)
            {
                epoch = 1;
                await ExecuteAsync(connection, transaction,
                    """
                    INSERT INTO documents(id,project_id,folder_id,path,path_key,file_name,extension,size,modified_utc,observation_epoch,tombstoned,available,last_seen_token,created_utc,updated_utc)
                    VALUES($id,$project,$folder,$path,$path_key,$name,$extension,$size,$modified,$epoch,0,1,$seen,$now,$now);
                    """,
                    [new("$id", documentId.ToString()), new("$project", observation.ProjectId.ToString()),
                     new("$folder", observation.FolderId.ToString()), new("$path", path), new("$path_key", pathKey),
                     new("$name", Path.GetFileName(path)), new("$extension", Path.GetExtension(path).ToLowerInvariant()),
                     new("$size", observation.Size), new("$modified", observation.ModifiedUtc.ToString("O")),
                     new("$epoch", epoch), new("$seen", (object?)observation.ReconciliationToken ?? DBNull.Value), new("$now", now)], token).ConfigureAwait(false);
            }
            else
            {
                if (changed)
                {
                    epoch++;
                }

                await ExecuteAsync(connection, transaction,
                    """
                    UPDATE documents SET folder_id=$folder,path=$path,path_key=$path_key,file_name=$name,extension=$extension,
                        size=$size,modified_utc=$modified,observation_epoch=$epoch,tombstoned=0,available=1,
                        last_seen_token=COALESCE($seen,last_seen_token),updated_utc=$now WHERE id=$id;
                    """,
                    [new("$folder", observation.FolderId.ToString()), new("$path", path), new("$path_key", pathKey),
                     new("$name", Path.GetFileName(path)), new("$extension", Path.GetExtension(path).ToLowerInvariant()),
                     new("$size", observation.Size), new("$modified", observation.ModifiedUtc.ToString("O")),
                     new("$epoch", epoch), new("$seen", (object?)observation.ReconciliationToken ?? DBNull.Value),
                     new("$now", now), new("$id", documentId.ToString())], token).ConfigureAwait(false);
            }

            if (changed)
            {
                await UpsertOpenJobAsync(connection, transaction, observation.ProjectId, documentId, epoch,
                    observation.Force ? IndexJobKind.Reindex : IndexJobKind.Index, now, token).ConfigureAwait(false);
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new ObservationResult(documentId, epoch, changed);
        }, cancellationToken);

    public Task HandleRenamedAsync(Guid projectId, Guid folderId, string oldPath, string newPath, CancellationToken cancellationToken = default) =>
        EnqueueAsync<object?>(async (connection, token) =>
        {
            var oldKey = PathKey(CanonicalPath(oldPath));
            var canonicalNew = CanonicalPath(newPath);
            var newKey = PathKey(canonicalNew);
            var now = DateTimeOffset.UtcNow.ToString("O");
            using var transaction = connection.BeginTransaction();
            var changed = await ExecuteAsync(connection, transaction,
                """
                UPDATE documents SET path=$new,path_key=$new_key,file_name=$name,extension=$extension,
                    folder_id=$folder,updated_utc=$now,observation_epoch=observation_epoch+1
                WHERE project_id=$project AND path_key=$old_key AND tombstoned=0;
                """,
                [new("$new", canonicalNew), new("$new_key", newKey), new("$name", Path.GetFileName(canonicalNew)),
                 new("$extension", Path.GetExtension(canonicalNew).ToLowerInvariant()), new("$folder", folderId.ToString()),
                 new("$now", now), new("$project", projectId.ToString()), new("$old_key", oldKey)], token).ConfigureAwait(false);
            if (changed > 0)
            {
                await ExecuteAsync(connection, transaction,
                    "UPDATE projects SET search_generation=search_generation+1,updated_utc=$now WHERE id=$project;",
                    [new("$now", now), new("$project", projectId.ToString())], token).ConfigureAwait(false);
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    public Task HandleDeletedAsync(Guid projectId, Guid folderId, string path, CancellationToken cancellationToken = default) =>
        EnqueueAsync<object?>(async (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            await TombstonePathAsync(connection, transaction, projectId, folderId, PathKey(CanonicalPath(path)), token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    public async Task CompleteReconciliationAsync(Guid projectId, Guid folderId, string tokenValue,
        CancellationToken cancellationToken = default)
    {
        var missing = await EnqueueAsync(async (connection, token) =>
        {
            var candidates = new List<(string PathKey, string Path, string UpdatedUtc)>();
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT path_key,path,updated_utc FROM documents WHERE project_id=$project AND folder_id=$folder AND tombstoned=0 AND COALESCE(last_seen_token,'')<>$token;";
                select.Parameters.AddWithValue("$project", projectId.ToString());
                select.Parameters.AddWithValue("$folder", folderId.ToString());
                select.Parameters.AddWithValue("$token", tokenValue);
                await using var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                }
            }
            return candidates;
        }, cancellationToken).ConfigureAwait(false);

        var tombstones = new List<(string PathKey, string UpdatedUtc)>();
        foreach (var candidate in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(candidate.Path) || IsFileSystemLink(new FileInfo(candidate.Path)))
                tombstones.Add((candidate.PathKey, candidate.UpdatedUtc));
        }
        if (tombstones.Count == 0) return;

        await EnqueueAsync<object?>(async (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            foreach (var (pathKey, updatedUtc) in tombstones)
            {
                await TombstonePathAsync(connection, transaction, projectId, folderId, pathKey, token, updatedUtc)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<IndexJobLease?> LeaseNextJobAsync(TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
        EnqueueAsync<IndexJobLease?>(async (connection, token) =>
        {
            var now = DateTimeOffset.UtcNow;
            using var transaction = connection.BeginTransaction();
            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                SELECT j.id,j.project_id,j.document_id,d.folder_id,d.path,d.extension,j.expected_epoch,j.kind,j.attempt
                FROM index_jobs j
                JOIN projects p ON p.id=j.project_id
                JOIN documents d ON d.id=j.document_id
                WHERE j.state IN ('queued','retry_wait') AND j.not_before_utc<=$now
                  AND p.state=$active AND d.tombstoned=0
                ORDER BY j.created_utc,j.id LIMIT 1;
                """;
            select.Parameters.AddWithValue("$now", now.ToString("O"));
            select.Parameters.AddWithValue("$active", (int)ProjectState.Active);
            IndexJobLease? lease = null;
            await using (var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    lease = new IndexJobLease(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                        Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)), reader.GetString(4), reader.GetString(5),
                        reader.GetInt64(6), (IndexJobKind)reader.GetInt32(7), reader.GetInt32(8));
                }
            }

            if (lease is not null)
            {
                await ExecuteAsync(connection, transaction,
                    "UPDATE index_jobs SET state='running',lease_until_utc=$lease,updated_utc=$now WHERE id=$id;",
                    [new("$lease", now.Add(leaseDuration).ToString("O")), new("$now", now.ToString("O")), new("$id", lease.JobId.ToString())], token).ConfigureAwait(false);
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return lease;
        }, cancellationToken);

    public Task<BeginRevisionResult> BeginRevisionAsync(IndexJobLease job, string sha256, long size, DateTimeOffset modifiedUtc, CancellationToken cancellationToken = default) =>
        EnqueueAsync(async (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            long epoch;
            string? existingSha;
            Guid? activeRevision;
            string currentPath;
            string currentFolderId;
            string currentPathKey;
            string currentFileName;
            string currentExtension;
            string? currentSeenToken;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT observation_epoch,sha256,active_revision_id,path,folder_id,path_key,file_name,extension,last_seen_token FROM documents WHERE id=$document AND project_id=$project AND tombstoned=0;";
                select.Parameters.AddWithValue("$document", job.DocumentId.ToString());
                select.Parameters.AddWithValue("$project", job.ProjectId.ToString());
                await using var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(token).ConfigureAwait(false);
                    return new BeginRevisionResult(false, true, null, "Document is no longer active.");
                }

                epoch = reader.GetInt64(0);
                existingSha = reader.IsDBNull(1) ? null : reader.GetString(1);
                activeRevision = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));
                currentPath = reader.GetString(3);
                currentFolderId = reader.GetString(4);
                currentPathKey = reader.GetString(5);
                currentFileName = reader.GetString(6);
                currentExtension = reader.GetString(7);
                currentSeenToken = reader.IsDBNull(8) ? null : reader.GetString(8);
            }

            if (epoch != job.ExpectedObservationEpoch)
            {
                await CompleteOrRequeueSupersededJobAsync(connection, transaction, job.JobId,
                    job.ExpectedObservationEpoch, token, job.Kind).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return new BeginRevisionResult(false, true, null, "A newer file observation superseded this job.");
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            if (activeRevision is null && existingSha is null)
            {
                var renameCandidates = new List<(Guid Id, string Path, Guid RevisionId, bool Tombstoned)>();
                await using (var candidates = connection.CreateCommand())
                {
                    candidates.Transaction = transaction;
                    candidates.CommandText = """
                        SELECT d.id,d.path,COALESCE(d.active_revision_id,
                            (SELECT r.id FROM document_revisions r WHERE r.document_id=d.id ORDER BY r.activated_utc DESC,r.created_utc DESC LIMIT 1)),d.tombstoned
                        FROM documents d
                        WHERE d.project_id=$project AND d.id<>$document AND d.sha256=$sha
                          AND (d.active_revision_id IS NOT NULL OR EXISTS(SELECT 1 FROM document_revisions r WHERE r.document_id=d.id));
                        """;
                    candidates.Parameters.AddWithValue("$project", job.ProjectId.ToString());
                    candidates.Parameters.AddWithValue("$document", job.DocumentId.ToString());
                    candidates.Parameters.AddWithValue("$sha", sha256);
                    await using var candidateReader = await candidates.ExecuteReaderAsync(token).ConfigureAwait(false);
                    while (await candidateReader.ReadAsync(token).ConfigureAwait(false))
                    {
                        var candidatePath = candidateReader.GetString(1);
                        if (!File.Exists(candidatePath) && !candidateReader.IsDBNull(2))
                            renameCandidates.Add((Guid.Parse(candidateReader.GetString(0)), candidatePath,
                                Guid.Parse(candidateReader.GetString(2)), candidateReader.GetInt64(3) != 0));
                    }
                }

                if (renameCandidates.Count == 1)
                {
                    var preservedId = renameCandidates[0].Id;
                    await ExecuteAsync(connection, transaction, "DELETE FROM documents WHERE id=$document;",
                        [new("$document", job.DocumentId.ToString())], token).ConfigureAwait(false);
                    await ExecuteAsync(connection, transaction,
                        """
                        UPDATE documents SET folder_id=$folder,path=$path,path_key=$path_key,file_name=$name,extension=$extension,
                            size=$size,modified_utc=$modified,observation_epoch=observation_epoch+1,available=1,
                            tombstoned=0,active_revision_id=$revision,last_seen_token=$seen,updated_utc=$now WHERE id=$id;
                        """,
                        [new("$folder", currentFolderId), new("$path", currentPath), new("$path_key", currentPathKey),
                         new("$name", currentFileName), new("$extension", currentExtension), new("$size", size),
                         new("$modified", modifiedUtc.ToString("O")), new("$revision", renameCandidates[0].RevisionId.ToString()),
                         new("$seen", (object?)currentSeenToken ?? DBNull.Value),
                         new("$now", now), new("$id", preservedId.ToString())], token).ConfigureAwait(false);
                    if (renameCandidates[0].Tombstoned)
                    {
                        await ExecuteAsync(connection, transaction, "UPDATE document_revisions SET status='active' WHERE id=$revision;",
                            [new("$revision", renameCandidates[0].RevisionId.ToString())], token).ConfigureAwait(false);
                        await ExecuteAsync(connection, transaction,
                            "INSERT INTO passages_fts(rowid,search_text) SELECT rowid,search_text FROM passages WHERE revision_id=$revision;",
                            [new("$revision", renameCandidates[0].RevisionId.ToString())], token).ConfigureAwait(false);
                    }
                    await ExecuteAsync(connection, transaction,
                        "UPDATE projects SET search_generation=search_generation+1,updated_utc=$now WHERE id=$project;",
                        [new("$now", now), new("$project", job.ProjectId.ToString())], token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return new BeginRevisionResult(false, false, null, "Document identity was preserved across an unambiguous hash-correlated rename.");
                }
            }

            if (job.Kind == IndexJobKind.Index && string.Equals(existingSha, sha256, StringComparison.OrdinalIgnoreCase) && activeRevision is not null)
            {
                await ExecuteAsync(connection, transaction,
                    "UPDATE documents SET size=$size,modified_utc=$modified,available=1,updated_utc=$now WHERE id=$id;",
                    [new("$size", size), new("$modified", modifiedUtc.ToString("O")), new("$now", now), new("$id", job.DocumentId.ToString())], token).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, "UPDATE index_jobs SET state='completed',lease_until_utc=NULL,updated_utc=$now WHERE id=$id;",
                    [new("$now", now), new("$id", job.JobId.ToString())], token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return new BeginRevisionResult(false, false, null, "The SHA-256 fingerprint is unchanged.");
            }

            if (job.Kind == IndexJobKind.Index && activeRevision is not null && !string.Equals(existingSha, sha256, StringComparison.OrdinalIgnoreCase))
            {
                await DeleteFtsRevisionAsync(connection, transaction, activeRevision.Value, token).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction,
                    "UPDATE document_revisions SET status='superseded' WHERE id=$revision;",
                    [new("$revision", activeRevision.Value.ToString())], token).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction,
                    "UPDATE documents SET active_revision_id=NULL WHERE id=$document;",
                    [new("$document", job.DocumentId.ToString())], token).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction,
                    "UPDATE projects SET search_generation=search_generation+1,updated_utc=$now WHERE id=$project;",
                    [new("$now", now), new("$project", job.ProjectId.ToString())], token).ConfigureAwait(false);
            }

            await ExecuteAsync(connection, transaction,
                "DELETE FROM document_revisions WHERE document_id=$document AND status='staging';",
                [new("$document", job.DocumentId.ToString())], token).ConfigureAwait(false);
            var revisionId = Guid.CreateVersion7();
            await ExecuteAsync(connection, transaction,
                "INSERT INTO document_revisions(id,document_id,sha256,status,created_utc) VALUES($id,$document,$sha,'staging',$now);",
                [new("$id", revisionId.ToString()), new("$document", job.DocumentId.ToString()), new("$sha", sha256), new("$now", now)], token).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "UPDATE documents SET sha256=$sha,size=$size,modified_utc=$modified,available=1,updated_utc=$now WHERE id=$document;",
                [new("$sha", sha256), new("$size", size), new("$modified", modifiedUtc.ToString("O")),
                 new("$now", now), new("$document", job.DocumentId.ToString())], token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new BeginRevisionResult(true, false, revisionId);
        }, cancellationToken);

    public Task<bool> CommitRevisionAsync(IndexCommitRequest request, CancellationToken cancellationToken = default) =>
        EnqueueAsync(async (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            long epoch;
            Guid? oldActive;
            string sourcePath;
            bool stagingRevisionExists;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT d.observation_epoch,d.active_revision_id,d.path,
                           EXISTS(SELECT 1 FROM document_revisions r
                                  WHERE r.id=$revision AND r.document_id=d.id AND r.status='staging')
                    FROM documents d
                    WHERE d.id=$document AND d.project_id=$project AND d.tombstoned=0;
                    """;
                select.Parameters.AddWithValue("$document", request.DocumentId.ToString());
                select.Parameters.AddWithValue("$project", request.ProjectId.ToString());
                select.Parameters.AddWithValue("$revision", request.RevisionId.ToString());
                await using var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(token).ConfigureAwait(false);
                    return false;
                }

                epoch = reader.GetInt64(0);
                oldActive = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));
                sourcePath = reader.GetString(2);
                stagingRevisionExists = reader.GetInt64(3) != 0;
            }

            if (epoch != request.ExpectedObservationEpoch || !stagingRevisionExists)
            {
                await ExecuteAsync(connection, transaction, "DELETE FROM document_revisions WHERE id=$revision;",
                    [new("$revision", request.RevisionId.ToString())], token).ConfigureAwait(false);
                await CompleteOrRequeueSupersededJobAsync(connection, transaction, request.JobId,
                    request.ExpectedObservationEpoch, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return false;
            }

            foreach (var node in request.ContentNodes.OrderBy(item => item.Depth).ThenBy(item => item.Ordinal))
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO content_nodes(id,revision_id,parent_id,ordinal,name,mime_type,relationship,depth,status) VALUES($id,$revision,$parent,$ordinal,$name,$mime,$relationship,$depth,$status);",
                    [new("$id", node.Id.ToString()), new("$revision", request.RevisionId.ToString()),
                     new("$parent", (object?)node.ParentId?.ToString() ?? DBNull.Value), new("$ordinal", node.Ordinal),
                     new("$name", node.Name), new("$mime", (object?)node.MimeType ?? DBNull.Value),
                     new("$relationship", node.Relationship), new("$depth", node.Depth), new("$status", node.Status)], token).ConfigureAwait(false);
            }

            foreach (var passage in request.Passages)
            {
                long rowId;
                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO passages(id,revision_id,content_id,ordinal,display_text,search_text,location_kind,page,sheet,cell_range,slide,structure_path,email_part,image_frame,extraction_method,ocr_confidence)
                        VALUES($id,$revision,$content,$ordinal,$display,$search,$kind,$page,$sheet,$range,$slide,$structure,$email,$frame,$method,$confidence)
                        RETURNING rowid;
                        """;
                    insert.Parameters.AddWithValue("$id", passage.Id.ToString());
                    insert.Parameters.AddWithValue("$revision", request.RevisionId.ToString());
                    insert.Parameters.AddWithValue("$content", passage.ContentId.ToString());
                    insert.Parameters.AddWithValue("$ordinal", passage.Ordinal);
                    insert.Parameters.AddWithValue("$display", passage.DisplayText);
                    insert.Parameters.AddWithValue("$search", passage.SearchText);
                    insert.Parameters.AddWithValue("$kind", (int)passage.Location.Kind);
                    insert.Parameters.AddWithValue("$page", (object?)passage.Location.Page ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$sheet", (object?)passage.Location.Sheet ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$range", (object?)passage.Location.CellRange ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$slide", (object?)passage.Location.Slide ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$structure", (object?)passage.Location.StructurePath ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$email", (object?)passage.Location.EmailPart ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$frame", (object?)passage.Location.ImageFrame ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$method", (int)passage.ExtractionMethod);
                    insert.Parameters.AddWithValue("$confidence", (object?)passage.OcrConfidence ?? DBNull.Value);
                    rowId = Convert.ToInt64(await insert.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                if (passage.Embedding is { Length: 384 } vector && request.EmbeddingPolicy is not null)
                {
                    var bytes = MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();
                    await ExecuteAsync(connection, transaction,
                        "INSERT INTO embeddings(passage_rowid,passage_id,revision_id,vector,policy_key) VALUES($row,$passage,$revision,$vector,$policy);",
                        [new("$row", rowId), new("$passage", passage.Id.ToString()), new("$revision", request.RevisionId.ToString()),
                         new("$vector", bytes), new("$policy", request.EmbeddingPolicy.Key)], token).ConfigureAwait(false);
                }
            }

            if (oldActive is not null && oldActive != request.RevisionId)
            {
                await DeleteFtsRevisionAsync(connection, transaction, oldActive.Value, token).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, "UPDATE document_revisions SET status='superseded' WHERE id=$id;",
                    [new("$id", oldActive.Value.ToString())], token).ConfigureAwait(false);
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            var policyJson = request.EmbeddingPolicy is null
                ? null
                : JsonSerializer.Serialize(request.EmbeddingPolicy, StorageJsonOptions);
            await ExecuteAsync(connection, transaction,
                "UPDATE document_revisions SET status='active',embedding_policy_json=$policy,activated_utc=$now WHERE id=$revision AND status='staging';",
                [new("$policy", (object?)policyJson ?? DBNull.Value), new("$now", now), new("$revision", request.RevisionId.ToString())], token).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "UPDATE documents SET active_revision_id=$revision,sha256=$sha,size=$size,modified_utc=$modified,available=1,updated_utc=$now WHERE id=$document;",
                [new("$revision", request.RevisionId.ToString()), new("$sha", request.Sha256), new("$size", request.Size),
                 new("$modified", request.ModifiedUtc.ToString("O")), new("$now", now), new("$document", request.DocumentId.ToString())], token).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO passages_fts(rowid,search_text) SELECT rowid,search_text FROM passages WHERE revision_id=$revision;",
                [new("$revision", request.RevisionId.ToString())], token).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "UPDATE projects SET search_generation=search_generation+1,updated_utc=$now WHERE id=$project;",
                [new("$now", now), new("$project", request.ProjectId.ToString())], token).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "UPDATE index_jobs SET state='completed',lease_until_utc=NULL,last_error=NULL,updated_utc=$now WHERE id=$job;",
                [new("$now", now), new("$job", request.JobId.ToString())], token).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO index_runs(id,project_id,document_id,started_utc,completed_utc,state) VALUES($id,$project,$document,$now,$now,'completed');",
                [new("$id", Guid.CreateVersion7().ToString()), new("$project", request.ProjectId.ToString()),
                 new("$document", request.DocumentId.ToString()), new("$now", now)], token).ConfigureAwait(false);

            await ClearDocumentErrorsAsync(connection, transaction, request.ProjectId, request.DocumentId, token).ConfigureAwait(false);
            foreach (var error in request.Errors)
            {
                await InsertErrorAsync(connection, transaction, request.ProjectId, request.DocumentId, error.Code,
                    error.ItemName is null ? error.Message : $"{error.ItemName}: {error.Message}", error.Retryable, 0, sourcePath, token).ConfigureAwait(false);
            }

            if (oldActive is not null && oldActive != request.RevisionId)
            {
                await ExecuteAsync(connection, transaction, "DELETE FROM document_revisions WHERE id=$id;",
                    [new("$id", oldActive.Value.ToString())], token).ConfigureAwait(false);
            }

            await TrimErrorsAsync(connection, transaction, request.ProjectId, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<EmbeddingRefreshSource?> LoadEmbeddingRefreshSourceAsync(IndexJobLease job,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync<EmbeddingRefreshSource?>(async (connection, token) =>
        {
            if (job.Kind != IndexJobKind.EmbeddingRefresh)
                throw new ArgumentException("The leased job is not an embedding refresh.", nameof(job));

            using var transaction = connection.BeginTransaction();
            Guid? revisionId = null;
            var isCurrent = false;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText =
                    """
                    SELECT j.state,j.kind,j.expected_epoch,d.observation_epoch,d.active_revision_id
                    FROM index_jobs j
                    JOIN documents d ON d.id=j.document_id
                    WHERE j.id=$job AND j.project_id=$project AND j.document_id=$document
                      AND d.tombstoned=0;
                    """;
                select.Parameters.AddWithValue("$job", job.JobId.ToString());
                select.Parameters.AddWithValue("$project", job.ProjectId.ToString());
                select.Parameters.AddWithValue("$document", job.DocumentId.ToString());
                await using var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    isCurrent = string.Equals(reader.GetString(0), "running", StringComparison.Ordinal) &&
                                (IndexJobKind)reader.GetInt32(1) == IndexJobKind.EmbeddingRefresh &&
                                reader.GetInt64(2) == job.ExpectedObservationEpoch &&
                                reader.GetInt64(3) == job.ExpectedObservationEpoch &&
                                !reader.IsDBNull(4);
                    if (isCurrent) revisionId = Guid.Parse(reader.GetString(4));
                }
            }

            if (!isCurrent || revisionId is null)
            {
                await CompleteOrRequeueSupersededJobAsync(connection, transaction, job.JobId,
                    job.ExpectedObservationEpoch, token, job.Kind).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return null;
            }

            var passages = new List<EmbeddingRefreshPassage>();
            await using (var passagesCommand = connection.CreateCommand())
            {
                passagesCommand.Transaction = transaction;
                passagesCommand.CommandText =
                    "SELECT id,search_text FROM passages WHERE revision_id=$revision ORDER BY rowid;";
                passagesCommand.Parameters.AddWithValue("$revision", revisionId.Value.ToString());
                await using var reader = await passagesCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    passages.Add(new EmbeddingRefreshPassage(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new EmbeddingRefreshSource(revisionId.Value, passages);
        }, cancellationToken);

    public Task<bool> CommitEmbeddingRefreshAsync(EmbeddingRefreshCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Embeddings.Any(item => item.Vector.Length != 384))
            throw new ArgumentException("Embedding refresh vectors must contain exactly 384 values.", nameof(request));
        if (request.Embeddings.Select(item => item.PassageId).Distinct().Count() != request.Embeddings.Count)
            throw new ArgumentException("Embedding refresh passage IDs must be unique.", nameof(request));

        return EnqueueAsync(async (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            var isCurrent = false;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText =
                    """
                    SELECT j.state,j.kind,j.expected_epoch,d.observation_epoch,d.active_revision_id,r.status
                    FROM index_jobs j
                    JOIN documents d ON d.id=j.document_id
                    LEFT JOIN document_revisions r ON r.id=d.active_revision_id
                    WHERE j.id=$job AND j.project_id=$project AND j.document_id=$document
                      AND d.tombstoned=0;
                    """;
                select.Parameters.AddWithValue("$job", request.JobId.ToString());
                select.Parameters.AddWithValue("$project", request.ProjectId.ToString());
                select.Parameters.AddWithValue("$document", request.DocumentId.ToString());
                await using var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    isCurrent = string.Equals(reader.GetString(0), "running", StringComparison.Ordinal) &&
                                (IndexJobKind)reader.GetInt32(1) == IndexJobKind.EmbeddingRefresh &&
                                reader.GetInt64(2) == request.ExpectedObservationEpoch &&
                                reader.GetInt64(3) == request.ExpectedObservationEpoch &&
                                !reader.IsDBNull(4) && Guid.Parse(reader.GetString(4)) == request.RevisionId &&
                                !reader.IsDBNull(5) && string.Equals(reader.GetString(5), "active", StringComparison.Ordinal);
                }
            }

            if (!isCurrent)
            {
                await CompleteOrRequeueSupersededJobAsync(connection, transaction, request.JobId,
                    request.ExpectedObservationEpoch, token, IndexJobKind.EmbeddingRefresh).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return false;
            }

            await using (var passageCount = connection.CreateCommand())
            {
                passageCount.Transaction = transaction;
                passageCount.CommandText = "SELECT COUNT(*) FROM passages WHERE revision_id=$revision;";
                passageCount.Parameters.AddWithValue("$revision", request.RevisionId.ToString());
                var expectedCount = Convert.ToInt32(await passageCount.ExecuteScalarAsync(token).ConfigureAwait(false));
                if (expectedCount != request.Embeddings.Count)
                    throw new InvalidOperationException("The active passage set changed during embedding refresh.");
            }

            await ExecuteAsync(connection, transaction, "DELETE FROM embeddings WHERE revision_id=$revision;",
                [new("$revision", request.RevisionId.ToString())], token).ConfigureAwait(false);
            foreach (var embedding in request.Embeddings)
            {
                var bytes = MemoryMarshal.AsBytes(embedding.Vector.AsSpan()).ToArray();
                var inserted = await ExecuteAsync(connection, transaction,
                    """
                    INSERT INTO embeddings(passage_rowid,passage_id,revision_id,vector,policy_key)
                    SELECT p.rowid,p.id,p.revision_id,$vector,$policy
                    FROM passages p
                    WHERE p.id=$passage AND p.revision_id=$revision;
                    """,
                    [new("$vector", bytes), new("$policy", request.Policy.Key),
                     new("$passage", embedding.PassageId.ToString()), new("$revision", request.RevisionId.ToString())],
                    token).ConfigureAwait(false);
                if (inserted != 1)
                    throw new InvalidOperationException("A persisted passage disappeared during embedding refresh.");
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            var policyJson = JsonSerializer.Serialize(request.Policy, StorageJsonOptions);
            await ExecuteAsync(connection, transaction,
                "UPDATE document_revisions SET embedding_policy_json=$policy WHERE id=$revision AND status='active';",
                [new("$policy", policyJson), new("$revision", request.RevisionId.ToString())], token).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "UPDATE projects SET search_generation=search_generation+1,updated_utc=$now WHERE id=$project;",
                [new("$now", now), new("$project", request.ProjectId.ToString())], token).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "UPDATE index_jobs SET state='completed',lease_until_utc=NULL,last_error=NULL,updated_utc=$now WHERE id=$job;",
                [new("$now", now), new("$job", request.JobId.ToString())], token).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO index_runs(id,project_id,document_id,started_utc,completed_utc,state) VALUES($id,$project,$document,$now,$now,'completed');",
                [new("$id", Guid.CreateVersion7().ToString()), new("$project", request.ProjectId.ToString()),
                 new("$document", request.DocumentId.ToString()), new("$now", now)], token).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "DELETE FROM project_errors WHERE project_id=$project AND document_id=$document AND code='embedding_refresh_failed';",
                [new("$project", request.ProjectId.ToString()), new("$document", request.DocumentId.ToString())], token)
                .ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public Task FailJobAsync(IndexJobLease job, string code, string message, bool retryable, CancellationToken cancellationToken = default) =>
        EnqueueAsync<object?>(async (connection, token) =>
        {
            var attempt = job.Attempt + 1;
            var shouldRetry = retryable && attempt <= 5;
            var now = DateTimeOffset.UtcNow;
            var delay = attempt switch
            {
                1 => TimeSpan.FromSeconds(5),
                2 => TimeSpan.FromSeconds(30),
                3 => TimeSpan.FromMinutes(2),
                4 => TimeSpan.FromMinutes(10),
                _ => TimeSpan.FromHours(1)
            };
            using var transaction = connection.BeginTransaction();
            await using (var currentJob = connection.CreateCommand())
            {
                currentJob.Transaction = transaction;
                currentJob.CommandText = "SELECT state,expected_epoch,kind FROM index_jobs WHERE id=$id;";
                currentJob.Parameters.AddWithValue("$id", job.JobId.ToString());
                await using var reader = await currentJob.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false) ||
                    !string.Equals(reader.GetString(0), "running", StringComparison.Ordinal))
                {
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }

                if (reader.GetInt64(1) > job.ExpectedObservationEpoch ||
                    (IndexJobKind)reader.GetInt32(2) != job.Kind)
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                    await CompleteOrRequeueSupersededJobAsync(connection, transaction, job.JobId,
                        job.ExpectedObservationEpoch, token, job.Kind).ConfigureAwait(false);
                    await ExecuteAsync(connection, transaction,
                        "DELETE FROM document_revisions WHERE document_id=$document AND status='staging';",
                        [new("$document", job.DocumentId.ToString())], token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }
            }

            await ExecuteAsync(connection, transaction,
                "UPDATE index_jobs SET state=$state,attempt=$attempt,not_before_utc=$next,lease_until_utc=NULL,last_error=$error,updated_utc=$now WHERE id=$id;",
                [new("$state", shouldRetry ? "retry_wait" : "failed"), new("$attempt", attempt),
                 new("$next", now.Add(delay).ToString("O")), new("$error", Limit(message, 2000)),
                 new("$now", now.ToString("O")), new("$id", job.JobId.ToString())], token).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "DELETE FROM document_revisions WHERE document_id=$document AND status='staging';",
                [new("$document", job.DocumentId.ToString())], token).ConfigureAwait(false);
            await ClearPriorJobErrorsAsync(connection, transaction, job.ProjectId, job.DocumentId, token).ConfigureAwait(false);
            await InsertErrorAsync(connection, transaction, job.ProjectId, job.DocumentId, code, message, retryable,
                attempt, job.SourcePath, token).ConfigureAwait(false);
            await TrimErrorsAsync(connection, transaction, job.ProjectId, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true
        };
        return new SqliteConnection(builder.ToString());
    }

    private async Task<T> EnqueueAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(async connection =>
        {
            try
            {
                completion.TrySetResult(await action(connection, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, cancellationToken).ConfigureAwait(false);
        return await completion.Task.ConfigureAwait(false);
    }

    private async Task TombstonePathAsync(SqliteConnection connection, SqliteTransaction transaction, Guid projectId,
        Guid folderId, string pathKey, CancellationToken cancellationToken, string? expectedUpdatedUtc = null)
    {
        Guid? documentId = null;
        Guid? revisionId = null;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id,active_revision_id FROM documents WHERE project_id=$project AND folder_id=$folder AND path_key=$path AND tombstoned=0 AND ($expected_updated IS NULL OR updated_utc=$expected_updated);";
            select.Parameters.AddWithValue("$project", projectId.ToString());
            select.Parameters.AddWithValue("$folder", folderId.ToString());
            select.Parameters.AddWithValue("$path", pathKey);
            select.Parameters.AddWithValue("$expected_updated", (object?)expectedUpdatedUtc ?? DBNull.Value);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                documentId = Guid.Parse(reader.GetString(0));
                revisionId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));
            }
        }

        if (documentId is null)
        {
            return;
        }

        if (revisionId is not null)
        {
            await DeleteFtsRevisionAsync(connection, transaction, revisionId.Value, cancellationToken).ConfigureAwait(false);
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await ExecuteAsync(connection, transaction,
            "UPDATE documents SET tombstoned=1,available=0,active_revision_id=NULL,observation_epoch=observation_epoch+1,updated_utc=$now WHERE id=$id;",
            [new("$now", now), new("$id", documentId.Value.ToString())], cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "UPDATE index_jobs SET state='completed',lease_until_utc=NULL,updated_utc=$now WHERE document_id=$document AND state IN ('queued','retry_wait','running');",
            [new("$now", now), new("$document", documentId.Value.ToString())], cancellationToken).ConfigureAwait(false);
        if (revisionId is not null)
        {
            await ExecuteAsync(connection, transaction, "UPDATE document_revisions SET status='superseded' WHERE id=$id;",
                [new("$id", revisionId.Value.ToString())], cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "UPDATE projects SET search_generation=search_generation+1,updated_utc=$now WHERE id=$project;",
                [new("$now", now), new("$project", projectId.ToString())], cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpsertOpenJobAsync(SqliteConnection connection, SqliteTransaction transaction, Guid projectId,
        Guid documentId, long epoch, IndexJobKind kind, string now, CancellationToken cancellationToken)
    {
        if (kind == IndexJobKind.EmbeddingRefresh)
        {
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO index_jobs(id,project_id,document_id,kind,state,expected_epoch,not_before_utc,created_utc,updated_utc)
                SELECT $id,$project,$document,$kind,'queued',$epoch,$now,$now,$now
                WHERE NOT EXISTS(
                  SELECT 1 FROM index_jobs
                  WHERE document_id=$document AND state IN ('queued','retry_wait','running')
                );
                """,
                [new("$id", Guid.CreateVersion7().ToString()), new("$project", projectId.ToString()),
                 new("$document", documentId.ToString()), new("$kind", (int)kind), new("$epoch", epoch),
                 new("$now", now)], cancellationToken).ConfigureAwait(false);
            return;
        }

        var updated = await ExecuteAsync(connection, transaction,
            """
            UPDATE index_jobs SET
                kind=CASE
                    WHEN $kind=$reindex THEN $reindex
                    WHEN $kind=$index AND kind=$embedding_refresh THEN $index
                    ELSE kind
                END,
                state=CASE WHEN state='running' THEN state ELSE 'queued' END,
                expected_epoch=$epoch,
                attempt=CASE WHEN state='running' THEN attempt ELSE 0 END,
                not_before_utc=CASE WHEN state='running' THEN not_before_utc ELSE $now END,
                lease_until_utc=CASE WHEN state='running' THEN lease_until_utc ELSE NULL END,
                last_error=CASE WHEN state='running' THEN last_error ELSE NULL END,
                updated_utc=$now
            WHERE document_id=$document AND state IN ('queued','retry_wait','running');
            """,
            [new("$kind", (int)kind), new("$reindex", (int)IndexJobKind.Reindex),
             new("$index", (object)(int)IndexJobKind.Index),
             new("$embedding_refresh", (int)IndexJobKind.EmbeddingRefresh), new("$epoch", epoch),
             new("$now", now), new("$document", documentId.ToString())], cancellationToken).ConfigureAwait(false);
        if (updated == 0)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO index_jobs(id,project_id,document_id,kind,state,expected_epoch,not_before_utc,created_utc,updated_utc) VALUES($id,$project,$document,$kind,'queued',$epoch,$now,$now,$now);",
                [new("$id", Guid.CreateVersion7().ToString()), new("$project", projectId.ToString()),
                 new("$document", documentId.ToString()), new("$kind", (int)kind), new("$epoch", epoch), new("$now", now)], cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<int> CompleteOrRequeueSupersededJobAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid jobId, long leasedEpoch, CancellationToken cancellationToken,
        IndexJobKind? leasedKind = null)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        return ExecuteAsync(connection, transaction,
            """
            UPDATE index_jobs SET
                state=CASE
                    WHEN expected_epoch>$leased_epoch OR ($leased_kind IS NOT NULL AND kind<>$leased_kind)
                    THEN 'queued' ELSE 'completed'
                END,
                attempt=CASE
                    WHEN expected_epoch>$leased_epoch OR ($leased_kind IS NOT NULL AND kind<>$leased_kind)
                    THEN 0 ELSE attempt
                END,
                not_before_utc=$now,
                lease_until_utc=NULL,
                last_error=CASE
                    WHEN expected_epoch>$leased_epoch OR ($leased_kind IS NOT NULL AND kind<>$leased_kind)
                    THEN NULL ELSE last_error
                END,
                updated_utc=$now
            WHERE id=$id;
            """,
            [new("$leased_epoch", leasedEpoch), new("$leased_kind", (object?)(int?)leasedKind ?? DBNull.Value),
             new("$now", now), new("$id", jobId.ToString())], cancellationToken);
    }

    private static async Task DeleteFtsRevisionAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid revisionId, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "INSERT INTO passages_fts(passages_fts,rowid,search_text) SELECT 'delete',rowid,search_text FROM passages WHERE revision_id=$revision;",
            [new("$revision", revisionId.ToString())], cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<Guid>> GetActiveRevisionIdsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string scopeColumn, Guid scopeId, CancellationToken cancellationToken)
    {
        if (scopeColumn is not ("project_id" or "folder_id"))
        {
            throw new ArgumentOutOfRangeException(nameof(scopeColumn));
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT active_revision_id FROM documents WHERE {scopeColumn}=$scope AND active_revision_id IS NOT NULL;";
        command.Parameters.AddWithValue("$scope", scopeId.ToString());
        var revisions = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            revisions.Add(Guid.Parse(reader.GetString(0)));
        }

        return revisions;
    }

    private static async Task InsertFolderAsync(SqliteConnection connection, SqliteTransaction transaction, Guid projectId,
        string folder, string now, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "INSERT INTO project_folders(id,project_id,path,path_key,created_utc) VALUES($id,$project,$path,$key,$now);",
            [new("$id", Guid.CreateVersion7().ToString()), new("$project", projectId.ToString()), new("$path", folder),
             new("$key", PathKey(folder)), new("$now", now)], cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureProjectExistsAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid projectId, CancellationToken cancellationToken)
    {
        _ = await GetProjectStateAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProjectState> GetProjectStateAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid projectId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT state FROM projects WHERE id=$id;";
        command.Parameters.AddWithValue("$id", projectId.ToString());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? throw new McpIndexException("project_not_found", "The project does not exist.")
            : (ProjectState)Convert.ToInt32(value);
    }

    private static async Task EnsureFolderBelongsToProjectAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid projectId, Guid folderId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM project_folders WHERE id=$folder AND project_id=$project;";
        command.Parameters.AddWithValue("$folder", folderId.ToString());
        command.Parameters.AddWithValue("$project", projectId.ToString());
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new McpIndexException("folder_not_found", "The project folder no longer exists.");
        }
    }

    private static async Task InsertErrorAsync(SqliteConnection connection, SqliteTransaction transaction, Guid projectId,
        Guid? documentId, string code, string message, bool retryable, int attempt, string? sourcePath,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "INSERT INTO project_errors(project_id,document_id,code,message,retryable,attempt,source_path,created_utc) VALUES($project,$document,$code,$message,$retryable,$attempt,$path,$now);",
            [new("$project", projectId.ToString()), new("$document", (object?)documentId?.ToString() ?? DBNull.Value),
             new("$code", code), new("$message", Limit(message, 2000)), new("$retryable", retryable ? 1 : 0),
             new("$attempt", attempt), new("$path", (object?)sourcePath ?? DBNull.Value), new("$now", DateTimeOffset.UtcNow.ToString("O"))], cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "UPDATE projects SET error_total=error_total+1 WHERE id=$project;",
            [new("$project", projectId.ToString())], cancellationToken).ConfigureAwait(false);
    }

    private static Task<int> ClearDocumentErrorsAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid projectId, Guid documentId, CancellationToken cancellationToken) => ExecuteAsync(connection, transaction,
        "DELETE FROM project_errors WHERE project_id=$project AND document_id=$document;",
        [new("$project", projectId.ToString()), new("$document", documentId.ToString())], cancellationToken);

    private static Task<int> ClearPriorJobErrorsAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid projectId, Guid documentId, CancellationToken cancellationToken) => ExecuteAsync(connection, transaction,
        "DELETE FROM project_errors WHERE project_id=$project AND document_id=$document AND attempt>0;",
        [new("$project", projectId.ToString()), new("$document", documentId.ToString())], cancellationToken);

    private static Task<int> TrimErrorsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid projectId,
        CancellationToken cancellationToken) => ExecuteAsync(connection, transaction,
        "DELETE FROM project_errors WHERE project_id=$project AND id NOT IN (SELECT id FROM project_errors WHERE project_id=$project ORDER BY id DESC LIMIT 1000);",
        [new("$project", projectId.ToString())], cancellationToken);

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        IReadOnlyList<SqliteParameter> parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateName(string name)
    {
        var normalized = TextNormalization.ForDisplay(name);
        if (normalized.Length is < 1 or > 120)
        {
            throw new McpIndexException("invalid_project_name", "Project names must contain 1 to 120 characters.");
        }

        return normalized;
    }

    private IReadOnlyList<string> ValidateFolders(IReadOnlyList<string> folders)
    {
        if (folders.Count == 0)
        {
            throw new McpIndexException("folders_required", "Select at least one folder.");
        }

        var canonical = folders.Select(CanonicalPath).Distinct(StringComparer.Ordinal).ToArray();
        var appDataKey = PathKey(_paths.DataDirectory);
        for (var index = 0; index < canonical.Length; index++)
        {
            if (!Directory.Exists(canonical[index]))
            {
                throw new McpIndexException("folder_unavailable", $"Folder does not exist or is unavailable: {canonical[index]}", true);
            }

            var rootInfo = new DirectoryInfo(canonical[index]);
            if (IsFileSystemLink(rootInfo))
            {
                throw new McpIndexException("unsafe_folder", $"Folder roots cannot be symbolic links: {canonical[index]}");
            }

            var key = PathKey(canonical[index]);
            if (IsSameOrChild(key, appDataKey))
            {
                throw new McpIndexException("unsafe_folder", "The application data directory cannot be indexed.");
            }

            for (var other = 0; other < canonical.Length; other++)
            {
                if (index != other && IsSameOrChild(key, PathKey(canonical[other])))
                {
                    throw new McpIndexException("nested_folder", "A project cannot contain duplicate or nested folder roots.");
                }
            }
        }

        return canonical;
    }

    private static bool IsFileSystemLink(FileSystemInfo info)
    {
        try
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0 && !string.IsNullOrEmpty(info.LinkTarget);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    internal static string CanonicalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    internal static string PathKey(string path)
    {
        var canonical = CanonicalPath(path);
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? canonical.ToUpperInvariant() : canonical;
    }

    internal static bool IsSameOrChild(string candidateKey, string rootKey)
    {
        if (string.Equals(candidateKey, rootKey, StringComparison.Ordinal))
        {
            return true;
        }

        return candidateKey.StartsWith(rootKey + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || candidateKey.StartsWith(rootKey + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
}
