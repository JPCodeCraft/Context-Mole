#:property TargetFramework=net10.0
#:project ../src/Core/MCPIndexSearch.Core.csproj
#:project ../src/Documents/MCPIndexSearch.Documents.csproj
#:project ../src/Infrastructure/MCPIndexSearch.Infrastructure.csproj
#:project ../src/Storage/MCPIndexSearch.Storage.csproj
#:project ../src/Indexing/MCPIndexSearch.Indexing.csproj
#:project ../src/Search/MCPIndexSearch.Search.csproj
#:package Microsoft.Extensions.Hosting

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MCPIndexSearch.Core;
using MCPIndexSearch.Documents;
using MCPIndexSearch.Indexing;
using MCPIndexSearch.Infrastructure;
using MCPIndexSearch.Search;
using MCPIndexSearch.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string zipEvidence = "ZIP archive indexing evidence 5d8c7fd1.";
const string traversalEvidence = "ZIP traversal materialization evidence 50b4450a.";
const string nestedEvidence = "Nested ZIP archive evidence f4ae0d30.";
var unsupportedBytes = new byte[] { 0, 1, 2, 3, 254, 255 };

var data = Environment.GetEnvironmentVariable("MCPINDEXSEARCH_DATA_DIR")
    ?? throw new InvalidOperationException("Set MCPINDEXSEARCH_DATA_DIR to an isolated smoke directory.");
var fixture = data + "-fixtures";
Directory.CreateDirectory(fixture);
var zipPath = Path.Combine(fixture, "archive.zip");
var rarPath = Path.Combine(fixture, "archive.rar");
await CreateZipFixtureAsync(zipPath, unsupportedBytes);

var rarFixture = Path.GetFullPath(Path.Combine("tools", "fixtures", "sharpcompress-rar4.rar"));
if (!File.Exists(rarFixture))
    throw new FileNotFoundException("The committed RAR regression fixture is missing.", rarFixture);
