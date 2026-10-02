using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContextMole.Core;

namespace ContextMole.Benchmarks;

public sealed record BenchmarkDocumentIdentityInput(string SourcePath, string OriginalId, string SourceSha256);
public sealed record BenchmarkDocumentIdentity(string OriginalId, string SourceSha256, Guid DocumentId);

/// <summary>Controls fresh benchmark identity noise before any production-derived data exists.</summary>
public static class VidoreBenchmarkIdentity
{
    public const string Protocol = "source-document-guid-v1";
    private const string Seed = "ContextMole-benchmark-document-identity-v1";

    public static Guid Derive(string dataset, string revision, string originalId, string sourceSha256)
    {
        string[] fields = [Seed, dataset, revision, originalId, sourceSha256.ToLowerInvariant()];
        if (fields.Any(value => string.IsNullOrWhiteSpace(value) || value.Contains('\0')) ||
            sourceSha256.Length != 64 || !sourceSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Stable identities require unambiguous source identities and a SHA256.");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', fields)));
        return new Guid(hash.AsSpan(0, 16));
    }

    public static async Task<IReadOnlyList<BenchmarkDocumentIdentity>> ApplyAsync(DbConnection connection,
        Guid projectId, string dataset, string revision, IReadOnlyList<BenchmarkDocumentIdentityInput> inputs,
        CancellationToken token = default)
    {
        if (connection.State != ConnectionState.Open) throw new ArgumentException("Open the owned benchmark connection first.");
        if (inputs.Count == 0) throw new ArgumentException("The complete benchmark corpus is required.");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var expected = inputs.ToDictionary(input => Path.GetFullPath(input.SourcePath), comparer);
        var targets = inputs.ToDictionary(input => input.OriginalId,
            input => Derive(dataset, revision, input.OriginalId, input.SourceSha256), StringComparer.Ordinal);
        if (targets.Values.Distinct().Count() != inputs.Count) throw new InvalidDataException("Stable document identities collide.");
        foreach (var input in inputs)
        {
            await using var source = File.OpenRead(input.SourcePath);
            if (!string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(source, token)),
                    input.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Verified source bytes changed: {input.OriginalId}.");
        }
        await Execute("PRAGMA foreign_keys=ON;", null);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await Execute("PRAGMA defer_foreign_keys=ON;", transaction);
        if (await Count("SELECT COUNT(*) FROM projects;", transaction) != 1 ||
            await Count("SELECT COUNT(*) FROM projects WHERE id=$project AND state=$active;", transaction,
                ("$project", projectId.ToString()), ("$active", (int)ProjectState.Active)) != 1)
            throw new InvalidOperationException("Identity control only accepts one fresh active benchmark project.");
        foreach (var table in new[] { "document_revisions", "content_nodes", "sections", "passages", "embeddings", "index_runs", "project_errors" })
            if (await Count($"SELECT COUNT(*) FROM {table};", transaction) != 0)
                throw new InvalidOperationException($"Identity control refuses existing derived data: {table}.");

        // Future schema additions must not leave a populated reference pointing at an old ID.
        var tables = new List<string>();
        await using (var command = Command("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';", transaction))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) tables.Add(reader.GetString(0));
        foreach (var table in tables)
        {
            var references = new List<string>();
            await using (var command = Command($"PRAGMA foreign_key_list({Quote(table)});", transaction))
            await using (var reader = await command.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token))
                    if (reader.GetString(2) == "documents") references.Add(reader.GetString(3));
            foreach (var column in references)
                if (!(table == "index_jobs" && column == "document_id") &&
                    await Count($"SELECT COUNT(*) FROM {Quote(table)} WHERE {Quote(column)} IS NOT NULL;", transaction) != 0)
                    throw new InvalidOperationException($"Unexpected populated document reference: {table}.{column}.");
        }

