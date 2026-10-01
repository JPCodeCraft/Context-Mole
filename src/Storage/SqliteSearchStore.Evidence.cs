using System.Text;
using System.Text.Json;

using ContextMole.Core;
using Microsoft.Data.Sqlite;

namespace ContextMole.Storage;

public sealed partial class SqliteSearchStore
{
    private const string CandidateColumns = """
        p.id,d.id,c.id,p.display_text,d.path,d.file_name,d.extension,d.modified_utc,
        p.location_kind,p.page,p.sheet,p.cell_range,p.slide,p.structure_path,p.email_part,p.image_frame,
        p.extraction_method,p.ocr_confidence,c.depth,
        """;
    private const string CandidateTail = """
        p.ordinal,p.body_text,p.title,p.heading,p.content_name,c.mime_type,p.email_subject,
        p.section_id,p.section_offset,p.location_json
        """;
    private const string ActiveCandidateJoins = """
        JOIN document_revisions r ON r.id=p.revision_id AND r.status='active'
        JOIN documents d ON d.id=r.document_id AND d.active_revision_id=r.id AND d.tombstoned=0
        JOIN content_nodes c ON c.id=p.content_id
        """;

    public async Task<long> GetSearchGenerationAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        return await ReadGenerationAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KeywordBranchSnapshot> LoadKeywordBranchesAsync(Guid projectId, string mainQuery,
        string? optionalQuery, int candidateLimit, SearchFilters? filters, SearchFieldWeights fieldWeights,
        SearchScope scope, CancellationToken cancellationToken = default)
    {
        if (candidateLimit is < 1 or > 10_000)
            throw new ContextMoleException("invalid_request", "candidate_limit must be between 1 and 10000.");
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var generation = await ReadGenerationAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
        // Both lexical branches, their provenance and section evidence share this read snapshot.
        var main = await ReadBranchAsync(mainQuery).ConfigureAwait(false);
        var optional = await ReadBranchAsync(optionalQuery).ConfigureAwait(false);
        if (scope == SearchScope.Section)
        {
            var hydrated = await HydrateSectionsAsync(connection, transaction, projectId,
                main.Concat(optional).ToList(), cancellationToken).ConfigureAwait(false);
            main = hydrated.Take(main.Count).ToList();
            optional = hydrated.Skip(main.Count).ToList();
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new KeywordBranchSnapshot(generation, main.Take(candidateLimit).ToArray(),
            optional.Take(candidateLimit).ToArray(), main.Count > candidateLimit, optional.Count > candidateLimit);

        async Task<List<SearchCandidate>> ReadBranchAsync(string? query)
        {
            if (string.IsNullOrWhiteSpace(query)) return [];
            var table = scope == SearchScope.Section ? "sections_fts" : "passages_fts";
            var score = $"bm25({table},$weight_body,$weight_title,$weight_heading,$weight_filename," +
                        "$weight_path,$weight_content_name,$weight_sheet,$weight_email_subject)";
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var sql = new StringBuilder("SELECT ").Append(CandidateColumns).Append('-').Append(score).Append(',')
                .Append(CandidateTail).Append(" FROM ").Append(table).Append(' ');
            sql.Append(scope == SearchScope.Section
                ? "JOIN sections s ON s.rowid=sections_fts.rowid JOIN passages p ON p.rowid=(SELECT member.rowid FROM passages member WHERE member.section_id=s.id ORDER BY member.ordinal,member.id LIMIT 1) "
                : "JOIN passages p ON p.rowid=passages_fts.rowid ");
            sql.Append(ActiveCandidateJoins).Append(" WHERE ").Append(table).Append(" MATCH $query AND d.project_id=$project");
            command.Parameters.AddWithValue("$project", projectId.ToString());
            command.Parameters.AddWithValue("$query", query);
            command.Parameters.AddWithValue("$weight_body", fieldWeights.Body);
            command.Parameters.AddWithValue("$weight_title", fieldWeights.Title);
            command.Parameters.AddWithValue("$weight_heading", fieldWeights.Heading);
            command.Parameters.AddWithValue("$weight_filename", fieldWeights.Filename);
            command.Parameters.AddWithValue("$weight_path", fieldWeights.Path);
            command.Parameters.AddWithValue("$weight_content_name", fieldWeights.ContentName);
            command.Parameters.AddWithValue("$weight_sheet", fieldWeights.Sheet);
            command.Parameters.AddWithValue("$weight_email_subject", fieldWeights.EmailSubject);
            AppendFilters(sql, command, filters, "d", "c");
            sql.Append(" ORDER BY ").Append(score).Append(",p.id LIMIT $limit;");
            command.Parameters.AddWithValue("$limit", candidateLimit + 1);
            command.CommandText = sql.ToString();
            var candidates = await ReadCandidateRowsAsync(connection, command, true, cancellationToken).ConfigureAwait(false);
            return candidates;
        }
    }

    public async Task<IReadOnlyList<SearchCandidate>> LoadCandidatesAsync(Guid projectId,
        IReadOnlyCollection<Guid> passageIds, long expectedGeneration, SearchScope scope,
        CancellationToken cancellationToken = default)
    {
        if (passageIds.Count == 0) return [];
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var generation = await ReadGenerationAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
        if (generation != expectedGeneration)
            throw new ContextMoleException("index_changed", "The project index changed before candidates were loaded.", true);
        var result = new List<SearchCandidate>();
        foreach (var batch in passageIds.Chunk(500))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var sql = new StringBuilder("SELECT ").Append(CandidateColumns).Append("NULL,").Append(CandidateTail)
                .Append(" FROM passages p ").Append(ActiveCandidateJoins)
                .Append(" WHERE d.project_id=$project AND p.id IN (");
            command.Parameters.AddWithValue("$project", projectId.ToString());
            AppendGuidParameters(sql, command, batch, "passage");
            command.CommandText = sql.Append(") ORDER BY p.content_id,p.ordinal,p.id;").ToString();
            result.AddRange(await ReadCandidateRowsAsync(connection, command, false, cancellationToken).ConfigureAwait(false));
        }
        if (scope == SearchScope.Section)
            result = await HydrateSectionsAsync(connection, transaction, projectId, result, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<List<SearchCandidate>> HydrateSectionsAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid projectId, List<SearchCandidate> candidates, CancellationToken cancellationToken)
    {
        var sections = new Dictionary<Guid, (string Text, string Heading)>();
        var members = new Dictionary<Guid, List<SearchCandidate>>();
        foreach (var batch in candidates.Where(candidate => candidate.SectionId.HasValue)
                     .Select(candidate => candidate.SectionId!.Value).Distinct().Chunk(500))
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                var sql = new StringBuilder("SELECT id,display_text,heading FROM sections WHERE id IN (");
                AppendGuidParameters(sql, command, batch, "section");
                command.CommandText = sql.Append(");").ToString();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    sections[Guid.Parse(reader.GetString(0))] = (reader.GetString(1), reader.GetString(2));
            }
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                var sql = new StringBuilder("SELECT ").Append(CandidateColumns).Append("NULL,").Append(CandidateTail)
                    .Append(" FROM passages p ").Append(ActiveCandidateJoins)
                    .Append(" WHERE d.project_id=$project AND p.section_id IN (");
                command.Parameters.AddWithValue("$project", projectId.ToString());
                AppendGuidParameters(sql, command, batch, "section");
                command.CommandText = sql.Append(") ORDER BY p.content_id,p.ordinal,p.id;").ToString();
                foreach (var member in await ReadCandidateRowsAsync(connection, command, false, cancellationToken).ConfigureAwait(false))
                {
                    if (member.SectionId is not { } sectionId) continue;
                    if (!members.TryGetValue(sectionId, out var list)) members[sectionId] = list = [];
                    list.Add(member);
                }
            }
        }
        return candidates.Select(candidate => candidate.SectionId is { } sectionId && sections.TryGetValue(sectionId, out var section)
            ? candidate with { SectionText = section.Text, Heading = section.Heading,
                SectionPassages = members.GetValueOrDefault(sectionId) ?? [] }
            : candidate).ToList();
    }

    public async Task<SectionReadResponse> ReadSectionAsync(Guid projectId, Guid sectionId, long expectedGeneration,
        int limit = 20, string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 50)
            throw new ContextMoleException("invalid_request", "Section reading accepts a limit between 1 and 50.");
        var offset = DecodeSectionCursor(cursor, projectId, sectionId, expectedGeneration);
        await using var connection = await OpenRequiredAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var generation = await ReadGenerationAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
        if (generation != expectedGeneration)
            throw new ContextMoleException("index_changed", "The index changed after search. Repeat the search.", true);
        Guid contentId;
        string heading;
        IReadOnlyList<string> headingPath;
        string kind;
        SourceLocation? location;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT s.content_id,s.heading,s.heading_path_json,s.kind,s.location_json FROM sections s
                JOIN documents d ON d.active_revision_id=s.revision_id AND d.tombstoned=0
                WHERE s.id=$section AND d.project_id=$project;
                """;
            command.Parameters.AddWithValue("$section", sectionId.ToString());
            command.Parameters.AddWithValue("$project", projectId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new ContextMoleException("section_not_found", "The section does not exist in this project revision.");
            contentId = Guid.Parse(reader.GetString(0));
            heading = reader.GetString(1);
            headingPath = JsonSerializer.Deserialize<string[]>(reader.GetString(2), StorageJsonOptions) ?? [];
            kind = reader.GetString(3);
            location = JsonSerializer.Deserialize<SourceLocation>(reader.GetString(4), StorageJsonOptions);
        }
        var ids = new List<Guid>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM passages WHERE section_id=$section ORDER BY ordinal,id LIMIT $limit OFFSET $offset;";
            command.Parameters.AddWithValue("$section", sectionId.ToString());
            command.Parameters.AddWithValue("$limit", limit + 1);
            command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(Guid.Parse(reader.GetString(0)));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var passages = ids.Count == 0 ? [] : await ReadPassagesCoreAsync(projectId, ids.Take(limit).ToArray(),
            0, 0, expectedGeneration, cancellationToken).ConfigureAwait(false);
        var next = ids.Count > limit ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new SectionCursor(projectId, sectionId, expectedGeneration, checked(offset + limit)), StorageJsonOptions)) : null;
        return new SectionReadResponse(sectionId, contentId, heading, passages, generation, next)
        { HeadingPath = headingPath, Kind = kind, Location = location };
    }

    private static int DecodeSectionCursor(string? cursor, Guid projectId, Guid sectionId, long generation)
    {
        if (cursor is null) return 0;
        try
        {
            if (cursor.Length > 2048) throw new FormatException();
            var value = JsonSerializer.Deserialize<SectionCursor>(Convert.FromBase64String(cursor), StorageJsonOptions);
            if (value is null || value.ProjectId != projectId || value.SectionId != sectionId ||
                value.Generation != generation || value.Offset is < 0 or > 10_000_000) throw new FormatException();
            return value.Offset;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ContextMoleException("invalid_cursor", "The section cursor is invalid for this request.");
        }
    }

    private sealed record SectionCursor(Guid ProjectId, Guid SectionId, long Generation, int Offset);
}
