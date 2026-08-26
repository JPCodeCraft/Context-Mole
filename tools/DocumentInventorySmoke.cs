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
var nested = Path.Combine(fixture, "sub");
Directory.CreateDirectory(nested);
var alphaPath = Path.Combine(fixture, "alpha.txt");
var bravoPath = Path.Combine(fixture, "Bravo.eml");
var charliePath = Path.Combine(fixture, "charlie.md");
var deltaPath = Path.Combine(nested, "delta.TXT");
var echoPath = Path.Combine(nested, "Écho.html");
await File.WriteAllTextAsync(alphaPath, "alpha inventory");
await File.WriteAllTextAsync(bravoPath, "bravo inventory");
await File.WriteAllTextAsync(charliePath, "charlie inventory");
await File.WriteAllTextAsync(deltaPath, "delta inventory");
await File.WriteAllTextAsync(echoPath, "echo inventory");

var builder = Host.CreateApplicationBuilder();
builder.Services.AddMcpIndexInfrastructure(includeOcr: false);
builder.Services.AddWritableMcpIndexStorage();
using var host = builder.Build();
await host.StartAsync();
var writer = host.Services.GetRequiredService<IIndexWriter>();
var store = host.Services.GetRequiredService<ISearchStore>();
var projectId = await writer.CreateProjectAsync(new CreateProjectRequest("Document inventory smoke", [fixture]));
var folderId = (await store.ListProjectsAsync()).Single(project => project.Id == projectId).Folders.Single().Id;

var alpha = await ObserveAsync(writer, projectId, folderId, alphaPath, new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
var alphaJob = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(5))
    ?? throw new InvalidOperationException("The alpha job was not leased.");
await CommitAsync(writer, alphaJob, alpha, alphaPath, "text/plain", includeAttachment: true);

var bravo = await ObserveAsync(writer, projectId, folderId, bravoPath, new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
var bravoJob = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(5))
    ?? throw new InvalidOperationException("The Bravo job was not leased.");
await writer.FailJobAsync(bravoJob, "extraction_failed", "Broken\r\ncontainer metadata", retryable: false);

var charlie = await ObserveAsync(writer, projectId, folderId, charliePath, new DateTimeOffset(2026, 1, 3, 12, 0, 0, TimeSpan.Zero));
var charlieJob = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(5))
    ?? throw new InvalidOperationException("The charlie job was not leased.");
if (charlieJob.DocumentId != charlie.DocumentId)
    throw new InvalidOperationException("The wrong processing job was leased.");

var delta = await ObserveAsync(writer, projectId, folderId, deltaPath, new DateTimeOffset(2026, 1, 4, 12, 0, 0, TimeSpan.Zero));

var initial = await store.ListDocumentsAsync(new DocumentListRequest(projectId));
if (initial.ReturnedCount != 4 || initial.ProjectId != projectId || initial.SearchGeneration < 1)
    throw new InvalidOperationException("The initial document inventory response is incomplete.");
AssertStatus(initial, alpha.DocumentId, DocumentInventoryStatus.Indexed);
AssertStatus(initial, bravo.DocumentId, DocumentInventoryStatus.Error);
AssertStatus(initial, charlie.DocumentId, DocumentInventoryStatus.Processing);
AssertStatus(initial, delta.DocumentId, DocumentInventoryStatus.Pending);

var indexed = initial.Documents.Single(document => document.DocumentId == alpha.DocumentId);
if (indexed is not { MimeType: "text/plain", ContentCount: 2, AttachmentCount: 1, ExtractedPassageCount: 2,
        ErrorCount: 0, IndexedFingerprint: not null, IndexRevisionId: not null, LastIndexedUtc: not null })
    throw new InvalidOperationException("Indexed root metadata or extraction counts are incorrect.");
if (!string.Equals(indexed.SourcePath, Path.GetFullPath(alphaPath), StringComparison.Ordinal))
    throw new InvalidOperationException("Stored source provenance was altered.");
var failed = initial.Documents.Single(document => document.DocumentId == bravo.DocumentId);
if (failed.ErrorCount != 1 || failed.ErrorSummary != "extraction_failed: Broken container metadata")
    throw new InvalidOperationException("The concise error summary is incorrect.");

var firstPage = await store.ListDocumentsAsync(new DocumentListRequest(projectId, Limit: 2));
if (firstPage.Documents.Select(document => document.FileName).SequenceEqual(["alpha.txt", "Bravo.eml"]) is false ||
    firstPage.NextCursor is null)
    throw new InvalidOperationException("The first deterministic filename page is incorrect.");