if (!string.Equals(await HashAsync(rarFixture),
        "60db161de57dc59aa12e0c45b1b70d78904da3d104e74748972ac38643f12802",
        StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("The committed RAR regression fixture fingerprint changed.");
File.Copy(rarFixture, rarPath, overwrite: true);

var builder = Host.CreateApplicationBuilder();
builder.Services.AddMcpIndexInfrastructure(includeOcr: false);
builder.Services.AddSingleton<IOcrEngine, NoDownloadOcrEngine>();
builder.Services.AddMcpIndexDocuments();
builder.Services.AddWritableMcpIndexStorage();
builder.Services.AddMcpIndexing();
builder.Services.AddMcpIndexSearch();
using var host = builder.Build();
await host.StartAsync();
var writer = host.Services.GetRequiredService<IIndexWriter>();
var store = host.Services.GetRequiredService<ISearchStore>();
var search = host.Services.GetRequiredService<HybridSearchService>();
var materializer = host.Services.GetRequiredService<IContentMaterializer>();
var paths = host.Services.GetRequiredService<IAppPaths>();
var projectId = await writer.CreateProjectAsync(new CreateProjectRequest("Archive smoke", [fixture]));
await WaitForIdleAsync(store, projectId, expectedDocuments: 2);

var zipHit = FindHit(await search.SearchAsync(new SearchRequest(projectId, zipEvidence, 10)), zipPath, 1);
var traversalHit = FindHit(await search.SearchAsync(new SearchRequest(projectId, traversalEvidence, 10)), zipPath, 1);
var nestedHit = FindHit(await search.SearchAsync(new SearchRequest(projectId, nestedEvidence, 10)), zipPath, 2);
var rarHit = (await search.SearchAsync(new SearchRequest(projectId, "unsigned bitwise right shift", 10))).Results
    .FirstOrDefault(item => PathsEqual(item.SourcePath, rarPath) && item.AttachmentChain.Count == 1)
    ?? throw new InvalidOperationException("RAR entry text was not indexed.");

var zip = await materializer.MaterializeAsync(projectId, zipHit.ContentId);
if (!zip.Temporary || !string.Equals((await File.ReadAllTextAsync(zip.LocalPath)).Trim(), zipEvidence,
        StringComparison.Ordinal) || !IsWithin(paths.TempDirectory, zip.LocalPath))
    throw new InvalidOperationException("The ZIP entry was not safely materialized.");

var traversal = await materializer.MaterializeAsync(projectId, traversalHit.ContentId);
if (!traversal.Temporary || traversal.AttachmentChain is not ["../escape.txt"] ||
    !string.Equals((await File.ReadAllTextAsync(traversal.LocalPath)).Trim(), traversalEvidence,
        StringComparison.Ordinal) || !IsWithin(paths.TempDirectory, traversal.LocalPath))
    throw new InvalidOperationException("The traversal-style ZIP entry escaped storage or lost exact provenance.");

var nested = await materializer.MaterializeAsync(projectId, nestedHit.ContentId);
if (!nested.Temporary || nested.AttachmentChain is not ["nested/archive.zip", "deep/nested-evidence.txt"] ||
    !string.Equals((await File.ReadAllTextAsync(nested.LocalPath)).Trim(), nestedEvidence,
        StringComparison.Ordinal) || !IsWithin(paths.TempDirectory, nested.LocalPath))
    throw new InvalidOperationException("The nested ZIP entry was not recursively materialized.");

var rar = await materializer.MaterializeAsync(projectId, rarHit.ContentId);
if (!rar.Temporary || !IsWithin(paths.TempDirectory, rar.LocalPath) ||
    !(await File.ReadAllTextAsync(rar.LocalPath)).Contains("namespace SharpCompress", StringComparison.Ordinal))
    throw new InvalidOperationException("The RAR entry was not safely materialized.");

var documents = (await store.ListDocumentsAsync(new DocumentListRequest(projectId))).Documents;
var rarDocument = documents.Single(document => PathsEqual(document.SourcePath, rarPath));
var rarAttachments = await store.ListAttachmentsAsync(projectId, rarDocument.DocumentId, null, 100);
var rarJpeg = rarAttachments.Items.Single(item => item.Name.EndsWith("test.jpg", StringComparison.Ordinal));
var materializedJpeg = await materializer.MaterializeAsync(projectId, rarJpeg.ContentId);
var jpegBytes = await File.ReadAllBytesAsync(materializedJpeg.LocalPath);
if (materializedJpeg.AttachmentChain.Count != 1 || materializedJpeg.AttachmentChain[0] != rarJpeg.Name ||
    jpegBytes is not [0xff, 0xd8, 0xff, ..] ||
    !string.Equals(materializedJpeg.Sha256, await HashAsync(materializedJpeg.LocalPath),
        StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("A later RAR entry did not preserve its ordinal, bytes, or fingerprint.");

var zipDocument = documents
    .Single(document => PathsEqual(document.SourcePath, zipPath));
var attachmentPage = await store.ListAttachmentsAsync(projectId, zipDocument.DocumentId, null, 100);
var unsupported = attachmentPage.Items.Single(item => item.Name == "payload.bin");
var unsupportedFile = await materializer.MaterializeAsync(projectId, unsupported.ContentId);
var materializedUnsupportedBytes = await File.ReadAllBytesAsync(unsupportedFile.LocalPath);
if (!unsupportedBytes.SequenceEqual(materializedUnsupportedBytes) ||
    !IsWithin(paths.TempDirectory, unsupportedFile.LocalPath))
    throw new InvalidOperationException("An indexed unsupported ZIP entry could not be materialized byte-for-byte.");

await writer.RemoveProjectAsync(projectId);
await host.StopAsync();
Console.WriteLine("ARCHIVE_SMOKE_OK zip=indexed rar=indexed nested=recursive traversal=contained materialization=verified");

static async Task CreateZipFixtureAsync(string path, byte[] unsupportedBytes)
{
    await using var nestedBytes = new MemoryStream();
    using (var nested = new ZipArchive(nestedBytes, ZipArchiveMode.Create, leaveOpen: true))
        await WriteEntryAsync(nested, "deep/nested-evidence.txt", nestedEvidence);

    await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
    await WriteEntryAsync(archive, "folder/zip-evidence.txt", zipEvidence);
    await WriteEntryAsync(archive, "../escape.txt", traversalEvidence);
    var nestedEntry = archive.CreateEntry("nested/archive.zip", CompressionLevel.SmallestSize);
    await using (var stream = nestedEntry.Open())
        await stream.WriteAsync(nestedBytes.ToArray());
    var unsupportedEntry = archive.CreateEntry("payload.bin", CompressionLevel.NoCompression);
    await using (var stream = unsupportedEntry.Open())
        await stream.WriteAsync(unsupportedBytes);
}

static async Task WriteEntryAsync(ZipArchive archive, string name, string text)
{
    var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
    await using var stream = entry.Open();
    await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
}

static SearchResultItem FindHit(SearchResponse response, string sourcePath, int expectedDepth) =>
    response.Results.FirstOrDefault(item => PathsEqual(item.SourcePath, sourcePath) &&
                                            item.AttachmentChain.Count == expectedDepth)
    ?? throw new InvalidOperationException($"Archive evidence at depth {expectedDepth} was not indexed.");

static async Task WaitForIdleAsync(ISearchStore store, Guid projectId, int expectedDocuments)
{
    for (var attempt = 0; attempt < 120; attempt++)
    {
        var project = (await store.ListProjectsAsync()).Single(item => item.Id == projectId);
        if (project.IndexedCount == expectedDocuments && project.PendingCount == 0)
            return;
        await Task.Delay(250);
    }
    throw new TimeoutException("The archive fixtures were not indexed.");
}

static async Task<string> HashAsync(string path)
{
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}

static bool IsWithin(string parent, string candidate)
{
    var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(candidate));
    return !Path.IsPathRooted(relative) && relative != ".." &&
           !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}

static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
    OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

sealed class NoDownloadOcrEngine : IOcrEngine
{
    public bool IsAvailable => false;
    public string UnavailableReason => "OCR is not used by the archive smoke.";
    public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new OcrResult(string.Empty, null));
}
