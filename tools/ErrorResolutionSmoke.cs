#:property TargetFramework=net10.0
#:project ../src/Core/MCPIndexSearch.Core.csproj
#:project ../src/Infrastructure/MCPIndexSearch.Infrastructure.csproj
#:project ../src/Storage/MCPIndexSearch.Storage.csproj
#:package Microsoft.Extensions.Hosting

using System.Security.Cryptography;
using MCPIndexSearch.Core;
using MCPIndexSearch.Infrastructure;
using MCPIndexSearch.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var data = Environment.GetEnvironmentVariable("MCPINDEXSEARCH_DATA_DIR")
    ?? throw new InvalidOperationException("Set MCPINDEXSEARCH_DATA_DIR to an isolated smoke directory.");
var fixture = data + "-fixtures";
Directory.CreateDirectory(fixture);
var source = Path.Combine(fixture, "resolved-error.txt");
var healthySource = Path.Combine(fixture, "healthy.txt");
var partialSource = Path.Combine(fixture, "partial-error.txt");
await File.WriteAllTextAsync(source, "A successful retry makes the earlier error obsolete.");
await File.WriteAllTextAsync(healthySource, "A healthy file must never be queued by retry failed files.");
await File.WriteAllTextAsync(partialSource, "A completed file with an extraction error must also be retried.");

var builder = Host.CreateApplicationBuilder();
builder.Services.AddMcpIndexInfrastructure(includeOcr: false);
builder.Services.AddWritableMcpIndexStorage();
using var host = builder.Build();
await host.StartAsync();

var writer = host.Services.GetRequiredService<IIndexWriter>();
var store = host.Services.GetRequiredService<ISearchStore>();
var paths = host.Services.GetRequiredService<IAppPaths>();
var projectId = await writer.CreateProjectAsync(new CreateProjectRequest("Error resolution smoke", [fixture]));
var folderId = (await store.ListProjectsAsync()).Single(project => project.Id == projectId).Folders.Single().Id;
var file = new FileInfo(source);
var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
var observation = await writer.ObserveFileAsync(new FileObservation(projectId, folderId, source, file.Length, modified));

var first = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1))
    ?? throw new InvalidOperationException("The first job was not leased.");
await writer.FailJobAsync(first, "permanent_failure", "First failed attempt", retryable: false);
await AssertUnresolvedAsync(store, projectId, expectedCount: 1, expectedMessage: "First failed attempt");

var healthyFile = new FileInfo(healthySource);
var healthyModified = new DateTimeOffset(healthyFile.LastWriteTimeUtc, TimeSpan.Zero);
var healthyObservation = await writer.ObserveFileAsync(new FileObservation(projectId, folderId, healthySource,
    healthyFile.Length, healthyModified));
var healthy = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1))
    ?? throw new InvalidOperationException("The healthy job was not leased.");
if (healthy.DocumentId != healthyObservation.DocumentId)
    throw new InvalidOperationException("The healthy file was not selected for its initial index.");
await CommitAsync(writer, healthy, healthyObservation, healthySource, healthyFile.Length, healthyModified);

var partialFile = new FileInfo(partialSource);
var partialModified = new DateTimeOffset(partialFile.LastWriteTimeUtc, TimeSpan.Zero);
var partialObservation = await writer.ObserveFileAsync(new FileObservation(projectId, folderId, partialSource,
    partialFile.Length, partialModified));
var partial = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1))
    ?? throw new InvalidOperationException("The partial-error job was not leased.");
await CommitAsync(writer, partial, partialObservation, partialSource, partialFile.Length, partialModified,
    [new ExtractionError("partial_extraction", "One embedded item could not be extracted.", false)]);
await AssertUnresolvedAsync(store, projectId, expectedCount: 2, expectedMessage: null);

var firstRetryCount = await writer.RetryFailedFilesAsync(projectId);
if (firstRetryCount != 2)
    throw new InvalidOperationException($"Expected exactly two files with errors to be queued, received {firstRetryCount}.");
if (await writer.RetryFailedFilesAsync(projectId) != 0)
    throw new InvalidOperationException("Files already queued for retry were queued a second time.");
