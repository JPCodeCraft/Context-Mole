using System.Text.Json;

using ContextMole.Broker;
using ContextMole.Broker.Protocol;
using ContextMole.Core;
using ContextMole.Infrastructure;

using Microsoft.Data.Sqlite;

namespace ContextMole.Tests;

public sealed class SoleEmbeddingModelTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-model")]
    [InlineData("999")]
    [InlineData("Granite97M")]
    [InlineData("1")]
    [InlineData("Granite311M")]
    [InlineData("gRaNiTe311m")]
    [InlineData("0")]
    public void FreshAndLegacySettingsUseOnly97M(string? persisted)
    {
        using var paths = new Paths();
        var settingsPath = Path.Combine(paths.DataDirectory, "ui-state", "embedding-model.txt");
        if (persisted is not null) File.WriteAllText(settingsPath, persisted);
        var settings = new EmbeddingModelSettings(paths);
        Assert.Equal(EmbeddingModelChoice.Granite97M, settings.Model);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.SetModel(EmbeddingModelChoice.Granite311M));
        settings.SetModel(EmbeddingModelChoice.Granite97M);
        Assert.Equal("Granite97M", File.ReadAllText(settingsPath));
        Assert.Equal(EmbeddingModelChoice.Granite97M, new EmbeddingModelSettings(paths).Model);
        Assert.Equal(0, (int)EmbeddingModelChoice.Granite311M); // Decode-only legacy persisted value.
        Assert.Equal(1, (int)EmbeddingModelChoice.Granite97M);
    }

    [Fact]
    public void FailedReadOnlyNormalizationNeverReactivatesLegacyModel()
    {
        if (OperatingSystem.IsWindows()) return; // Unix directory permissions give a deterministic denied write.
        using var paths = new Paths();
        var directory = Path.Combine(paths.DataDirectory, "ui-state");
        var settingsPath = Path.Combine(directory, "embedding-model.txt");
        File.WriteAllText(settingsPath, "Granite311M");
        var original = File.GetUnixFileMode(directory);
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var settings = new EmbeddingModelSettings(paths);
            Assert.Equal(EmbeddingModelChoice.Granite97M, settings.Model);
            Assert.False(settings.RefreshFromDisk());
            Assert.Equal("Granite311M", File.ReadAllText(settingsPath));
            Assert.Throws<ContextMoleException>(() => settings.SetModel(EmbeddingModelChoice.Granite97M));
            File.SetUnixFileMode(directory, original);
            Assert.False(settings.RefreshFromDisk());
            Assert.Equal("Granite97M", File.ReadAllText(settingsPath));
        }
        finally { File.SetUnixFileMode(directory, original); }
    }

    [Fact]
    public async Task MetadataReadersDoNotCleanCachesOrLoadUnsupportedLegacyModels()
    {
        using var paths = new Paths();
        // Native UI startup owns retired-cache cleanup. Shared metadata readers and
        // rejected install requests must not clean historical benchmark/tool caches.
        var legacyDirectory = Path.Combine(paths.AssetsDirectory, "granite", "44399559930365213510b1ee2eb15ded83374f0e");
        Directory.CreateDirectory(legacyDirectory);
        foreach (var file in new[] { "tokenizer.json", "model.onnx", "model_quint8_avx2.onnx", "installation-complete" })
            File.WriteAllText(Path.Combine(legacyDirectory, file), "Retained legacy fixture; never loaded.");
        File.WriteAllText(Path.Combine(paths.DataDirectory, "ui-state", "embedding-model.txt"), "Granite311M");
        var settings = new EmbeddingModelSettings(paths);
        var cpu = new StorageFixedCpuSettings();
        using var budget = new GlobalCpuBudget(cpu);
        using var installer = new GraniteModelInstaller(paths, settings, budget);
        Assert.False(installer.IsModelInstalled(EmbeddingModelChoice.Granite311M));
        Assert.False(installer.HasModelAssets(EmbeddingModelChoice.Granite311M));
        var rejected = await Assert.ThrowsAsync<ContextMoleException>(() =>
            installer.InstallAsync(EmbeddingModelChoice.Granite311M, true, cancellationToken: Token));
        Assert.Equal("model_not_supported", rejected.Code);
        await using var generator = new GraniteEmbeddingGenerator(paths, cpu, settings, budget);
        await generator.ReloadAsync(Token);
        Assert.False(generator.IsAvailable); // 97M is absent; existing 311M assets are not a fallback.
        Assert.Equal(GraniteEmbeddingModels.Get(EmbeddingModelChoice.Granite97M).CreatePolicy(false), generator.Policy);
        await Assert.ThrowsAsync<ContextMoleException>(() => generator.EmbedQueryAsync("query", Token));
        await using var broker = new BrokerSearchRuntimeManager(paths, cpu, settings, budget, null!, null!, TimeProvider.System);
        var status = await broker.GetEmbeddingStatusAsync(Token);
        Assert.False(status.IsAvailable);
        Assert.Equal(generator.Policy, status.Policy);
        Assert.All(Directory.GetFiles(legacyDirectory), path =>
            Assert.Equal("Retained legacy fixture; never loaded.", File.ReadAllText(path)));
        Assert.False(Directory.Exists(Path.Combine(paths.AssetsDirectory, "granite", GraniteEmbeddingModels.Get(settings.Model).Revision)));
    }

    [Fact]
    public void ForgedDefinitionsAndLegacyBrokerPoliciesAreRejectedBeforeInference()
    {
        using var paths = new Paths();
        var compact = GraniteEmbeddingModels.Get(EmbeddingModelChoice.Granite97M);
        var forged = compact with { Choice = EmbeddingModelChoice.Granite311M, ModelId = "legacy311" };
        Assert.Equal("model_not_supported", Assert.Throws<ContextMoleException>(() => forged.CreatePolicy(false)).Code);
        Assert.Equal("model_not_supported", Assert.Throws<ContextMoleException>(() =>
            GraniteEmbeddingDiagnostics.ValidateProfiles(paths, forged, 1, Token)).Code);
        Assert.Equal("model_not_supported", Assert.Throws<ContextMoleException>(() =>
            GraniteEmbeddingInputEncoding.Limit([2, 10], forged, 512, true)).Code);
        GraniteEmbeddingModels.EnsureCurrentPolicy(compact.CreatePolicy(false));
        GraniteEmbeddingModels.EnsureCurrentPolicy(compact.CreatePolicy(true));
        foreach (var unsupported in new[]
        {
            LegacyPolicy(), compact.CreatePolicy(false) with { TokenizationVersion = null },
            compact.CreatePolicy(false) with { ModelSha256 = "wrong" },
            compact.CreatePolicy(false) with { PreparationVersion = "layout-v4/spans-v3/body-context-v3" }
        })
            Assert.Equal("model_policy_mismatch", Assert.Throws<ContextMoleException>(() =>
                GraniteEmbeddingModels.EnsureCurrentPolicy(unsupported)).Code);
        var json = JsonSerializer.Serialize(LegacyPolicy());
        Assert.Equal(LegacyPolicy().Key, JsonSerializer.Deserialize<EmbeddingPolicy>(json)!.Key);
        Assert.DoesNotContain("TokenizationVersion", json);
    }

    [Fact]
    public async Task BrokerClientRejectsReturnedLegacyPolicyAndKeepsCurrentIdentity()
    {
        using var paths = new Paths();
        var settings = new EmbeddingModelSettings(paths);
        // Exercise the exact production response guard without opening IPC or loading a model.
        await using var client = new BrokerEmbeddingGenerator(null!, paths, settings);
        var current = GraniteEmbeddingModels.Get(settings.Model).CreatePolicy(false);
        client.UpdatePolicy(current);
        Assert.Equal(current, client.Policy);
        var rejected = Assert.Throws<ContextMoleException>(() => client.UpdatePolicy(LegacyPolicy()));
        Assert.Equal("model_policy_mismatch", rejected.Code);
        Assert.Equal(current, client.Policy);
    }

    [Fact]
    public async Task BrokerMetadataTransitionsTrack97MAssetsAndLeaveLegacyFilesUntouched()
    {
        using var paths = new Paths();
        var legacyDirectory = Path.Combine(paths.AssetsDirectory, "granite", "44399559930365213510b1ee2eb15ded83374f0e");
        Directory.CreateDirectory(legacyDirectory);
        var sentinel = Path.Combine(legacyDirectory, "model.onnx");
        File.WriteAllText(sentinel, "Legacy asset must remain unchanged.");
        var settings = new EmbeddingModelSettings(paths);
        var model = GraniteEmbeddingModels.Get(settings.Model);
        var directory = Path.Combine(paths.AssetsDirectory, "granite", model.Revision);
        // This metadata-only proxy owns a tokenizer; no broker RPC or ONNX session is used.
        await using var client = new BrokerEmbeddingGenerator(null!, paths, settings);
        await client.ReloadAsync(Token);
        Assert.False(client.IsAvailable);
        Assert.Equal(model.CreatePolicy(false), client.Policy);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "tokenizer.json"), EmbeddingInputEncodingTests.Fixture(model));
        File.WriteAllText(Path.Combine(directory, "model.onnx"), "Fixture only; never opened as an ONNX model.");
        File.WriteAllText(Path.Combine(directory, "installation-complete"), model.Revision);
        await client.ReloadAsync(Token);
        Assert.True(client.IsAvailable);
        Assert.Equal(model.CreatePolicy(false), client.Policy);
        File.WriteAllText(Path.Combine(directory, "repair-required"), "Synthetic repair requirement.");
        await client.ReloadAsync(Token);
        Assert.False(client.IsAvailable);
        File.Delete(Path.Combine(directory, "repair-required"));
        await client.ReloadAsync(Token);
        Assert.True(client.IsAvailable);
        File.Delete(Path.Combine(directory, "installation-complete"));
        await client.ReloadAsync(Token);
        Assert.False(client.IsAvailable);
        Assert.Equal(model.CreatePolicy(false), client.Policy);
        Assert.Equal("Legacy asset must remain unchanged.", File.ReadAllText(sentinel));
    }

    [Fact]
    public void BrokerEmbeddingRequestsCannotSelectAModel()
    {
        var request = JsonSerializer.Deserialize<BrokerEmbedQueryRequest>(
            "{\"query\":\"example\",\"model\":\"Granite311M\"}", BrokerJson.Options)!;
        Assert.Equal("example", request.Query);
        Assert.DoesNotContain("model", JsonSerializer.Serialize(request, BrokerJson.Options), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EmbeddingModelChoice.Granite97M, GraniteEmbeddingModels.DefaultChoice);
    }

    internal static EmbeddingPolicy LegacyPolicy() => new(
        "ibm-granite/granite-embedding-311m-multilingual-r2", "44399559930365213510b1ee2eb15ded83374f0e",
        "75f9f258bf5013f5fe8a4dad61dd0fd16ac0cbaa7a106e3d3f41c2d04a42d541",
        "0087c868b33bad550a78a08d19798cfd7f713cde4f020803b8f51f405503e15f", "fp32", 768, 384, "cls", "l2-after-matryoshka");

    private sealed class Paths : IAppPaths, IDisposable
    {
        public Paths()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "ContextMole.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(DataDirectory, "ui-state"));
        }
        public string DataDirectory { get; }
        public string DatabasePath => Path.Combine(DataDirectory, "index.db");
        public string AssetsDirectory => Path.Combine(DataDirectory, "assets");
        public string LogsDirectory => Path.Combine(DataDirectory, "logs");
        public string TempDirectory => Path.Combine(DataDirectory, "temp");
        public void Dispose() => Directory.Delete(DataDirectory, true);
    }
}

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class SoleEmbeddingModelMigrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Evidence = "Canonical recovery evidence retains the exact checksum and literal citation.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy311VectorsStayExcludedAndLiteralEvidenceSurvivesRefresh(bool failRefresh)
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await database.CreateProjectAsync("97M migration", Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "retained.txt");
        await File.WriteAllTextAsync(path, Evidence, Token);
        var pending = await database.ObserveAndLeaseAsync(project, folder, path, false, Token);
        var legacy = SoleEmbeddingModelTests.LegacyPolicy();
        var corrected = GraniteEmbeddingModels.Get(EmbeddingModelChoice.Granite97M).CreatePolicy(false);
        var committed = await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length,
            new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero), Evidence,
            cancellationToken: Token, embeddingPolicy: legacy);
        await AssertEvidenceAsync(database, project, committed.PassageId);
        Assert.Empty((await database.Store.LoadVectorSnapshotAsync(project, corrected, Token)).Entries);
        await database.Writer.RequestEmbeddingRefreshAsync(project, corrected, false, Token);
        var refresh = Assert.IsType<IndexJobLease>(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
        Assert.Equal(IndexJobKind.EmbeddingRefresh, refresh.Kind);
        var source = Assert.IsType<EmbeddingRefreshSource>(await database.Writer.LoadEmbeddingRefreshSourceAsync(refresh, Token));
        await AssertEvidenceAsync(database, project, committed.PassageId);
        if (failRefresh)
        {
            await database.Writer.FailJobAsync(refresh, "embedding_refresh_failed", "Synthetic unavailable 97M model", false, Token);
            Assert.Single((await database.Store.LoadVectorSnapshotAsync(project, legacy, Token)).Entries);
            Assert.Empty((await database.Store.LoadVectorSnapshotAsync(project, corrected, Token)).Entries);
            Assert.False((await database.Store.LoadVectorSnapshotMetadataAsync(project, corrected, Token)).IsComplete);
        }
        else
        {
            Assert.True(await database.Writer.CommitEmbeddingRefreshAsync(new EmbeddingRefreshCommitRequest(refresh.JobId,
                project, refresh.DocumentId, source.RevisionId, refresh.ExpectedObservationEpoch,
                source.Passages.Select(p => new PassageEmbedding(p.PassageId, StorageTestDatabase.TestVector())).ToArray(), corrected), Token));
            Assert.Single((await database.Store.LoadVectorSnapshotAsync(project, corrected, Token)).Entries);
            Assert.Empty((await database.Store.LoadVectorSnapshotAsync(project, legacy, Token)).Entries);
            Assert.True((await database.Store.LoadVectorSnapshotMetadataAsync(project, corrected, Token)).IsComplete);
        }
        await AssertEvidenceAsync(database, project, committed.PassageId);
        Assert.Equal(pending.Sha256, await StorageTestDatabase.HashAsync(path, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldPreparation311IndexRequiresSourceReindexAndFailureRetainsEvidence(bool failReindex)
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await database.CreateProjectAsync("Old 311M preparation", Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "retained.txt");
        await File.WriteAllTextAsync(path, Evidence, Token);
        var pending = await database.ObserveAndLeaseAsync(project, folder, path, false, Token);
        var modified = new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero);
        var legacy = SoleEmbeddingModelTests.LegacyPolicy() with { PreparationVersion = "layout-v4/spans-v3/body-context-v3" };
        var corrected = GraniteEmbeddingModels.Get(EmbeddingModelChoice.Granite97M).CreatePolicy(false);
        var committed = await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length, modified,
            Evidence, cancellationToken: Token, embeddingPolicy: legacy);
        await using (var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath}"))
        {
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE document_revisions SET preparation_version=$old WHERE id=$revision;";
            command.Parameters.AddWithValue("$old", legacy.PreparationVersion);
            command.Parameters.AddWithValue("$revision", committed.RevisionId.ToString());
            await command.ExecuteNonQueryAsync(Token);
        }
        Assert.Empty((await database.Store.LoadVectorSnapshotAsync(project, corrected, Token)).Entries);
        await database.Writer.RequestEmbeddingRefreshAsync(project, corrected, true, Token);
        var reindex = Assert.IsType<IndexJobLease>(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
        Assert.Equal(IndexJobKind.Reindex, reindex.Kind);
        await AssertEvidenceAsync(database, project, committed.PassageId);
        if (failReindex)
        {
            await database.Writer.FailJobAsync(reindex, "source_unavailable", "Synthetic source unavailable during reindex", false, Token);
            await database.Writer.RequestEmbeddingRefreshAsync(project, corrected, true, Token);
            var retry = Assert.IsType<IndexJobLease>(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
            Assert.Equal(IndexJobKind.Reindex, retry.Kind);
            await database.Writer.FailJobAsync(retry, "source_unavailable", "Synthetic source unavailable during retry", false, Token);
            Assert.Empty((await database.Store.LoadVectorSnapshotAsync(project, corrected, Token)).Entries);
            Assert.Single((await database.Store.LoadVectorSnapshotAsync(project, legacy, Token)).Entries);
            await AssertEvidenceAsync(database, project, committed.PassageId);
        }
        else
        {
            var current = await database.CommitAsync(reindex, pending.Sha256, pending.File.Length, modified,
                Evidence, cancellationToken: Token, embeddingPolicy: corrected);
            Assert.True((await database.Store.LoadVectorSnapshotMetadataAsync(project, corrected, Token)).IsComplete);
            Assert.Single((await database.Store.LoadVectorSnapshotAsync(project, corrected, Token)).Entries);
            Assert.Empty((await database.Store.LoadVectorSnapshotAsync(project, legacy, Token)).Entries);
            await AssertEvidenceAsync(database, project, current.PassageId);
        }
        Assert.Equal(pending.Sha256, await StorageTestDatabase.HashAsync(path, Token));
    }

    private static async Task AssertEvidenceAsync(StorageTestDatabase database, Guid project, Guid passage)
    {
        Assert.Equal(Evidence, Assert.Single(await database.Store.ReadPassagesAsync(project, [passage], 0, 0, Token)).Text);
        var lexical = await database.Store.KeywordSearchAsync(project, TextNormalization.QuoteFtsTerms("recovery evidence"), 10, null, Token);
        Assert.Single(lexical.Candidates);
    }
}
