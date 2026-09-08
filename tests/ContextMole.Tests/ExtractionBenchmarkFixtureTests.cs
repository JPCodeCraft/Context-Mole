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
            Assert.True(texts.Sum(value => value.Length) >= fixture.MinimumCharacters,
                $"{fixture.Id}: expected at least {fixture.MinimumCharacters} extracted characters.");
            foreach (var expected in fixture.ExpectedText)
                Assert.Contains(TextNormalization.ForSearch(expected), text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<string> SectionTexts(ExtractedNode node) =>
        node.Sections.Select(section => section.Text).Concat(node.Attachments.SelectMany(SectionTexts));

    private sealed record FixtureManifest(Fixture[] Fixtures);
    private sealed record Fixture(string Id, string File, bool RequiresOcr, string[] ExpectedText,
        string Sha256, int MinimumSections = 1, int MinimumCharacters = 0);
    private sealed class UnexpectedOcrEngine : IOcrEngine
    {
        public bool IsAvailable => false;
        public string? UnavailableReason => "Native benchmark fixtures must not invoke OCR.";
        public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(UnavailableReason);
    }
}
