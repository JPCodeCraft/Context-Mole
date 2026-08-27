#:property TargetFramework=net10.0
#:project ../src/Core/ContextMole.Core.csproj
#:project ../src/Documents/ContextMole.Documents.csproj
#:project ../src/Infrastructure/ContextMole.Infrastructure.csproj
#:project ../src/Storage/ContextMole.Storage.csproj
#:project ../src/Indexing/ContextMole.Indexing.csproj
#:package Microsoft.Extensions.Hosting

using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Indexing;
using ContextMole.Infrastructure;
using ContextMole.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var source = Environment.GetEnvironmentVariable("CONTEXTMOLE_SMOKE_SOURCE")
    ?? throw new InvalidOperationException("Set CONTEXTMOLE_SMOKE_SOURCE to an EML fixture.");
var data = Environment.GetEnvironmentVariable("CONTEXTMOLE_DATA_DIR")
    ?? throw new InvalidOperationException("Set CONTEXTMOLE_DATA_DIR to an isolated smoke directory.");
var fixture = data + "-fixtures";
Directory.CreateDirectory(fixture);
var copy = Path.Combine(fixture, Path.GetFileName(source));
File.Copy(source, copy, overwrite: true);

var builder = Host.CreateApplicationBuilder();
builder.Services.AddContextMoleInfrastructure(includeOcr: false);
builder.Services.AddSingleton<IOcrEngine, NoDownloadOcrEngine>();
builder.Services.AddContextMoleDocuments();
builder.Services.AddWritableContextMoleStorage();
builder.Services.AddContextMoleIndexing();
using var host = builder.Build();
await host.StartAsync();
var writer = host.Services.GetRequiredService<IIndexWriter>();
var store = host.Services.GetRequiredService<ISearchStore>();
var projectId = await writer.CreateProjectAsync(new CreateProjectRequest("EML regression smoke", [fixture]));
var first = await WaitForIdleAsync(store, projectId, minimumGeneration: 1);
if (first.IndexedCount != 1)
    throw new InvalidOperationException($"The EML copy was not indexed: indexed={first.IndexedCount}, errors={first.ErrorCount}.");

await writer.RequestReindexAsync(projectId);
var second = await WaitForIdleAsync(store, projectId, first.SearchGeneration + 1);
var errors = await store.ListProjectErrorsAsync(projectId, 100);
if (errors.Any(error => error.Code == "indexing_failed" || error.Message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("The EML reindex still produced a foreign-key failure.");

await writer.RemoveProjectAsync(projectId);
await host.StopAsync();
Console.WriteLine($"EML_REGRESSION_SMOKE_OK generation={second.SearchGeneration} extraction_errors={errors.Count}");

static async Task<ProjectSummary> WaitForIdleAsync(ISearchStore store, Guid projectId, long minimumGeneration)
{
    for (var attempt = 0; attempt < 240; attempt++)
    {
        var project = (await store.ListProjectsAsync()).Single(item => item.Id == projectId);
        if (project.PendingCount == 0 && project.SearchGeneration >= minimumGeneration) return project;
        await Task.Delay(250);
    }
    throw new TimeoutException("The EML indexing smoke did not become idle.");
}

sealed class NoDownloadOcrEngine : IOcrEngine
{
    public bool IsAvailable => false;
    public string UnavailableReason => "OCR downloads are intentionally disabled for this extraction regression.";
    public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new OcrResult(string.Empty, null));
}
