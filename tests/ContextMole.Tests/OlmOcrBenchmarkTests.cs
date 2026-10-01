using ContextMole.Benchmarks;
using ContextMole.Core;
using ContextMole.Infrastructure;

namespace ContextMole.Tests;

public sealed class OlmOcrBenchmarkTests
{
    private static readonly OlmOcrDocument[] Documents = [new("tables/test.pdf", "pdfs/test.pdf", new string('a', 64), "table_tests")];

    [Fact]
    public void OriginalChecksAndCategoriesRetainedAndBaselineAddedOnlyWhenAbsent()
    {
        var checks = OlmOcrBenchmarkEvaluation.ReadChecks("""
            {"pdf":"tables/test.pdf","page":2,"id":"a","type":"present","text":"keep","checked":"rejected"}
            {"pdf":"tables/test.pdf","page":1,"id":"b","type":"math","math":"x=y"}
            """, Documents);
        Assert.Equal(3, checks.Length);
        Assert.Equal("rejected", checks[0].String("checked"));
        Assert.Equal("table_tests", checks[1].Category);
        Assert.Equal("baseline", checks[2].Category);
        Assert.Equal(1, checks[2].Page);
        var baseline = OlmOcrBenchmarkEvaluation.ReadChecks("""
            {"pdf":"tables/test.pdf","page":2,"id":"baseline","type":"baseline"}
            """, Documents);
        Assert.Single(baseline);
        Assert.Equal(2, baseline[0].Page);
    }

    [Fact]
    public void MalformedDuplicateAndUnselectedChecksAreRejected()
    {
        const string row = """{"pdf":"tables/test.pdf","page":1,"id":"a","type":"present","text":"x"}""";
        Assert.Throws<InvalidDataException>(() => OlmOcrBenchmarkEvaluation.ReadChecks(row + "\n" + row, Documents));
        Assert.Throws<InvalidDataException>(() => OlmOcrBenchmarkEvaluation.ReadChecks(row.Replace("tables/test.pdf", "other.pdf"), Documents));
        Assert.Throws<InvalidDataException>(() => OlmOcrBenchmarkEvaluation.ReadChecks(row.Replace("\"page\":1", "\"page\":0"), Documents));
        Assert.Throws<InvalidDataException>(() => OlmOcrBenchmarkEvaluation.ReadChecks("", Documents));
    }

    [Fact]
    public void TableRelationsRequireOneCellSatisfyingAllDirectionsAndHeaders()
    {
        var checks = Checks("""
            {"pdf":"tables/test.pdf","page":1,"id":"good","type":"table","cell":"12","left":"Alice","up":"Count","top_heading":"Count","left_heading":"Alice","right":null}
            {"pdf":"tables/test.pdf","page":1,"id":"bad","type":"table","cell":"12","left":"Bob","up":"Count"}
            """);
        var output = Output("Owner\tCount\nAlice\t12\nBob\t20", [[ ["Owner", "Count"], ["Alice", "12"], ["Bob", "20"] ]]);
        var report = OlmOcrBenchmarkEvaluation.Evaluate(checks, [output], TestContext.Current.CancellationToken);
        Assert.Equal("passed", report.Checks[0].Status);
        Assert.Equal("failed", report.Checks[1].Status);
        Assert.Contains("TSV", report.Checks[0].Reason);
    }

    [Fact]
    public void EmptyTableCellsBlockAdjacencyAndSimilarityComparesWholeCell()
    {
        var checks = Checks("""
            {"pdf":"tables/test.pdf","page":1,"id":"skip","type":"table","cell":"123","left":"Alice","max_diffs":0}
            {"pdf":"tables/test.pdf","page":1,"id":"partial","type":"table","cell":"12","max_diffs":0}
            """);
        var report = OlmOcrBenchmarkEvaluation.Evaluate(checks, [Output("", [[["Alice", "", "123"]]])], TestContext.Current.CancellationToken);
        Assert.Equal("failed", report.Checks[0].Status);
        Assert.Equal("failed", report.Checks[1].Status);
    }

    [Fact]
    public void UnsupportedTypesRemainInCategoryDenominatorsAndCannotProducePassVerdict()
    {
        var checks = Checks("""
            {"pdf":"tables/test.pdf","page":1,"id":"text","type":"present","text":"visible"}
            {"pdf":"tables/test.pdf","page":1,"id":"math","type":"math","math":"x=y"}
            {"pdf":"tables/test.pdf","page":1,"id":"bold","type":"format","format":"bold","text":"visible"}
            {"pdf":"tables/test.pdf","page":1,"id":"footnote","type":"footnote","marker":"1"}
            {"pdf":"tables/test.pdf","page":1,"id":"future","type":"future"}
            """);
        var report = OlmOcrBenchmarkEvaluation.Evaluate(checks, [Output("visible")], TestContext.Current.CancellationToken);
        Assert.Equal("incomplete", report.Verdict);
        Assert.Equal(5, report.Total);
        Assert.Equal(1, report.Passed);
        Assert.Equal(4, report.Unsupported);
        Assert.Equal(.2, report.Coverage);
        Assert.Equal(1d, report.SupportedPassRate);
        Assert.Equal(.2, report.ConservativeCategoryMacro);
    }

