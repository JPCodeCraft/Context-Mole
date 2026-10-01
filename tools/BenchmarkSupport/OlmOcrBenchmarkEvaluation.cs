using System.Text.Json;
using System.Text.Json.Serialization;
using ContextMole.Core;

namespace ContextMole.Benchmarks;

public sealed record OlmOcrManifest(int Version, string Dataset, string Revision, string Selection,
    OlmOcrDocument[] Documents,
    [property: JsonPropertyName("checks_file")] string ChecksFile,
    [property: JsonPropertyName("checks_sha256")] string ChecksSha256)
{
    public const string DatasetId = "allenai/olmOCR-bench";
    public const string DatasetRevision = "54a96a6fb6a2bd3b297e59869491db4d3625b711";
    public const string EvaluatorRevision = "f7cfe4c22098b154c76b6ec950d1c0a464eecf8d";

    public void Validate()
    {
        if (Version != 1 || Dataset != DatasetId || Revision != DatasetRevision ||
            string.IsNullOrWhiteSpace(Selection) || Documents is not { Length: > 0 } ||
            string.IsNullOrWhiteSpace(ChecksFile) || !IsHash(ChecksSha256))
            throw new InvalidDataException("An olmOCR manifest must contain the supported pinned dataset, documents and checks hash.");
        if (Documents.Select(document => document.Id).Distinct(StringComparer.Ordinal).Count() != Documents.Length)
            throw new InvalidDataException("Duplicate olmOCR PDF identifiers.");
        foreach (var document in Documents)
            if (string.IsNullOrWhiteSpace(document.Id) || string.IsNullOrWhiteSpace(document.File) ||
                string.IsNullOrWhiteSpace(document.Category) || !IsHash(document.Sha256))
                throw new InvalidDataException("Invalid olmOCR document metadata.");
    }

    public static string ResolveCachePath(string manifestPath, string relativeFile)
    {
        if (string.IsNullOrWhiteSpace(relativeFile) || Path.IsPathRooted(relativeFile))
            throw new InvalidDataException("Benchmark files must be cache-relative.");
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var path = Path.GetFullPath(Path.Combine(root, relativeFile));
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("A benchmark file escaped the cache directory.");
        return path;
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

public sealed record OlmOcrDocument(string Id, string File, string Sha256, string Category);
public sealed record OlmOcrCheck(string Pdf, int Page, string Id, string Type, int MaxDiffs, string Category, JsonElement Data)
{
    public string? String(string name) => Data.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
        ? value.GetString() : null;
    public int? Integer(string name) => Data.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
        ? value.GetInt32() : null;
    public bool Boolean(string name, bool fallback) => Data.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
        ? value.GetBoolean() : fallback;
}

public sealed record OlmOcrPageOutput(int Page, string Text, string[] Headings, string[][][] Tables);
public sealed record OlmOcrDocumentOutput(string Pdf, int PageCount, OlmOcrPageOutput[] Pages, string[] Errors);
public sealed record OlmOcrCheckResult(string Id, string Pdf, int Page, string Category, string Type, string Status, string Reason);
public sealed record OlmOcrCategoryScore(string Category, int Total, int Passed, int Failed, int Unsupported, int Errors,
    double Coverage, double? SupportedPassRate, double ConservativePassRate);
public sealed record OlmOcrEvaluationReport(string Verdict, int Total, int Passed, int Failed, int Unsupported, int Errors,
    double Coverage, double? SupportedPassRate, double ConservativeCategoryMacro, OlmOcrCategoryScore[] Categories,
    OlmOcrCheckResult[] Checks);

/// <summary>Independent consumer-output adaptation of pinned olmOCR checks; not the official benchmark score.</summary>
public static class OlmOcrBenchmarkEvaluation
{
    public static OlmOcrCheck[] ReadChecks(string jsonl, IReadOnlyList<OlmOcrDocument> documents, bool addBaseline = true)
    {
        var categories = documents.ToDictionary(document => document.Id, document => document.Category, StringComparer.Ordinal);
        var checks = new List<OlmOcrCheck>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(jsonl);
        for (var lineNumber = 1; reader.ReadLine() is { } line; lineNumber++)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var json = JsonDocument.Parse(line);
            var data = json.RootElement;
            var pdf = data.GetProperty("pdf").GetString() ?? "";
            var id = data.GetProperty("id").GetString() ?? "";
            var type = data.GetProperty("type").GetString() ?? "";
            var page = data.GetProperty("page").GetInt32();
            var maxDiffs = data.TryGetProperty("max_diffs", out var differences) ? differences.GetInt32() : 0;
            if (!categories.TryGetValue(pdf, out var category) || string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(type) || page < 1 || maxDiffs is < 0 or > 256 || !identifiers.Add(id))
                throw new InvalidDataException($"Invalid, duplicate, or unselected olmOCR check at JSONL line {lineNumber}.");
            checks.Add(new OlmOcrCheck(pdf, page, id, type, maxDiffs, type == "baseline" ? "baseline" : category, data.Clone()));
        }
        if (checks.Count == 0 || documents.Any(document => checks.All(check => check.Pdf != document.Id)))
            throw new InvalidDataException("Every selected PDF must retain upstream checks.");
        if (addBaseline)
            foreach (var document in documents.Where(document => !checks.Any(check => check.Pdf == document.Id && check.Type == "baseline")))
            {
                var id = $"contextmole:auto-baseline:{document.Id}";
                if (!identifiers.Add(id)) throw new InvalidDataException("Synthetic baseline identifier collision.");
                checks.Add(new OlmOcrCheck(document.Id, 1, id, "baseline", 0, "baseline",
                    JsonSerializer.SerializeToElement(new { max_repeats = 30, check_disallowed_characters = true })));
            }
        return checks.ToArray();
    }

    public static OlmOcrDocumentOutput Project(string pdf, int pageCount, ExtractionResult extraction)
    {
        if (pageCount < 1) throw new ArgumentOutOfRangeException(nameof(pageCount));
        var pages = Enumerable.Range(1, pageCount).Select(page =>
        {
            var sections = extraction.Root.Sections.Where(section => section.Location.Page == page).ToArray();
            // Canonical evidence deliberately includes annotated boilerplate, matching what the product exposes.
            var headings = sections.Where(section => section.Heading is not null &&
                OlmOcrBenchmarkText.Normalize(section.Text) == OlmOcrBenchmarkText.Normalize(section.Heading))
                .Select(section => section.Heading!).Distinct(StringComparer.Ordinal).ToArray();
            var tables = sections.Where(section => IsTable(section.Location.StructurePath))
                .GroupBy(section => section.Location.StructurePath, StringComparer.Ordinal)
                .Select(group => group.SelectMany(section => section.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Split('\n').Where(row => row.Length > 0).Select(row => row.Split('\t'))).ToArray()).ToArray();
            return new OlmOcrPageOutput(page, string.Join("\n", sections.Select(section => section.Text)), headings, tables);
        }).ToArray();
        return new OlmOcrDocumentOutput(pdf, pageCount, pages, extraction.Errors.Select(error => $"{error.Code}: {error.Message}").ToArray());
    }

    public static OlmOcrEvaluationReport Evaluate(IReadOnlyList<OlmOcrCheck> checks, IReadOnlyList<OlmOcrDocumentOutput> documents,
        CancellationToken cancellationToken = default)
    {
        if (checks.Count == 0) throw new ArgumentException("No checks to evaluate.", nameof(checks));
        var outputs = documents.ToDictionary(document => document.Pdf, StringComparer.Ordinal);
        var results = checks.Select(check =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return EvaluateCheck(check, outputs.GetValueOrDefault(check.Pdf), cancellationToken);
        }).ToArray();
        var categories = results.GroupBy(result => result.Category, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => Score(group.Key, group.ToArray())).ToArray();
        var total = Score("all", results);
        return new OlmOcrEvaluationReport(total.Unsupported > 0 || total.Errors > 0 ? "incomplete" : total.Failed > 0 ? "failed" : "passed",
            total.Total, total.Passed, total.Failed, total.Unsupported, total.Errors, total.Coverage, total.SupportedPassRate,
            categories.Average(category => category.ConservativePassRate), categories, results);
    }

    private static OlmOcrCheckResult EvaluateCheck(OlmOcrCheck check, OlmOcrDocumentOutput? output, CancellationToken cancellationToken)
    {
        OlmOcrCheckResult Result(string status, string reason) => new(check.Id, check.Pdf, check.Page, check.Category, check.Type, status, reason);
        if (check.Type is "math" or "footnote")
            return Result("unsupported", check.Type == "math" ? "LaTeX/KaTeX equation rendering equivalence is unavailable." : "PDF footnote marker association is not retained.");
        if (check.Type == "format" && check.Data.TryGetProperty("format", out var format) &&
            format.ValueKind != JsonValueKind.String)
            return Result("error", "A format check requires a string format field.");
        if (check.Type == "format" && check.String("format") is not "heading")
            return Result("unsupported", "Bold and italic formatting are not retained by the PDF extraction projection.");
        if (check.Type is not ("present" or "absent" or "order" or "table" or "baseline" or "format"))
            return Result("unsupported", "Unknown upstream check type; no checks are silently skipped.");
        if (output is null || check.Page > output.PageCount)
            return Result("error", "The selected PDF/page has no extraction output.");
        if (output.Errors.Length > 0)
            return Result("error", "Extraction reported errors: " + string.Join("; ", output.Errors));
        var page = output.Pages.SingleOrDefault(page => page.Page == check.Page);
        if (page is null) return Result("error", "The physical PDF page has no extraction output.");
        try
        {
            bool passed;
            var reason = "";
            switch (check.Type)
            {
                case "present":
                case "absent":
                    var match = OlmOcrBenchmarkText.MatchText(Required(check, "text"), page.Text, check.MaxDiffs,
                        check.Boolean("case_sensitive", true), check.Integer("first_n"), check.Integer("last_n"));
                    passed = check.Type == "present" ? match : !match;
                    reason = check.MaxDiffs == 0 ? "Exact symmetric normalized partial match." : "Adapted bounded Levenshtein partial match (not RapidFuzz).";
                    break;
                case "order":
                    var before = OlmOcrBenchmarkText.FindNearStarts(OlmOcrBenchmarkText.Normalize(Required(check, "before")), OlmOcrBenchmarkText.Normalize(page.Text), check.MaxDiffs);
                    var after = OlmOcrBenchmarkText.FindNearStarts(OlmOcrBenchmarkText.Normalize(Required(check, "after")), OlmOcrBenchmarkText.Normalize(page.Text), check.MaxDiffs);
                    passed = before.Any(left => after.Any(right => left < right));
                    reason = "Case-sensitive normalized substring starts, with consolidated approximate matches.";
                    break;
                case "table":
                    passed = page.Tables.Any(table => MatchesTable(check, table, cancellationToken));
                    reason = "Adapted rectangular TSV cell graph; no HTML rowspan/colspan or marked-header information.";
                    break;
                case "baseline":
                    (passed, reason) = OlmOcrBenchmarkText.CheckBaseline(page.Text, check.Integer("max_length"),
                        check.Boolean("max_length_skips_image_alt_tags", false), check.Integer("max_repeats") ?? 30,
                        check.Boolean("check_disallowed_characters", true));
                    break;
                default:
                    passed = page.Headings.Any(heading => OlmOcrBenchmarkText.MatchText(Required(check, "text"), heading,
                        check.MaxDiffs, check.Boolean("case_sensitive", true)));
                    reason = "Adapted explicit extracted-heading metadata; surrounding body text is not treated as a heading.";
                    break;
            }
            return Result(passed ? "passed" : "failed", reason);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return Result("error", "Invalid check or adapter work limit: " + exception.Message);
        }
    }

    private static bool MatchesTable(OlmOcrCheck check, string[][] table, CancellationToken cancellationToken)
    {
        var cell = Required(check, "cell");
        for (var row = 0; row < table.Length; row++)
            for (var column = 0; column < table[row].Length; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CellMatches(cell, table[row][column], check.MaxDiffs)) continue;
                bool Relation(string name, IEnumerable<string> candidates) => string.IsNullOrEmpty(check.String(name)) ||
                    candidates.Any(candidate => CellMatches(check.String(name)!, candidate, check.MaxDiffs));
                string At(int r, int c) => r >= 0 && r < table.Length && c >= 0 && c < table[r].Length ? table[r][c] : "";
                // A retained empty TSV cell still occupies its position; never jump over it.
                IEnumerable<string> Direction(int dr, int dc) => [At(row + dr, column + dc)];
                if (Relation("up", Direction(-1, 0)) && Relation("down", Direction(1, 0)) &&
                    Relation("left", Direction(0, -1)) && Relation("right", Direction(0, 1)) &&
                    Relation("top_heading", row > 0 ? new[] { At(0, column) } : []) &&
                    Relation("left_heading", column > 0 ? new[] { At(row, 0) } : [])) return true;
            }
        return false;
    }

    private static bool CellMatches(string expected, string actual, int maxDiffs)
    {
        var normalized = OlmOcrBenchmarkText.Normalize(expected);
        var length = normalized.EnumerateRunes().Count();
        if (length == 0 || string.IsNullOrWhiteSpace(actual)) return false;
        return OlmOcrBenchmarkText.IndelSimilarity(normalized, OlmOcrBenchmarkText.Normalize(actual)) >= Math.Max(.5, 1d - (double)maxDiffs / length);
    }

    private static string Required(OlmOcrCheck check, string name) => !string.IsNullOrEmpty(check.String(name))
        ? check.String(name)! : throw new ArgumentException($"{check.Type} requires nonempty '{name}'.");
    private static bool IsTable(string? path) => path is not null &&
        (path.StartsWith("table[", StringComparison.Ordinal) || path.Contains("/table[", StringComparison.Ordinal)) &&
        !path.EndsWith("/caption", StringComparison.Ordinal);
    private static OlmOcrCategoryScore Score(string category, OlmOcrCheckResult[] checks)
    {
        var passed = checks.Count(check => check.Status == "passed");
        var failed = checks.Count(check => check.Status == "failed");
        var supported = passed + failed;
        return new OlmOcrCategoryScore(category, checks.Length, passed, failed, checks.Count(check => check.Status == "unsupported"),
            checks.Count(check => check.Status == "error"), (double)supported / checks.Length,
            supported == 0 ? null : (double)passed / supported, (double)passed / checks.Length);
    }
}
