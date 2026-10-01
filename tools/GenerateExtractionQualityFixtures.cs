#:property TargetFramework=net10.0
#:package PdfPig@0.1.16
#:package SkiaSharp@4.150.1

using System.Text;

using SkiaSharp;

using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

// Synthetic, repository-owned content. Regenerate deliberately and update manifest hashes.
var directory = Path.GetFullPath(args.FirstOrDefault() ?? "benchmarks/extraction/quality");
Directory.CreateDirectory(directory);
var builder = new PdfDocumentBuilder();
var font = builder.AddStandard14Font(Standard14Font.Helvetica);
var first = builder.AddPage(600, 700);
AddMargins(first, 1);
first.AddText("Contract terms", 22, new PdfPoint(40, 620), font);
var left = new[] { "Left first: notice must be delivered.", "Left second: retain signed evidence.", "Left third: supplier obligations remain.", "Left final: return confidential records." };
var right = new[] { "Right first: delivery follows approval.", "Right second: confirm final handover.", "Right third: archive the signed receipt.", "Right final: close the service agreement." };
for (var row = 0; row < left.Length; row++)
{
    first.AddText(right[row], 10, new PdfPoint(330, 560 - row * 20), font);
    first.AddText(left[row], 10, new PdfPoint(40, 560 - row * 20), font);
}
var second = builder.AddPage(600, 700);
AddMargins(second, 2);
second.AddText("Invoice amounts", 22, new PdfPoint(40, 620), font);
var table = new[] { new[] { "Vendor", "Amount", "Status" }, new[] { "Ada", "42", "Paid" }, new[] { "Grace", "93", "Open" }, new[] { "Linus", "17", "Paid" } };
for (var row = 0; row < table.Length; row++)
    for (var column = 0; column < table[row].Length; column++)
        second.AddText(table[row][column], 12, new PdfPoint(40 + column * 170, 560 - row * 22), font);
second.AddText("The finance team verifies every invoice amount and preserves the original supplier payment evidence.", 10, new PdfPoint(40, 400), font);
var third = builder.AddPage(600, 700);
AddMargins(third, 3);
third.AddText("Portuguese records", 22, new PdfPoint(40, 620), font);
third.AddText("Approval records retain every signed contract and notice period for this supplier.", 12, new PdfPoint(40, 560), font);
third.AddText("The supplier keeps all delivery records and the final signed documentation.", 12, new PdfPoint(40, 536), font);
third.AddText("The evidence remains readable and searchable after document extraction.", 12, new PdfPoint(40, 512), font);
File.WriteAllBytes(Path.Combine(directory, "layout-and-tables.pdf"), builder.Build());
File.WriteAllBytes(Path.Combine(directory, "portuguese-native.pdf"), WinAnsiPdf([
    "A aprovação em São Paulo exige atenção ao prazo e à caução do contrato.",
    "O fornecedor mantém os registros de entrega e a documentação inter-",
    "nacional assinada. A informação permanece legível após a extração." ]));

const string scannedText = "Aprovação em São Paulo\nO prazo de entrega exige atenção.\nA caução protege o contrato assinado.\nO fornecedor conserva os registros.\nA documentação permanece disponível.";
using var typeface = SKTypeface.FromFile("C:/Windows/Fonts/arial.ttf") ?? throw new InvalidOperationException("Arial is required to regenerate the synthetic scan.");
using var bitmap = new SKBitmap(1600, 1100);
using (var canvas = new SKCanvas(bitmap))
using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
using (var scanFont = new SKFont(typeface, 52))
{
    canvas.Clear(SKColors.White);
    var row = 0;
    foreach (var line in scannedText.Split('\n')) canvas.DrawText(line, 80, 130 + row++ * 150, SKTextAlign.Left, scanFont, paint);
}
using var image = SKImage.FromBitmap(bitmap);
using var png = image.Encode(SKEncodedImageFormat.Png, 100);
File.WriteAllBytes(Path.Combine(directory, "portuguese-scan.png"), png.ToArray());
File.WriteAllText(Path.Combine(directory, "portuguese-scan.txt"), scannedText + "\n", new UTF8Encoding(false));
var mixed = new PdfDocumentBuilder();
var mixedFont = mixed.AddStandard14Font(Standard14Font.Helvetica);
var mixedPage = mixed.AddPage(600, 700);
const string nativeReference = "Native reference: the following scanned approval has important supplier delivery and contract evidence.";
mixedPage.AddText(nativeReference, 9, new PdfPoint(35, 640), mixedFont);
mixedPage.AddPng(png.ToArray(), new PdfRectangle(35, 120, 565, 550));
File.WriteAllBytes(Path.Combine(directory, "mixed-native-scan.pdf"), mixed.Build());
File.WriteAllText(Path.Combine(directory, "mixed-native-scan.txt"), nativeReference + "\n" + scannedText + "\n", new UTF8Encoding(false));
var rotated = new PdfDocumentBuilder();
var rotatedFont = rotated.AddStandard14Font(Standard14Font.Helvetica);
var rotatedPage = rotated.AddPage(600, 700);
rotatedPage.SetRotation(new UglyToad.PdfPig.Content.PageRotationDegrees(90));
rotatedPage.AddText("Rotated page evidence preserves the notice period and final supplier handover obligations for the contract.",
    10, new PdfPoint(40, 560), rotatedFont);
File.WriteAllBytes(Path.Combine(directory, "rotated-native.pdf"), rotated.Build());
Console.WriteLine($"Generated synthetic extraction quality fixtures in {directory}.");

void AddMargins(PdfPageBuilder page, int number)
{
    page.AddText("Context Mole synthetic archive", 9, new PdfPoint(40, 675), font);
    page.AddText($"Page {number}", 9, new PdfPoint(40, 25), font);
}

static byte[] WinAnsiPdf(string[] lines)
{
    var commands = "BT /F1 12 Tf 40 600 Td " + string.Join(" 0 -24 Td ", lines.Select(line =>
        "(" + string.Concat(line.Select(character => character is >= ' ' and <= '~' && character is not ('(' or ')' or '\\')
            ? character.ToString() : "\\" + Convert.ToString(character, 8).PadLeft(3, '0'))) + ") Tj")) + " ET";
    var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Count 1 /Kids [4 0 R] >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 700] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >>",
        $"<< /Length {Encoding.ASCII.GetByteCount(commands)} >>\nstream\n{commands}\nendstream" };
    var document = new StringBuilder("%PDF-1.4\n");
    var offsets = new List<int> { 0 };
    for (var index = 0; index < objects.Length; index++)
    { offsets.Add(document.Length); document.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
    var xref = document.Length;
    document.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
    foreach (var offset in offsets.Skip(1)) document.Append($"{offset:D10} 00000 n \n");
    document.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    return Encoding.ASCII.GetBytes(document.ToString());
}
