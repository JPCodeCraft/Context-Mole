#:property TargetFramework=net10.0
#:property PublishAot=false
#:property NuGetLockFilePath=../artifacts/SearchEvaluationStressBenchmark.packages.lock.json
#:project ../src/Search/ContextMole.Search.csproj
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ContextMole.Core;
using ContextMole.Search;

var text = string.Join(' ', Enumerable.Repeat("ordinary supporting context", 10_000)) + " required needle";
var candidate = new SearchCandidate(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "required needle",
    "/documents/example.txt", "example.txt", ".txt", DateTimeOffset.UnixEpoch,
    new SourceLocation(LocationKind.Document), [], ExtractionMethod.NativeText, null)
    { SectionId = Guid.NewGuid(), SectionText = text };
SearchClause[] clauses = [
    new("required", "required", SearchClauseOccur.Must, Fields: [SearchField.Body]),
    new("optional", "needle", SearchClauseOccur.Should, Fields: [SearchField.Body]),
    new("excluded", "obsolete", SearchClauseOccur.MustNot, Fields: [SearchField.Body])];
var prepare = typeof(StructuredSearchQuery).GetMethod("Prepare", BindingFlags.Static | BindingFlags.NonPublic);
object? query = prepare?.Invoke(null, [clauses]);
var evaluate = query?.GetType().GetMethod("Evaluate");
ClauseEvaluation Run() => query is null ? StructuredSearchQuery.Evaluate(candidate, clauses, 1) :
    (ClauseEvaluation)evaluate!.Invoke(query, [candidate, 1])!;
if (!Run().IsMatch) throw new Exception("Invalid fixture");
var samples = new List<object>();
for (var round = 0; round < 3; round++) {
    GC.Collect(); GC.WaitForPendingFinalizers();
    var before = GC.GetAllocatedBytesForCurrentThread();
    var watch = Stopwatch.StartNew();
    query = prepare?.Invoke(null, [clauses]);
    for (var i = 0; i < 200; i++) if (!Run().IsMatch) throw new Exception("Missing match");
    watch.Stop();
    samples.Add(new { milliseconds = watch.Elapsed.TotalMilliseconds,
        allocated_bytes = GC.GetAllocatedBytesForCurrentThread() - before });
}
var report = JsonSerializer.Serialize(new { model = "none", section_characters = text.Length,
    iterations = 200, scope = "clause evaluator only; complete cold request including preparation and first scan", prepared = prepare is not null,
    samples }, new JsonSerializerOptions { WriteIndented = true });
var outputIndex = Array.IndexOf(args, "--output");
if (outputIndex >= 0)
{
    if (outputIndex + 1 >= args.Length) throw new ArgumentException("--output needs a file path.");
    var output = Path.GetFullPath(args[outputIndex + 1]);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, report + Environment.NewLine);
}
Console.WriteLine(report);
