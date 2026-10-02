using System.Text;

using ContextMole.Core;
using Microsoft.Data.Sqlite;

namespace ContextMole.Storage;

public sealed partial class DatabaseWriterService
{
    public Task<HideProjectIssuesResult> HideProjectIssuesAsync(Guid projectId, string sourcePath,
        CancellationToken cancellationToken = default) => EnqueueAsync(async (connection, token) =>
    {
        using var transaction = connection.BeginTransaction();
        await EnsureProjectExistsAsync(connection, transaction, projectId, token).ConfigureAwait(false);
        var key = IssuePathKey(sourcePath);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT signature,COUNT(*) FROM project_errors e WHERE e.project_id=$project AND e.root_path_key=$path
              AND NOT EXISTS(SELECT 1 FROM issue_acknowledgements a WHERE a.project_id=e.project_id AND a.signature=e.signature)
            GROUP BY signature;
            """;
        select.Parameters.AddWithValue("$project", projectId.ToString());
        select.Parameters.AddWithValue("$path", key);
        var signatures = new List<(string Signature, int IssueCount)>();
        await using (var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) signatures.Add((reader.GetString(0), reader.GetInt32(1)));
        var ids = new List<Guid>();
        foreach (var signature in signatures)
        {
            var id = Guid.CreateVersion7();
            await ExecuteAsync(connection, transaction,
                "INSERT INTO issue_acknowledgements VALUES($id,$project,$signature,$path,$now);",
                [new("$id", id.ToString()), new("$project", projectId.ToString()), new("$signature", signature.Signature),
                 new("$path", key), new("$now", DateTimeOffset.UtcNow.ToString("O"))], token).ConfigureAwait(false);
            ids.Add(id);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new HideProjectIssuesResult(ids, signatures.Sum(item => item.IssueCount));
    }, cancellationToken);

    public Task RestoreProjectIssueAcknowledgementsAsync(Guid projectId, IReadOnlyList<Guid> acknowledgementIds,
        CancellationToken cancellationToken = default) => EnqueueAsync<object?>(async (connection, token) =>
    {
        using var transaction = connection.BeginTransaction();
        foreach (var id in acknowledgementIds.Distinct())
            await ExecuteAsync(connection, transaction,
                "DELETE FROM issue_acknowledgements WHERE project_id=$project AND id=$id;",
                [new("$project", projectId.ToString()), new("$id", id.ToString())], token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return null;
    }, cancellationToken);

    public Task RestoreProjectIssuesAsync(Guid projectId, string? sourcePath = null,
        CancellationToken cancellationToken = default) => EnqueueAsync<object?>(async (connection, token) =>
    {
        await ExecuteAsync(connection, null,
            "DELETE FROM issue_acknowledgements WHERE project_id=$project AND ($path IS NULL OR root_path_key=$path);",
            [new("$project", projectId.ToString()), new("$path", sourcePath is null ? DBNull.Value : IssuePathKey(sourcePath))],
            token).ConfigureAwait(false);
        return null;
    }, cancellationToken);

    public Task<FileExclusionResult> ExcludeFileAsync(Guid projectId, string sourcePath,
        CancellationToken cancellationToken = default) => EnqueueAsync(async (connection, token) =>
    {
        var path = CanonicalPath(sourcePath);
        var key = PathKey(path);
        if (Directory.Exists(path))
            throw new ContextMoleException("exact_file_required", "Exclusions apply to an exact file path, not a folder.");
        using var transaction = connection.BeginTransaction();
        await FindAuthorizedFolderAsync(connection, transaction, projectId, key, token).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var changed = await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO file_exclusions VALUES($project,$path,$source,$now);",
            [new("$project", projectId.ToString()), new("$path", key), new("$source", path), new("$now", now)], token)
            .ConfigureAwait(false) > 0;
        await RemoveExcludedEvidenceAsync(connection, transaction, projectId, key, token).ConfigureAwait(false);
        if (changed)
            await ExecuteAsync(connection, transaction,
                "UPDATE projects SET search_generation=search_generation+1,updated_utc=$now WHERE id=$project;",
                [new("$project", projectId.ToString()), new("$now", now)], token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new FileExclusionResult(changed, false);
    }, cancellationToken);

    public Task<FileExclusionResult> IncludeFileAsync(Guid projectId, string sourcePath,
        CancellationToken cancellationToken = default) => EnqueueAsync(async (connection, token) =>
    {
        var path = CanonicalPath(sourcePath);
        var key = PathKey(path);
        using var transaction = connection.BeginTransaction();
        var folderId = await FindAuthorizedFolderAsync(connection, transaction, projectId, key, token, allowOutside: true).ConfigureAwait(false);
        var changed = await ExecuteAsync(connection, transaction,
            "DELETE FROM file_exclusions WHERE project_id=$project AND path_key=$path;",
            [new("$project", projectId.ToString()), new("$path", key)], token).ConfigureAwait(false) > 0;
        if (!changed)
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new FileExclusionResult(false, false);
        }
        if (folderId is null)
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new FileExclusionResult(true, false);
        }
        if (!SupportedContent.IsSupported(path))
            throw new ContextMoleException("unsupported_file", "This file type cannot be indexed.");
        var now = DateTimeOffset.UtcNow.ToString("O");
        await ExecuteAsync(connection, transaction, """
            INSERT INTO documents(id,project_id,folder_id,path,path_key,file_name,extension,size,modified_utc,
                observation_epoch,tombstoned,available,created_utc,updated_utc)
            VALUES($id,$project,$folder,$path,$key,$name,$extension,0,$now,1,0,1,$now,$now)
            ON CONFLICT(project_id,path_key) DO UPDATE SET tombstoned=0,available=1,
              folder_id=$folder,observation_epoch=observation_epoch+1,sha256=NULL,
              failure_source_fingerprint=NULL,updated_utc=$now;
            """, [new("$id", Guid.CreateVersion7().ToString()), new("$project", projectId.ToString()),
                new("$folder", folderId.ToString()), new("$path", path), new("$key", key),
                new("$name", Path.GetFileName(path)), new("$extension", StoredExtension(path)), new("$now", now)], token)
            .ConfigureAwait(false);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT id,observation_epoch FROM documents WHERE project_id=$project AND path_key=$key;";
        select.Parameters.AddWithValue("$project", projectId.ToString());
        select.Parameters.AddWithValue("$key", key);
        Guid documentId; long epoch;
        await using (var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            await reader.ReadAsync(token).ConfigureAwait(false);
            documentId = Guid.Parse(reader.GetString(0)); epoch = reader.GetInt64(1);
        }
        await UpsertOpenJobAsync(connection, transaction, projectId, documentId, epoch, IndexJobKind.Reindex, now, token)
            .ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new FileExclusionResult(true, true);
    }, cancellationToken);

    public Task<RetryFailedFilesResult> RetryFileAsync(Guid projectId, Guid documentId,
        CancellationToken cancellationToken = default) => RetryFailedFilesCoreAsync(projectId, documentId, cancellationToken);

    private static async Task<Guid?> FindAuthorizedFolderAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid projectId, string pathKey, CancellationToken token, bool allowOutside = false)
    {
        await EnsureProjectExistsAsync(connection, transaction, projectId, token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,path_key FROM project_folders WHERE project_id=$project ORDER BY length(path_key) DESC;";
        command.Parameters.AddWithValue("$project", projectId.ToString());
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var root = reader.GetString(1);
            if (pathKey != root && IsSameOrChild(pathKey, root)) return Guid.Parse(reader.GetString(0));
        }
        if (allowOutside) return null;
        throw new ContextMoleException("file_outside_project", "Choose an exact file inside this project's watched folders.");
    }

    private static async Task RemoveExcludedEvidenceAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid projectId, string key, CancellationToken token)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT r.id FROM document_revisions r JOIN documents d ON r.document_id=d.id WHERE d.project_id=$project AND d.path_key=$path;";
        select.Parameters.AddWithValue("$project", projectId.ToString()); select.Parameters.AddWithValue("$path", key);
        var revisions = new List<Guid>();
        await using (var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) revisions.Add(Guid.Parse(reader.GetString(0)));
        foreach (var revision in revisions) await DeleteFtsRevisionAsync(connection, transaction, revision, token).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            UPDATE index_jobs SET state='completed',lease_until_utc=NULL,last_error=NULL
            WHERE document_id IN(SELECT id FROM documents WHERE project_id=$project AND path_key=$path);
            UPDATE documents SET tombstoned=1,available=0,observation_epoch=observation_epoch+1,
                active_revision_id=NULL,sha256=NULL,failure_source_fingerprint=NULL
            WHERE project_id=$project AND path_key=$path;
            DELETE FROM document_revisions WHERE document_id IN(SELECT id FROM documents WHERE project_id=$project AND path_key=$path);
            DELETE FROM project_errors WHERE project_id=$project AND (root_path_key=$path OR document_id IN(
              SELECT id FROM documents WHERE project_id=$project AND path_key=$path));
            DELETE FROM issue_acknowledgements WHERE project_id=$project AND root_path_key=$path;
            """, [new("$project", projectId.ToString()), new("$path", key)], token).ConfigureAwait(false);
        await PruneIssueLifecyclesAsync(connection, transaction, projectId, token).ConfigureAwait(false);
    }

    private static async Task RefreshIssueRootIdentityAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid projectId, Guid documentId, string sourcePath, CancellationToken token)
    {
        var root = PathKey(sourcePath);
        await using var select = connection.CreateCommand(); select.Transaction = transaction;
        select.CommandText = "SELECT id,source_fingerprint,component_key,code,message,created_utc,component_name FROM project_errors WHERE project_id=$project AND document_id=$document;";
        select.Parameters.AddWithValue("$project", projectId.ToString()); select.Parameters.AddWithValue("$document", documentId.ToString());
        var rows = new List<(long Id, string Signature, string Created)>();
        await using (var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                rows.Add((reader.GetInt64(0), IssueSignature(root, reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), CanonicalIssueCause(reader.GetString(4), reader.IsDBNull(6) ? null : reader.GetString(6))), reader.GetString(5)));
        foreach (var row in rows)
            await ExecuteAsync(connection, transaction, """
                UPDATE project_errors SET root_path_key=$root,source_path=$path,signature=$signature WHERE id=$id;
                INSERT OR IGNORE INTO issue_lifecycles VALUES($project,$signature,$created,$created,1);
                """, [new("$root", root), new("$path", sourcePath), new("$signature", row.Signature),
                    new("$id", row.Id), new("$project", projectId.ToString()), new("$created", row.Created)], token).ConfigureAwait(false);
        await PruneIssueLifecyclesAsync(connection, transaction, projectId, token).ConfigureAwait(false);
    }

    private static async Task<bool> IsExcludedPathAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid projectId, string key, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM file_exclusions WHERE project_id=$project AND path_key=$path);";
        command.Parameters.AddWithValue("$project", projectId.ToString()); command.Parameters.AddWithValue("$path", key);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 0;
    }

    private static async Task<bool> IsExcludedDocumentAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid projectId, Guid documentId, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM documents d JOIN file_exclusions x ON x.project_id=d.project_id AND x.path_key=d.path_key WHERE d.project_id=$project AND d.id=$document);";
        command.Parameters.AddWithValue("$project", projectId.ToString()); command.Parameters.AddWithValue("$document", documentId.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 0;
    }

    private static string IssuePathKey(string path) => string.IsNullOrWhiteSpace(path) ? string.Empty : PathKey(CanonicalPath(path));

    private static string CanonicalIssueCause(string message, string? componentName)
    {
        var prefix = componentName is null ? null : componentName + ": ";
        return prefix is not null && message.StartsWith(prefix, StringComparison.Ordinal)
            ? message[prefix.Length..].Trim() : message.Trim();
    }

    private static string IssueSignature(string root, string fingerprint, string component, string code, string message)
    {
        var signature = new StringBuilder();
        foreach (var part in new[] { root, fingerprint, component, code, message })
            signature.Append(part.EnumerateRunes().Count()).Append(':').Append(part);
        return signature.ToString();
    }

    private static async Task<string> LoadComponentKeyAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid contentId, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE chain(id,parent_id,ordinal,relationship,name,depth) AS (
              SELECT id,parent_id,ordinal,relationship,name,0 FROM content_nodes WHERE id=$content
              UNION ALL SELECT n.id,n.parent_id,n.ordinal,n.relationship,n.name,c.depth+1
              FROM content_nodes n JOIN chain c ON n.id=c.parent_id
            ) SELECT c.parent_id,c.ordinal,c.relationship,c.name,
                (SELECT COUNT(*) FROM content_nodes n WHERE n.parent_id=c.parent_id AND n.ordinal<c.ordinal
                  AND name_key(n.relationship)=name_key(c.relationship) AND name_key(n.name)=name_key(c.name))
              FROM chain c ORDER BY c.depth DESC;
            """;
        command.Parameters.AddWithValue("$content", contentId.ToString());
        var key = new StringBuilder("root");
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            if (!reader.IsDBNull(0)) key.Append('/').Append(TextNormalization.NameKey(reader.GetString(2))).Append('\u001f')
                .Append(TextNormalization.NameKey(reader.GetString(3))).Append('\u001f').Append(reader.GetInt32(4));
        return key.ToString();
    }

    private static Task<int> PruneIssueLifecyclesAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid projectId, CancellationToken token) => ExecuteAsync(connection, transaction,
        """
        DELETE FROM issue_lifecycles WHERE project_id=$project AND NOT EXISTS(
          SELECT 1 FROM project_errors e WHERE e.project_id=issue_lifecycles.project_id AND e.signature=issue_lifecycles.signature);
        DELETE FROM issue_acknowledgements WHERE project_id=$project AND NOT EXISTS(
          SELECT 1 FROM project_errors e WHERE e.project_id=issue_acknowledgements.project_id AND e.signature=issue_acknowledgements.signature);
        """,
        [new("$project", projectId.ToString())], token);
}
