using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ContextMole.Core;

namespace ContextMole.Benchmarks;

/// <summary>Independent author-specified facts. Never generated from extractor/search output.</summary>
public sealed record MixedFormatManifest(int Version, MixedFormatFixture[] Fixtures,
    MixedFormatFact[] Facts, MixedFormatQuery[] Queries)
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true, WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
    public static async Task<MixedFormatManifest> LoadAsync(string path, CancellationToken token = default)
    {
        var manifest = JsonSerializer.Deserialize<MixedFormatManifest>(await File.ReadAllBytesAsync(path, token), JsonOptions)
            ?? throw new InvalidDataException("Missing mixed-format manifest.");
        manifest.Validate();
        return manifest;
    }
    public void Validate()
    {
        if (Version != 1 || Fixtures.Length == 0 || Facts.Length == 0 || Queries.Length == 0)
            throw new InvalidDataException("Unsupported or empty mixed-format corpus.");
        Unique(Fixtures.Select(value => value.Id), "fixture");
        Unique(Facts.Select(value => value.Id), "fact");
        Unique(Queries.Select(value => value.Id), "query");
        var roots = Fixtures.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        var facts = Facts.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var fixture in Fixtures)
        {
            if (Path.IsPathRooted(fixture.File) || fixture.File.Split('/', '\\').Any(value => value is "." or "..") ||
                fixture.Sha256.Length != 64 || !fixture.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException($"Invalid fixture path/hash: {fixture.Id}.");
        }
        foreach (var fact in Facts)
            if (!roots.Contains(fact.Root) || fact.Anchors.Length == 0 || fact.Anchors.Any(string.IsNullOrWhiteSpace) ||
                fact.Chain.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException($"Invalid fact: {fact.Id}.");
        foreach (var query in Queries)
            if (string.IsNullOrWhiteSpace(query.Text) || query.Terms.Length == 0 ||
                query.Relevant.Any(value => !facts.Contains(value)) || query.Negative != (query.Relevant.Length == 0) ||
                query.Split is not ("development" or "holdout") || query.Scope is not ("any" or "root_only" or "attachments_only"))
                throw new InvalidDataException($"Invalid query: {query.Id}.");
    }
    public static async Task VerifyFixturesAsync(string manifestPath, IEnumerable<MixedFormatFixture> fixtures,
        CancellationToken token = default)
    {
        foreach (var fixture in fixtures)
        {
            await using var stream = File.OpenRead(ResolvePath(manifestPath, fixture));
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
            if (!actual.Equals(fixture.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Fixture hash mismatch: {fixture.Id}.");
        }
    }
    public static string ResolvePath(string manifestPath, MixedFormatFixture fixture)
    {
        var directory = Path.GetFullPath(Path.GetDirectoryName(manifestPath)!);
        var path = Path.GetFullPath(Path.Combine(directory, fixture.File));
        if (!ProjectValidation.IsSameOrChild(path, directory) || path == directory)
            throw new InvalidDataException("Fixture path escaped corpus directory.");
        return path;
    }
    private static void Unique(IEnumerable<string> ids, string kind)
    {
        var values = ids.ToArray();
        if (values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidDataException($"Invalid or duplicate {kind} id.");
    }
}
public sealed record MixedFormatFixture(string Id, string File, string Sha256, bool RequiresOcr = false);
public sealed record MixedFormatFact(string Id, string Root, string[] Chain, string[] Anchors,
    SourceLocation Location, string? Heading = null, string? Notes = null);
public sealed record MixedFormatQuery(string Id, string Text, string[] Terms, string[] Relevant,
    string Language, string Scope, bool Negative, string Split);

public static class MixedFormatEvidence
{
    // Compare only the author-specified fields: PDF regions are intentionally not self-labeled.
    public static bool MatchesLocation(SourceLocation actual, SourceLocation expected) => actual.Kind == expected.Kind &&
        (expected.Page is null || actual.Page == expected.Page) &&
        (expected.Sheet is null || actual.Sheet == expected.Sheet) &&
        (expected.CellRange is null || actual.CellRange == expected.CellRange) &&
        (expected.Slide is null || actual.Slide == expected.Slide) &&
        (expected.StructurePath is null || ContainsStructure(actual.StructurePath, expected.StructurePath)) &&
        (expected.EmailPart is null || actual.EmailPart == expected.EmailPart) &&
        (expected.ImageFrame is null || actual.ImageFrame == expected.ImageFrame);
    private static bool ContainsStructure(string? actual, string expected)
    {
        if (actual == expected) return true;
        if (actual is null || !actual.Contains("..", StringComparison.Ordinal)) return false;
        var target = Regex.Match(expected, @"^(.*)\[(\d+)\]$", RegexOptions.CultureInvariant);
        if (!target.Success || !int.TryParse(target.Groups[2].Value, out var wanted)) return false;
        var parts = actual.Split("..", StringSplitOptions.None).Select(value => Regex.Match(value,
            @"^(.*)\[(\d+)\]$", RegexOptions.CultureInvariant)).ToArray();
        if (parts.Any(value => !value.Success || value.Groups[1].Value != target.Groups[1].Value)) return false;
        var indices = parts.Select(value => int.TryParse(value.Groups[2].Value, out var index) ? index : -1).ToArray();
        return indices.All(value => value >= 0) && indices.SequenceEqual(indices.Order()) && wanted >= indices[0] && wanted <= indices[^1];
    }
    public static bool MatchesSource(string root, IReadOnlyList<string> chain, SourceLocation location, MixedFormatFact fact) =>
        root == fact.Root && chain.SequenceEqual(fact.Chain, StringComparer.Ordinal) && MatchesLocation(location, fact.Location);
    public static double AnchorCoverage(string text, MixedFormatFact fact)
    {
        var normalized = TextNormalization.ForSearch(text);
        return fact.Anchors.Count(anchor => HasLiteralAnchor(normalized, TextNormalization.ForSearch(anchor))) / (double)fact.Anchors.Length;
    }
    private static bool HasLiteralAnchor(string text, string anchor)
    {
        for (var start = 0; start <= text.Length - anchor.Length; )
        {
            start = text.IndexOf(anchor, start, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return false;
            var end = start + anchor.Length;
            var before = start == 0 || !IsWord(anchor[0]) || !IsWord(text[start - 1]);
            var after = end == text.Length || !IsWord(anchor[^1]) || !IsWord(text[end]);
            if (char.IsDigit(anchor[0]) && start > 0 && (text[start - 1] is '+' or '-' ||
                start > 1 && text[start - 1] is '.' or ',' && char.IsDigit(text[start - 2]))) before = false;
            if (char.IsDigit(anchor[^1]) && end + 1 < text.Length && text[end] is '.' or ',' && char.IsDigit(text[end + 1])) after = false;
            if (before && after) return true;
            start++;
        }
        return false;
        static bool IsWord(char value) => char.IsLetterOrDigit(value) || char.IsSurrogate(value) ||
            char.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.ConnectorPunctuation;
    }
    public static IEnumerable<(string[] Chain, ExtractedSection Section)> Sections(ExtractedNode node, string[]? chain = null)
    {
        chain ??= [];
        foreach (var section in node.Sections) yield return (chain, section);
        foreach (var attachment in node.Attachments)
            foreach (var section in Sections(attachment, [.. chain, attachment.Name])) yield return section;
    }
}
