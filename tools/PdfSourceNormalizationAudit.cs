#:property TargetFramework=net10.0
#:property PublishAot=false
#:property NuGetLockFilePath=../artifacts/PdfSourceNormalizationAudit.packages.lock.json
#:project ../tools/BenchmarkSupport/ContextMole.BenchmarkSupport.csproj
#:project ../src/Documents/ContextMole.Documents.csproj

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ContextMole.Benchmarks;
using ContextMole.Core;
using ContextMole.Documents;
using UglyToad.PdfPig;

if (args.Length != 2) throw new ArgumentException("Usage: dotnet run --file tools/PdfSourceNormalizationAudit.cs -- <pinned-manifest> <output-json>");
var manifestPath = Path.GetFullPath(args[0]);
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
var manifest = JsonSerializer.Deserialize<VidoreManifest>(manifestBytes, options)!;
manifest.Validate();
var wraps = new Regex(@"([\p{L}\p{M}])\u00AD[ \t]*(?:\r\n|[\r\n\u2028])[ \t]*([\p{Ll}])", RegexOptions.CultureInvariant);
var trailingLineWhitespace = new Regex(@"[ \t\f\v]+\n", RegexOptions.CultureInvariant);
var extractor = new DocumentExtractionRegistry(new NativeOnly());
var documents = new List<object>();
foreach (var document in manifest.Documents)
{
    var path = VidoreManifest.ResolvePdfPath(manifestPath, document.File);
    var bytes = await File.ReadAllBytesAsync(path);
    var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
    if (hash != document.Sha256) throw new InvalidDataException("Original PDF hash mismatch.");
    using var pdf = PdfDocument.Open(bytes);
    var glyphSoftHyphens = pdf.GetPages().Sum(page => page.Letters.Sum(letter => letter.Value.Count(character => character == '\u00AD')));
    var extraction = await extractor.ExtractAsync(new ExtractionRequest(path), CancellationToken.None);
    var changes = new List<object>();
    var oldAll = new StringBuilder();
    var newAll = new StringBuilder();
    var rawSoftHyphens = 0;
    var rawWraps = 0;
    foreach (var section in extraction.Root.Sections)
    {
        var table = section.Location.StructurePath?.Contains("table[", StringComparison.Ordinal) == true;
        var oldText = LegacyDisplay(section.Text, table);
        var newText = TextNormalization.ForDisplay(section.Text, table);
        oldAll.Append(oldText).Append('\0');
        newAll.Append(newText).Append('\0');
        rawSoftHyphens += section.Text.Count(character => character == '\u00AD');
        rawWraps += wraps.Matches(section.Text).Count;
        if (oldText != newText)
            changes.Add(new { section.Location.Page, section.Location.StructurePath,
                wraps = wraps.Matches(section.Text).Count, oldUtf16Length = oldText.Length, newUtf16Length = newText.Length,
                oldSha256 = HashText(oldText), newSha256 = HashText(newText) });
    }
    documents.Add(new { document.Id, originalPdfSha256 = hash, pageCount = pdf.NumberOfPages,
        rawSections = extraction.Root.Sections.Count, glyphSoftHyphens, rawSectionSoftHyphens = rawSoftHyphens,
        rawExplicitSoftHyphenWraps = rawWraps, changedSections = changes.Count, oldCanonicalSha256 = HashText(oldAll.ToString()),
        newCanonicalSha256 = HashText(newAll.ToString()), changes, extraction.Errors });
    Console.WriteLine($"{document.Id}: {rawWraps} explicit wraps; {changes.Count} changed sections.");
}
var report = new { version = 1, diagnostic = "native_pdf_display_discretionary_hyphen_prepass",
    manifest.Dataset, manifest.Revision, manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
    extractionBuildIdentity = typeof(DocumentExtractionRegistry).Assembly.ManifestModule.ModuleVersionId,
    normalizationBuildIdentity = typeof(TextNormalization).Assembly.ManifestModule.ModuleVersionId,
    modelInference = false, ocr = false,
    comparison = "Exact layout-v3 display cleanup versus the current discretionary-hyphen single-line prepass applied to the same current raw native sections. This is a normalization diagnostic, not a complete replay of old extraction or a quality score.",
    fullOriginalPdfHashesVerified = true, largeSourceExcerptsIncluded = false, documents };
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, options) + "\n");

string LegacyDisplay(string value, bool preserveTableWhitespace)
{
    if (string.IsNullOrWhiteSpace(value)) return "";
    var builder = new StringBuilder(value.Length);
    foreach (var rune in value.EnumerateRunes())
    {
        if (rune.Value == 0x00AD) continue;
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.Control or UnicodeCategory.Format && rune.Value is not ('\n' or '\r' or '\t')) continue;
        builder.Append(rune.ToString());
    }
    var cleaned = builder.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
    return preserveTableWhitespace ? cleaned : trailingLineWhitespace.Replace(cleaned, "\n").Trim();
}
static string HashText(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
sealed class NativeOnly : IOcrEngine
{
    public bool IsAvailable => false;
    public string UnavailableReason => "Native-only normalization audit.";
    public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken token) => Task.FromResult(new OcrResult("", null));
    public Task<OcrResult> RecognizeAsync(Func<CancellationToken, Task<OcrRequest>> prepareRequest, CancellationToken token) => Task.FromResult(new OcrResult("", null));
}
