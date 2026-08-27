#:property TargetFramework=net10.0
#:project ../src/Core/MCPIndexSearch.Core.csproj
#:project ../src/Documents/MCPIndexSearch.Documents.csproj

using System.Text;
using MCPIndexSearch.Core;
using MCPIndexSearch.Documents;

var data = Environment.GetEnvironmentVariable("MCPINDEXSEARCH_DATA_DIR")
    ?? throw new InvalidOperationException("Set MCPINDEXSEARCH_DATA_DIR to an isolated smoke directory.");
Directory.CreateDirectory(data);
var extractor = new DocumentExtractionRegistry(new UnexpectedOcrEngine());

var disguisedHtml = Path.Combine(data, "pixel.png");
await File.WriteAllTextAsync(disguisedHtml,
    "\n<html><head><title>Archived budget page</title></head><body>Recover text by content signature.</body></html>");
var htmlResult = await extractor.ExtractAsync(new ExtractionRequest(disguisedHtml), CancellationToken.None);
if (htmlResult.Errors.Count != 0 || htmlResult.Root.Sections.SingleOrDefault()?.Text.Contains("Recover text", StringComparison.Ordinal) != true)
    throw new InvalidOperationException("HTML content carrying an image extension was not recovered by signature.");

var invalidImage = Path.Combine(data, "broken.png");
await File.WriteAllBytesAsync(invalidImage, [0x89, 0x50, 0x4e, 0x47, 0x00, 0x01]);
var imageResult = await extractor.ExtractAsync(new ExtractionRequest(invalidImage), CancellationToken.None);
if (imageResult.Errors.Count != 1 || imageResult.Errors[0].Code != "malformed_document")
    throw new InvalidOperationException("Malformed image data did not produce a stable malformed_document error.");

var email = Path.Combine(data, "legacy-attachments.eml");
await File.WriteAllTextAsync(email, BuildEmail(), Encoding.ASCII);
var emailResult = await extractor.ExtractAsync(new ExtractionRequest(email), CancellationToken.None);
if (emailResult.Errors.Count != 0 || emailResult.Root.Attachments.Count != 3 ||
    emailResult.Root.Attachments.Any(item => item.Status != "unsupported_format"))
    throw new InvalidOperationException("Unsupported embedded items were not retained without failing their parent email.");

Console.WriteLine("EXTRACTION_ROBUSTNESS_SMOKE_OK disguised_html=indexed invalid_image=malformed embedded_unsupported=retained");

static string BuildEmail() =>
    "From: sender@example.test\r\n" +
    "To: recipient@example.test\r\n" +
    "Subject: Legacy attachments\r\n" +
    "MIME-Version: 1.0\r\n" +
    "Content-Type: multipart/mixed; boundary=fixture\r\n\r\n" +
    "--fixture\r\nContent-Type: text/plain\r\n\r\nThe parent message remains searchable.\r\n" +
    Attachment("image/x-emf", "image1.emf") +
    Attachment("image/x-wmf", "image2.wmf") +
    Attachment("application/vnd.ms-excel", "legacy.xls") +
    "--fixture--\r\n";

static string Attachment(string contentType, string name) =>
    $"--fixture\r\nContent-Type: {contentType}; name=\"{name}\"\r\n" +
    $"Content-Disposition: attachment; filename=\"{name}\"\r\n" +
    "Content-Transfer-Encoding: base64\r\n\r\nAQIDBA==\r\n";

sealed class UnexpectedOcrEngine : IOcrEngine
{
    public bool IsAvailable => false;
    public string UnavailableReason => "OCR must not be reached by this smoke.";
    public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException(UnavailableReason));
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
        Task.FromException<OcrResult>(new InvalidOperationException(UnavailableReason));
}
