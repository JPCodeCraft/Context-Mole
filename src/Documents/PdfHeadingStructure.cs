using System.Text.RegularExpressions;

using ContextMole.Core;

using UglyToad.PdfPig.Content;

namespace ContextMole.Documents;

/// <summary>
/// PDF heading, caption and list-marker structure. A PDF has no markup, so headings are
/// recognized from type size and weight across the whole document, numbering such as
/// "3.1" gives nesting depth, and markers that layout analysis cut off their line
/// ("2.1", "•") are rejoined with their text.
/// </summary>
public sealed partial class DocumentExtractionRegistry
{
    /// <param name="TypicalSize">Body type size: the length-weighted median over native text.</param>
    /// <param name="HeadingSizes">Distinct larger heading sizes, largest first.</param>
    /// <param name="BoldHeadings">False when body text itself is mostly bold.</param>
    private sealed record PdfHeadingProfile(double TypicalSize, IReadOnlyList<double> HeadingSizes, bool BoldHeadings);

    private const double HeadingSizeRatio = 1.2;

    private static PdfHeadingProfile PdfHeadings(IEnumerable<IReadOnlyList<PdfTextBlock>> pages)
    {
        var blocks = pages.SelectMany(page => page)
            .Where(block => !block.IsTable && block.FontSize > 0 && block.Method == ExtractionMethod.NativeText)
            .ToArray();
        // A page with one title and one long body block uses the body font as its baseline.
        var sizes = blocks.GroupBy(block => block.FontSize).OrderBy(group => group.Key)
            .Select(group => (Size: group.Key, Weight: group.Sum(block => (long)Math.Max(1, block.Text.Length)))).ToArray();
        var midpoint = sizes.Sum(item => item.Weight) / 2;
        long cumulative = 0;
        var typical = 0d;
        foreach (var item in sizes)
        {
            cumulative += item.Weight;
            if (cumulative > midpoint) { typical = item.Size; break; }
        }
        if (typical <= 0) return new PdfHeadingProfile(0, [], false);
        var headingSizes = blocks.Where(block => block.FontSize >= typical * HeadingSizeRatio && HasHeadingShape(block.Text))
            .Select(block => RoundedSize(block.FontSize)).Distinct().OrderDescending().ToArray();
        var body = blocks.Where(block => Math.Abs(block.FontSize - typical) <= typical * 0.05).ToArray();
        var bodyLength = body.Sum(block => (double)block.Text.Length);
        var boldShare = bodyLength == 0 ? 1 : body.Sum(block => block.BoldFraction * block.Text.Length) / bodyLength;
        return new PdfHeadingProfile(typical, headingSizes, boldShare < 0.5);
    }

    private static void AddPdfSections(List<ExtractedSection> sections, IReadOnlyList<PdfTextBlock> blocks,
        int pageNumber, HeadingContext heading, PdfHeadingProfile profile)
    {
        var ordered = GroupOcrParagraphs(blocks).Where(block => !string.IsNullOrWhiteSpace(block.Text)).ToArray();
        var ordinal = 0;
        (PdfTextBlock Block, int Section)? lastHeading = null;
        for (var index = 0; index < ordered.Length; index++)
        {
            var block = ordered[index];
            var next = index + 1 < ordered.Length ? ordered[index + 1] : null;
            var path = block.IsTable ? $"page[{pageNumber}]/table[{++ordinal}]" : $"page[{pageNumber}]/block[{++ordinal}]";
            // A caption directly above a table describes it; search uses it as that table's context.
            if (!block.IsTable && next is { IsTable: true } && TableCaption().IsMatch(block.Text.TrimStart()))
                path = $"page[{pageNumber}]/table[{ordinal + 1}]/caption";
            if (HeadingLevel(block, next, profile) is { } level)
            {
                var text = Whitespace().Replace(block.Text, " ").Trim();
                if (lastHeading is { } previous && ContinuesHeading(previous.Block, block))
                {
                    // A wrapped title split into two layout blocks is one heading, not two siblings.
                    heading.Extend(text);
                    sections[previous.Section] = sections[previous.Section] with
                    {
                        Heading = heading.Heading,
                        HeadingPath = heading.Path
                    };
                }
                else heading.Start(level, text, "pdf-heading");
                lastHeading = (block, sections.Count);
            }
            else lastHeading = null;
            sections.Add(new ExtractedSection(block.Text,
                new SourceLocation(LocationKind.Page, Page: pageNumber, StructurePath: path, Region: block.Region,
                    LayoutWarning: block.Warning), block.Method, block.Confidence, heading.Heading,
                heading.Key == "document" ? $"page:{pageNumber}" : heading.Key, heading.Path));
        }
    }

