using System.Security.Cryptography;
using System.Text.Json;

using ContextMole.Core;
using ContextMole.Documents;

namespace ContextMole.Tests;

public sealed class ExtractionBenchmarkFixtureTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "benchmarks", "extraction");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task BenchmarkFixturesArePinnedAndNativeExtractionPreservesQualityAnchors()
    {
        var manifest = JsonSerializer.Deserialize<FixtureManifest>(
            await File.ReadAllTextAsync(Path.Combine(FixtureDirectory, "manifest.json"), TestContext.Current.CancellationToken),
            JsonOptions);
        Assert.NotNull(manifest);
        Assert.NotEmpty(manifest.Fixtures);
        Assert.Contains(manifest.Fixtures, fixture => fixture.RequiresOcr);
        Assert.Contains(manifest.Fixtures, fixture => !fixture.RequiresOcr);
        var extractor = new DocumentExtractionRegistry(new UnexpectedOcrEngine());
        foreach (var fixture in manifest.Fixtures)
        {
            var path = Path.Combine(FixtureDirectory, fixture.File);
            Assert.NotEmpty(fixture.ExpectedText);
            Assert.Equal(64, fixture.Sha256.Length);
            await using (var stream = File.OpenRead(path))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
                Assert.Equal(fixture.Sha256, hash, ignoreCase: true);
            }
            if (fixture.RequiresOcr) continue; // Real model timing/quality checks run with the opt-in benchmark --ocr flag.

            var result = await extractor.ExtractAsync(new ExtractionRequest(path), TestContext.Current.CancellationToken);
            Assert.Empty(result.Errors);
            var texts = SectionTexts(result.Root).ToArray();
            Assert.True(texts.Length >= fixture.MinimumSections, $"{fixture.Id}: expected at least {fixture.MinimumSections} sections.");
            var text = TextNormalization.ForSearch(string.Join('\n', texts));
            Assert.True(string.Join('\n', texts).Length >= fixture.MinimumCharacters,
                $"{fixture.Id}: expected at least {fixture.MinimumCharacters} extracted characters.");
            foreach (var expected in fixture.ExpectedText)
                Assert.Contains(TextNormalization.ForSearch(expected), text, StringComparison.OrdinalIgnoreCase);
            var position = 0;
            foreach (var anchor in fixture.OrderedText ?? [])
            {
                var normalizedAnchor = TextNormalization.ForSearch(anchor);
                var found = text.IndexOf(normalizedAnchor, position, StringComparison.OrdinalIgnoreCase);
                Assert.True(found >= 0, $"{fixture.Id}: missing or out-of-order anchor '{anchor}'.");
                position = found + normalizedAnchor.Length;
            }
            foreach (var page in fixture.PageAnchors ?? [])
            {
                var pageText = TextNormalization.ForSearch(string.Join('\n', result.Root.Sections
                    .Where(section => section.Location.Page == page.Page).Select(section => section.Text)));
                foreach (var anchor in page.Text)
                    Assert.Contains(TextNormalization.ForSearch(anchor), pageText, StringComparison.OrdinalIgnoreCase);
            }
            foreach (var table in fixture.TableAnchors ?? [])
                Assert.Contains(result.Root.Sections, section => section.Text.Contains(table, StringComparison.Ordinal));
            if (fixture.RequireRegions) Assert.All(result.Root.Sections, section => Assert.NotNull(section.Location.Region));
            Assert.True(result.Root.Sections.Count(section => section.IsBoilerplate) >= fixture.MinimumBoilerplateSections);
        }
    }

    private static IEnumerable<string> SectionTexts(ExtractedNode node) =>
        node.Sections.Select(section => section.Text).Concat(node.Attachments.SelectMany(SectionTexts));

    private sealed record FixtureManifest(Fixture[] Fixtures);
    private sealed record Fixture(string Id, string File, bool RequiresOcr, string[] ExpectedText,
        string Sha256, int MinimumSections = 1, int MinimumCharacters = 0,
        string[]? OrderedText = null, PageAnchor[]? PageAnchors = null, string[]? TableAnchors = null,
        bool RequireRegions = false, int MinimumBoilerplateSections = 0);
    private sealed record PageAnchor(int Page, string[] Text);
    private sealed class UnexpectedOcrEngine : IOcrEngine
    {
        public bool IsAvailable => false;
        public string? UnavailableReason => "Native benchmark fixtures must not invoke OCR.";
        public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(UnavailableReason);
    }
}
