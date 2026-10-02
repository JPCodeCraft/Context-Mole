using ContextMole.Core;

namespace ContextMole.Indexing;

/// <summary>Recognizes separated layout markers without removing canonical or lexical evidence.</summary>
public static class SemanticEvidenceEligibility
{
    public static IReadOnlySet<int> FindSeparatedListMarkers(IReadOnlyList<ExtractedSection> sections,
        string? documentTitle = null)
    {
        var excluded = new HashSet<int>();
        var title = TextNormalization.ForSearch(documentTitle);
        var located = sections.Select((section, ordinal) => (Section: section, Ordinal: ordinal))
            .Where(item => IsLocatedBlock(item.Section))
            .GroupBy(item => (item.Section.Location.Kind, item.Section.Location.Page,
                item.Section.Location.ImageFrame, item.Section.SectionKey));
        foreach (var group in located)
        {
            var context = group.Where(item => !item.Section.IsBoilerplate &&
                SemanticTextPreparation.CleanBody(item.Section.Text).Length > 0).ToArray();
            var peers = context.Where(item => IsReliableBlock(item.Section) && HasReliableText(item.Section) &&
                item.Section.Text.Count(char.IsLetter) >= 2).ToArray();
            foreach (var (section, ordinal) in group)
            {
                var text = TextNormalization.ForSearch(section.Text);
                var marker = section.Location.Region!;
                // A heading occurrence is useful evidence, including a numeric chapter label.
                if (!IsListMarker(text) || !IsReliableBlock(section) || !HasReliableText(section) || section.IsBoilerplate ||
                    marker.Width > marker.Height * 5 ||
                    string.Equals(text, title, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, TextNormalization.ForSearch(section.Heading), StringComparison.OrdinalIgnoreCase))
                    continue;
                // Infix arithmetic, decimal literals and identifiers can resemble list labels.
                // A nearby left operand/context is evidence against interpreting a list prefix.
                if (context.Any(peer => peer.Ordinal != ordinal && IsLeftHandContext(marker, peer.Section.Location.Region!)))
                    continue;
                if (peers.Any(peer => peer.Ordinal != ordinal &&
                    peer.Section.Method == section.Method && IsRightHandItem(marker, peer.Section.Location.Region!)))
                    excluded.Add(ordinal);
            }
        }
        return excluded;
    }

    private static bool IsReliableBlock(ExtractedSection section) =>
        IsLocatedBlock(section) && section.Location.LayoutWarning is null;

    private static bool IsLocatedBlock(ExtractedSection section) =>
        (section.Location.Kind == LocationKind.Page && section.Location.Page is > 0 ||
         section.Location.Kind == LocationKind.ImageFrame && section.Location.ImageFrame is > 0) &&
        section.Method is ExtractionMethod.NativeText or ExtractionMethod.Ocr &&
        section.Location.Region is { X: >= 0, Y: >= 0, Width: > 0, Height: > 0 } region &&
        region.X + region.Width <= 1 && region.Y + region.Height <= 1 &&
        section.Location.StructurePath is { } path &&
        (path.StartsWith("block[", StringComparison.Ordinal) || path.Contains("/block[", StringComparison.Ordinal)) &&
        !path.Contains("table[", StringComparison.OrdinalIgnoreCase);

    private static bool IsListMarker(string text) =>
        // Numeric/letter labels can be values, member-access prefixes or parenthesized factors.
        // Middle dot is a multiplication operator. Keep every such ambiguous shape eligible.
        text is "•" or "◦" or "▪" or "‣" or "⁃";

    private static bool HasReliableText(ExtractedSection section) =>
        section.Method != ExtractionMethod.Ocr || section.OcrConfidence is >= 50 and <= 100;

    private static bool IsRightHandItem(SourceRegion marker, SourceRegion body)
    {
        var gap = body.X - (marker.X + marker.Width);
        var overlap = Math.Min(marker.Y + marker.Height, body.Y + body.Height) - Math.Max(marker.Y, body.Y);
        // Displayed-page geometry keeps another column or a nearby paragraph from lending its
        // meaning to the marker. A body block can contain several list items on different lines.
        return gap is >= -0.001 and <= 0.05 && overlap >= marker.Height * 0.5;
    }

    private static bool IsLeftHandContext(SourceRegion marker, SourceRegion context)
    {
        var gap = marker.X - (context.X + context.Width);
        var overlap = Math.Min(marker.Y + marker.Height, context.Y + context.Height) - Math.Max(marker.Y, context.Y);
        return context.X < marker.X && gap <= 0.05 && overlap >= marker.Height * 0.5;
    }
}
