using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ContextMole.Broker;
using ContextMole.Broker.Protocol;
using ContextMole.Core;
using ContextMole.Mcp;
using ModelContextProtocol.Server;

namespace ContextMole.Tests;

public sealed class SearchSessionTests
{
    [Fact]
    public void DiversityDefersGroupsAndCursorReplayKeepsFixedPages()
    {
        var clock = new SessionClock();
        var cache = new SearchSessionCache(clock);
        var project = Guid.NewGuid();
        var firstDocument = Guid.NewGuid();
        var otherDocument = Guid.NewGuid();
        var groups = new[] { Group(firstDocument), Group(firstDocument), Group(otherDocument), Group(firstDocument) };
        var request = new SearchRequest(project, ResultOptions: new SearchResultOptions(GroupLimit: 2, MaxGroupsPerDocument: 1));
        var first = cache.Start(request, Response(groups));
        Assert.Equal([groups[0].ContentId, groups[2].ContentId], first.Results.Select(group => group.ContentId));
        var second = cache.Get(project, first.NextCursor!).Response;
        var replay = cache.Get(project, first.NextCursor!).Response;
        Assert.Equal([groups[1].ContentId], second.Results.Select(group => group.ContentId));
        Assert.Equal(second.Results, replay.Results);
        Assert.Equal(second.NextCursor, replay.NextCursor);
        var third = cache.Get(project, second.NextCursor!).Response;
        Assert.Equal(groups[3].ContentId, Assert.Single(third.Results).ContentId);
        Assert.False(third.HasMore);
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public void SessionsExpireAbsolutelyAndRejectProjectMismatch()
    {
        var clock = new SessionClock();
        var cache = new SearchSessionCache(clock);
        var project = Guid.NewGuid();
        var first = cache.Start(new SearchRequest(project, ResultOptions: new SearchResultOptions(GroupLimit: 1)),
            Response([Group(Guid.NewGuid()), Group(Guid.NewGuid())]));
        Assert.Equal("invalid_cursor", Assert.Throws<ContextMoleException>(() => cache.Get(Guid.NewGuid(), first.NextCursor!)).Code);
        clock.Advance(TimeSpan.FromMinutes(9));
        _ = cache.Get(project, first.NextCursor!);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("cursor_expired", Assert.Throws<ContextMoleException>(() => cache.Get(project, first.NextCursor!)).Code);
    }

    [Fact]
    public void MemoryBudgetEvictsLeastRecentlyUsedSessionAndRejectsOversizeRecords()
    {
        var clock = new SessionClock();
        var cache = new SearchSessionCache(clock, 9000);
        var project = Guid.NewGuid();
        var request = new SearchRequest(project, ResultOptions: new SearchResultOptions(GroupLimit: 1));
        var response = Response([Group(Guid.NewGuid()), Group(Guid.NewGuid())]);
        var first = cache.Start(request, response);
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = cache.Start(request, response);
        Assert.Equal("cursor_expired", Assert.Throws<ContextMoleException>(() => cache.Get(project, first.NextCursor!)).Code);
        Assert.NotNull(cache.Get(project, second.NextCursor!));
        Assert.Equal("search_session_too_large", Assert.Throws<ContextMoleException>(() =>
            new SearchSessionCache(clock, 1024).Start(request, response)).Code);
    }

    [Fact]
    public void CompactWireOmitsRawRankingAndFullWireRetainsIt()
    {
        var preview = new SearchResultItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "needle", false,
            "source.txt", "source.txt", ".txt", DateTimeOffset.UnixEpoch, new SourceLocation(LocationKind.Document),
            [], ExtractionMethod.NativeText, null, 0.012, 0.5, null, 1, null, null, ["needle"], [SearchField.Body], [])
            { ExcerptLength = 6 };
        var group = Group(preview.DocumentId) with { Previews = [preview] };
        var compact = JsonSerializer.SerializeToElement(SearchWireResponse.FromDomain(Response([group]), SearchDetail.Compact), BrokerJson.Options);
        var full = JsonSerializer.SerializeToElement(SearchWireResponse.FromDomain(Response([group]), SearchDetail.Full), BrokerJson.Options);
        var compactGroup = compact.GetProperty("results")[0];
        Assert.False(compactGroup.TryGetProperty("details", out _));
        var compactPreview = compactGroup.GetProperty("previews")[0];
        Assert.False(compactPreview.TryGetProperty("keyword_score", out _));
        Assert.False(compactPreview.TryGetProperty("details", out _));
        Assert.Equal(JsonValueKind.Null, compactPreview.GetProperty("below_similarity_threshold").ValueKind);
        Assert.Equal(0.5, full.GetProperty("results")[0].GetProperty("previews")[0].GetProperty("details").GetProperty("keyword_score").GetDouble());
    }

