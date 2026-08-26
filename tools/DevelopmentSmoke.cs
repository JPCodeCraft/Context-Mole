#:property TargetFramework=net10.0
#:project ../src/Core/MCPIndexSearch.Core.csproj
#:project ../src/Documents/MCPIndexSearch.Documents.csproj
#:project ../src/Infrastructure/MCPIndexSearch.Infrastructure.csproj
#:project ../src/Storage/MCPIndexSearch.Storage.csproj
#:project ../src/Indexing/MCPIndexSearch.Indexing.csproj
#:project ../src/Search/MCPIndexSearch.Search.csproj
#:package Microsoft.Extensions.Hosting

using System.Security.Cryptography;
using MCPIndexSearch.Core;
using MCPIndexSearch.Documents;
using MCPIndexSearch.Indexing;
using MCPIndexSearch.Infrastructure;
using MCPIndexSearch.Search;
using MCPIndexSearch.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var data = Environment.GetEnvironmentVariable("MCPINDEXSEARCH_DATA_DIR") ?? throw new InvalidOperationException("Set MCPINDEXSEARCH_DATA_DIR.");
var fixture = data + "-fixtures";
Directory.CreateDirectory(fixture);
var source = Path.Combine(fixture, "multilingual.txt");
await File.WriteAllTextAsync(source, "Pesquisa local café contrato. Local research contract evidence. Investigación local del contrato.");
var builder = Host.CreateApplicationBuilder();
builder.Services.AddMcpIndexInfrastructure(includeOcr: true);
builder.Services.AddMcpIndexDocuments();
builder.Services.AddWritableMcpIndexStorage();
builder.Services.AddMcpIndexing();
builder.Services.AddMcpIndexSearch();
using var host = builder.Build();
await host.StartAsync();
var writer = host.Services.GetRequiredService<IIndexWriter>();
var store = host.Services.GetRequiredService<ISearchStore>();
var search = host.Services.GetRequiredService<HybridSearchService>();
var activities = host.Services.GetRequiredService<IndexingActivityTracker>();
var projectId = await writer.CreateProjectAsync(new CreateProjectRequest("Development smoke", [fixture]));

ProjectSummary? project = null;
for (var attempt = 0; attempt < 60; attempt++)
{
    project = (await store.ListProjectsAsync()).SingleOrDefault(item => item.Id == projectId);
    if (project is { IndexedCount: 1, PendingCount: 0 }) break;
    await Task.Delay(250);
}
if (project is not { IndexedCount: 1 }) throw new InvalidOperationException("Fixture was not indexed.");

var result = await search.SearchAsync(new SearchRequest(projectId, "café contrato", 10));
if (result.Results.Count == 0 || result.Results[0].SourcePath != source) throw new InvalidOperationException("FTS search did not return exact provenance.");
var documentId = result.Results[0].DocumentId;
var renamed = Path.Combine(fixture, "renamed-multilingual.txt");
File.Move(source, renamed, true);
source = renamed;
for (var attempt = 0; attempt < 40; attempt++)
{
    result = await search.SearchAsync(new SearchRequest(projectId, "café contrato", 10));
    if (result.Results.FirstOrDefault() is { } renamedResult && renamedResult.SourcePath == source && renamedResult.DocumentId == documentId) break;
    await Task.Delay(250);
}
var identity = result.Results.FirstOrDefault();
if (identity is null || identity.SourcePath != source || identity.DocumentId != documentId)
    throw new InvalidOperationException($"Watcher rename did not preserve identity. Expected {documentId} at {source}; received {identity?.DocumentId} at {identity?.SourcePath}.");

await File.AppendAllTextAsync(source, " Atualização incremental do watcher.");
var changedGeneration = result.SearchGeneration;
for (var attempt = 0; attempt < 60; attempt++)
{
    project = (await store.ListProjectsAsync()).Single(item => item.Id == projectId);
    if (project.PendingCount == 0 && project.SearchGeneration > changedGeneration) break;
    await Task.Delay(250);
}
if (project.SearchGeneration <= changedGeneration) throw new InvalidOperationException("Modified file was not incrementally reindexed.");

await writer.SetProjectPausedAsync(projectId, true);
await File.AppendAllTextAsync(source, " Alteração acumulada durante pausa.");
await Task.Delay(1500);
project = (await store.ListProjectsAsync()).Single(item => item.Id == projectId);
if (project.PendingCount == 0) throw new InvalidOperationException("Paused watcher change was not retained as backlog.");
await writer.SetProjectPausedAsync(projectId, false);
for (var attempt = 0; attempt < 60; attempt++)
{
    project = (await store.ListProjectsAsync()).Single(item => item.Id == projectId);
    if (project.PendingCount == 0) break;
    await Task.Delay(250);
}
if (project.PendingCount != 0) throw new InvalidOperationException("Paused backlog did not resume.");
await writer.RequestReindexAsync(projectId);
for (var attempt = 0; attempt < 60; attempt++)
{
    project = (await store.ListProjectsAsync()).Single(item => item.Id == projectId);
    if (project.PendingCount == 0) break;
    await Task.Delay(250);
}
var timing = activities.GetSnapshot(projectId);
for (var attempt = 0; attempt < 20 && (timing.ActiveItems.Count != 0 || timing.AverageCompletedDuration is null); attempt++)
{
    await Task.Delay(50);
    timing = activities.GetSnapshot(projectId);
}
if (timing.ActiveItems.Count != 0 || timing.AverageCompletedDuration is null || timing.CompletedSampleCount == 0)
    throw new InvalidOperationException("Indexing activity timing did not retain a completed-file session average.");

var stableHash = await HashFileAsync(source);
var stableTime = File.GetLastWriteTimeUtc(source);
await Task.Delay(500);
var afterHash = await HashFileAsync(source);
var afterTime = File.GetLastWriteTimeUtc(source);
if (stableHash != afterHash || stableTime != afterTime) throw new InvalidOperationException("Indexing changed the source file.");
File.Delete(source);
for (var attempt = 0; attempt < 40; attempt++)
{
    project = (await store.ListProjectsAsync()).Single(item => item.Id == projectId);
    if (project.DocumentCount == 0) break;
    await Task.Delay(250);
}
if (project.DocumentCount != 0) throw new InvalidOperationException("Deleted file was not tombstoned.");
await writer.RemoveProjectAsync(projectId);
await host.StopAsync();
Console.WriteLine($"SMOKE_OK mode={result.ActualMode} generation={result.SearchGeneration} source={result.Results[0].SourcePath}");

static async Task<string> HashFileAsync(string path)
{
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}
