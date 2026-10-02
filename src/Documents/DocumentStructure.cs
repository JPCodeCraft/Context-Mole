using System.Text;

using AngleSharp.Dom;
using AngleSharp.Html.Parser;

using ContextMole.Core;

namespace ContextMole.Documents;

public sealed partial class DocumentExtractionRegistry
{
    private sealed class HeadingContext
    {
        private readonly List<(int Level, string Text)> _path = [];
        private int _ordinal;
        public string Key { get; private set; } = "document";
        public string? Heading => _path.Count == 0 ? null : _path[^1].Text;
        public string[] Path => _path.Select(item => item.Text).ToArray();

        public void Start(int level, string text, string prefix = "heading")
        {
            while (_path.Count > 0 && _path[^1].Level >= level) _path.RemoveAt(_path.Count - 1);
            _path.Add((level, TextNormalization.ForDisplay(text)));
            Key = $"{prefix}:{++_ordinal}";
        }
    }

    private static IReadOnlyList<ExtractedSection> HtmlSections(string html, ExtractionMethod method,
        SourceLocation? baseLocation = null)
    {
        var document = new HtmlParser().ParseDocument(html);
        foreach (var element in document.QuerySelectorAll("script,style,template,noscript,iframe,object,embed,svg,canvas"))
            element.Remove();
        var sections = new List<ExtractedSection>();
        var heading = new HeadingContext();
        var ordinal = 0;
        var tableOrdinal = 0;
        Walk(document.Body ?? document.DocumentElement, false);
        return sections;

        void Emit(string text, bool boilerplate, string? path = null, string? warning = null, bool table = false)
        {
            // Individual cells are already normalized. Trimming the complete TSV would erase
            // leading/trailing empty cells and move values into the wrong columns.
            if (!table) text = TextNormalization.ForDisplay(text);
            if (string.IsNullOrWhiteSpace(text)) return;
            var location = baseLocation ?? new SourceLocation(LocationKind.Structure);
            location = location with { StructurePath = path ?? $"html/block[{++ordinal}]", LayoutWarning = warning };
            sections.Add(new ExtractedSection(text, location, method, Heading: heading.Heading,
                SectionKey: heading.Key, HeadingPath: heading.Path, IsBoilerplate: boilerplate));
        }

        void Walk(INode container, bool boilerplate)
        {
            var pending = new StringBuilder();
            foreach (var child in container.ChildNodes)
            {
                if (child is IText text)
                {
                    pending.Append(text.Data);
                    continue;
                }
                if (child is not IElement element) continue;
                var tag = element.LocalName;
                if (tag == "br") { pending.Append('\n'); continue; }
                if (tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                {
                    Flush();
                    var title = TextNormalization.ForDisplay(element.TextContent);
                    if (title.Length == 0) continue;
                    heading.Start(tag[1] - '0', title);
                    Emit(title, boilerplate);
                }
                else if (tag == "table")
                {
                    Flush();
                    var path = $"html/table[{++tableOrdinal}]";
                    foreach (var caption in element.Children.Where(child => child.LocalName == "caption"))
                        Emit(InlineText(caption), boilerplate, path + "/caption");
                    var rows = element.QuerySelectorAll("tr")
                        .Where(row => ReferenceEquals(row.Closest("table"), element)).ToArray();
                    var ambiguous = rows.SelectMany(row => row.Children)
                        .Any(cell => cell.GetAttribute("colspan") is { } span && span != "1" ||
                                     cell.GetAttribute("rowspan") is { } rowSpan && rowSpan != "1");
                    var table = string.Join('\n', rows.Select(row => string.Join('\t', row.Children
                        .Where(cell => cell.LocalName is "td" or "th")
                        .Select(cell => TextNormalization.ForSearch(InlineText(cell))))));
                    Emit(table, boilerplate, path,
                        ambiguous ? "table_spans: merged cells retained; cell alignment is uncertain." : null, table: true);
                }
                else if (tag is "p" or "div" or "section" or "article" or "main" or "aside" or "nav" or
                         "header" or "footer" or "blockquote" or "ul" or "ol" or "li" or "pre" or "figure" or
                         "figcaption" or "dl" or "dt" or "dd" or "hr")
                {
                    Flush();
                    Walk(element, boilerplate || tag is "nav" or "header" or "footer");
                }
                else if (tag == "img")
                    pending.Append(element.GetAttribute("alt"));
                else
                    AppendInline(element, pending);
            }
            Flush();

            void Flush()
            {
                Emit(pending.ToString(), boilerplate);
                pending.Clear();
            }
        }
    }

    private static string InlineText(INode node)
    {
        var text = new StringBuilder();
        AppendInline(node, text);
        return text.ToString();
    }

    private static void AppendInline(INode node, StringBuilder text)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText value) text.Append(value.Data);
            else if (child is IElement element)
            {
                if (element.LocalName == "br") text.Append('\n');
                else if (element.LocalName == "img") text.Append(element.GetAttribute("alt"));
                else
                {
                    // Inline wrappers can contain blocks (including table cells and custom
                    // elements). TextContent alone silently turns <p>one</p><p>two</p> into onetwo.
                    var boundary = element.LocalName is "p" or "div" or "section" or "article" or "li" or
                        "ul" or "ol" or "blockquote" or "pre" or "tr" or "td" or "th" or "caption" or
                        "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "dt" or "dd" or "figcaption";
                    if (boundary) text.Append('\n');
                    AppendInline(element, text);
                    if (boundary) text.Append('\n');
                }
            }
        }
    }
}
