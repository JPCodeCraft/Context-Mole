using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MCPIndexSearch.Core;
using Microsoft.Data.Sqlite;

namespace MCPIndexSearch.Storage;

public sealed class SqliteSearchStore : ISearchStore
{
    private static readonly JsonSerializerOptions CursorJsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private readonly IAppPaths _paths;

    public SqliteSearchStore(IAppPaths paths) => _paths = paths;

    public async Task<bool> IsInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.DatabasePath))
        {
            return false;
        }

        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
            var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return version is not null and not DBNull && Convert.ToInt32(version) == Schema.CurrentVersion;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsInitializedAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id,p.name,p.state,p.search_generation,
              (SELECT COUNT(*) FROM documents d WHERE d.project_id=p.id AND d.tombstoned=0),
              (SELECT COUNT(*) FROM index_jobs j WHERE j.project_id=p.id AND j.state IN ('queued','retry_wait','running')),
              (SELECT COUNT(*) FROM documents d WHERE d.project_id=p.id AND d.tombstoned=0 AND d.active_revision_id IS NOT NULL),
              (SELECT COUNT(*) FROM project_errors e WHERE e.project_id=p.id),
              (SELECT MAX(completed_utc) FROM index_runs r WHERE r.project_id=p.id AND r.state='completed'),
              (SELECT d.path FROM index_jobs j JOIN documents d ON d.id=j.document_id WHERE j.project_id=p.id AND j.state='running' ORDER BY j.updated_utc LIMIT 1)
            FROM projects p ORDER BY p.name_key;
            """;
        var rows = new List<(Guid Id, string Name, ProjectState State, long Generation, int Documents, int Pending, int Indexed, int Errors, DateTimeOffset? Last, string? Current)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), (ProjectState)reader.GetInt32(2),
                    reader.GetInt64(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
        }

        var projects = new List<ProjectSummary>(rows.Count);
        foreach (var row in rows)
        {
            var folders = await LoadFoldersAsync(connection, row.Id, cancellationToken).ConfigureAwait(false);
            projects.Add(new ProjectSummary(row.Id, row.Name, row.State, folders, row.Generation, row.Documents,
                row.Pending, row.Indexed, row.Errors, row.Last, row.Current));
        }

        return projects;
    }

    public async Task<DocumentListResponse> ListDocumentsAsync(DocumentListRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateDocumentListRequest(request);
        try
        {
            await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
            connection.CreateCollation("UNICODE_NOCASE", (left, right) =>
                string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
            connection.CreateFunction("unicode_contains", (string value, string query) =>
                value.Contains(query, StringComparison.OrdinalIgnoreCase), isDeterministic: true);
            using var transaction = connection.BeginTransaction(deferred: true);

            ProjectState projectState;
            long searchGeneration;
            await using (var projectCommand = connection.CreateCommand())
            {
                projectCommand.Transaction = transaction;
                projectCommand.CommandText = "SELECT state,search_generation FROM projects WHERE id=$project;";
                projectCommand.Parameters.AddWithValue("$project", request.ProjectId.ToString());
                await using var reader = await projectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new McpIndexException("project_not_found", "The indexing project does not exist.");
                projectState = (ProjectState)reader.GetInt32(0);
                searchGeneration = reader.GetInt64(1);
            }

            var folders = new List<(string Path, string PathKey)>();
            await using (var folderCommand = connection.CreateCommand())
            {
                folderCommand.Transaction = transaction;
                folderCommand.CommandText = "SELECT path,path_key FROM project_folders WHERE project_id=$project ORDER BY path_key;";
                folderCommand.Parameters.AddWithValue("$project", request.ProjectId.ToString());
                await using var reader = await folderCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    folders.Add((reader.GetString(0), reader.GetString(1)));
            }

            var extensions = NormalizeExtensions(request.Extensions);
            var pathPrefixes = NormalizeAuthorizedPathPrefixes(request.PathPrefixes, folders);
            var nameQuery = string.IsNullOrWhiteSpace(request.NameQuery) ? null : request.NameQuery.Trim();
            var filterFingerprint = DocumentFilterFingerprint(request, extensions, pathPrefixes, nameQuery);
            var cursor = DecodeDocumentCursor(request.Cursor, request, searchGeneration, filterFingerprint);
            var sortExpression = DocumentSortExpression(request.SortBy);
            var direction = request.SortDirection == DocumentSortDirection.Asc ? "ASC" : "DESC";

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var sql = new StringBuilder("""
                WITH ranked_jobs AS (
                  SELECT document_id,state,updated_utc,id,
                    ROW_NUMBER() OVER (
                      PARTITION BY document_id
                      ORDER BY CASE WHEN state IN ('queued','retry_wait','running') THEN 0 ELSE 1 END,
                               updated_utc DESC,id DESC
                    ) AS job_rank
                  FROM index_jobs WHERE project_id=$project
                ),
                current_jobs AS (
                  SELECT document_id,state FROM ranked_jobs WHERE job_rank=1
                ),
                ranked_errors AS (
                  SELECT document_id,code,message,
                    COUNT(*) OVER (PARTITION BY document_id) AS error_count,
                    ROW_NUMBER() OVER (PARTITION BY document_id ORDER BY created_utc DESC,id DESC) AS error_rank
                  FROM project_errors WHERE project_id=$project AND document_id IS NOT NULL
                ),
                document_errors AS (
                  SELECT document_id,code,message,error_count FROM ranked_errors WHERE error_rank=1
                ),
                content_counts AS (
                  SELECT c.revision_id,COUNT(*) AS content_count,
                    SUM(CASE WHEN c.depth>0 THEN 1 ELSE 0 END) AS attachment_count
                  FROM content_nodes c
                  WHERE c.revision_id IN (
                    SELECT active_revision_id FROM documents
                    WHERE project_id=$project AND tombstoned=0 AND active_revision_id IS NOT NULL
                  )
                  GROUP BY c.revision_id
                ),
                passage_counts AS (
                  SELECT passage.revision_id,COUNT(*) AS passage_count
                  FROM passages passage
                  WHERE passage.revision_id IN (
                    SELECT active_revision_id FROM documents
                    WHERE project_id=$project AND tombstoned=0 AND active_revision_id IS NOT NULL
                  )
                  GROUP BY passage.revision_id
                ),
                inventory AS (
                  SELECT document.id AS document_id,document.folder_id,document.path AS source_path,
                    document.path_key,document.file_name,document.extension AS file_type,root.mime_type,
                    document.size AS size_bytes,document.modified_utc,
                    CASE
                      WHEN job.state='running' THEN 'processing'
                      WHEN job.state IN ('queued','retry_wait') AND $project_state<>$active_state THEN 'paused'
                      WHEN job.state IN ('queued','retry_wait') THEN 'pending'
                      WHEN COALESCE(error.error_count,0)>0 OR job.state='failed' THEN 'error'
                      WHEN document.active_revision_id IS NOT NULL THEN 'indexed'
                      WHEN $project_state<>$active_state THEN 'paused'
                      ELSE 'pending'
                    END AS current_status,
                    COALESCE(content.content_count,0) AS content_count,
                    COALESCE(content.attachment_count,0) AS attachment_count,
                    COALESCE(passage.passage_count,0) AS passage_count,
                    COALESCE(error.error_count,0) AS error_count,error.code AS error_code,error.message AS error_message,
                    revision.sha256 AS indexed_fingerprint,revision.id AS index_revision_id,
                    revision.activated_utc AS last_indexed_utc
                  FROM documents document
                  JOIN project_folders authorized_folder
                    ON authorized_folder.id=document.folder_id AND authorized_folder.project_id=document.project_id
                  LEFT JOIN current_jobs job ON job.document_id=document.id
                  LEFT JOIN document_errors error ON error.document_id=document.id
                  LEFT JOIN document_revisions revision
                    ON revision.id=document.active_revision_id AND revision.status='active'
                  LEFT JOIN content_nodes root ON root.id=(
                    SELECT root_node.id FROM content_nodes root_node
                    WHERE root_node.revision_id=document.active_revision_id AND root_node.parent_id IS NULL
                    ORDER BY root_node.ordinal,root_node.id LIMIT 1
                  )
                  LEFT JOIN content_counts content ON content.revision_id=document.active_revision_id
                  LEFT JOIN passage_counts passage ON passage.revision_id=document.active_revision_id
                  WHERE document.project_id=$project AND document.tombstoned=0
                )
                SELECT document_id,folder_id,source_path,file_name,file_type,mime_type,size_bytes,modified_utc,
                  current_status,content_count,attachment_count,passage_count,error_count,error_code,error_message,
                  indexed_fingerprint,index_revision_id,last_indexed_utc,
                """);
            sql.Append(sortExpression).Append(" AS cursor_value FROM inventory WHERE 1=1");
            command.Parameters.AddWithValue("$project", request.ProjectId.ToString());
            command.Parameters.AddWithValue("$project_state", (int)projectState);
            command.Parameters.AddWithValue("$active_state", (int)ProjectState.Active);

            if (request.Status != DocumentStatusFilter.All)
            {
                sql.Append(" AND current_status=$status");
                command.Parameters.AddWithValue("$status", StatusValue(request.Status));
            }
            if (extensions.Count > 0)
            {
                sql.Append(" AND file_type IN (");
                for (var index = 0; index < extensions.Count; index++)
                {
                    if (index > 0) sql.Append(',');
                    var parameter = $"$extension{index}";
                    sql.Append(parameter);
                    command.Parameters.AddWithValue(parameter, extensions[index]);
                }
                sql.Append(')');
            }
            if (pathPrefixes.Count > 0)
            {
                sql.Append(" AND (");
                for (var index = 0; index < pathPrefixes.Count; index++)
                {
                    if (index > 0) sql.Append(" OR ");
                    var parameter = $"$path{index}";
                    sql.Append($"(path_key={parameter} OR (substr(path_key,1,length({parameter}))={parameter} AND substr(path_key,length({parameter})+1,1) IN ('/','\\'))) ");
                    command.Parameters.AddWithValue(parameter, pathPrefixes[index]);
                }
                sql.Append(')');
            }
            if (nameQuery is not null)
            {
                sql.Append(" AND unicode_contains(file_name,$name_query)=1");
                command.Parameters.AddWithValue("$name_query", nameQuery);
            }
            if (request.ModifiedFromUtc is not null)
            {
                sql.Append(" AND modified_utc >= $modified_from");
                command.Parameters.AddWithValue("$modified_from", request.ModifiedFromUtc.Value.ToUniversalTime().ToString("O"));
            }
            if (request.ModifiedToUtc is not null)
            {
                sql.Append(" AND modified_utc <= $modified_to");
                command.Parameters.AddWithValue("$modified_to", request.ModifiedToUtc.Value.ToUniversalTime().ToString("O"));
            }
            AppendDocumentCursorPredicate(sql, command, cursor, sortExpression, request.SortDirection);
            sql.Append(" ORDER BY ").Append(sortExpression).Append(' ').Append(direction)
                .Append(",document_id ASC LIMIT $take;");
            command.Parameters.AddWithValue("$take", request.Limit + 1);
            command.CommandText = sql.ToString();

            var rows = new List<(DocumentInventoryItem Item, string? CursorValue)>();
            await using var resultReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await resultReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var status = Enum.Parse<DocumentInventoryStatus>(resultReader.GetString(8), ignoreCase: true);
                var errorSummary = BuildErrorSummary(resultReader.IsDBNull(13) ? null : resultReader.GetString(13),
                    resultReader.IsDBNull(14) ? null : resultReader.GetString(14));
                var item = new DocumentInventoryItem(Guid.Parse(resultReader.GetString(0)), Guid.Parse(resultReader.GetString(1)),
                    resultReader.GetString(2), resultReader.GetString(3), resultReader.GetString(4),
                    resultReader.IsDBNull(5) ? null : resultReader.GetString(5), resultReader.GetInt64(6),
                    DateTimeOffset.Parse(resultReader.GetString(7)), status, resultReader.GetInt32(9),
                    resultReader.GetInt32(10), resultReader.GetInt32(11), resultReader.GetInt32(12), errorSummary,
                    resultReader.IsDBNull(15) ? null : resultReader.GetString(15),
                    resultReader.IsDBNull(16) ? null : Guid.Parse(resultReader.GetString(16)),
                    resultReader.IsDBNull(17) ? null : DateTimeOffset.Parse(resultReader.GetString(17)));
                rows.Add((item, resultReader.IsDBNull(18) ? null : resultReader.GetString(18)));
            }

            var hasNext = rows.Count > request.Limit;
            if (hasNext)
                rows.RemoveAt(rows.Count - 1);
            var items = rows.Select(row => row.Item).ToArray();
            var nextCursor = hasNext && rows.Count > 0
                ? EncodeDocumentCursor(new DocumentCursor(1, request.ProjectId, searchGeneration, filterFingerprint,
                    request.SortBy, request.SortDirection, rows[^1].CursorValue, rows[^1].Item.DocumentId))
                : null;
            return new DocumentListResponse(request.ProjectId, searchGeneration, items.Length, items, nextCursor);
        }
        catch (SqliteException exception)
        {
            throw new McpIndexException("index_unavailable", $"The document index is unavailable: {exception.Message}", true);
        }
    }

    public async Task<IReadOnlyList<ProjectErrorInfo>> ListProjectErrorsAsync(Guid projectId, int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,project_id,document_id,code,message,retryable,attempt,created_utc,source_path
            FROM project_errors WHERE project_id=$project ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$project", projectId.ToString());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        var errors = new List<ProjectErrorInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            errors.Add(new ProjectErrorInfo(reader.GetInt64(0), Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), reader.GetString(3), reader.GetString(4),
                reader.GetInt64(5) != 0, reader.GetInt32(6), DateTimeOffset.Parse(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return errors;
    }

    public async Task<KeywordSearchPage> KeywordSearchAsync(Guid projectId, string ftsQuery, int count,
        SearchFilters? filters, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var generation = await ReadGenerationAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ftsQuery))
        {
            return new KeywordSearchPage(generation, []);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var sql = new StringBuilder("""
            SELECT p.id,d.id,c.id,p.display_text,d.path,d.file_name,d.extension,d.modified_utc,
                   p.location_kind,p.page,p.sheet,p.cell_range,p.slide,p.structure_path,p.email_part,p.image_frame,
                   p.extraction_method,p.ocr_confidence,c.depth,-bm25(passages_fts)
            FROM passages_fts
            JOIN passages p ON p.rowid=passages_fts.rowid
            JOIN document_revisions r ON r.id=p.revision_id AND r.status='active'
            JOIN documents d ON d.id=r.document_id AND d.active_revision_id=r.id AND d.tombstoned=0
            JOIN content_nodes c ON c.id=p.content_id
            WHERE passages_fts MATCH $query AND d.project_id=$project
            """);
        command.Parameters.AddWithValue("$query", ftsQuery);
        command.Parameters.AddWithValue("$project", projectId.ToString());
        AppendFilters(sql, command, filters, "d", "c");
        sql.Append(" ORDER BY bm25(passages_fts),p.id LIMIT $limit;");
        command.Parameters.AddWithValue("$limit", Math.Clamp(count, 1, 500));
        command.CommandText = sql.ToString();
        var rows = await ReadCandidateRowsAsync(connection, command, includeScore: true, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new KeywordSearchPage(generation, rows);
    }

    public async Task<VectorSnapshot> LoadVectorSnapshotAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var generation = await ReadGenerationAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
        await using var metadata = connection.CreateCommand();
        metadata.Transaction = transaction;
        metadata.CommandText = """
            SELECT COUNT(*),COUNT(DISTINCT r.embedding_policy_json),MIN(r.embedding_policy_json)
            FROM embeddings e
            JOIN document_revisions r ON r.id=e.revision_id AND r.status='active'
            JOIN documents d ON d.id=r.document_id AND d.active_revision_id=r.id AND d.tombstoned=0
            WHERE d.project_id=$project;
            """;
        metadata.Parameters.AddWithValue("$project", projectId.ToString());
        long total;
        int policyCount;
        EmbeddingPolicy? policy;
        await using (var metadataReader = await metadata.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await metadataReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            total = metadataReader.GetInt64(0);
            policyCount = metadataReader.GetInt32(1);
            policy = metadataReader.IsDBNull(2) ? null : JsonSerializer.Deserialize<EmbeddingPolicy>(metadataReader.GetString(2));
        }
        if (total == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new VectorSnapshot(generation, null, []);
        }
        if (policyCount != 1 || policy is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new VectorSnapshot(generation, null, [], Warning: "The active project contains incompatible embedding policy generations.");
        }
        if (total * 1664L > 512L * 1024 * 1024)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new VectorSnapshot(generation, policy, [], RequiresStreaming: true);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT e.passage_id,d.id,c.id,d.path,d.extension,d.modified_utc,c.depth,e.vector,r.embedding_policy_json
            FROM embeddings e
            JOIN passages p ON p.rowid=e.passage_rowid
            JOIN document_revisions r ON r.id=e.revision_id AND r.status='active'
            JOIN documents d ON d.id=r.document_id AND d.active_revision_id=r.id AND d.tombstoned=0
            JOIN content_nodes c ON c.id=p.content_id
            WHERE d.project_id=$project ORDER BY e.passage_rowid;
            """;
        command.Parameters.AddWithValue("$project", projectId.ToString());
        var entries = new List<VectorEntry>();
        var incompatible = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(8))
            {
                continue;
            }

            var current = JsonSerializer.Deserialize<EmbeddingPolicy>(reader.GetString(8));
            if (current is null)
            {
                continue;
            }

            if (!string.Equals(policy.Key, current.Key, StringComparison.Ordinal))
            {
                incompatible = true;
                break;
            }

            var bytes = (byte[])reader[7];
            if (bytes.Length != 1536)
            {
                incompatible = true;
                break;
            }

            entries.Add(new VectorEntry(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)), reader.GetString(3), reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)), reader.GetInt32(6) > 0,
                MemoryMarshal.Cast<byte, float>(bytes.AsSpan()).ToArray()));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return incompatible
            ? new VectorSnapshot(generation, null, [], Warning: "An embedding vector or policy is invalid.")
            : new VectorSnapshot(generation, policy, entries);
    }

    public async IAsyncEnumerable<VectorEntry> StreamVectorEntriesAsync(Guid projectId, long expectedGeneration,
        SearchFilters? filters, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var generation = await ReadGenerationAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
        if (generation != expectedGeneration)
            throw new McpIndexException("index_changed", "The project index changed before semantic streaming began.", true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var sql = new StringBuilder("""
            SELECT e.passage_id,d.id,c.id,d.path,d.extension,d.modified_utc,c.depth,e.vector
            FROM embeddings e
            JOIN passages p ON p.rowid=e.passage_rowid
            JOIN document_revisions r ON r.id=e.revision_id AND r.status='active'
            JOIN documents d ON d.id=r.document_id AND d.active_revision_id=r.id AND d.tombstoned=0
            JOIN content_nodes c ON c.id=p.content_id
            WHERE d.project_id=$project
            """);
        command.Parameters.AddWithValue("$project", projectId.ToString());
        AppendFilters(sql, command, filters, "d", "c");
        sql.Append(" ORDER BY e.passage_rowid;");
        command.CommandText = sql.ToString();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var bytes = (byte[])reader[7];
            if (bytes.Length != 1536) continue;
            yield return new VectorEntry(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)), reader.GetString(3), reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)), reader.GetInt32(6) > 0,
                MemoryMarshal.Cast<byte, float>(bytes.AsSpan()).ToArray());
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchCandidate>> LoadCandidatesAsync(Guid projectId,
        IReadOnlyCollection<Guid> passageIds, long expectedGeneration, CancellationToken cancellationToken = default)
    {
        if (passageIds.Count == 0)
        {
            return [];
        }

        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var generation = await ReadGenerationAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
        if (generation != expectedGeneration)
            throw new McpIndexException("index_changed", "The project index changed before semantic candidates were loaded.", true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var sql = new StringBuilder("""
            SELECT p.id,d.id,c.id,p.display_text,d.path,d.file_name,d.extension,d.modified_utc,
                   p.location_kind,p.page,p.sheet,p.cell_range,p.slide,p.structure_path,p.email_part,p.image_frame,
                   p.extraction_method,p.ocr_confidence,c.depth,NULL
            FROM passages p
            JOIN document_revisions r ON r.id=p.revision_id AND r.status='active'
            JOIN documents d ON d.id=r.document_id AND d.active_revision_id=r.id AND d.tombstoned=0
            JOIN content_nodes c ON c.id=p.content_id
            WHERE d.project_id=$project AND p.id IN (
            """);
        command.Parameters.AddWithValue("$project", projectId.ToString());
        AppendGuidParameters(sql, command, passageIds, "passage");
        sql.Append(");");
        command.CommandText = sql.ToString();
        var candidates = await ReadCandidateRowsAsync(connection, command, includeScore: false, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return candidates;
    }

    public async Task<IReadOnlyList<PassageInfo>> ReadPassagesAsync(Guid projectId,
        IReadOnlyCollection<Guid> passageIds, int contextBefore, int contextAfter,
        CancellationToken cancellationToken = default)
    {
        if (passageIds.Count is < 1 or > 50)
        {
            throw new McpIndexException("invalid_request", "read_passages accepts between 1 and 50 passage IDs.");
        }

        contextBefore = Math.Clamp(contextBefore, 0, 3);
        contextAfter = Math.Clamp(contextAfter, 0, 3);
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        var requested = passageIds.ToHashSet();
        var found = new Dictionary<Guid, PassageInfo>();

        foreach (var passageId in passageIds)
        {
            var anchor = await LoadPassageAnchorAsync(connection, projectId, passageId, cancellationToken).ConfigureAwait(false);
            if (anchor is null)
            {
                found[passageId] = new PassageInfo(passageId, Guid.Empty, Guid.Empty, 0, string.Empty, string.Empty,
                    string.Empty, string.Empty, DateTimeOffset.MinValue, new SourceLocation(LocationKind.Document), [],
                    ExtractionMethod.Unsupported, null, true, "stale_passage");
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT p.id,d.id,c.id,p.ordinal,p.display_text,d.path,d.file_name,d.extension,d.modified_utc,
                       p.location_kind,p.page,p.sheet,p.cell_range,p.slide,p.structure_path,p.email_part,p.image_frame,
                       p.extraction_method,p.ocr_confidence
                FROM passages p
                JOIN document_revisions r ON r.id=p.revision_id AND r.status='active'
                JOIN documents d ON d.id=r.document_id AND d.active_revision_id=r.id AND d.tombstoned=0
                JOIN content_nodes c ON c.id=p.content_id
                WHERE d.project_id=$project AND p.content_id=$content AND p.ordinal BETWEEN $start AND $end
                ORDER BY p.ordinal;
                """;
            command.Parameters.AddWithValue("$project", projectId.ToString());
            command.Parameters.AddWithValue("$content", anchor.Value.ContentId.ToString());
            command.Parameters.AddWithValue("$start", Math.Max(0, anchor.Value.Ordinal - contextBefore));
            command.Parameters.AddWithValue("$end", anchor.Value.Ordinal + contextAfter);
            var rows = new List<PassageRow>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(ReadPassageRow(reader));
                }
            }

            foreach (var row in rows)
            {
                var chain = await LoadAttachmentChainAsync(connection, row.ContentId, cancellationToken).ConfigureAwait(false);
                found[row.PassageId] = new PassageInfo(row.PassageId, row.DocumentId, row.ContentId, row.Ordinal,
                    row.Text, row.SourcePath, row.FileName, row.FileType, row.ModifiedUtc, row.Location, chain,
                    row.Method, row.OcrConfidence, requested.Contains(row.PassageId));
            }
        }

        return found.Values.OrderByDescending(item => item.Requested).ThenBy(item => item.ContentId).ThenBy(item => item.Ordinal).ToArray();
    }

    public async Task<DocumentInfo?> GetDocumentInfoAsync(Guid projectId, Guid documentId, Guid? contentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        if (contentId is not null && !await ContentBelongsToDocumentAsync(connection, documentId, contentId.Value, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id,d.project_id,d.path,d.file_name,d.extension,d.size,d.modified_utc,d.sha256,d.active_revision_id,d.available,
              (SELECT COUNT(*) FROM passages p WHERE p.revision_id=d.active_revision_id),
              (SELECT COUNT(*) FROM content_nodes c WHERE c.revision_id=d.active_revision_id AND c.depth>0)
            FROM documents d WHERE d.id=$document AND d.project_id=$project AND d.tombstoned=0;
            """;
        command.Parameters.AddWithValue("$document", documentId.ToString());
        command.Parameters.AddWithValue("$project", projectId.ToString());
        DocumentInfo? info = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var path = reader.GetString(2);
                var available = reader.GetInt64(9) != 0 && File.Exists(path);
                info = new DocumentInfo(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), path,
                    reader.GetString(3), reader.GetString(4), reader.GetInt64(5), DateTimeOffset.Parse(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : reader.GetString(7), !reader.IsDBNull(8), available,
                    reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)), reader.GetInt32(10), reader.GetInt32(11),
                    new Dictionary<ExtractionMethod, int>(), []);
            }
        }

        if (info is null)
        {
            return null;
        }

        var summary = new Dictionary<ExtractionMethod, int>();
        if (info.ActiveRevisionId is { } activeRevision)
        {
            await using var summaryCommand = connection.CreateCommand();
            summaryCommand.CommandText = "SELECT extraction_method,COUNT(*) FROM passages WHERE revision_id=$revision GROUP BY extraction_method;";
            summaryCommand.Parameters.AddWithValue("$revision", activeRevision.ToString());
            await using var summaryReader = await summaryCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await summaryReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                summary[(ExtractionMethod)summaryReader.GetInt32(0)] = summaryReader.GetInt32(1);
        }
        var errors = await LoadDocumentErrorsAsync(connection, projectId, documentId, cancellationToken).ConfigureAwait(false);
        return info with { ExtractionSummary = summary, Errors = errors };
    }

    public async Task<AttachmentPage> ListAttachmentsAsync(Guid projectId, Guid documentId, string? cursor,
        int limit, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var offset = DecodeCursor(cursor);
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE tree(id,parent_id,ordinal,name,mime_type,relationship,depth,status,sort_path) AS (
              SELECT c.id,c.parent_id,c.ordinal,c.name,c.mime_type,c.relationship,c.depth,c.status,printf('%08d',c.ordinal)
              FROM content_nodes c JOIN documents d ON d.active_revision_id=c.revision_id
              WHERE d.id=$document AND d.project_id=$project AND c.parent_id IS NULL
              UNION ALL
              SELECT c.id,c.parent_id,c.ordinal,c.name,c.mime_type,c.relationship,c.depth,c.status,tree.sort_path||'.'||printf('%08d',c.ordinal)
              FROM content_nodes c JOIN tree ON c.parent_id=tree.id
            )
            SELECT id,parent_id,depth,ordinal,name,mime_type,relationship,status
            FROM tree WHERE depth>0 ORDER BY sort_path,id LIMIT $take OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$document", documentId.ToString());
        command.Parameters.AddWithValue("$project", projectId.ToString());
        command.Parameters.AddWithValue("$take", limit + 1);
        command.Parameters.AddWithValue("$offset", offset);
        var items = new List<AttachmentInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new AttachmentInfo(Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        }

        var hasNext = items.Count > limit;
        if (hasNext)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new AttachmentPage(items, hasNext ? EncodeCursor(offset + limit) : null);
    }

    public async Task<ResolvedLocalFile?> ResolveLocalFileAsync(Guid projectId, Guid documentId, Guid? contentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        if (contentId is not null && !await ContentBelongsToDocumentAsync(connection, documentId, contentId.Value, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM documents WHERE id=$document AND project_id=$project AND tombstoned=0;";
        command.Parameters.AddWithValue("$document", documentId.ToString());
        command.Parameters.AddWithValue("$project", projectId.ToString());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not string path)
        {
            return null;
        }

        var available = File.Exists(path);
        var resident = available && IsResident(path);
        var chain = contentId is null ? [] : await LoadAttachmentChainAsync(connection, contentId.Value, cancellationToken).ConfigureAwait(false);
        return new ResolvedLocalFile(documentId, contentId, path, available, resident, chain);
    }

    public async Task<IndexedContentMaterialization?> GetContentMaterializationAsync(Guid projectId, Guid contentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id,d.path,pf.path,d.size,d.modified_utc,r.sha256,r.id
            FROM content_nodes c
            JOIN document_revisions r ON r.id=c.revision_id AND r.status='active'
            JOIN documents d ON d.active_revision_id=r.id AND d.id=r.document_id AND d.tombstoned=0
            JOIN project_folders pf ON pf.id=d.folder_id AND pf.project_id=d.project_id
            WHERE d.project_id=$project AND c.id=$content;
            """;
        command.Parameters.AddWithValue("$project", projectId.ToString());
        command.Parameters.AddWithValue("$content", contentId.ToString());

        (Guid DocumentId, string SourcePath, string FolderPath, long Size, DateTimeOffset Modified,
            string Fingerprint, Guid RevisionId)? row = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                row = (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                    DateTimeOffset.Parse(reader.GetString(4)), reader.GetString(5), Guid.Parse(reader.GetString(6)));
            }
        }

        if (row is null)
            return null;

        await using var chainCommand = connection.CreateCommand();
        chainCommand.CommandText = """
            WITH RECURSIVE chain(id,revision_id,parent_id,ordinal,name,mime_type,relationship,depth,status) AS (
              SELECT id,revision_id,parent_id,ordinal,name,mime_type,relationship,depth,status
              FROM content_nodes WHERE id=$content AND revision_id=$revision
              UNION ALL
              SELECT c.id,c.revision_id,c.parent_id,c.ordinal,c.name,c.mime_type,c.relationship,c.depth,c.status
              FROM content_nodes c JOIN chain ON chain.parent_id=c.id
              WHERE c.revision_id=$revision
            )
            SELECT id,parent_id,ordinal,name,mime_type,relationship,depth,status
            FROM chain ORDER BY depth;
            """;
        chainCommand.Parameters.AddWithValue("$content", contentId.ToString());
        chainCommand.Parameters.AddWithValue("$revision", row.Value.RevisionId.ToString());
        var chain = new List<IndexedMaterializationNode>();
        await using var chainReader = await chainCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await chainReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            chain.Add(new IndexedMaterializationNode(Guid.Parse(chainReader.GetString(0)),
                chainReader.IsDBNull(1) ? null : Guid.Parse(chainReader.GetString(1)), chainReader.GetInt32(2),
                chainReader.GetString(3), chainReader.IsDBNull(4) ? null : chainReader.GetString(4),
                chainReader.GetString(5), chainReader.GetInt32(6), chainReader.GetString(7)));
        }

        if (chain.Count == 0 || chain[0].ParentContentId is not null || chain[^1].ContentId != contentId)
            return null;

        return new IndexedContentMaterialization(projectId, row.Value.DocumentId, contentId, row.Value.SourcePath,
            row.Value.FolderPath, row.Value.Size, row.Value.Modified, row.Value.Fingerprint, row.Value.RevisionId, chain);
    }

    private static void ValidateDocumentListRequest(DocumentListRequest request)
    {
        if (!Enum.IsDefined(request.Status) || !Enum.IsDefined(request.SortBy) || !Enum.IsDefined(request.SortDirection))
            throw new McpIndexException("invalid_filter", "status, sort_by, or sort_direction is invalid.");
        if (request.Limit is < 1 or > 500)
            throw new McpIndexException("invalid_limit", "limit must be between 1 and 500.");
        if (request.Extensions is { Count: > 100 })
            throw new McpIndexException("invalid_filter", "extensions may contain at most 100 values.");
        if (request.PathPrefixes is { Count: > 100 })
            throw new McpIndexException("invalid_filter", "path_prefixes may contain at most 100 values.");
        if (request.NameQuery is { Length: > 256 } || request.NameQuery?.Contains('\0') == true)
            throw new McpIndexException("invalid_filter", "name_query must contain at most 256 valid characters.");
        if (request.ModifiedFromUtc > request.ModifiedToUtc)
            throw new McpIndexException("invalid_filter", "modified_from_utc must not be later than modified_to_utc.");
        if (request.Cursor is { Length: > 4096 })
            throw new McpIndexException("invalid_cursor", "The document cursor is invalid.");
    }

    private static IReadOnlyList<string> NormalizeExtensions(IReadOnlyList<string>? requested)
    {
        if (requested is null or { Count: 0 })
            return [];
        var extensions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in requested)
        {
            var extension = value?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(extension))
                throw new McpIndexException("invalid_filter", "extensions cannot contain empty values.");
            if (!extension.StartsWith('.'))
                extension = "." + extension;
            if (extension.Length is < 2 or > 17 || extension.Skip(1).Any(character => !char.IsLetterOrDigit(character)))
                throw new McpIndexException("invalid_filter", $"Invalid file extension filter: {value}");
            extensions.Add(extension);
        }
        return extensions.Order(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> NormalizeAuthorizedPathPrefixes(IReadOnlyList<string>? requested,
        IReadOnlyList<(string Path, string PathKey)> folders)
    {
        if (requested is null or { Count: 0 })
            return [];
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in requested)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > 4096 || raw.Contains('\0') || raw.IndexOfAny(['*', '?']) >= 0)
                throw new McpIndexException("invalid_filter", "path_prefixes contains an invalid path.");
            var value = raw.Trim();
            try
            {
                if (Path.IsPathFullyQualified(value))
                {
                    var key = DatabaseWriterService.PathKey(value);
                    if (!folders.Any(folder => IsSameOrChildPathKey(key, folder.PathKey)))
                        throw new McpIndexException("invalid_filter", "A path_prefix is outside the folders authorized for this project.");
                    prefixes.Add(key);
                }
                else
                {
                    if (Path.IsPathRooted(value))
                        throw new McpIndexException("invalid_filter", "A path_prefix is not a fully qualified or safe relative path.");
                    foreach (var folder in folders)
                    {
                        var key = DatabaseWriterService.PathKey(Path.Combine(folder.Path, value));
                        if (!IsSameOrChildPathKey(key, folder.PathKey))
                            throw new McpIndexException("invalid_filter", "A relative path_prefix escapes an authorized project folder.");
                        prefixes.Add(key);
                    }
                }
                if (prefixes.Count > 500)
                    throw new McpIndexException("invalid_filter", "Relative path_prefix expansion exceeds the safe query limit.");
            }
            catch (McpIndexException)
            {
                throw;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new McpIndexException("invalid_filter", "path_prefixes contains an invalid path.");
            }
        }
        return prefixes.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsSameOrChildPathKey(string candidate, string folder) =>
        string.Equals(candidate, folder, StringComparison.Ordinal) ||
        (candidate.Length > folder.Length && candidate.StartsWith(folder, StringComparison.Ordinal) &&
         candidate[folder.Length] is '/' or '\\');

    private static string DocumentFilterFingerprint(DocumentListRequest request, IReadOnlyList<string> extensions,
        IReadOnlyList<string> pathPrefixes, string? nameQuery)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            status = request.Status.ToString(),
            extensions,
            path_prefixes = pathPrefixes,
            name_query = nameQuery?.ToUpperInvariant(),
            modified_from_utc = request.ModifiedFromUtc?.ToUniversalTime().ToString("O"),
            modified_to_utc = request.ModifiedToUtc?.ToUniversalTime().ToString("O"),
            sort_by = request.SortBy.ToString(),
            sort_direction = request.SortDirection.ToString()
        }, CursorJsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static DocumentCursor? DecodeDocumentCursor(string? encoded, DocumentListRequest request,
        long searchGeneration, string filterFingerprint)
    {
        if (string.IsNullOrWhiteSpace(encoded))
            return null;
        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            var cursor = JsonSerializer.Deserialize<DocumentCursor>(Convert.FromBase64String(base64), CursorJsonOptions);
            if (cursor is null || cursor.Version != 1 || cursor.ProjectId != request.ProjectId ||
                cursor.SearchGeneration != searchGeneration || cursor.FilterFingerprint != filterFingerprint ||
                cursor.SortBy != request.SortBy || cursor.SortDirection != request.SortDirection ||
                cursor.DocumentId == Guid.Empty ||
                (cursor.PrimaryValue is null && request.SortBy != DocumentSortField.LastIndexedUtc))
                throw new FormatException();
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            throw new McpIndexException("invalid_cursor", "The document cursor is invalid, stale, or belongs to different filters.");
        }
    }

    private static string EncodeDocumentCursor(DocumentCursor cursor)
    {
        var base64 = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, CursorJsonOptions));
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string DocumentSortExpression(DocumentSortField sortBy) => sortBy switch
    {
        DocumentSortField.FileName => "file_name COLLATE UNICODE_NOCASE",
        DocumentSortField.SourcePath => "path_key",
        DocumentSortField.ModifiedUtc => "modified_utc",
        DocumentSortField.LastIndexedUtc => "last_indexed_utc",
        DocumentSortField.Status => "current_status",
        _ => throw new McpIndexException("invalid_filter", "sort_by is invalid.")
    };

    private static string StatusValue(DocumentStatusFilter status) => status switch
    {
        DocumentStatusFilter.Indexed => "indexed",
        DocumentStatusFilter.Pending => "pending",
        DocumentStatusFilter.Processing => "processing",
        DocumentStatusFilter.Paused => "paused",
        DocumentStatusFilter.Error => "error",
        _ => throw new McpIndexException("invalid_filter", "status is invalid.")
    };

    private static void AppendDocumentCursorPredicate(StringBuilder sql, SqliteCommand command, DocumentCursor? cursor,
        string sortExpression, DocumentSortDirection direction)
    {
        if (cursor is null)
            return;
        command.Parameters.AddWithValue("$cursor_document", cursor.DocumentId.ToString());
        if (cursor.PrimaryValue is null)
        {
            sql.Append(direction == DocumentSortDirection.Asc
                ? $" AND (({sortExpression} IS NULL AND document_id>$cursor_document) OR {sortExpression} IS NOT NULL)"
                : $" AND ({sortExpression} IS NULL AND document_id>$cursor_document)");
            return;
        }

        command.Parameters.AddWithValue("$cursor_primary", cursor.PrimaryValue);
        var comparison = direction == DocumentSortDirection.Asc ? '>' : '<';
        sql.Append($" AND (({sortExpression}{comparison}$cursor_primary) OR ({sortExpression}=$cursor_primary AND document_id>$cursor_document)");
        if (direction == DocumentSortDirection.Desc && cursor.SortBy == DocumentSortField.LastIndexedUtc)
            sql.Append($" OR {sortExpression} IS NULL");
        sql.Append(')');
    }

    private static string? BuildErrorSummary(string? code, string? message)
    {
        if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(message))
            return null;
        var conciseMessage = string.IsNullOrWhiteSpace(message)
            ? null
            : string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (conciseMessage is { Length: > 300 })
            conciseMessage = conciseMessage[..297] + "...";
        return string.IsNullOrWhiteSpace(conciseMessage) ? code : $"{code}: {conciseMessage}";
    }

    private sealed record DocumentCursor(int Version, Guid ProjectId, long SearchGeneration, string FilterFingerprint,
        DocumentSortField SortBy, DocumentSortDirection SortDirection, string? PrimaryValue, Guid DocumentId);

    private async Task<SqliteConnection> OpenRequiredAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.DatabasePath))
        {
            throw new McpIndexException("not_initialized", "The index database does not exist. Start the desktop application first.");
        }

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        try
        {
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null or DBNull || Convert.ToInt32(value) != Schema.CurrentVersion)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new McpIndexException("schema_incompatible", "The index schema is missing or incompatible. Start the desktop application to migrate it.");
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA temp_store=MEMORY; PRAGMA query_only=ON;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<long> ReadGenerationAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid projectId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT search_generation FROM projects WHERE id=$project;";
        command.Parameters.AddWithValue("$project", projectId.ToString());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? throw new McpIndexException("project_not_found", "The project does not exist.")
            : Convert.ToInt64(value);
    }

    private static async Task<IReadOnlyList<ProjectFolderInfo>> LoadFoldersAsync(SqliteConnection connection, Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,path FROM project_folders WHERE project_id=$project ORDER BY path_key;";
        command.Parameters.AddWithValue("$project", projectId.ToString());
        var folders = new List<ProjectFolderInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            folders.Add(new ProjectFolderInfo(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        }

        return folders;
    }

    private static async Task<List<SearchCandidate>> ReadCandidateRowsAsync(SqliteConnection connection, SqliteCommand command,
        bool includeScore, CancellationToken cancellationToken)
    {
        var raw = new List<CandidateRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                raw.Add(new CandidateRow(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7)),
                    ReadLocation(reader, 8), (ExtractionMethod)reader.GetInt32(16), reader.IsDBNull(17) ? null : reader.GetDouble(17),
                    reader.GetInt32(18), includeScore && !reader.IsDBNull(19) ? reader.GetDouble(19) : null));
            }
        }

        var result = new List<SearchCandidate>(raw.Count);
        foreach (var row in raw)
        {
            var chain = await LoadAttachmentChainAsync(connection, row.ContentId, cancellationToken).ConfigureAwait(false);
            result.Add(new SearchCandidate(row.PassageId, row.DocumentId, row.ContentId, row.DisplayText, row.SourcePath,
                row.FileName, row.FileType, row.ModifiedUtc, row.Location, chain, row.Method, row.OcrConfidence,
                KeywordScore: row.Score));
        }

        return result;
    }

    private static void AppendFilters(StringBuilder sql, SqliteCommand command, SearchFilters? filters,
        string documentAlias, string contentAlias)
    {
        if (filters is null)
        {
            return;
        }

        if (filters.DocumentIds is { Count: > 0 })
        {
            sql.Append($" AND {documentAlias}.id IN (");
            AppendGuidParameters(sql, command, filters.DocumentIds, "document");
            sql.Append(')');
        }

        if (filters.PathPrefixes is { Count: > 0 })
        {
            sql.Append(" AND (");
            for (var index = 0; index < filters.PathPrefixes.Count; index++)
            {
                if (index > 0) sql.Append(" OR ");
                var name = $"$path{index}";
                sql.Append($"({documentAlias}.path_key={name} OR (substr({documentAlias}.path_key,1,length({name}))={name} AND substr({documentAlias}.path_key,length({name})+1,1) IN ('/','\\'))) ");
                command.Parameters.AddWithValue(name, DatabaseWriterService.PathKey(filters.PathPrefixes[index]));
            }
            sql.Append(')');
        }

        if (filters.Extensions is { Count: > 0 })
        {
            sql.Append($" AND lower({documentAlias}.extension) IN (");
            for (var index = 0; index < filters.Extensions.Count; index++)
            {
                if (index > 0) sql.Append(',');
                var name = $"$extension{index}";
                sql.Append(name);
                var extension = filters.Extensions[index];
                command.Parameters.AddWithValue(name, (extension.StartsWith('.') ? extension : $".{extension}").ToLowerInvariant());
            }
            sql.Append(')');
        }

        if (filters.ModifiedFromUtc is not null)
        {
            sql.Append($" AND {documentAlias}.modified_utc >= $modified_from");
            command.Parameters.AddWithValue("$modified_from", filters.ModifiedFromUtc.Value.ToString("O"));
        }

        if (filters.ModifiedToUtc is not null)
        {
            sql.Append($" AND {documentAlias}.modified_utc <= $modified_to");
            command.Parameters.AddWithValue("$modified_to", filters.ModifiedToUtc.Value.ToString("O"));
        }

        if (filters.AttachmentScope == AttachmentScope.RootOnly)
        {
            sql.Append($" AND {contentAlias}.depth=0");
        }
        else if (filters.AttachmentScope == AttachmentScope.AttachmentsOnly)
        {
            sql.Append($" AND {contentAlias}.depth>0");
        }
    }

    private static void AppendGuidParameters(StringBuilder sql, SqliteCommand command,
        IEnumerable<Guid> values, string prefix)
    {
        var index = 0;
        foreach (var value in values)
        {
            if (index > 0) sql.Append(',');
            var name = $"${prefix}{index++}";
            sql.Append(name);
            command.Parameters.AddWithValue(name, value.ToString());
        }
    }

    private static async Task<IReadOnlyList<string>> LoadAttachmentChainAsync(SqliteConnection connection, Guid contentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE chain(id,parent_id,name,depth) AS (
              SELECT id,parent_id,name,depth FROM content_nodes WHERE id=$content
              UNION ALL
              SELECT c.id,c.parent_id,c.name,c.depth FROM content_nodes c JOIN chain ON chain.parent_id=c.id
            )
            SELECT name FROM chain WHERE depth>0 ORDER BY depth;
            """;
        command.Parameters.AddWithValue("$content", contentId.ToString());
        var chain = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            chain.Add(reader.GetString(0));
        }

        return chain;
    }

    private static async Task<(Guid ContentId, int Ordinal)?> LoadPassageAnchorAsync(SqliteConnection connection,
        Guid projectId, Guid passageId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.content_id,p.ordinal FROM passages p
            JOIN document_revisions r ON r.id=p.revision_id AND r.status='active'
            JOIN documents d ON d.active_revision_id=r.id AND d.id=r.document_id AND d.tombstoned=0
            WHERE d.project_id=$project AND p.id=$passage;
            """;
        command.Parameters.AddWithValue("$project", projectId.ToString());
        command.Parameters.AddWithValue("$passage", passageId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (Guid.Parse(reader.GetString(0)), reader.GetInt32(1))
            : null;
    }

    private static PassageRow ReadPassageRow(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
        reader.GetInt32(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
        DateTimeOffset.Parse(reader.GetString(8)), ReadLocation(reader, 9), (ExtractionMethod)reader.GetInt32(17),
        reader.IsDBNull(18) ? null : reader.GetDouble(18));

    private static SourceLocation ReadLocation(SqliteDataReader reader, int start) => new(
        (LocationKind)reader.GetInt32(start),
        reader.IsDBNull(start + 1) ? null : reader.GetInt32(start + 1),
        reader.IsDBNull(start + 2) ? null : reader.GetString(start + 2),
        reader.IsDBNull(start + 3) ? null : reader.GetString(start + 3),
        reader.IsDBNull(start + 4) ? null : reader.GetInt32(start + 4),
        reader.IsDBNull(start + 5) ? null : reader.GetString(start + 5),
        reader.IsDBNull(start + 6) ? null : reader.GetString(start + 6),
        reader.IsDBNull(start + 7) ? null : reader.GetInt32(start + 7));

    private static async Task<bool> ContentBelongsToDocumentAsync(SqliteConnection connection, Guid documentId,
        Guid contentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM content_nodes c JOIN documents d ON d.active_revision_id=c.revision_id WHERE d.id=$document AND c.id=$content AND d.tombstoned=0;";
        command.Parameters.AddWithValue("$document", documentId.ToString());
        command.Parameters.AddWithValue("$content", contentId.ToString());
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<IReadOnlyList<ProjectErrorInfo>> LoadDocumentErrorsAsync(SqliteConnection connection,
        Guid projectId, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,code,message,retryable,attempt,created_utc,source_path FROM project_errors WHERE project_id=$project AND document_id=$document ORDER BY id DESC LIMIT 100;";
        command.Parameters.AddWithValue("$project", projectId.ToString());
        command.Parameters.AddWithValue("$document", documentId.ToString());
        var errors = new List<ProjectErrorInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            errors.Add(new ProjectErrorInfo(reader.GetInt64(0), projectId, documentId, reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3) != 0, reader.GetInt32(4), DateTimeOffset.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return errors;
    }

    private static bool IsResident(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            const FileAttributes recallOnOpen = (FileAttributes)0x00040000;
            const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;
            return (attributes & (FileAttributes.Offline | recallOnOpen | recallOnDataAccess)) == 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            return int.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)), out var value) && value >= 0 ? value : 0;
        }
        catch (FormatException)
        {
            throw new McpIndexException("invalid_cursor", "The attachment cursor is invalid.");
        }
    }

    private static string EncodeCursor(int value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value.ToString()));

    private sealed record CandidateRow(Guid PassageId, Guid DocumentId, Guid ContentId, string DisplayText,
        string SourcePath, string FileName, string FileType, DateTimeOffset ModifiedUtc, SourceLocation Location,
        ExtractionMethod Method, double? OcrConfidence, int Depth, double? Score);

    private sealed record PassageRow(Guid PassageId, Guid DocumentId, Guid ContentId, int Ordinal, string Text,
        string SourcePath, string FileName, string FileType, DateTimeOffset ModifiedUtc, SourceLocation Location,
        ExtractionMethod Method, double? OcrConfidence);
}
