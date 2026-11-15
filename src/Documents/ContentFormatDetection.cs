using System.Text;

using ContextMole.Core;

namespace ContextMole.Documents;

/// <summary>
/// Chooses how stored bytes are parsed. Extraction and materialization must make the same choice: content that
/// was indexed as a container (for example a PDF saved with a misleading name) has to be re-opened the same way
/// to locate its indexed attachments again.
/// </summary>
internal static class ContentFormatDetection
{
    private const int HtmlSniffBytes = 512;

    public static ContentFormatDescriptor? Resolve(ReadOnlySpan<byte> bytes, string name, string? mimeType)
    {
        if (LooksLikeHtml(bytes))
            return SupportedContent.FindByExtension(".html");
        if (bytes.StartsWith("%PDF-"u8))
            return SupportedContent.FindByExtension(".pdf");
        return SupportedContent.Resolve(name, mimeType);
    }

    private static bool LooksLikeHtml(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return false;
        var prefix = Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, HtmlSniffBytes)])
            .TrimStart('﻿', '\0', ' ', '\t', '\r', '\n');
        return prefix.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith("<head", StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith("<body", StringComparison.OrdinalIgnoreCase);
    }
}