    [Fact]
    public void ExtractionErrorsCannotPassAbsenceAndMissingPagesAreExplicitErrors()
    {
        var checks = Checks("""
            {"pdf":"tables/test.pdf","page":1,"id":"absent","type":"absent","text":"secret"}
            {"pdf":"tables/test.pdf","page":2,"id":"page","type":"present","text":"text"}
            """);
        var report = OlmOcrBenchmarkEvaluation.Evaluate(checks, [Output("") with { Errors = ["ocr_timeout"] }], TestContext.Current.CancellationToken);
        Assert.Equal(2, report.Errors);
        Assert.Equal(0, report.Passed);
        Assert.Equal("incomplete", report.Verdict);
    }

    [Fact]
    public void ProjectionRetainsCanonicalOrderPagesBoilerplateAndOnlyExplicitTablesHeadings()
    {
        ExtractedSection Section(string text, int page, string path, string? heading = null, bool boilerplate = false) =>
            new(text, new SourceLocation(LocationKind.Page, Page: page, StructurePath: path), ExtractionMethod.NativeText,
                Heading: heading, IsBoilerplate: boilerplate);
        var extraction = new ExtractionResult(new ExtractedNode("test.pdf", "application/pdf", "root",
            [Section("Title", 1, "page[1]/block[1]", "Title"), Section("Header", 1, "page[1]/block[2]", boilerplate: true),
                Section("body", 1, "page[1]/block[3]", "Title"), Section("A\tB\n1\t2", 1, "page[1]/table[4]"),
                Section("caption", 1, "page[1]/table[4]/caption"), Section("second", 2, "page[2]/block[1]")],
            [new ExtractedNode("attachment", "text/plain", "pdf-embedded-file", [Section("attachment secret", 1, "line[1]")], [])]), []);
        var output = OlmOcrBenchmarkEvaluation.Project("tables/test.pdf", 2, extraction);
        Assert.Equal("Title\nHeader\nbody\nA\tB\n1\t2\ncaption", output.Pages[0].Text);
        Assert.Equal(["Title"], output.Pages[0].Headings);
        Assert.Single(output.Pages[0].Tables);
        Assert.Equal(["1", "2"], output.Pages[0].Tables[0][1]);
        Assert.Equal("second", output.Pages[1].Text);
        Assert.Empty(output.Pages[1].Tables);
    }

    [Fact]
    public void HeadingFormatRequiresExplicitHeadingRatherThanBodyHeadingContext()
    {
        var checks = Checks("""
            {"pdf":"tables/test.pdf","page":1,"id":"heading","type":"format","format":"heading","text":"Title"}
            """);
        Assert.Equal("failed", OlmOcrBenchmarkEvaluation.Evaluate(checks, [Output("Title")], TestContext.Current.CancellationToken).Checks[0].Status);
        var output = Output("Title") with { Pages = [new OlmOcrPageOutput(1, "Title", ["Title"], [])] };
        Assert.Equal("passed", OlmOcrBenchmarkEvaluation.Evaluate(checks, [output], TestContext.Current.CancellationToken).Checks[0].Status);
    }

    [Fact]
    public void CategoryMacroWeightsCategoriesEquallyRatherThanChecks()
    {
        var checks = Checks("""
            {"pdf":"tables/test.pdf","page":1,"id":"a","type":"present","text":"visible"}
            {"pdf":"tables/test.pdf","page":1,"id":"b","type":"present","text":"visible"}
            {"pdf":"tables/test.pdf","page":1,"id":"c","type":"baseline","max_length":1}
            """);
        var report = OlmOcrBenchmarkEvaluation.Evaluate(checks, [Output("visible")], TestContext.Current.CancellationToken);
        Assert.Equal(.5, report.ConservativeCategoryMacro);
        Assert.Equal(2d / 3, report.SupportedPassRate);
        Assert.Equal("failed", report.Verdict);
    }

