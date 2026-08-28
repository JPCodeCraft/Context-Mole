using System.Reflection;

using Microsoft.Data.Sqlite;

namespace ContextMole.Storage;

internal static class Schema
{
    public const int CurrentVersion = 7;

    public static async Task MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA temp_store=MEMORY;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA wal_autocheckpoint=1000;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection,
            "CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);",
            cancellationToken).ConfigureAwait(false);

        var assembly = typeof(Schema).Assembly;
        var migrations = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(name => (Name: name, Version: ParseVersion(name)))
            .OrderBy(item => item.Version)
            .ToArray();
        if (migrations.Length == 0 || migrations[^1].Version != CurrentVersion)
            throw new InvalidOperationException("Embedded SQLite migrations are missing or out of sequence.");

        foreach (var migration in migrations)
        {
            if (await IsAppliedAsync(connection, migration.Version, cancellationToken).ConfigureAwait(false)) continue;
            await using var stream = assembly.GetManifestResourceStream(migration.Name)
                ?? throw new InvalidOperationException($"Embedded migration {migration.Name} could not be opened.");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO schema_migrations(version,applied_utc) VALUES($version,$now);";
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection,
            "UPDATE index_jobs SET state='queued',lease_until_utc=NULL,updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE state='running';",
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "DELETE FROM document_revisions WHERE status='staging';", cancellationToken).ConfigureAwait(false);
    }

    private static int ParseVersion(string resourceName)
    {
        var file = resourceName[(resourceName.LastIndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
        var prefix = file[..file.IndexOf('_')];
        return int.TryParse(prefix, out var version) ? version : throw new InvalidOperationException($"Migration {resourceName} has no numeric prefix.");
    }

    private static async Task<bool> IsAppliedAsync(SqliteConnection connection, int version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version=$version);";
        command.Parameters.AddWithValue("$version", version);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