    [Fact]
    public void SessionBudgetIncludesAttachmentChainsClauseIdentifiersAndLocations()
    {
        var clock = new SessionClock();
        var request = new SearchRequest(Guid.NewGuid(), ResultOptions: new SearchResultOptions(GroupLimit: 1));
        var large = new string('x', 10_000);
        var group = Group(Guid.NewGuid()) with { AttachmentChain = [large], MatchedClauseIds = [large],
            ClauseEvidence = [new SearchClauseEvidence(large, [Guid.NewGuid()], [SearchField.Body])] };
        Assert.Equal("search_session_too_large", Assert.Throws<ContextMoleException>(() =>
            new SearchSessionCache(clock, 64_000).Start(request, Response([group, Group(Guid.NewGuid())]))).Code);
        var preview = new SearchResultItem(Guid.NewGuid(), group.DocumentId, group.ContentId, "small", false,
            "source.txt", "source.txt", ".txt", DateTimeOffset.UnixEpoch,
            new SourceLocation(LocationKind.Structure, StructurePath: large, LayoutWarning: large), [large],
            ExtractionMethod.NativeText, null, 0, null, null, null, null, null, [], [], []) { ExcerptLength = 5 };
        Assert.Equal("search_session_too_large", Assert.Throws<ContextMoleException>(() =>
            new SearchSessionCache(clock, 64_000).Start(request,
                Response([Group(group.DocumentId) with { Previews = [preview] }, Group(Guid.NewGuid())]))).Code);
    }

    [Fact]
    public void SearchAndReadToolsAdvertiseTypedOutputSchemas()
    {
        var methods = typeof(BrokerMcpTools).GetMethods().Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null).ToArray();
        Assert.All(methods, method => Assert.NotNull(method.GetCustomAttribute<McpServerToolAttribute>()!.OutputSchemaType));
        var search = typeof(BrokerMcpTools).GetMethod(nameof(BrokerMcpTools.SearchProject))!;
        Assert.Contains(search.GetParameters(), parameter => parameter.Name == "match_scope");
        Assert.Contains(search.GetParameters(), parameter => parameter.Name == "retrieval_options");
        Assert.Contains(search.GetParameters(), parameter => parameter.Name == "cursor");
        var tool = McpServerTool.Create(search, _ => throw new NotSupportedException(),
            new McpServerToolCreateOptions { SerializerOptions = new JsonSerializerOptions(BrokerJson.Options)
                { TypeInfoResolver = new DefaultJsonTypeInfoResolver() } });
        var input = tool.ProtocolTool.InputSchema.GetProperty("properties");
        Assert.True(input.TryGetProperty("match_scope", out _));
        Assert.True(input.TryGetProperty("retrieval_options", out _));
        var output = tool.ProtocolTool.OutputSchema!.Value.GetProperty("properties");
        Assert.True(output.TryGetProperty("results", out _));
        Assert.True(output.TryGetProperty("candidate_limit_reached", out _));
        var read = McpServerTool.Create(typeof(BrokerMcpTools).GetMethod(nameof(BrokerMcpTools.ReadPassages))!,
            _ => throw new NotSupportedException(), new McpServerToolCreateOptions
            { SerializerOptions = new JsonSerializerOptions(BrokerJson.Options)
                { TypeInfoResolver = new DefaultJsonTypeInfoResolver() } });
        Assert.Contains(read.ProtocolTool.InputSchema.GetProperty("required").EnumerateArray(),
            property => property.GetString() == "expected_search_generation");
    }

    [Fact]
    public void PassageReadProtocolRejectsMissingExpectedGeneration()
    {
        var request = JsonSerializer.Serialize(new { project_id = Guid.NewGuid(), passage_ids = new[] { Guid.NewGuid() },
            context_before = 0, context_after = 0 }, BrokerJson.Options);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BrokerReadPassagesRequest>(request, BrokerJson.Options));
        var complete = new BrokerReadPassagesRequest(Guid.NewGuid(), [Guid.NewGuid()], 0, 0, 0);
        Assert.Equal(0, JsonSerializer.Deserialize<BrokerReadPassagesRequest>(
            JsonSerializer.Serialize(complete, BrokerJson.Options), BrokerJson.Options)!.ExpectedSearchGeneration);
    }

    private static SearchResultGroup Group(Guid document) => new(document, Guid.NewGuid(), "source.txt", "source.txt",
        ".txt", "source.txt", "text/plain", ".txt", [], 1, 1, 0, []) { Title = "Evidence" };

    private static SearchResponse Response(IReadOnlyList<SearchResultGroup> groups) =>
        new(SearchMode.Keyword, "keyword", [], 7, groups.Count, new SearchBranchCandidateDepths(groups.Count, 0, 0),
            false, 1, groups.Count - 1, [], groups.Take(1).ToArray()) { RankedGroups = groups, CandidateLimit = 1000 };

    private sealed class SessionClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
