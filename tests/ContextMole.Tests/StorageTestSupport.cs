using System.Security.Cryptography;

using ContextMole.Core;
using ContextMole.Storage;

using Microsoft.Data.Sqlite;

namespace ContextMole.Tests;

[CollectionDefinition(nameof(SqliteIntegrationCollection), DisableParallelization = true)]
public sealed class SqliteIntegrationCollection
{
}

internal sealed class StorageTestDatabase : IAsyncDisposable
{
    private StorageTestDatabase(StorageTestPaths paths, DatabaseWriterService writer, SqliteSearchStore store)
    {
        Paths = paths;
        Writer = writer;
        Store = store;
    }

    public StorageTestPaths Paths { get; }
    public DatabaseWriterService Writer { get; }
    public SqliteSearchStore Store { get; }

    public static async Task<StorageTestDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        var paths = new StorageTestPaths();
        var writer = new DatabaseWriterService(paths);
        var store = new SqliteSearchStore(paths);
        await writer.StartAsync(cancellationToken);
        await writer.Ready.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        return new StorageTestDatabase(paths, writer, store);
    }

    public async Task<(Guid ProjectId, Guid FolderId)> CreateProjectAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var projectId = await Writer.CreateProjectAsync(new CreateProjectRequest(name, [Paths.SourceDirectory]),
            cancellationToken);
        var folderId = (await Store.ListProjectsAsync(cancellationToken)).Single(project => project.Id == projectId)
            .Folders.Single().Id;
        return (projectId, folderId);
    }

    public async Task<(ObservationResult Observation, IndexJobLease Job, FileInfo File, string Sha256)> ObserveAndLeaseAsync(
        Guid projectId,
        Guid folderId,
        string path,
        bool force,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        var observation = await Writer.ObserveFileAsync(new FileObservation(projectId, folderId, path, file.Length,
            modified, Force: force), cancellationToken);
        var job = await Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken)
            ?? throw new InvalidOperationException("The observed test document was not leased.");
        return (observation, job, file, await HashAsync(path, cancellationToken));
    }

    public async Task<CommittedTestDocument> CommitAsync(
        IndexJobLease job,
        string sha256,
        long size,
        DateTimeOffset modified,
        string text,
        bool includeVector = true,
        IReadOnlyList<ExtractionError>? errors = null,
        IReadOnlyList<ContentNodeDraft>? nodes = null,
        IReadOnlyList<PassageDraft>? passages = null,
        CancellationToken cancellationToken = default,
        EmbeddingPolicy? embeddingPolicy = null)
    {
        var begin = await Writer.BeginRevisionAsync(job, sha256, size, modified, cancellationToken);
        if (!begin.ShouldExtract || begin.RevisionId is null)
            throw new InvalidOperationException($"The test revision did not begin: {begin.Reason}");

        var rootId = Guid.CreateVersion7();
        var passageId = Guid.CreateVersion7();
        nodes ??=
        [
            new ContentNodeDraft(rootId, null, 0, Path.GetFileName(job.SourcePath), MimeFor(job.Extension), "root", 0)
        ];
        passages ??=
        [
            new PassageDraft(passageId, rootId, 0, text, TextNormalization.ForSearch(text),
                new SourceLocation(LocationKind.Document), ExtractionMethod.NativeText, null,
                includeVector ? TestVector() : null, TextNormalization.ForSearch(text),
                Title: null, FileName: Path.GetFileName(job.SourcePath),
                SourcePath: Path.GetFullPath(job.SourcePath), ContentName: Path.GetFileName(job.SourcePath))
        ];

        var committed = await Writer.CommitRevisionAsync(new IndexCommitRequest(job.JobId, job.ProjectId,
            job.DocumentId, begin.RevisionId.Value, job.ExpectedObservationEpoch, sha256, size, modified, nodes,
            passages, includeVector ? embeddingPolicy ?? TestEmbeddingPolicy : null, errors ?? []), cancellationToken);
        if (!committed)
            throw new InvalidOperationException("The test revision was unexpectedly rejected.");
        return new CommittedTestDocument(job.DocumentId, begin.RevisionId.Value, nodes[0].Id, passages[0].Id);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Writer.StopAsync(CancellationToken.None);
        }
        finally
        {
            Writer.Dispose();
            SqliteConnection.ClearAllPools();
            Paths.Dispose();
        }
    }

    public static readonly EmbeddingPolicy TestEmbeddingPolicy =
        new("tests", "1", "model", "tokenizer", "fp32", 384, 384, "mean", "l2");

    public static float[] TestVector(int axis = 0, float value = 1f)
    {
        var vector = new float[384];
        vector[axis] = value;
        return vector;
    }

    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string MimeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".eml" => "message/rfc822",
        ".md" => "text/markdown",
        _ => "text/plain"
    };
}

internal sealed record CommittedTestDocument(Guid DocumentId, Guid RevisionId, Guid ContentId, Guid PassageId);

internal sealed class StorageTestPaths : IAppPaths, IDisposable
{
    private readonly string _ownedRoot;

    public StorageTestPaths()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "ContextMole-tests");
        _ownedRoot = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        DataDirectory = Path.Combine(_ownedRoot, "data");
        DatabasePath = Path.Combine(DataDirectory, "index.db");
        AssetsDirectory = Path.Combine(DataDirectory, "assets");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        TempDirectory = Path.Combine(DataDirectory, "temp");
        SourceDirectory = Path.Combine(_ownedRoot, "source");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(SourceDirectory);
    }

    public string DataDirectory { get; }
    public string RootDirectory => _ownedRoot;
    public string DatabasePath { get; }
    public string AssetsDirectory { get; }
    public string LogsDirectory { get; }
    public string TempDirectory { get; }
    public string SourceDirectory { get; }

    public void Dispose()
    {
        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ContextMole-tests"));
        var owned = Path.GetFullPath(_ownedRoot);
        var relative = Path.GetRelativePath(testRoot, owned);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("The test directory escaped its owned temporary root.");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
        }
    }
}

internal sealed class StorageFixedCpuSettings(int threadLimit = 2) : ICpuUsageSettings
{
    public CpuUsageProfile Profile => CpuUsageProfile.Normal;
    public int LogicalProcessorCount => threadLimit;
    public int ThreadLimit => threadLimit;
    public int MaximumThreadLimit => threadLimit;
    public event EventHandler? Changed { add { } remove { } }
    public void SetProfile(CpuUsageProfile profile) => throw new NotSupportedException();
}

internal sealed class StorageUnavailableEmbeddings : IEmbeddingGenerator
{
    public bool IsAvailable => false;
    public string UnavailableReason => "Embeddings are intentionally disabled in this test.";
    public EmbeddingPolicy? Policy => null;
    public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    public Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Embeddings should not be generated in this test.");
    public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Embeddings should not be generated in this test.");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class StorageNoOcr : IOcrEngine
{
    public bool IsAvailable => false;
    public string UnavailableReason => "OCR is intentionally disabled in this test.";
    public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new OcrResult(string.Empty, null));
}