var retriedDocuments = new HashSet<Guid>();
for (var index = 0; index < 2; index++)
{
    var retry = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1))
        ?? throw new InvalidOperationException("A retry job was not leased.");
    if (retry.Kind != IndexJobKind.Reindex || !retriedDocuments.Add(retry.DocumentId))
        throw new InvalidOperationException("Retry failed files produced a duplicate or non-reindex job.");
    if (retry.DocumentId == observation.DocumentId)
        await writer.FailJobAsync(retry, "permanent_failure", "Latest failed attempt", retryable: false);
    else if (retry.DocumentId == partialObservation.DocumentId)
        await CommitAsync(writer, retry, partialObservation, partialSource, partialFile.Length, partialModified);
    else
        throw new InvalidOperationException("Retry failed files queued the healthy document.");
}
if (!retriedDocuments.SetEquals([observation.DocumentId, partialObservation.DocumentId]))
    throw new InvalidOperationException("Retry failed files did not select exactly the documents carrying errors.");
await AssertUnresolvedAsync(store, projectId, expectedCount: 1, expectedMessage: "Latest failed attempt");

await writer.SetProjectPausedAsync(projectId, paused: true);
await ExpectCodeAsync("project_paused", () => writer.RetryFailedFilesAsync(projectId));
await writer.SetProjectPausedAsync(projectId, paused: false);
var secondRetryCount = await writer.RetryFailedFilesAsync(projectId);
if (secondRetryCount != 1)
    throw new InvalidOperationException($"Expected exactly one failed file on the second retry, received {secondRetryCount}.");
var successful = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1))
    ?? throw new InvalidOperationException("The successful job was not leased.");
if (successful.DocumentId != observation.DocumentId)
    throw new InvalidOperationException("The healthy file was incorrectly queued during the second retry.");
await CommitAsync(writer, successful, observation, source, file.Length, modified);
await AssertUnresolvedAsync(store, projectId, expectedCount: 0, expectedMessage: null);
if (await writer.RetryFailedFilesAsync(projectId) != 0)
    throw new InvalidOperationException("A project without unresolved file errors queued retry work.");
if (await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1)) is not null)
    throw new InvalidOperationException("Retry failed files queued a healthy document.");

await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
             {
                 DataSource = paths.DatabasePath,
                 Mode = SqliteOpenMode.ReadOnly
             }.ToString()))
{
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT error_total FROM projects WHERE id=$project;";
    command.Parameters.AddWithValue("$project", projectId.ToString());
    var lifetimeTotal = Convert.ToInt32(await command.ExecuteScalarAsync());
    if (lifetimeTotal != 3)
        throw new InvalidOperationException($"Expected the internal lifetime aggregate to remain 3, received {lifetimeTotal}.");
}

await writer.RemoveProjectAsync(projectId);
await host.StopAsync();
Console.WriteLine("ERROR_RESOLUTION_SMOKE_OK unresolved=0 lifetime=3 retry_scope=failed_only");

static async Task CommitAsync(IIndexWriter writer, IndexJobLease job, ObservationResult observation, string path,
    long size, DateTimeOffset modified, IReadOnlyList<ExtractionError>? errors = null)
{
    var sha = await HashAsync(path);
    var begin = await writer.BeginRevisionAsync(job, sha, size, modified);
    if (!begin.ShouldExtract || begin.RevisionId is null)
        throw new InvalidOperationException($"The successful revision did not begin: {begin.Reason}");
    var committed = await writer.CommitRevisionAsync(new IndexCommitRequest(job.JobId, job.ProjectId,
        observation.DocumentId, begin.RevisionId.Value, job.ExpectedObservationEpoch, sha, size, modified,
        [], [], null, errors ?? []));
    if (!committed) throw new InvalidOperationException("The successful revision was not committed.");
}

static async Task AssertUnresolvedAsync(ISearchStore store, Guid projectId, int expectedCount, string? expectedMessage)
{
    var errors = await store.ListProjectErrorsAsync(projectId, 25);
    var project = (await store.ListProjectsAsync()).Single(item => item.Id == projectId);
    if (errors.Count != expectedCount || project.ErrorCount != expectedCount)
        throw new InvalidOperationException($"Expected {expectedCount} unresolved errors, received rows={errors.Count}, summary={project.ErrorCount}.");
    if (expectedMessage is not null && (errors.Count != 1 || errors[0].Message != expectedMessage))
        throw new InvalidOperationException($"Expected latest error '{expectedMessage}', received '{errors.FirstOrDefault()?.Message}'.");
}

static async Task ExpectCodeAsync(string expectedCode, Func<Task<int>> action)
{
    try
    {
        await action();
        throw new InvalidOperationException($"Expected {expectedCode}.");
    }
    catch (McpIndexException exception) when (exception.Code == expectedCode)
    {
    }
}

static async Task<string> HashAsync(string path)
{
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}
