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
await File.WriteAllTextAsync(source, "A successful retry makes the earlier error obsolete.");

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
await writer.FailJobAsync(first, "temporary_failure", "First failed attempt", retryable: true);
await AssertUnresolvedAsync(store, projectId, expectedCount: 1, expectedMessage: "First failed attempt");

await writer.ObserveFileAsync(new FileObservation(projectId, folderId, source, file.Length, modified, Force: true));
var second = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1))
    ?? throw new InvalidOperationException("The retry job was not leased.");
await writer.FailJobAsync(second, "temporary_failure", "Latest failed attempt", retryable: true);
await AssertUnresolvedAsync(store, projectId, expectedCount: 1, expectedMessage: "Latest failed attempt");

await writer.ObserveFileAsync(new FileObservation(projectId, folderId, source, file.Length, modified, Force: true));
var successful = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1))
    ?? throw new InvalidOperationException("The successful job was not leased.");
var sha = await HashAsync(source);
var begin = await writer.BeginRevisionAsync(successful, sha, file.Length, modified);
if (!begin.ShouldExtract || begin.RevisionId is null)
    throw new InvalidOperationException($"The successful revision did not begin: {begin.Reason}");

var committed = await writer.CommitRevisionAsync(new IndexCommitRequest(successful.JobId, projectId,
    observation.DocumentId, begin.RevisionId.Value, successful.ExpectedObservationEpoch, sha, file.Length, modified,
    [], [], null, []));
if (!committed) throw new InvalidOperationException("The successful revision was not committed.");
await AssertUnresolvedAsync(store, projectId, expectedCount: 0, expectedMessage: null);

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
    if (lifetimeTotal != 2)
        throw new InvalidOperationException($"Expected the internal lifetime aggregate to remain 2, received {lifetimeTotal}.");
}

await writer.RemoveProjectAsync(projectId);
await host.StopAsync();
Console.WriteLine("ERROR_RESOLUTION_SMOKE_OK unresolved=0 lifetime=2");

static async Task AssertUnresolvedAsync(ISearchStore store, Guid projectId, int expectedCount, string? expectedMessage)
{
    var errors = await store.ListProjectErrorsAsync(projectId, 25);
    var project = (await store.ListProjectsAsync()).Single(item => item.Id == projectId);
    if (errors.Count != expectedCount || project.ErrorCount != expectedCount)
        throw new InvalidOperationException($"Expected {expectedCount} unresolved errors, received rows={errors.Count}, summary={project.ErrorCount}.");
    if (expectedMessage is not null && (errors.Count != 1 || errors[0].Message != expectedMessage))
        throw new InvalidOperationException($"Expected latest error '{expectedMessage}', received '{errors.FirstOrDefault()?.Message}'.");
}

static async Task<string> HashAsync(string path)
{
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}
