#:property TargetFramework=net10.0
#:project ../src/Core/ContextMole.Core.csproj
#:project ../src/Infrastructure/ContextMole.Infrastructure.csproj
#:project ../src/Storage/ContextMole.Storage.csproj
#:package Microsoft.Extensions.Hosting

using System.Security.Cryptography;
using ContextMole.Core;
using ContextMole.Infrastructure;
using ContextMole.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var data = Environment.GetEnvironmentVariable("CONTEXTMOLE_DATA_DIR")
    ?? throw new InvalidOperationException("Set CONTEXTMOLE_DATA_DIR to an isolated smoke directory.");
var fixture = data + "-fixtures";
Directory.CreateDirectory(fixture);
var source = Path.Combine(fixture, "reader-writer.txt");
await File.WriteAllTextAsync(source, "The reader snapshot must not table-lock the revision writer.");

var builder = Host.CreateApplicationBuilder();
builder.Services.AddContextMoleInfrastructure(includeOcr: false);
builder.Services.AddWritableContextMoleStorage();
using var host = builder.Build();
await host.StartAsync();
var writer = host.Services.GetRequiredService<IIndexWriter>();
var store = host.Services.GetRequiredService<ISearchStore>();
var projectId = await writer.CreateProjectAsync(new CreateProjectRequest("SQLite WAL concurrency smoke", [fixture]));
var project = (await store.ListProjectsAsync()).Single(item => item.Id == projectId);
var folderId = project.Folders.Single().Id;
var file = new FileInfo(source);
var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
var observed = await writer.ObserveFileAsync(new FileObservation(projectId, folderId, source, file.Length, modified));
var hash = await HashAsync(source);
await CommitNextAsync(writer, hash, file.Length, modified, "first revision");

project = (await store.ListProjectsAsync()).Single(item => item.Id == projectId);
await writer.RequestReindexAsync(projectId);
var next = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1))
    ?? throw new InvalidOperationException("The replacement job was not leased.");

await using (var reader = store.StreamVectorEntriesAsync(projectId, project.SearchGeneration, null).GetAsyncEnumerator())
{
    if (!await reader.MoveNextAsync())
        throw new InvalidOperationException("The held semantic reader did not expose the committed vector.");

    var revision = await writer.BeginRevisionAsync(next, hash, file.Length, modified);
    if (!revision.ShouldExtract || revision.RevisionId is null)
        throw new InvalidOperationException("The writer could not begin a revision while a reader snapshot was held.");
    await CommitAsync(writer, next, revision.RevisionId.Value, hash, file.Length, modified, "second revision");
}

project = (await store.ListProjectsAsync()).Single(item => item.Id == projectId);
if (project.PendingCount != 0 || project.ErrorCount != 0 || project.IndexedCount != 1)
    throw new InvalidOperationException($"Unexpected final state: pending={project.PendingCount}, errors={project.ErrorCount}, indexed={project.IndexedCount}.");

var generationBeforeMigration = project.SearchGeneration;
var migrationPolicy = new EmbeddingPolicy("smoke-small", "2", "model-small", "tokenizer", "fp32", 384, 384,
    "mean", "l2");
await writer.RequestEmbeddingRefreshAsync(projectId, migrationPolicy, retryFailed: false);
var clearedMetadata = await store.LoadVectorSnapshotMetadataAsync(projectId);
if (clearedMetadata.EntryCount != 0 || clearedMetadata.SearchGeneration <= generationBeforeMigration)
    throw new InvalidOperationException("Embedding-policy migration did not retire the previous vectors and generation.");
var keywordAfterClear = await store.KeywordSearchAsync(projectId, "second", 10, null);
if (keywordAfterClear.Candidates.Count != 1)
    throw new InvalidOperationException("Embedding-policy migration removed the active keyword index.");

var failedMigration = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1))
    ?? throw new InvalidOperationException("The embedding migration job was not leased.");
await writer.FailJobAsync(failedMigration, "migration_smoke", "Deliberate non-retryable migration failure.", retryable: false);
var metadataAfterFailure = await store.LoadVectorSnapshotMetadataAsync(projectId);
if (metadataAfterFailure.EntryCount != 0 ||
    (await store.KeywordSearchAsync(projectId, "second", 10, null)).Candidates.Count != 1)
    throw new InvalidOperationException("A failed embedding migration stranded or removed keyword search.");

await writer.RemoveProjectAsync(projectId);
await host.StopAsync();
Console.WriteLine("SQLITE_WAL_CONCURRENCY_SMOKE_OK reader_snapshot=held revision_write=committed embedding_migration=failure_safe");

static async Task CommitNextAsync(IIndexWriter writer, string hash, long size, DateTimeOffset modified, string text)
{
    var job = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1))
        ?? throw new InvalidOperationException("The initial job was not leased.");
    var revision = await writer.BeginRevisionAsync(job, hash, size, modified);
    if (!revision.ShouldExtract || revision.RevisionId is null)
        throw new InvalidOperationException("The initial revision did not begin.");
    await CommitAsync(writer, job, revision.RevisionId.Value, hash, size, modified, text);
}

static async Task CommitAsync(IIndexWriter writer, IndexJobLease job, Guid revisionId, string hash, long size,
    DateTimeOffset modified, string text)
{
    var contentId = Guid.CreateVersion7();
    var vector = Enumerable.Repeat(0.125f, 384).ToArray();
    var policy = new EmbeddingPolicy("smoke", "1", "model", "tokenizer", "fp32", 384, 384, "mean", "l2");
    var node = new ContentNodeDraft(contentId, null, 0, Path.GetFileName(job.SourcePath), "text/plain", "root", 0);
    var passage = new PassageDraft(Guid.CreateVersion7(), contentId, 0, text, text,
        new SourceLocation(LocationKind.Document), ExtractionMethod.NativeText, null, vector);
    var committed = await writer.CommitRevisionAsync(new IndexCommitRequest(job.JobId, job.ProjectId, job.DocumentId,
        revisionId, job.ExpectedObservationEpoch, hash, size, modified, [node], [passage], policy, []));
    if (!committed)
        throw new InvalidOperationException("The revision did not commit.");
}

static async Task<string> HashAsync(string path)
{
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}