var secondPage = await store.ListDocumentsAsync(new DocumentListRequest(projectId, Limit: 2, Cursor: firstPage.NextCursor));
if (secondPage.Documents.Select(document => document.FileName).SequenceEqual(["charlie.md", "delta.TXT"]) is false ||
    secondPage.NextCursor is not null)
    throw new InvalidOperationException("The second deterministic filename page is incorrect.");

var errorOnly = await store.ListDocumentsAsync(new DocumentListRequest(projectId, DocumentStatusFilter.Error));
if (errorOnly.Documents.Single().DocumentId != bravo.DocumentId)
    throw new InvalidOperationException("The error status filter is incorrect.");
var extensionOnly = await store.ListDocumentsAsync(new DocumentListRequest(projectId, Extensions: ["EML"]));
if (extensionOnly.Documents.Single().DocumentId != bravo.DocumentId)
    throw new InvalidOperationException("The extension filter is incorrect.");
var nameOnly = await store.ListDocumentsAsync(new DocumentListRequest(projectId, NameQuery: "BRAVO"));
if (nameOnly.Documents.Single().DocumentId != bravo.DocumentId)
    throw new InvalidOperationException("The case-insensitive filename filter is incorrect.");
var pathOnly = await store.ListDocumentsAsync(new DocumentListRequest(projectId, PathPrefixes: ["sub"]));
if (pathOnly.Documents.Single().DocumentId != delta.DocumentId)
    throw new InvalidOperationException("The authorized relative path filter is incorrect.");
var modified = await store.ListDocumentsAsync(new DocumentListRequest(projectId,
    ModifiedFromUtc: new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero),
    SortBy: DocumentSortField.ModifiedUtc, SortDirection: DocumentSortDirection.Desc));
if (modified.Documents.Select(document => document.DocumentId).SequenceEqual([delta.DocumentId, charlie.DocumentId]) is false)
    throw new InvalidOperationException("Modified-time filtering or descending sorting is incorrect.");

var nullDatePage = await store.ListDocumentsAsync(new DocumentListRequest(projectId,
    SortBy: DocumentSortField.LastIndexedUtc, Limit: 2));
var nullDateNext = await store.ListDocumentsAsync(new DocumentListRequest(projectId,
    SortBy: DocumentSortField.LastIndexedUtc, Limit: 2, Cursor: nullDatePage.NextCursor));
if (nullDatePage.Documents.Concat(nullDateNext.Documents).Select(document => document.DocumentId).Distinct().Count() != 4)
    throw new InvalidOperationException("Nullable last-indexed pagination skipped or duplicated a document.");
var descendingDatePage = await store.ListDocumentsAsync(new DocumentListRequest(projectId,
    SortBy: DocumentSortField.LastIndexedUtc, SortDirection: DocumentSortDirection.Desc, Limit: 2));
var descendingDateNext = await store.ListDocumentsAsync(new DocumentListRequest(projectId,
    SortBy: DocumentSortField.LastIndexedUtc, SortDirection: DocumentSortDirection.Desc, Limit: 2,
    Cursor: descendingDatePage.NextCursor));
if (descendingDatePage.Documents.Concat(descendingDateNext.Documents).Select(document => document.DocumentId).Distinct().Count() != 4)
    throw new InvalidOperationException("Descending nullable last-indexed pagination skipped or duplicated a document.");

await ExpectCodeAsync("project_not_found", () => store.ListDocumentsAsync(new DocumentListRequest(Guid.CreateVersion7())));
await ExpectCodeAsync("invalid_limit", () => store.ListDocumentsAsync(new DocumentListRequest(projectId, Limit: 0)));
await ExpectCodeAsync("invalid_filter", () => store.ListDocumentsAsync(new DocumentListRequest(projectId,
    Status: (DocumentStatusFilter)999)));
await ExpectCodeAsync("invalid_filter", () => store.ListDocumentsAsync(new DocumentListRequest(projectId,
    PathPrefixes: [Path.GetDirectoryName(fixture)!])));
await ExpectCodeAsync("invalid_cursor", () => store.ListDocumentsAsync(new DocumentListRequest(projectId, Cursor: "not-a-cursor")));
await ExpectCodeAsync("invalid_cursor", () => store.ListDocumentsAsync(new DocumentListRequest(projectId,
    DocumentStatusFilter.Indexed, Limit: 2, Cursor: firstPage.NextCursor)));

var deltaJob = await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(5))
    ?? throw new InvalidOperationException("The delta job was not leased.");
if (deltaJob.DocumentId != delta.DocumentId)
    throw new InvalidOperationException("The pending delta job was not selected.");
