using System.Text.Json.Serialization;
using ContextMole.Core;

namespace ContextMole.Broker.Protocol;

public sealed record SearchWireSource(string Path, string FileName, string ContentName,
    IReadOnlyList<string> AttachmentChain);
public sealed record SearchWirePreview(Guid PassageId, string Excerpt, int ExcerptStart, int ExcerptLength,
    bool Truncated, SourceLocation Location, Guid? SectionId, IReadOnlyList<string> MatchedClauseIds,
    IReadOnlyList<SearchField> MatchedFields, IReadOnlyList<SearchMatchSpan> MatchSpans,
    IReadOnlyList<SearchFieldMatch> FieldMatches, double? SemanticSimilarity, bool? BelowSimilarityThreshold)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SearchPreviewDetails? Details { get; init; }
}
public sealed record SearchPreviewDetails(double FusedScore, double? KeywordScore, int? KeywordRank,
    int? SemanticRank, ExtractionMethod ExtractionMethod, double? OcrConfidence, DateTimeOffset ModifiedUtc);
public sealed record SearchWireGroup(Guid DocumentId, Guid ContentId, Guid? SectionId, string Title,
    SearchWireSource Source, IReadOnlyList<Guid> EvidencePassageIds, IReadOnlyList<SearchClauseEvidence> ClauseEvidence,
    double? SemanticSimilarity, Guid? SemanticAnchorPassageId, bool? BelowSimilarityThreshold,
    IReadOnlyList<SearchWirePreview> Previews)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SearchGroupDetails? Details { get; init; }
}
public sealed record SearchGroupDetails(double FusedScore, int TotalMatchCount, int CollapsedMatchCount,
    string RootExtension, string? ContentExtension, string? ContentMimeType);
public sealed record SearchWireResponse(SearchMode RequestedMode, string ActualMode, SearchScope MatchScope,
    IReadOnlyList<SearchWarning> Warnings, long SearchGeneration, int CandidateLimit,
    SearchBranchCandidateDepths InspectedCandidateDepths, int CandidateMatchCount, bool CandidateLimitReached,
    SearchBranchDiagnosticsMap Branches, string StopReason, int ReturnedGroupCount, bool HasMore, string? NextCursor, IReadOnlyList<SearchWireGroup> Results)
{
    public static SearchWireResponse FromDomain(SearchResponse response, SearchDetail detail) => new(
        response.RequestedMode, response.ActualMode, response.MatchScope, response.Warnings, response.SearchGeneration,
        response.CandidateLimit, response.InspectedCandidateDepths, response.CandidateMatchCount,
        response.CandidateLimitReached, response.Branches, response.StopReason, response.ReturnedGroupCount, response.HasMore,
        response.NextCursor, response.Results.Select(group => new SearchWireGroup(group.DocumentId, group.ContentId,
            group.SectionId, group.Title ?? group.ContentName, new SearchWireSource(group.SourcePath, group.FileName,
                group.ContentName, group.AttachmentChain),
            detail == SearchDetail.Full ? group.EvidencePassageIds : group.CompactEvidencePassageIds,
            detail == SearchDetail.Full ? group.ClauseEvidence : group.CompactClauseEvidence,
            group.SemanticSimilarity, group.SemanticAnchorPassageId, group.BelowSimilarityThreshold,
            group.Previews.Select(preview => new SearchWirePreview(preview.PassageId, preview.Excerpt, preview.ExcerptStart,
                preview.ExcerptLength, preview.Truncated, preview.Location, preview.SectionId, preview.MatchedClauseIds,
                preview.MatchedFields, preview.MatchSpans,
                detail == SearchDetail.Full ? preview.FieldMatches : preview.FieldMatches.Take(4).ToArray(),
                preview.SemanticSimilarity, preview.BelowSimilarityThreshold)
            {
                Details = detail == SearchDetail.Full ? new SearchPreviewDetails(preview.FusedScore,
                    preview.KeywordScore, preview.KeywordRank, preview.SemanticRank, preview.ExtractionMethod,
                    preview.OcrConfidence, preview.ModifiedUtc) : null
            }).ToArray())
        {
            Details = detail == SearchDetail.Full ? new SearchGroupDetails(group.Score, group.TotalMatchCount,
                group.CollapsedMatchCount, group.RootExtension, group.ContentExtension, group.ContentMimeType) : null
        }).ToArray());
}

public sealed record ReadPassagesResponse(long SearchGeneration, IReadOnlyList<PassageInfo> Passages);
