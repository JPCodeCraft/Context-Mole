using ContextMole.Benchmarks;
using ContextMole.Core;
using Microsoft.Data.Sqlite;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class VidoreBenchmarkIdentityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Dataset = "benchmark/test";
    private const string Revision = "fixed-revision";

    [Fact]
    public async Task SeparateFreshProjectsHaveIdenticalSourceIdentitiesAndReapplicationIsANoOp()
    {
        var first = await CreateMappings(false);
        var second = await CreateMappings(true);
        Assert.Equal(first, second);

        async Task<IReadOnlyList<BenchmarkDocumentIdentity>> CreateMappings(bool reverse)
        {
            await using var database = await StorageTestDatabase.CreateAsync(Token);
            var fixture = await Observe(database, reverse);
            await using var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath}");
            await connection.OpenAsync(Token);
            var mappings = await VidoreBenchmarkIdentity.ApplyAsync(connection, fixture.Project, Dataset, Revision, fixture.Inputs, Token);
            Assert.Equal(mappings, await VidoreBenchmarkIdentity.ApplyAsync(connection, fixture.Project, Dataset, Revision, fixture.Inputs, Token));
            return mappings;
        }
    }

    [Theory]
    [InlineData("running")]
    [InlineData("derived")]
    [InlineData("hash")]
    [InlineData("unknown-reference")]
    [InlineData("unknown-document")]
    public async Task NonFreshOrMismatchedInputsFailClosed(string invalid)
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var fixture = await Observe(database, false);
        await using var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath}");
        await connection.OpenAsync(Token);
        var ids = await Ids(connection, "documents", "id");
        if (invalid is "running" or "derived")
        {
            var job = Assert.IsType<IndexJobLease>(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
            if (invalid == "derived")
            {
                var file = new FileInfo(job.SourcePath);
                await database.CommitAsync(job, await StorageTestDatabase.HashAsync(file.FullName, Token), file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), "Preserved canonical body.", cancellationToken: Token);
            }
        }
        if (invalid == "unknown-reference")
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE extra_reference(document_id TEXT REFERENCES documents(id)); INSERT INTO extra_reference SELECT id FROM documents LIMIT 1;";
            await command.ExecuteNonQueryAsync(Token);
        }
        var inputs = fixture.Inputs;
        if (invalid == "hash") inputs = [inputs[0] with { SourceSha256 = new string('0', 64) }, inputs[1]];
        if (invalid == "unknown-document")
        {
            var other = Path.Combine(database.Paths.SourceDirectory, "other.txt");
            await File.WriteAllTextAsync(other, "a", Token);
            inputs = [inputs[0] with { SourcePath = other }, inputs[1]];
        }
        await Assert.ThrowsAnyAsync<Exception>(() => VidoreBenchmarkIdentity.ApplyAsync(connection,
            fixture.Project, Dataset, Revision, inputs, Token));
        Assert.Equal(ids, await Ids(connection, "documents", "id"));
    }

    [Fact]
    public async Task MidAssignmentFailureRollsBackDocumentAndJobIdentities()
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var fixture = await Observe(database, false);
        await using var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath}");
        await connection.OpenAsync(Token);
        var documents = await Ids(connection, "documents", "id");
        var jobs = await Ids(connection, "index_jobs", "document_id");
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER fail_second BEFORE UPDATE OF id ON documents WHEN OLD.file_name='b.txt' BEGIN SELECT RAISE(ABORT,'synthetic assignment failure'); END;";
            await command.ExecuteNonQueryAsync(Token);
        }
        await Assert.ThrowsAsync<SqliteException>(() => VidoreBenchmarkIdentity.ApplyAsync(connection,
            fixture.Project, Dataset, Revision, fixture.Inputs, Token));
        Assert.Equal(documents, await Ids(connection, "documents", "id"));
        Assert.Equal(jobs, await Ids(connection, "index_jobs", "document_id"));
    }

    [Fact]
    public void IdentityUsesOnlyUnambiguousVersionedSourceInputs()
    {
        var hash = new string('a', 64);
        var id = VidoreBenchmarkIdentity.Derive(Dataset, Revision, "original", hash);
        Assert.Equal(id, VidoreBenchmarkIdentity.Derive(Dataset, Revision, "original", hash.ToUpperInvariant()));
        Assert.NotEqual(id, VidoreBenchmarkIdentity.Derive(Dataset, Revision, "other", hash));
        Assert.NotEqual(id, VidoreBenchmarkIdentity.Derive(Dataset, Revision, "original", new string('b', 64)));
        Assert.Throws<ArgumentException>(() => VidoreBenchmarkIdentity.Derive(Dataset, Revision, "bad\0identity", hash));
    }

    private static async Task<(Guid Project, BenchmarkDocumentIdentityInput[] Inputs)> Observe(StorageTestDatabase database, bool reverse)
    {
        var (project, folder) = await database.CreateProjectAsync("Identity fixture", Token);
        var inputs = new List<BenchmarkDocumentIdentityInput>();
        foreach (var name in reverse ? new[] { "b", "a" } : new[] { "a", "b" })
        {
            var path = Path.Combine(database.Paths.SourceDirectory, name + ".txt");
            await File.WriteAllTextAsync(path, name, Token);
            var file = new FileInfo(path);
            await database.Writer.ObserveFileAsync(new FileObservation(project, folder, path, file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)), Token);
            inputs.Add(new BenchmarkDocumentIdentityInput(path, name, await StorageTestDatabase.HashAsync(path, Token)));
        }
        return (project, inputs.OrderBy(input => input.OriginalId, StringComparer.Ordinal).ToArray());
    }

    private static async Task<string[]> Ids(SqliteConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} ORDER BY {column};";
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token)) ids.Add(reader.GetString(0));
        return ids.ToArray();
    }
}
