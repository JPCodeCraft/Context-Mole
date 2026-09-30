using System.Diagnostics;

using ContextMole.Core;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ContextMole.Storage;

internal static class Schema
{
    public const int CurrentVersion = 7;

    public static async Task MigrateAsync(SqliteConnection connection, ILogger logger,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(Schema).Assembly;
        var migrations = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(name => (Name: name, Version: ParseVersion(name)))
            .OrderBy(item => item.Version)
            .ToArray();
        if (!migrations.Select(item => item.Version).SequenceEqual(Enumerable.Range(1, CurrentVersion)))
            throw new InvalidOperationException("Embedded SQLite migrations must be unique and contiguous from 1 to the current version.");

        var initialVersion = await ReadVersionAsync(connection, allowEmpty: true, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Database schema inspected at {DatabasePath}: current {InitialVersion}, expected {ExpectedVersion}, pending migrations {PendingVersions}",
            connection.DataSource, initialVersion, CurrentVersion,
            string.Join(", ", migrations.Where(item => item.Version > initialVersion).Select(item => item.Version)));
        if (initialVersion > CurrentVersion)
            throw Incompatible(initialVersion, "The database was created by a newer application. Install a matching or newer version.");

        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA temp_store=MEMORY;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA wal_autocheckpoint=1000;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection,
            "CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);",
            cancellationToken).ConfigureAwait(false);

        foreach (var migration in migrations.Where(item => item.Version > initialVersion))
        {
            var started = Stopwatch.GetTimestamp();
            logger.LogInformation("Applying database migration {MigrationVersion}: {MigrationResource}", migration.Version, migration.Name);
            try
            {
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
                logger.LogInformation("Database migration {MigrationVersion} completed in {ElapsedMilliseconds} ms",
                    migration.Version, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Database migration {MigrationVersion} canceled; its transaction was rolled back", migration.Version);
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Database migration {MigrationVersion} ({MigrationResource}) failed at {DatabasePath}; SQLite code {SqliteCode}, extended code {SqliteExtendedCode}",
                    migration.Version, migration.Name, connection.DataSource,
                    (exception as SqliteException)?.SqliteErrorCode, (exception as SqliteException)?.SqliteExtendedErrorCode);
                throw new ContextMoleException("migration_failed",
                    $"Database migration {migration.Version} ({migration.Name}) failed: {exception.Message}",
                    innerException: exception);
            }
        }

        if (initialVersion == CurrentVersion)
            logger.LogInformation("Database schema is already current at version {SchemaVersion}", CurrentVersion);
        await RecoverInterruptedJobsAsync(connection, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Interrupted database jobs and staging revisions recovered");
        await ValidateAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ValidateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var version = await ReadVersionAsync(connection, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        if (version != CurrentVersion)
            throw Incompatible(version, version > CurrentVersion
                ? "Install a matching or newer application version."
                : "Start the desktop application to migrate the index.");
    }

    private static async Task<int> ReadVersionAsync(SqliteConnection connection, bool allowEmpty,
        CancellationToken cancellationToken)
    {
        await using var metadata = connection.CreateCommand();
        metadata.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        var tables = new List<string>();
        await using (var reader = await metadata.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) tables.Add(reader.GetString(0));
        if (!tables.Contains("schema_migrations", StringComparer.Ordinal))
        {
            if (allowEmpty && tables.Count == 0) return 0;
            throw Incompatible(null, "Migration metadata is missing. The database will not be recreated automatically.");
        }

        await using var columns = connection.CreateCommand();
        columns.CommandText = "SELECT name FROM pragma_table_info('schema_migrations');";
        var columnNames = new List<string>();
        await using (var reader = await columns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) columnNames.Add(reader.GetString(0));
        if (!columnNames.Contains("version", StringComparer.Ordinal) || !columnNames.Contains("applied_utc", StringComparer.Ordinal))
            throw Incompatible(null, "Migration metadata is malformed.");

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
        var versions = new List<int>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0) || reader.GetValue(0) is not long value || value is < 1 or > int.MaxValue)
                    throw Incompatible(null, "Migration history contains an invalid version.");
                versions.Add((int)value);
            }
        var latest = versions.Count == 0 ? 0 : versions[^1];
        if (versions.Where((version, index) => version != index + 1).Any())
            throw Incompatible(latest, "Migration history contains gaps or duplicate versions.");
        if (latest == 0 && (!allowEmpty || tables.Count > 1))
            throw Incompatible(0, "Migration history is empty for an existing index.");
        return latest;
    }

    private static ContextMoleException Incompatible(int? actual, string reason) => new("schema_incompatible",
        $"Index schema version {actual?.ToString() ?? "unknown"} is incompatible with expected version {CurrentVersion}. {reason}");

    internal static async Task RecoverInterruptedJobsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE index_jobs SET state='queued',lease_until_utc=NULL,
              updated_utc=$now WHERE state='running';
            DELETE FROM document_revisions WHERE status='staging';
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int ParseVersion(string resourceName)
    {
        var file = resourceName[(resourceName.LastIndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
        var prefix = file[..file.IndexOf('_')];
        return int.TryParse(prefix, out var version) ? version : throw new InvalidOperationException($"Migration {resourceName} has no numeric prefix.");
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
