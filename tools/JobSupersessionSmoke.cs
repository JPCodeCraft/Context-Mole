#:property TargetFramework=net10.0
#:project ../src/Core/MCPIndexSearch.Core.csproj
#:project ../src/Infrastructure/MCPIndexSearch.Infrastructure.csproj
#:project ../src/Storage/MCPIndexSearch.Storage.csproj
#:package Microsoft.Extensions.Hosting

using System.Security.Cryptography;
using MCPIndexSearch.Core;
using MCPIndexSearch.Infrastructure;
using MCPIndexSearch.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var data = Environment.GetEnvironmentVariable("MCPINDEXSEARCH_DATA_DIR")
    ?? throw new InvalidOperationException("Set MCPINDEXSEARCH_DATA_DIR to an isolated smoke directory.");
var fixture = data + "-fixtures";
Directory.CreateDirectory(fixture);
var source = Path.Combine(fixture, "concurrent-update.eml");
await File.WriteAllTextAsync(source, "From: source@example.test\r\nTo: reader@example.test\r\nSubject: Supersession\r\n\r\nEvidence body.");

var builder = Host.CreateApplicationBuilder();
builder.Services.AddMcpIndexInfrastructure(includeOcr: false);
builder.Services.AddWritableMcpIndexStorage();
using var host = builder.Build();
await host.StartAsync();
var writer = host.Services.GetRequiredService<IIndexWriter>();
var store = host.Services.GetRequiredService<ISearchStore>();
var projectId = await writer.CreateProjectAsync(new CreateProjectRequest("Job supersession smoke", [fixture]));
var folderId = (await store.ListProjectsAsync()).Single(item => item.Id == projectId).Folders.Single().Id;
var file = new FileInfo(source);
var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
var observed = await writer.ObserveFileAsync(new FileObservation(projectId, folderId, source, file.Length, modified));
var hash = await HashAsync(source);

var first = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1))
    ?? throw new InvalidOperationException("The first job was not leased.");
var firstRevision = await writer.BeginRevisionAsync(first, hash, file.Length, modified);
if (!firstRevision.ShouldExtract || firstRevision.RevisionId is null)
    throw new InvalidOperationException("The first staging revision did not begin.");

await writer.ObserveFileAsync(new FileObservation(projectId, folderId, source, file.Length, modified, Force: true));
if (await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1)) is not null)
    throw new InvalidOperationException("A running job was incorrectly made leaseable by a newer observation.");

var staleCommitted = await writer.CommitRevisionAsync(new IndexCommitRequest(first.JobId, projectId, observed.DocumentId,
    firstRevision.RevisionId.Value, first.ExpectedObservationEpoch, hash, file.Length, modified, [], [], null, []));
if (staleCommitted) throw new InvalidOperationException("The superseded revision was incorrectly activated.");

var latest = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1))
    ?? throw new InvalidOperationException("The newer observation was not requeued.");
var latestRevision = await writer.BeginRevisionAsync(latest, hash, file.Length, modified);
if (!latestRevision.ShouldExtract || latestRevision.RevisionId is null)
    throw new InvalidOperationException("The replacement staging revision did not begin.");

var rootId = Guid.CreateVersion7();
var attachmentId = Guid.CreateVersion7();
var nodes = new[]
{
    new ContentNodeDraft(rootId, null, 0, "concurrent-update.eml", "message/rfc822", "root", 0),
    new ContentNodeDraft(attachmentId, rootId, 0, "attachment.txt", "text/plain", "attachment", 1)
};
var passage = new PassageDraft(Guid.CreateVersion7(), attachmentId, 0, "Evidence body.", "Evidence body.",
    new SourceLocation(LocationKind.EmailPart, EmailPart: "attachment:0"), ExtractionMethod.NativeText, null, null);
var committed = await writer.CommitRevisionAsync(new IndexCommitRequest(latest.JobId, projectId, observed.DocumentId,
    latestRevision.RevisionId.Value, latest.ExpectedObservationEpoch, hash, file.Length, modified, nodes, [passage], null, []));
if (!committed) throw new InvalidOperationException("The replacement revision did not commit.");

var project = (await store.ListProjectsAsync()).Single(item => item.Id == projectId);
if (project.PendingCount != 0 || project.ErrorCount != 0 || project.IndexedCount != 1)
    throw new InvalidOperationException($"Unexpected final state: pending={project.PendingCount}, errors={project.ErrorCount}, indexed={project.IndexedCount}.");

await writer.RemoveProjectAsync(projectId);
await host.StopAsync();
Console.WriteLine("JOB_SUPERSESSION_SMOKE_OK pending=0 errors=0 indexed=1");

static async Task<string> HashAsync(string path)
{
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}
