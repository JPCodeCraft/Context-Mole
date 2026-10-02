using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using ContextMole.Core;
using Microsoft.Data.Sqlite;

namespace ContextMole.Storage;

public sealed partial class SqliteSearchStore
{
    private const string IssueGroupsSql = """
        WITH issue_rows AS (
          SELECT e.*,CASE WHEN a.id IS NULL THEN 0 ELSE 1 END AS hidden
          FROM project_errors e LEFT JOIN issue_acknowledgements a
            ON a.project_id=e.project_id AND a.signature=e.signature WHERE e.project_id=$project
        ), groups AS (
          SELECT e.root_path_key,MAX(e.document_id) document_id,
            COALESCE(MAX(d.path),MAX(e.source_path),'') source_path,
            COALESCE(MAX(d.file_name),'Project issue') file_name,COUNT(*) issue_count,SUM(e.hidden) hidden_count,
            CASE WHEN MAX(CASE WHEN d.active_revision_id IS NOT NULL AND EXISTS(
              SELECT 1 FROM passages p WHERE p.revision_id=d.active_revision_id) THEN 1 ELSE 0 END)=0 THEN 1
              WHEN MAX(CASE WHEN e.attempt=0 AND e.code<>'embedding_refresh_failed' THEN 1 ELSE 0 END)>0
                THEN 2 ELSE 3 END impact,
            MAX(e.retryable) retryable,MAX(e.attempt) attempt,
            MAX(CASE WHEN ($query='' OR instr(lower(COALESCE(d.path,e.source_path,'')),lower($query))>0
              OR instr(lower(e.message),lower($query))>0 OR instr(lower(e.code),lower($query))>0
              OR instr(lower(COALESCE(e.component_name,'')),lower($query))>0)
              AND ($code='' OR e.code=$code)
              AND ($visibility=2 OR ($visibility=0 AND e.hidden=0) OR ($visibility=1 AND e.hidden=1)) THEN 1 ELSE 0 END) matches_filter
          FROM issue_rows e LEFT JOIN documents d ON d.id=e.document_id
          GROUP BY e.root_path_key
        ), filtered AS (
          SELECT * FROM groups WHERE matches_filter=1 AND ($impact=0 OR impact=$impact)
            AND ($visibility=2 OR ($visibility=0 AND issue_count>hidden_count)
              OR ($visibility=1 AND hidden_count>0))
        )
        """;

