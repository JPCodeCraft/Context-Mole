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

var data = Environment.GetEnvironmentVariable("MCPINDEXSEARCH_DATA_DIR")
    ?? throw new InvalidOperationException("Set MCPINDEXSEARCH_DATA_DIR to an isolated smoke directory.");
var fixture = data + "-fixtures";
Directory.CreateDirectory(fixture);
var source = Path.Combine(fixture, "materialize.eml");
const string attachmentText = "Materialized attachment evidence.";
var sourceText = """
    From: source@example.test
    To: reader@example.test
    Subject: Materialization smoke
    MIME-Version: 1.0
    Content-Type: multipart/mixed; boundary="materialize-boundary"

    --materialize-boundary
    Content-Type: text/plain; charset=utf-8

    Root materialization evidence.
    --materialize-boundary
    Content-Type: text/plain; name="../escape.txt"
    Content-Disposition: attachment; filename="../escape.txt"
    Content-Transfer-Encoding: base64

    TWF0ZXJpYWxpemVkIGF0dGFjaG1lbnQgZXZpZGVuY2Uu
    --materialize-boundary--
    """.Replace("\n", "\r\n", StringComparison.Ordinal);
await File.WriteAllTextAsync(source, sourceText);

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
var projectId = await writer.CreateProjectAsync(new CreateProjectRequest("Materialization smoke", [fixture]));
await WaitForIdleAsync(store, projectId);

var rootSearch = await search.SearchAsync(new SearchRequest(projectId, "Root materialization evidence", 10));
var rootHit = rootSearch.Results.FirstOrDefault(item => item.AttachmentChain.Count == 0)
    ?? throw new InvalidOperationException($"Root passage was not found. Mode={rootSearch.ActualMode}; warnings={string.Join(" | ", rootSearch.Warnings)}; results={rootSearch.Results.Count}.");
var attachmentSearch = await search.SearchAsync(new SearchRequest(projectId, "Materialized attachment evidence", 10));
var attachmentHit = attachmentSearch.Results.FirstOrDefault(item => item.AttachmentChain.Count > 0)
    ?? throw new InvalidOperationException($"Attachment passage was not found. Mode={attachmentSearch.ActualMode}; warnings={string.Join(" | ", attachmentSearch.Warnings)}; results={attachmentSearch.Results.Count}.");
var root = await materializer.MaterializeAsync(projectId, rootHit.ContentId);
if (root.Temporary || !PathsEqual(root.LocalPath, source) || !PathsEqual(root.SourcePath, source) || root.AttachmentChain.Count != 0)
    throw new InvalidOperationException("Root materialization did not return the verified source file.");

var attachment = await materializer.MaterializeAsync(projectId, attachmentHit.ContentId);
if (!attachment.Temporary || !File.Exists(attachment.LocalPath) ||
    !string.Equals((await File.ReadAllTextAsync(attachment.LocalPath)).Trim(), attachmentText, StringComparison.Ordinal))
    throw new InvalidOperationException("The indexed attachment was not materialized correctly.");
if (!IsWithin(paths.TempDirectory, attachment.LocalPath) || attachment.AttachmentChain.Count != 1)
    throw new InvalidOperationException("The attachment escaped controlled temporary storage or lost its provenance chain.");
if (!string.Equals(attachment.Sha256, await HashAsync(attachment.LocalPath), StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("The returned attachment fingerprint is incorrect.");
var repeated = await materializer.MaterializeAsync(projectId, attachmentHit.ContentId);
if (!PathsEqual(attachment.LocalPath, repeated.LocalPath))
    throw new InvalidOperationException("Repeated materialization did not safely reuse the verified temporary file.");

await ExpectCodeAsync("content_not_found", () => materializer.MaterializeAsync(projectId, Guid.CreateVersion7()));

await File.WriteAllTextAsync(source, sourceText.Replace("Root materialization evidence.",
    "Root materialization evidencf.", StringComparison.Ordinal));
await ExpectCodeAsync("source_changed", () => materializer.MaterializeAsync(projectId, attachmentHit.ContentId));
await File.WriteAllTextAsync(source, sourceText);

var missingPath = source + ".missing";
File.Move(source, missingPath);
await ExpectCodeAsync("source_missing", () => materializer.MaterializeAsync(projectId, attachmentHit.ContentId));
File.Move(missingPath, source);

var previousLimit = Environment.GetEnvironmentVariable(ContentMaterializationService.MaxBytesEnvironmentVariable);
try
{
    Environment.SetEnvironmentVariable(ContentMaterializationService.MaxBytesEnvironmentVariable, "32");
    var limited = new ContentMaterializationService(store, paths, host.Services.GetRequiredService<IGlobalCpuBudget>());
    await ExpectCodeAsync("size_limit_exceeded", () => limited.MaterializeAsync(projectId, rootHit.ContentId));
}
finally
{
    Environment.SetEnvironmentVariable(ContentMaterializationService.MaxBytesEnvironmentVariable, previousLimit);
}

await writer.RemoveProjectAsync(projectId);
await host.StopAsync();
Console.WriteLine("MATERIALIZATION_SMOKE_OK root=verified attachment=extracted traversal=contained source_change=blocked missing=blocked limit=enforced");

static async Task WaitForIdleAsync(ISearchStore store, Guid projectId)
{
    for (var attempt = 0; attempt < 120; attempt++)
    {
        var project = (await store.ListProjectsAsync()).Single(item => item.Id == projectId);
        if (project is { IndexedCount: 1, PendingCount: 0 })
            return;
        await Task.Delay(250);
    }
    throw new TimeoutException("The materialization fixture was not indexed.");
}

static async Task ExpectCodeAsync(string expected, Func<Task<MaterializedContent>> action)
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
    public string UnavailableReason => "OCR is not used by the materialization smoke.";
    public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new OcrResult(string.Empty, null));
}
