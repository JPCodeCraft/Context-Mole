using System.ComponentModel;

using ContextMole.Core;

namespace ContextMole.Mcp;

public sealed record McpSearchClause(
    [property: Description("Stable caller-defined ID echoed in matched_clause_ids (1-64 ASCII letters, numbers, dot, underscore, or hyphen).")] string Id,
    [property: Description("Text to match: one token for term/prefix, or one-or-more tokens for phrase.")] string Text,
    [property: Description("Boolean role: must, should, or must_not.")] SearchClauseOccur Occur = SearchClauseOccur.Should,
    [property: Description("Match behavior: exact term, ordered phrase, or token prefix.")] SearchMatchKind Match = SearchMatchKind.Term,
    [property: Description("Optional target fields; omission searches body, title, heading, filename, path, content_name, sheet, and email_subject.")] IReadOnlyList<SearchField>? Fields = null)
{
    public SearchClause ToDomain() => new(Id, Text, Occur, Match, Fields);
}

public sealed record McpSearchFieldWeights(
    [property: Description("Body text weight, default 1.0.")] double Body = 1.0,
    [property: Description("Document title weight, default 3.0.")] double Title = 3.0,
    [property: Description("Section heading weight, default 2.0.")] double Heading = 2.0,
    [property: Description("Root filename weight, default 2.5.")] double Filename = 2.5,
    [property: Description("Authorized source path weight, default 0.5.")] double Path = 0.5,
    [property: Description("Root/attachment/archive-entry name weight, default 2.5.")] double ContentName = 2.5,
    [property: Description("Worksheet name weight, default 1.5.")] double Sheet = 1.5,
    [property: Description("Email subject weight, default 3.0.")] double EmailSubject = 3.0)
{
    public SearchFieldWeights ToDomain() => new(Body, Title, Heading, Filename, Path, ContentName, Sheet, EmailSubject);
}

public sealed record McpSearchBranchWeights(
    [property: Description("Keyword reciprocal-rank-fusion weight, default 1.0.")] double Keyword = 1.0,
    [property: Description("Semantic reciprocal-rank-fusion weight, default 1.0.")] double Semantic = 1.0)
{
    public SearchBranchWeights ToDomain() => new(Keyword, Semantic);
}

public sealed record McpSearchFilters(
    [property: Description("Optional stable root document IDs.")] IReadOnlyList<Guid>? DocumentIds = null,
    [property: Description("Optional stable content IDs returned by search/list_attachments; use for focused follow-ups.")] IReadOnlyList<Guid>? ContentIds = null,
    [property: Description("Optional authorized source-directory prefixes.")] IReadOnlyList<string>? PathPrefixes = null,
    [property: Description("Optional root source extensions such as .msg or pdf.")] IReadOnlyList<string>? RootExtensions = null,
    [property: Description("Optional nested/root content-name extensions; e.g. pdf finds a PDF attachment inside an email or archive.")] IReadOnlyList<string>? ContentExtensions = null,
    [property: Description("Optional inclusive source modified-time lower bound.")] DateTimeOffset? ModifiedFromUtc = null,
    [property: Description("Optional inclusive source modified-time upper bound.")] DateTimeOffset? ModifiedToUtc = null,
    [property: Description("any, root_only, or attachments_only.")] AttachmentScope AttachmentScope = AttachmentScope.Any)
{
    public SearchFilters ToDomain() => new(DocumentIds, ContentIds, PathPrefixes, RootExtensions, ContentExtensions,
        ModifiedFromUtc, ModifiedToUtc, AttachmentScope);
}

public sealed record McpSearchResultOptions(
    [property: Description("Maximum content groups returned, 1-50; default 10.")] int GroupLimit = 10,
    [property: Description("Maximum consolidated passage previews per content group, 1-10; default 1.")] int PreviewsPerGroup = 1,
    [property: Description("Diversity cap per root document, 1-50; default 2.")] int MaxGroupsPerDocument = 2,
    [property: Description("Cosine score below which any preview with a semantic score is marked low_confidence; default 0.25, allowed -1 to 1.")] double SemanticConfidenceThreshold = 0.25,
    [property: Description("False by default so borderline semantic leads remain visible. True hides semantic-only matches below the threshold.")] bool StrictSemanticThreshold = false)
{
    public SearchResultOptions ToDomain() => new(GroupLimit, PreviewsPerGroup, MaxGroupsPerDocument,
        SemanticConfidenceThreshold, StrictSemanticThreshold);
}

public sealed record ToolError(string Code, string Message, bool Retryable);
public sealed record ErrorEnvelope(ToolError Error);
