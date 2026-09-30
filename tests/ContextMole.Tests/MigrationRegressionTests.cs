using System.Collections.Concurrent;
using System.Text.Json;

using ContextMole.Broker;
using ContextMole.Broker.Protocol;
using ContextMole.Core;
using ContextMole.Storage;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class MigrationRegressionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task StartupUpgradesToCurrentAndRepeatedStartupDoesNotReapply(int version)
    {
        using var paths = new MigrationTestPaths();
        if (version > 0) await CreateLegacyAsync(paths, version);
        var log = new TestLogger<DatabaseWriterService>();
        using (var writer = new DatabaseWriterService(paths, log))
        {
            await writer.StartAsync(Token);
            Assert.True(writer.Ready.IsCompletedSuccessfully);
            Assert.True(await new SqliteSearchStore(paths).IsInitializedAsync(Token));
            await writer.StopAsync(Token);
        }
        Assert.Equal(7L, await ScalarAsync(paths, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(Enumerable.Range(version + 1, 7 - version),
            log.Entries.Where(entry => entry.Message.StartsWith("Applying database migration", StringComparison.Ordinal))
                .Select(entry => Convert.ToInt32(entry.Properties["MigrationVersion"])));
        Assert.Contains(log.Entries, entry => entry.Properties.GetValueOrDefault("DatabasePath") as string == paths.DatabasePath);
        Assert.Contains(log.Entries, entry => entry.Message.StartsWith("Database ready", StringComparison.Ordinal));
        var repeatedLog = new TestLogger<DatabaseWriterService>();
        using (var writer = new DatabaseWriterService(paths, repeatedLog))
        {
            await writer.StartAsync(Token);
            await writer.StopAsync(Token);
        }
        Assert.DoesNotContain(repeatedLog.Entries, entry => entry.Message.StartsWith("Applying database migration", StringComparison.Ordinal));
        Assert.Contains(repeatedLog.Entries, entry => entry.Message.Contains("already current", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedMigrationRollsBackAndNextStartupResumesFromLastCommittedVersion()
    {
        using var paths = new MigrationTestPaths();
        await CreateLegacyAsync(paths, 5);
        var source = Path.Combine(paths.SourceDirectory, "preserved.txt");
        await File.WriteAllTextAsync(source, "source remains unchanged", Token);
        var project = Guid.NewGuid();
        var folder = Guid.NewGuid();
        await using (var connection = await OpenAsync(paths))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO projects(id,name,name_key,state,created_utc,updated_utc)
                  VALUES($project,'Preserved','PRESERVED',0,$now,$now);
                INSERT INTO project_folders(id,project_id,path,path_key,created_utc)
                  VALUES($folder,$project,$path,$path,$now);
                CREATE TRIGGER fail_migration BEFORE UPDATE ON projects
                  BEGIN SELECT RAISE(ABORT,'intentional migration failure'); END;
                """;
            command.Parameters.AddWithValue("$project", project.ToString());
            command.Parameters.AddWithValue("$folder", folder.ToString());
            command.Parameters.AddWithValue("$path", paths.SourceDirectory);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(Token);
        }
        var log = new TestLogger<DatabaseWriterService>();
        using (var failed = new DatabaseWriterService(paths, log))
        {
            var error = await Assert.ThrowsAsync<ContextMoleException>(() => failed.StartAsync(Token));
            Assert.Equal("migration_failed", error.Code);
            Assert.Contains("migration 7", error.Message);
            Assert.IsType<SqliteException>(error.InnerException);
            Assert.True(failed.Ready.IsFaulted);
            await Assert.ThrowsAsync<ContextMoleException>(() => failed.Ready);
        }
        Assert.Equal(6L, await ScalarAsync(paths, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(0L, await ScalarAsync(paths, "SELECT COUNT(*) FROM pragma_table_info('passages') WHERE name='body_text';"));
        Assert.Equal(1L, await ScalarAsync(paths, "SELECT COUNT(*) FROM pragma_table_info('passages_fts') WHERE name='search_text';"));
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Error &&
            Convert.ToInt32(entry.Properties.GetValueOrDefault("MigrationVersion")) == 7 &&
            Convert.ToInt32(entry.Properties.GetValueOrDefault("SqliteCode")) == 19 && entry.Exception is SqliteException);

        await ExecuteAsync(paths, "DROP TRIGGER fail_migration;");
        var retryLog = new TestLogger<DatabaseWriterService>();
        using (var retry = new DatabaseWriterService(paths, retryLog))
        {
            await retry.StartAsync(Token);
            var summary = Assert.Single(await new SqliteSearchStore(paths).ListProjectsAsync(Token));
            Assert.Equal(project, summary.Id);
            Assert.Equal(paths.SourceDirectory, Assert.Single(summary.Folders).Path);
            await retry.StopAsync(Token);
        }
        Assert.Equal(7L, await ScalarAsync(paths, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal("source remains unchanged", await File.ReadAllTextAsync(source, Token));
        Assert.Single(retryLog.Entries, entry => entry.Message.StartsWith("Applying database migration", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("INSERT INTO schema_migrations VALUES(8,'future');", "version 8")]
    [InlineData("DELETE FROM schema_migrations WHERE version=3;", "gaps")]
    [InlineData("DROP TABLE schema_migrations;", "missing")]
    [InlineData("DROP TABLE schema_migrations; CREATE TABLE schema_migrations(wrong TEXT);", "malformed")]
    public async Task InvalidOrNewerHistoriesAreRejectedWithoutChangingDatabase(string damage, string expected)
    {
        using var paths = new MigrationTestPaths();
        await CreateLegacyAsync(paths, 7);
        await ExecuteAsync(paths, damage);
        var before = await File.ReadAllBytesAsync(paths.DatabasePath, Token);
        using var writer = new DatabaseWriterService(paths);
        var startupError = await Assert.ThrowsAsync<ContextMoleException>(() => writer.StartAsync(Token));
        Assert.Equal("schema_incompatible", startupError.Code);
        Assert.Contains(expected, startupError.Message);
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, await File.ReadAllBytesAsync(paths.DatabasePath, Token));
        var store = new SqliteSearchStore(paths);
        var readError = await Assert.ThrowsAsync<ContextMoleException>(() => store.IsInitializedAsync(Token));
        Assert.Equal("schema_incompatible", readError.Code);
        Assert.False(readError.Retryable);
    }

    [Fact]
    public async Task InitializationChecksDistinguishMissingOldAndUnreadableIndexes()
    {
        using var paths = new MigrationTestPaths();
        var log = new TestLogger<SqliteSearchStore>();
        var store = new SqliteSearchStore(paths, log);
        Assert.False(await store.IsInitializedAsync(Token));
        await CreateLegacyAsync(paths, 5);
        var old = await Assert.ThrowsAsync<ContextMoleException>(() => store.IsInitializedAsync(Token));
        Assert.Equal("schema_incompatible", old.Code);
        Assert.Contains("version 5", old.Message);
        Assert.Contains("expected version 7", old.Message);
        var inventory = await Assert.ThrowsAsync<ContextMoleException>(() =>
            store.ListDocumentsAsync(new DocumentListRequest(Guid.NewGuid()), Token));
        Assert.Equal(old.Code, inventory.Code);
        Assert.False(inventory.Retryable);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning &&
            entry.Properties.GetValueOrDefault("DatabasePath") as string == paths.DatabasePath);
    }

    [Fact]
    public async Task CorruptionAndInvalidPathsPreserveCauseAndAreNotSchemaErrors()
    {
        using var paths = new MigrationTestPaths();
        await File.WriteAllTextAsync(paths.DatabasePath, "not a sqlite database", Token);
        var log = new TestLogger<SqliteSearchStore>();
        var corrupt = await Assert.ThrowsAsync<ContextMoleException>(() =>
            new SqliteSearchStore(paths, log).IsInitializedAsync(Token));
        Assert.Equal("index_unavailable", corrupt.Code);
        Assert.False(corrupt.Retryable);
        var sqlite = Assert.IsType<SqliteException>(corrupt.InnerException);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Error &&
            Convert.ToInt32(entry.Properties.GetValueOrDefault("SqliteCode")) == sqlite.SqliteErrorCode &&
            Convert.ToInt32(entry.Properties.GetValueOrDefault("SqliteExtendedCode")) == sqlite.SqliteExtendedErrorCode);
        SqliteConnection.ClearAllPools();
        File.Delete(paths.DatabasePath);
        Directory.CreateDirectory(paths.DatabasePath);
        var inaccessible = await Assert.ThrowsAsync<ContextMoleException>(() =>
            new SqliteSearchStore(paths).IsInitializedAsync(Token));
        Assert.Equal("index_unavailable", inaccessible.Code);
        Assert.False(inaccessible.Retryable);
    }

    [Theory]
    [InlineData(BrokerToolMethods.ListProjects, "old", "schema_incompatible")]
    [InlineData(BrokerToolMethods.ListDocuments, "old", "schema_incompatible")]
    [InlineData(BrokerToolMethods.ListProjects, "corrupt", "index_unavailable")]
    [InlineData(BrokerToolMethods.ListDocuments, "corrupt", "index_unavailable")]
    [InlineData(BrokerToolMethods.ListProjects, "missing", "not_initialized")]
    [InlineData(BrokerToolMethods.ListDocuments, "missing", "not_initialized")]
    public async Task BrokerInventoryToolsPreserveInitializationFailureDetails(string method, string state, string code)
    {
        using var paths = new MigrationTestPaths();
        if (state == "old") await CreateLegacyAsync(paths, 5);
        if (state == "corrupt") await File.WriteAllTextAsync(paths.DatabasePath, "not a sqlite database", Token);
        var dispatcher = new BrokerRequestDispatcher(new SqliteSearchStore(paths), null!, paths, null!,
            new BrokerActivityTracker(TimeProvider.System), new TestLifetime());
        var payload = JsonSerializer.SerializeToElement(new BrokerListDocumentsRequest(Guid.NewGuid(),
            "all", null, null, null, null, null, "file_name", "asc", 10, null), BrokerJson.Options);
        var error = await Assert.ThrowsAsync<ContextMoleException>(() => dispatcher.DispatchAsync(
            new BrokerRpcRequest(Guid.NewGuid(), method, payload, DateTimeOffset.UtcNow.AddSeconds(10)), Token));
        Assert.Equal(code, error.Code);
        Assert.False(error.Retryable);
        if (state == "old")
        {
            Assert.Contains("version 5", error.Message);
            Assert.Contains("expected version 7", error.Message);
        }
        if (state == "corrupt") Assert.IsType<SqliteException>(error.InnerException);
    }

    [Fact]
    public async Task HostDoesNotAnnounceStartedUntilMigrationFinishes()
    {
        using var paths = new MigrationTestPaths();
        await CreateLegacyAsync(paths, 5);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new TestLogger<DatabaseWriterService>(entry =>
        {
            if (!entry.Message.StartsWith("Applying database migration", StringComparison.Ordinal)) return;
            entered.TrySetResult();
            release.Wait(Token);
        });
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAppPaths>(paths);
        builder.Services.AddSingleton<ILogger<DatabaseWriterService>>(log);
        builder.Services.AddWritableContextMoleStorage();
        using var host = builder.Build();
        var startup = host.StartAsync(Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(startup.IsCompleted);
            Assert.False(host.Services.GetRequiredService<IIndexWriter>().Ready.IsCompleted);
            Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
            release.Set();
            await startup;
            Assert.True(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        }
        finally
        {
            release.Set();
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CancellationWhileStartingStopsWriterWithoutLoggingFailure()
    {
        using var paths = new MigrationTestPaths();
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var log = new TestLogger<DatabaseWriterService>(entry =>
        {
            if (!entry.Message.StartsWith("Initializing database", StringComparison.Ordinal)) return;
            entered.TrySetResult();
            release.Wait(startupCancellation.Token);
        });
        using var writer = new DatabaseWriterService(paths, log);
        var startup = writer.StartAsync(startupCancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await startupCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
            Assert.True(writer.Ready.IsCanceled);
            Assert.DoesNotContain(log.Entries, entry => entry.Level >= LogLevel.Error);
        }
        finally
        {
            release.Set();
            await writer.StopAsync(CancellationToken.None);
        }
    }

    private static async Task CreateLegacyAsync(MigrationTestPaths paths, int version)
    {
        await using var connection = await OpenAsync(paths);
        await using var metadata = connection.CreateCommand();
        metadata.CommandText = "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);";
        await metadata.ExecuteNonQueryAsync(Token);
        var assembly = typeof(SqliteSearchStore).Assembly;
        for (var number = 1; number <= version; number++)
        {
            var resource = assembly.GetManifestResourceNames().Single(name => name.Contains($".Migrations.{number:000}_", StringComparison.Ordinal));
            await using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            await using var command = connection.CreateCommand();
            command.CommandText = await reader.ReadToEndAsync(Token);
            await command.ExecuteNonQueryAsync(Token);
            command.CommandText = "INSERT INTO schema_migrations VALUES($version,$now);";
            command.Parameters.AddWithValue("$version", number);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(Token);
        }
    }

    private static async Task<SqliteConnection> OpenAsync(MigrationTestPaths paths)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(Token);
        return connection;
    }

    private static async Task ExecuteAsync(MigrationTestPaths paths, string sql)
    {
        await using var connection = await OpenAsync(paths);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<long> ScalarAsync(MigrationTestPaths paths, string sql)
    {
        await using var connection = await OpenAsync(paths);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));
    }

    private sealed class MigrationTestPaths : IAppPaths, IDisposable
    {
        private readonly StorageTestPaths _paths = new();
        public string DataDirectory => _paths.DataDirectory;
        public string DatabasePath => _paths.DatabasePath;
        public string AssetsDirectory => _paths.AssetsDirectory;
        public string LogsDirectory => _paths.LogsDirectory;
        public string TempDirectory => _paths.TempDirectory;
        public string SourceDirectory => _paths.SourceDirectory;
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            _paths.Dispose();
        }
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception,
        Dictionary<string, object?> Properties);

    private sealed class TestLogger<T>(Action<LogEntry>? onLog = null) : ILogger<T>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(item => item.Key, item => item.Value) : [];
            var entry = new LogEntry(logLevel, formatter(state, exception), exception, properties);
            Entries.Enqueue(entry);
            onLog?.Invoke(entry);
        }
    }
}
