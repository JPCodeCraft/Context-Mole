using System.Text;

using ContextMole.Core;
using ContextMole.Documents;

namespace ContextMole.Tests;

public sealed class ContentFormatDetectionTests
{
    [Theory]
    [InlineData("%PDF-1.7\n...", "scan.bin", null, ContentFormatKind.Pdf)]
    [InlineData("%PDF-1.4", "attachment.txt", "text/plain", ContentFormatKind.Pdf)]
    [InlineData("﻿  <!DOCTYPE html><p>x</p>", "page.txt", null, ContentFormatKind.Html)]
    [InlineData("<html><body>x</body></html>", "export.dat", "application/octet-stream", ContentFormatKind.Html)]
    [InlineData("plain words", "notes.txt", null, ContentFormatKind.PlainText)]
    public void ContentIsRecognizedFromItsBytesBeforeItsName(string content, string name, string? mimeType,
        ContentFormatKind expected) =>
        Assert.Equal(expected, ContentFormatDetection.Resolve(Encoding.UTF8.GetBytes(content), name, mimeType)?.Kind);

    [Fact]
    public void EmptyContentFallsBackToNameAndMimeType() =>
        Assert.Equal(SupportedContent.Resolve("report.pdf", null)?.Kind,
            ContentFormatDetection.Resolve([], "report.pdf", null)?.Kind);
}