await CommitAsync(writer, deltaJob, delta, deltaPath, "text/plain", includeAttachment: false);
await ExpectCodeAsync("invalid_cursor", () => store.ListDocumentsAsync(new DocumentListRequest(projectId,
    Limit: 2, Cursor: firstPage.NextCursor)));

var echo = await ObserveAsync(writer, projectId, folderId, echoPath, new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero));
var unicodeName = await store.ListDocumentsAsync(new DocumentListRequest(projectId, NameQuery: "éCHO"));
if (unicodeName.Documents.Single().DocumentId != echo.DocumentId)
    throw new InvalidOperationException("Unicode case-insensitive filename filtering is incorrect.");
await writer.SetProjectPausedAsync(projectId, paused: true);
var paused = await store.ListDocumentsAsync(new DocumentListRequest(projectId, DocumentStatusFilter.Paused));
if (paused.Documents.Single().DocumentId != echo.DocumentId)
    throw new InvalidOperationException("Paused project work was not represented with paused document status.");
var stillIndexed = await store.ListDocumentsAsync(new DocumentListRequest(projectId, DocumentStatusFilter.Indexed));
if (!stillIndexed.Documents.Select(document => document.DocumentId).Order().SequenceEqual(new[] { alpha.DocumentId, delta.DocumentId }.Order()))
    throw new InvalidOperationException("Pausing the project incorrectly changed completed document status.");

await writer.RemoveProjectAsync(projectId);
await host.StopAsync();
Console.WriteLine("DOCUMENT_INVENTORY_SMOKE_OK statuses=covered filters=covered pagination=stable metadata=verified errors=structured");

static async Task<ObservationResult> ObserveAsync(IIndexWriter writer, Guid projectId, Guid folderId, string path,
    DateTimeOffset modifiedUtc)
{
    var info = new FileInfo(path);
    return await writer.ObserveFileAsync(new FileObservation(projectId, folderId, path, info.Length, modifiedUtc));
}

static async Task CommitAsync(IIndexWriter writer, IndexJobLease job, ObservationResult observation, string path,
    string mimeType, bool includeAttachment)
{
    var bytes = await File.ReadAllBytesAsync(path);
    var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
    var modified = job.SourcePath.EndsWith("alpha.txt", StringComparison.OrdinalIgnoreCase)
        ? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
        : new DateTimeOffset(2026, 1, 4, 12, 0, 0, TimeSpan.Zero);
    var begin = await writer.BeginRevisionAsync(job, hash, bytes.LongLength, modified);
    if (!begin.ShouldExtract || begin.RevisionId is null)
        throw new InvalidOperationException("The inventory revision did not begin.");
    var rootId = Guid.CreateVersion7();
    var nodes = new List<ContentNodeDraft>
    {
        new(rootId, null, 0, Path.GetFileName(path), mimeType, "root", 0)
    };
    var passages = new List<PassageDraft>
    {
        new(Guid.CreateVersion7(), rootId, 0, "root inventory text", "root inventory text",
            new SourceLocation(LocationKind.Document), ExtractionMethod.NativeText, null, null)
    };
    if (includeAttachment)
    {
        var attachmentId = Guid.CreateVersion7();
        nodes.Add(new ContentNodeDraft(attachmentId, rootId, 0, "attachment.pdf", "application/pdf", "email-attachment", 1));
        passages.Add(new PassageDraft(Guid.CreateVersion7(), attachmentId, 0, "attachment inventory text",
            "attachment inventory text", new SourceLocation(LocationKind.EmailPart, EmailPart: "attachment:0"),
            ExtractionMethod.Attachment, null, null));
    }
    var committed = await writer.CommitRevisionAsync(new IndexCommitRequest(job.JobId, job.ProjectId, observation.DocumentId,
        begin.RevisionId.Value, job.ExpectedObservationEpoch, hash, bytes.LongLength, modified, nodes, passages, null, []));
    if (!committed)
        throw new InvalidOperationException("The inventory revision did not commit.");
}

static void AssertStatus(DocumentListResponse response, Guid documentId, DocumentInventoryStatus expected)
{
    var actual = response.Documents.Single(document => document.DocumentId == documentId).Status;
    if (actual != expected)
        throw new InvalidOperationException($"Expected {expected} for {documentId}, received {actual}.");
}

static async Task ExpectCodeAsync(string expected, Func<Task<DocumentListResponse>> action)
{
    try
    {
        await action();
        throw new InvalidOperationException($"Expected {expected}.");
    }
    catch (McpIndexException exception) when (exception.Code == expected)
    {
    }
}