        var mappings = new List<(Guid Previous, Guid Current, BenchmarkDocumentIdentityInput Input)>();
        await using (var command = Command("SELECT id,path,active_revision_id,tombstoned,project_id FROM documents ORDER BY path_key;", transaction))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                if (!reader.IsDBNull(2) || reader.GetBoolean(3) || reader.GetString(4) != projectId.ToString() ||
                    !expected.TryGetValue(Path.GetFullPath(reader.GetString(1)), out var input))
                    throw new InvalidOperationException("The fresh document set differs from the verified complete corpus.");
                mappings.Add((Guid.Parse(reader.GetString(0)), targets[input.OriginalId], input));
            }
        if (mappings.Count != inputs.Count || mappings.Select(item => item.Input.OriginalId).Distinct().Count() != inputs.Count)
            throw new InvalidOperationException("Missing or duplicate benchmark documents.");
        var previousIds = mappings.Select(item => item.Previous).ToHashSet();
        if (mappings.Any(item => item.Current != item.Previous && previousIds.Contains(item.Current)))
            throw new InvalidOperationException("A target identity collides with another existing document.");
        if (await Count("SELECT COUNT(*) FROM index_jobs;", transaction) != inputs.Count ||
            await Count("""
                SELECT COUNT(*) FROM index_jobs j JOIN documents d ON d.id=j.document_id
                WHERE j.state<>'queued' OR j.lease_until_utc IS NOT NULL OR j.attempt<>0
                  OR j.expected_epoch<>d.observation_epoch OR j.project_id<>$project
                  OR j.kind NOT IN ($index,$reindex);
                """, transaction, ("$project", projectId.ToString()), ("$index", (int)IndexJobKind.Index),
                ("$reindex", (int)IndexJobKind.Reindex)) != 0 ||
            await Count("SELECT COUNT(*) FROM (SELECT document_id FROM index_jobs GROUP BY document_id HAVING COUNT(*)<>1);", transaction) != 0)
            throw new InvalidOperationException("Identity control requires one queued, unleased initial job per document.");
        var before = await NonIdentityDigest(transaction);
        foreach (var mapping in mappings.Where(item => item.Previous != item.Current))
        {
            if (await Execute("UPDATE documents SET id=$new WHERE id=$old;", transaction,
                    ("$new", mapping.Current.ToString()), ("$old", mapping.Previous.ToString())) != 1 ||
                await Execute("UPDATE index_jobs SET document_id=$new WHERE document_id=$old;", transaction,
                    ("$new", mapping.Current.ToString()), ("$old", mapping.Previous.ToString())) != 1)
                throw new InvalidOperationException("A fresh fixture row changed during identity assignment.");
        }
        if (before != await NonIdentityDigest(transaction))
            throw new InvalidOperationException("Identity assignment changed non-identity document/job fields.");
        await using (var command = Command("PRAGMA foreign_key_check;", transaction))
        await using (var reader = await command.ExecuteReaderAsync(token))
            if (await reader.ReadAsync(token)) throw new InvalidOperationException("Identity fixture violates foreign keys.");
        foreach (var mapping in mappings)
            if (await Count("SELECT COUNT(*) FROM documents d JOIN index_jobs j ON j.document_id=d.id WHERE d.id=$new;",
                    transaction, ("$new", mapping.Current.ToString())) != 1 ||
                mapping.Previous != mapping.Current && await Count("SELECT COUNT(*) FROM documents WHERE id=$old;",
                    transaction, ("$old", mapping.Previous.ToString())) != 0)
                throw new InvalidOperationException("Identity assignment verification failed.");
        await transaction.CommitAsync(token);
        return mappings.Select(item => new BenchmarkDocumentIdentity(item.Input.OriginalId,
            item.Input.SourceSha256.ToLowerInvariant(), item.Current)).OrderBy(item => item.OriginalId, StringComparer.Ordinal).ToArray();

        DbCommand Command(string sql, DbTransaction? tx, params (string Name, object Value)[] parameters)
        {
            var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            return command;
        }
        async Task<int> Execute(string sql, DbTransaction? tx, params (string Name, object Value)[] parameters)
        {
            await using var command = Command(sql, tx, parameters);
            return await command.ExecuteNonQueryAsync(token);
        }
        async Task<long> Count(string sql, DbTransaction tx, params (string Name, object Value)[] parameters)
        {
            await using var command = Command(sql, tx, parameters);
            return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
        }
        async Task<string> NonIdentityDigest(DbTransaction tx)
        {
            using var bytes = new MemoryStream();
            using var output = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
            foreach (var (table, order, excluded) in new[] { ("documents", "path_key", "id"), ("index_jobs", "id", "document_id") })
            {
                output.Write(table);
                await using var command = Command($"SELECT * FROM {table} ORDER BY {order};", tx);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    output.Write((byte)1);
                    for (var column = 0; column < reader.FieldCount; column++)
                    {
                        var name = reader.GetName(column);
                        if (name == excluded) continue;
                        output.Write(name); output.Write(!reader.IsDBNull(column));
                        if (!reader.IsDBNull(column)) output.Write(Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture) ?? "");
                    }
                }
                output.Write((byte)0);
            }
            output.Flush(); return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