    [Fact]
    public void CachePathsCannotEscapeManifestDirectory()
    {
        var manifest = Path.Combine(Path.GetTempPath(), "olmocr", "manifest.json");
        Assert.Throws<InvalidDataException>(() => OlmOcrManifest.ResolveCachePath(manifest, "../secret.pdf"));
        Assert.Throws<InvalidDataException>(() => OlmOcrManifest.ResolveCachePath(manifest, Path.GetFullPath("secret.pdf")));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "olmocr", "pdfs", "test.pdf"), OlmOcrManifest.ResolveCachePath(manifest, "pdfs/test.pdf"));
    }

    [Fact]
    public void EvaluationHonorsCancellationBeforeScoringAndReportsMalformedFormat()
    {
        var checks = Checks("""
            {"pdf":"tables/test.pdf","page":1,"id":"bad","type":"format","format":123,"text":"Title"}
            """);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => OlmOcrBenchmarkEvaluation.Evaluate(checks, [Output("Title")], cancellation.Token));
        Assert.Equal("error", OlmOcrBenchmarkEvaluation.Evaluate(checks, [Output("Title")], TestContext.Current.CancellationToken).Checks[0].Status);
    }

    [Fact]
    public async Task CancelledFirstCachedOptInCannotReuseUnverifiedReadyState()
    {
        using var paths = new CachePaths();
        var settings = new CacheCpuSettings();
        using var budget = new GlobalCpuBudget(settings);
        using var engine = new PpOcrV6Engine(paths, settings, budget);
        engine.MarkAssetsPrepared();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.PrepareCachedAssetsAsync(cancellation.Token));
        var error = await Assert.ThrowsAsync<ContextMoleException>(() => engine.PrepareAssetsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("ocr_cached_assets_unavailable", error.Code);
        Assert.False(engine.AreAssetsReady);
        Assert.False(Directory.Exists(paths.AssetsDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedOcrPreparationRejectsMissingOrWrongHashWithoutChangingCache(bool wrongHash)
    {
        using var paths = new CachePaths();
        var settings = new CacheCpuSettings();
        using var budget = new GlobalCpuBudget(settings);
        using var engine = new PpOcrV6Engine(paths, settings, budget);
        var directory = Path.Combine(paths.AssetsDirectory, "pp-ocrv6-medium", $"{PpOcrV6Engine.DetectorRevision[..12]}-{PpOcrV6Engine.RecognizerRevision[..12]}");
        string[] files = ["detector.onnx", "recognizer.onnx", "recognizer.yml"];
        if (wrongHash)
        {
            Directory.CreateDirectory(directory);
            foreach (var file in files) await File.WriteAllTextAsync(Path.Combine(directory, file), "unchanged-invalid-model", TestContext.Current.CancellationToken);
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<ContextMoleException>(() => engine.PrepareCachedAssetsAsync(cancellation.Token));
        Assert.Equal("ocr_cached_assets_unavailable", error.Code);
        Assert.False(engine.AreAssetsReady);
        // Even normal internal preparation stays cached-only after opt-in fails.
        error = await Assert.ThrowsAsync<ContextMoleException>(() => engine.PrepareAssetsAsync(cancellation.Token));
        Assert.Equal("ocr_cached_assets_unavailable", error.Code);
        Assert.False(Directory.Exists(paths.DataDirectory));
        if (wrongHash)
        {
            Assert.Equal(files.Order(), Directory.GetFiles(directory).Select(Path.GetFileName).Order());
            foreach (var file in files) Assert.Equal("unchanged-invalid-model", await File.ReadAllTextAsync(Path.Combine(directory, file), TestContext.Current.CancellationToken));
        }
        else Assert.False(Directory.Exists(paths.AssetsDirectory));
    }

    private static OlmOcrCheck[] Checks(string jsonl) => OlmOcrBenchmarkEvaluation.ReadChecks(jsonl, Documents, addBaseline: false);
    private static OlmOcrDocumentOutput Output(string text, string[][][]? tables = null) =>
        new("tables/test.pdf", 1, [new OlmOcrPageOutput(1, text, [], tables ?? [])], []);
    private sealed class CachePaths : IAppPaths, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ContextMole-olmocr-cache-test", Guid.NewGuid().ToString("N"));
        public string DataDirectory => Path.Combine(_root, "data");
        public string DatabasePath => Path.Combine(DataDirectory, "index.db");
        public string AssetsDirectory => Path.Combine(_root, "assets");
        public string LogsDirectory => Path.Combine(DataDirectory, "logs");
        public string TempDirectory => Path.Combine(DataDirectory, "temp");
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
    private sealed class CacheCpuSettings : ICpuUsageSettings
    {
        public CpuUsageProfile Profile => CpuUsageProfile.Normal;
        public int LogicalProcessorCount => 1;
        public int ThreadLimit => 1;
        public int MaximumThreadLimit => 1;
        public event EventHandler? Changed { add { } remove { } }
        public void SetProfile(CpuUsageProfile profile) => throw new NotSupportedException();
    }
}