    public async Task<ProjectIssueListResponse> ListProjectIssuesAsync(ProjectIssueListRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Limit is < 1 or > 100 || (request.Query?.Length ?? 0) > 500 || (request.Code?.Length ?? 0) > 200 ||
            !Enum.IsDefined(request.Impact) || !Enum.IsDefined(request.Visibility))
            throw new ContextMoleException("invalid_issue_request", "Use a page size from 1 to 100 and bounded issue filters.");
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var version = await ReadIssuesVersionAsync(connection, transaction, request.ProjectId, cancellationToken).ConfigureAwait(false);
        var filter = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { request.ProjectId, request.Query, request.Code, request.Impact, request.Visibility }))));
        var cursor = DecodeIssueCursor(request.Cursor, request.ProjectId, version, filter, allowVersionChange: true);
        var issuesChanged = cursor is not null && cursor.Version != version;
        int totalFiles, visibleFiles, hiddenFiles, totalIssues, hiddenIssues, filteredFiles, filteredIssues;
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = IssueGroupsSql + """
                SELECT COUNT(*),COALESCE(SUM(issue_count>hidden_count),0),COALESCE(SUM(hidden_count>0),0),
                  COALESCE(SUM(issue_count),0),COALESCE(SUM(hidden_count),0),
                  (SELECT COUNT(*) FROM filtered),(SELECT COALESCE(SUM(issue_count),0) FROM filtered) FROM groups;
                """;
            AddIssueFilterParameters(count, request);
            await using var reader = await count.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            totalFiles = reader.GetInt32(0); visibleFiles = reader.GetInt32(1); hiddenFiles = reader.GetInt32(2);
            totalIssues = reader.GetInt32(3); hiddenIssues = reader.GetInt32(4);
            filteredFiles = reader.GetInt32(5); filteredIssues = reader.GetInt32(6);
        }
        var rows = new List<(string Key, Guid? Document, string Path, string Name, int Count, int Hidden,
            ProjectIssueImpact Impact, bool Retryable, int Attempt, string? State, string? Due, int ProjectState)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = IssueGroupsSql + """
                SELECT f.root_path_key,f.document_id,f.source_path,f.file_name,f.issue_count,f.hidden_count,
                  f.impact,f.retryable,f.attempt,j.state,j.not_before_utc,p.state
                FROM filtered f JOIN projects p ON p.id=$project LEFT JOIN index_jobs j ON j.id=(
                  SELECT id FROM index_jobs WHERE document_id=f.document_id
                  ORDER BY CASE WHEN state IN('queued','retry_wait','running') THEN 0 ELSE 1 END,updated_utc DESC,id DESC LIMIT 1)
                WHERE ($after IS NULL OR f.root_path_key>$after) ORDER BY f.root_path_key LIMIT $limit;
                """;
            AddIssueFilterParameters(select, request);
            select.Parameters.AddWithValue("$after", (object?)cursor?.After ?? DBNull.Value);
            select.Parameters.AddWithValue("$limit", request.Limit + 1);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                    reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
                    (ProjectIssueImpact)reader.GetInt32(6), reader.GetInt32(7) != 0, reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetInt32(11)));
        }
        var groups = new List<ProjectIssueGroup>();
        foreach (var row in rows.Take(request.Limit))
        {
            var retry = row.State == "running" ? ProjectIssueRetryState.Running
                : row.ProjectState == (int)ProjectState.Paused && row.State is "queued" or "retry_wait" ? ProjectIssueRetryState.Paused
                : row.State == "retry_wait" && DateTimeOffset.Parse(row.Due!) > DateTimeOffset.UtcNow ? ProjectIssueRetryState.Scheduled
                : row.State is "queued" or "retry_wait" ? ProjectIssueRetryState.Queued
                : row.Retryable && row.Attempt > 5 ? ProjectIssueRetryState.Exhausted : ProjectIssueRetryState.Manual;
            var details = await LoadIssueDetailsAsync(connection, transaction, request.ProjectId, row.Key,
                request.Visibility, 50, null, version, cancellationToken).ConfigureAwait(false);
            groups.Add(new ProjectIssueGroup(row.Document, row.Path, row.Name, row.Impact, row.Count, row.Hidden,
                retry, retry == ProjectIssueRetryState.Scheduled ? DateTimeOffset.Parse(row.Due!) : null,
                row.Document is not null && row.ProjectState == (int)ProjectState.Active &&
                    retry is ProjectIssueRetryState.Manual or ProjectIssueRetryState.Exhausted,
                details.Details, details.NextCursor));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var next = rows.Count > request.Limit
            ? EncodeIssueCursor(new IssueCursor(request.ProjectId, version, filter, rows[request.Limit - 1].Key)) : null;
        return new ProjectIssueListResponse(request.ProjectId, totalFiles, visibleFiles, hiddenFiles, totalIssues,
            hiddenIssues, groups, next) { FilteredFileCount = filteredFiles, FilteredIssueCount = filteredIssues, IssuesChangedDuringPaging = issuesChanged };
    }

    public async Task<ProjectIssueDetailsResponse> ListProjectIssueDetailsAsync(Guid projectId, string sourcePath,
        ProjectIssueVisibility visibility = ProjectIssueVisibility.All, int limit = 50, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100 || !Enum.IsDefined(visibility))
            throw new ContextMoleException("invalid_issue_request", "Use a detail page size from 1 to 100.");
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var version = await ReadIssuesVersionAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
        var key = string.IsNullOrWhiteSpace(sourcePath) ? string.Empty : DatabaseWriterService.PathKey(DatabaseWriterService.CanonicalPath(sourcePath));
        var result = await LoadIssueDetailsAsync(connection, transaction, projectId, key, visibility, limit, cursor,
            version, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<ProjectIssueDetailsResponse> LoadIssueDetailsAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid projectId, string rootKey, ProjectIssueVisibility visibility, int limit,
        string? encodedCursor, string version, CancellationToken token)
    {
        version = await ReadIssuesVersionAsync(connection, transaction, projectId, token, rootKey).ConfigureAwait(false);
        var filter = rootKey + ":" + visibility;
        var cursor = DecodeIssueCursor(encodedCursor, projectId, version, filter);
        var after = cursor is null ? 0 : long.Parse(cursor.After, System.Globalization.CultureInfo.InvariantCulture);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT e.id,e.signature,e.content_id,e.component_key,e.component_name,e.code,e.message,e.retryable,
              e.attempt,e.created_utc,a.id IS NOT NULL,COALESCE(l.first_seen_utc,e.created_utc),
              COALESCE(l.last_seen_utc,e.created_utc),COALESCE(l.occurrence_count,1)
            FROM project_errors e LEFT JOIN issue_acknowledgements a ON a.project_id=e.project_id AND a.signature=e.signature
            LEFT JOIN issue_lifecycles l ON l.project_id=e.project_id AND l.signature=e.signature
            WHERE e.project_id=$project AND e.root_path_key=$root AND e.id>$after
              AND ($visibility=2 OR ($visibility=0 AND a.id IS NULL) OR ($visibility=1 AND a.id IS NOT NULL))
            ORDER BY e.id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$project", projectId.ToString()); command.Parameters.AddWithValue("$root", rootKey);
        command.Parameters.AddWithValue("$after", after); command.Parameters.AddWithValue("$visibility", (int)visibility);
        command.Parameters.AddWithValue("$limit", limit + 1);
        var details = new List<ProjectIssueDetail>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            details.Add(new ProjectIssueDetail(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetInt32(7) != 0, reader.GetInt32(8), DateTimeOffset.Parse(reader.GetString(9)), reader.GetInt32(10) != 0)
                { FirstSeenUtc = DateTimeOffset.Parse(reader.GetString(11)), LastSeenUtc = DateTimeOffset.Parse(reader.GetString(12)), OccurrenceCount = reader.GetInt32(13) });
        var next = details.Count > limit
            ? EncodeIssueCursor(new IssueCursor(projectId, version, filter, details[limit - 1].Id.ToString(System.Globalization.CultureInfo.InvariantCulture))) : null;
        return new ProjectIssueDetailsResponse(details.Take(limit).ToArray(), next);
    }

    public async Task<bool> IsFileExcludedAsync(Guid projectId, string sourcePath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM file_exclusions WHERE project_id=$project AND path_key=$path);";
        command.Parameters.AddWithValue("$project", projectId.ToString());
        command.Parameters.AddWithValue("$path", DatabaseWriterService.PathKey(DatabaseWriterService.CanonicalPath(sourcePath)));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    public async Task<IReadOnlyList<ExcludedFileInfo>> ListExcludedFilesAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_path,excluded_utc FROM file_exclusions WHERE project_id=$project ORDER BY path_key;";
        command.Parameters.AddWithValue("$project", projectId.ToString());
        var rows = new List<ExcludedFileInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(new(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1))));
        return rows;
    }

    private static void AddIssueFilterParameters(SqliteCommand command, ProjectIssueListRequest request)
    {
        command.Parameters.AddWithValue("$project", request.ProjectId.ToString());
        command.Parameters.AddWithValue("$query", request.Query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$code", request.Code?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$impact", (int)request.Impact); command.Parameters.AddWithValue("$visibility", (int)request.Visibility);
    }

    private static async Task<string> ReadIssuesVersionAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid projectId, CancellationToken token, string? rootKey = null)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$project", projectId.ToString());
        if (rootKey is null)
        {
            command.CommandText = "SELECT issue_generation FROM projects WHERE id=$project;";
            return Convert.ToString(await command.ExecuteScalarAsync(token).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) ?? "0";
        }
        command.CommandText = """
            SELECT e.id,e.signature,COALESCE(a.id,'') FROM project_errors e
            LEFT JOIN issue_acknowledgements a ON a.project_id=e.project_id AND a.signature=e.signature
            WHERE e.project_id=$project AND e.root_path_key=$root ORDER BY e.id;
            """;
        command.Parameters.AddWithValue("$root", rootKey);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            foreach (var value in new[] { reader.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(1), reader.GetString(2) })
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed record IssueCursor(Guid ProjectId, string Version, string Filter, string After);
    private static string EncodeIssueCursor(IssueCursor cursor) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cursor)));
    private static IssueCursor? DecodeIssueCursor(string? encoded, Guid project, string version, string filter, bool allowVersionChange = false)
    {
        if (encoded is null) return null;
        try
        {
            if (encoded.Length > 16000) throw new FormatException();
            var cursor = JsonSerializer.Deserialize<IssueCursor>(Convert.FromBase64String(encoded));
            if (cursor is null || cursor.ProjectId != project || cursor.Filter != filter) throw new FormatException();
            if (!allowVersionChange && cursor.Version != version) throw new ContextMoleException("issues_changed", "Issues changed while paging. Refresh the list.");
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ContextMoleException("invalid_cursor", "The issue cursor does not match the current filters.");
        }
    }
}