    /// <summary>
    /// Larger type ranks headings by size across the document. Bold lines at about body size
    /// count only when numbered ("3.1 Traces") or followed by a paragraph, so bold form labels and table
    /// cells do not fragment the document into one-line sections. Numbering depth nests
    /// "2.1" under "2" when both share a size.
    /// </summary>
    private static int? HeadingLevel(PdfTextBlock block, PdfTextBlock? next, PdfHeadingProfile profile)
    {
        if (block.IsTable || profile.TypicalSize <= 0 || block.Method != ExtractionMethod.NativeText ||
            block.Text.Count(char.IsLetter) < 2) return null;
        var numbering = SectionNumber().Match(block.Text.TrimStart());
        var depth = numbering.Success ? numbering.Groups[1].Value.Split('.').Length : 0;
        if (block.FontSize >= profile.TypicalSize * HeadingSizeRatio && HasHeadingShape(block.Text))
        {
            var rank = profile.HeadingSizes.TakeWhile(size => size > RoundedSize(block.FontSize)).Count() + 1;
            return rank * 8 + depth;
        }
        var text = block.Text.Trim();
        // Numbered subheadings are often set a point below body text ("2.1.1" in 11pt over 12pt).
        if (!profile.BoldHeadings || block.BoldFraction < 0.9 ||
            block.FontSize < profile.TypicalSize * (depth > 0 ? 0.85 : 0.95) ||
            text.Contains('\n') || text.Length > 120 || text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 12 ||
            text[^1] is '.' or ',' or ';' or ':')
            return null;
        var paragraphFollows = next is { IsTable: false, Region: { } below } && block.Region is { } region &&
            below.Y >= region.Y + region.Height * 0.5 && next.BoldFraction < 0.5 &&
            next.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 8;
        if (depth == 0 && !paragraphFollows) return null;
        return (profile.HeadingSizes.Count + 1) * 8 + Math.Max(1, depth);
    }

    private static bool HasHeadingShape(string text) =>
        text.Length <= 180 && text.Count(character => character == '\n') <= 2 &&
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 18;

    private static double RoundedSize(double size) => Math.Round(size * 2) / 2;

    private static bool ContinuesHeading(PdfTextBlock previous, PdfTextBlock block)
    {
        if (previous.Region is not { } above || block.Region is not { } region) return false;
        var lineHeight = above.Height / (previous.Text.Count(character => character == '\n') + 1);
        var gap = region.Y - (above.Y + above.Height);
        var overlap = Math.Min(above.X + above.Width, region.X + region.Width) - Math.Max(above.X, region.X);
        return Math.Abs(previous.FontSize - block.FontSize) <= Math.Max(0.5, previous.FontSize * 0.05) &&
               Math.Abs(previous.BoldFraction - block.BoldFraction) < 0.5 &&
               gap >= -lineHeight * 0.25 && gap <= lineHeight * 1.2 && overlap > 0 &&
               !SectionNumber().IsMatch(block.Text.TrimStart());
    }

    /// <summary>
    /// Layout analysis cuts a section number or bullet off its line when a wide gap follows
    /// it. Rejoin a one-line marker with the block that starts on the same baseline to its right.
    /// </summary>
    private static IReadOnlyList<PdfTextBlock> JoinLineMarkers(IReadOnlyList<PdfTextBlock> blocks)
    {
        var result = new List<PdfTextBlock>(blocks.Count);
        for (var index = 0; index < blocks.Count; index++)
        {
            var marker = blocks[index];
            if (index + 1 < blocks.Count && blocks[index + 1] is { IsTable: false, Region: { } body } next &&
                marker is { IsTable: false, Region: { } region } && next.Method == marker.Method &&
                !marker.Text.Contains('\n') && LineMarker().IsMatch(marker.Text.Trim()) &&
                SharesFirstLine(region, body, next.Text))
            {
                var warnings = new[] { marker.Warning, next.Warning }.Where(value => value is not null).Distinct().ToArray();
                result.Add(next with
                {
                    Text = $"{marker.Text.Trim()} {next.Text}",
                    Region = UnionRegions([region, body]),
                    Warning = warnings.Length == 0 ? null : string.Join(' ', warnings)
                });
                index++;
                continue;
            }
            result.Add(marker);
        }
        return result;

        static bool SharesFirstLine(SourceRegion marker, SourceRegion body, string bodyText)
        {
            var lineHeight = body.Height / (bodyText.Count(character => character == '\n') + 1);
            var center = marker.Y + marker.Height / 2;
            var gap = body.X - (marker.X + marker.Width);
            return gap >= -0.005 && gap <= 0.08 &&
                   center >= body.Y - lineHeight * 0.5 && center <= body.Y + lineHeight;
        }
    }

    private static bool IsBoldLetter(Letter letter)
    {
        if (letter.FontDetails is { } font && (font.IsBold || font.Weight >= 600)) return true;
        var name = letter.FontName;
        return name is not null && BoldFontName().IsMatch(name);
    }

    [GeneratedRegex(@"^(\d{1,3}(?:\.\d{1,3}){0,5})\.?(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SectionNumber();

    // "2.1", "3.", "A.", "IV.", "(a)", "ii)", or a bullet glyph.
    [GeneratedRegex(@"^(?:\d{1,3}(?:\.\d{1,3}){0,5}\.?|[A-Za-z][.)]|\(?[a-z0-9]{1,4}\)|[IVXLC]{1,6}\.|[•◦▪‣⁃●○■□►▸–\-*])$",
        RegexOptions.CultureInvariant)]
    private static partial Regex LineMarker();

    [GeneratedRegex(@"bold|black|heavy|semibold|demi", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoldFontName();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
