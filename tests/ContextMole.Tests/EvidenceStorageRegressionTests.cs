using ContextMole.Core;
using ContextMole.Search;
using ContextMole.Storage;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class EvidenceStorageRegressionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("café ação under_score ½ ＡＢＣ", "cafe acao under_score 1 2 abc")]
    [InlineData("con-\ntrato português", "contrato portugues")]
    [InlineData("① ﬁnal ¼", "1 final 1 4")]
    public async Task CanonicalTokensAndRealFtsAgreeOnNormalization(string body, string canonical)
    {
        Assert.Equal(canonical, LexicalText.Canonicalize(body));
        Assert.Equal(LexicalText.Tokens(body), LexicalText.Tokens(canonical));
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await database.CreateProjectAsync("Canonical tokens", Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "neutral.txt");
        await File.WriteAllTextAsync(path, body, Token);
        var pending = await database.ObserveAndLeaseAsync(project, folder, path, false, Token);
        var committed = await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length,
            new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero), body, false, cancellationToken: Token);
        foreach (var term in LexicalText.Tokens(body))
        {
            var query = StructuredSearchQuery.BuildFtsQuery([new SearchClause("body", term, Fields: [SearchField.Body])], 1);
            var snapshot = await database.Store.LoadKeywordBranchesAsync(project, query, "", 10, null,
                new SearchFieldWeights(), SearchScope.Passage, Token);
            var candidate = Assert.Single(snapshot.MainCandidates);
            Assert.Equal(committed.PassageId, candidate.PassageId);
            Assert.True(StructuredSearchQuery.Evaluate(candidate,
                [new SearchClause("body", term, Fields: [SearchField.Body])], 1).IsMatch);
        }
        var phrase = StructuredSearchQuery.BuildFtsQuery([new SearchClause("phrase", body,
            Match: SearchMatchKind.Phrase, Fields: [SearchField.Body])], 1);
        Assert.Single((await database.Store.LoadKeywordBranchesAsync(project, phrase, "", 10, null,
            new SearchFieldWeights(), SearchScope.Passage, Token)).MainCandidates);
    }

    [Fact]
    public async Task SectionsPreserveDistributedEvidenceReadingOrderAndRevisionGuards()
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await database.CreateProjectAsync("Section evidence", Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "evidence.txt");
        await File.WriteAllTextAsync(path, "source evidence", Token);
        var pending = await database.ObserveAndLeaseAsync(project, folder, path, false, Token);
        var content = Guid.NewGuid();
        var section = Guid.NewGuid();
        var secondSection = Guid.NewGuid();
        var texts = new[] { "Alpha café", "Beta under_score", "Alpha excluded" };
        var passages = texts.Select((text, ordinal) => new PassageDraft(Guid.NewGuid(), content, ordinal,
            text, text, new SourceLocation(LocationKind.Page, Page: ordinal + 1,
                Region: new SourceRegion(.1, .2, .6, .1)), ExtractionMethod.NativeText, null, null,
            BodySearchText: text, ContentName: "evidence.txt")
            { SectionId = ordinal < 2 ? section : secondSection, SectionOffset = ordinal == 1 ? texts[0].Length + 1 : 0 }).ToArray();
        var committed = await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length,
            new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero), "", false,
            nodes: [new ContentNodeDraft(content, null, 0, "evidence.txt", "text/plain", "root", 0)],
            passages: passages, cancellationToken: Token,
            sections: [new SectionDraft(section, content, 0, string.Join('\n', texts.Take(2)), "Repeated title", ["Contract", "Repeated title"], "heading", passages[0].Location),
                new SectionDraft(secondSection, content, 1, texts[2], "Repeated title", [], "heading", passages[2].Location)]);
        var clauses = new[] { new SearchClause("a", "alpha", SearchClauseOccur.Must),
            new SearchClause("b", "beta", SearchClauseOccur.Must), new SearchClause("x", "excluded", SearchClauseOccur.MustNot) };
        var query = StructuredSearchQuery.BuildFtsQuery(clauses, 0);
        var scopePassage = await database.Store.LoadKeywordBranchesAsync(project, query, "", 10, null,
            new SearchFieldWeights(), SearchScope.Passage, Token);
        Assert.Empty(scopePassage.MainCandidates);
        var scopeSection = await database.Store.LoadKeywordBranchesAsync(project, query, "", 10, null,
            new SearchFieldWeights(), SearchScope.Section, Token);
        var candidate = Assert.Single(scopeSection.MainCandidates);
        Assert.Equal(section, candidate.SectionId);
        Assert.Equal(texts.Take(2), candidate.SectionPassages!.Select(member => member.DisplayText));
        Assert.Equal(string.Join('\n', texts.Take(2)), candidate.SectionText);
        Assert.Equal(passages[0].Location.Region, candidate.Location.Region);
        var firstPage = await database.Store.ReadSectionAsync(project, section, scopeSection.SearchGeneration, 1, cancellationToken: Token);
        Assert.Equal(passages[0].Id, Assert.Single(firstPage.Passages).PassageId);
        Assert.Equal(new[] { "Contract", "Repeated title" }, firstPage.HeadingPath);
        Assert.Equal("heading", firstPage.Kind);
        Assert.Equal(passages[0].Location, firstPage.Location);
        Assert.Equal(0, firstPage.Passages[0].SectionOffset);
        Assert.NotNull(firstPage.NextCursor);
        var secondPage = await database.Store.ReadSectionAsync(project, section, scopeSection.SearchGeneration, 1, firstPage.NextCursor, Token);
        Assert.Equal(passages[1].Id, Assert.Single(secondPage.Passages).PassageId);
        Assert.Equal(texts[0].Length + 1, secondPage.Passages[0].SectionOffset);
        Assert.Null(secondPage.NextCursor);
        var mismatch = await Assert.ThrowsAsync<ContextMoleException>(() => database.Store.ReadSectionAsync(project,
            secondSection, scopeSection.SearchGeneration, 1, firstPage.NextCursor, Token));
        Assert.Equal("invalid_cursor", mismatch.Code);
        var read = await database.Store.ReadPassagesAsync(project, [passages[1].Id], 1, 1,
            scopeSection.SearchGeneration, Token);
        Assert.Equal(passages.Select(passage => passage.Id), read.Select(passage => passage.PassageId));
        await database.Writer.RequestReindexAsync(project, Token);
        // Publication, rather than merely queueing a job, changes the generation.
        var reindex = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token);
        Assert.NotNull(reindex);
        await database.CommitAsync(reindex!, pending.Sha256, pending.File.Length,
            new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero), "new evidence", false, cancellationToken: Token);
        var stale = await Assert.ThrowsAsync<ContextMoleException>(() => database.Store.ReadPassagesAsync(project,
            [committed.PassageId], 0, 0, scopeSection.SearchGeneration, Token));
        Assert.Equal("index_changed", stale.Code);
    }

    [Fact]
    public async Task ContentInfoUsesSelectedAttachmentMetadataAndEvidence()
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await database.CreateProjectAsync("Attachment info", Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "root.eml");
        await File.WriteAllTextAsync(path, "email", Token);
        var pending = await database.ObserveAndLeaseAsync(project, folder, path, false, Token);
        var root = Guid.NewGuid(); var child = Guid.NewGuid();
        var passages = new[] { new PassageDraft(Guid.NewGuid(), root, 0, "Root", "Root",
                new SourceLocation(LocationKind.EmailPart), ExtractionMethod.Email, null, null),
            new PassageDraft(Guid.NewGuid(), child, 0, "PDF evidence", "PDF evidence",
                new SourceLocation(LocationKind.Page, Page: 2), ExtractionMethod.NativeText, null, null) };
        var committed = await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length,
            new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero), "", false,
            errors: [new ExtractionError("pdf_warning", "ambiguous table", false, "report.pdf"),
                new ExtractionError("email_warning", "root warning", false, "root.eml")],
            nodes: [new ContentNodeDraft(root, null, 0, "root.eml", "message/rfc822", "root", 0),
                new ContentNodeDraft(child, root, 0, "report.pdf", "application/pdf", "attachment", 1)],
            passages: passages, cancellationToken: Token);
        var info = await database.Store.GetDocumentInfoAsync(project, committed.DocumentId, child, Token);
        Assert.NotNull(info);
        Assert.Equal(child, info.ContentId);
        Assert.Equal("report.pdf", info.ContentName);
        Assert.Equal("application/pdf", info.ContentMimeType);
        Assert.Equal("root.eml", info.FileName);
        Assert.Equal(1, info.PassageCount);
        Assert.Equal(0, info.AttachmentCount);
        Assert.Equal(1, Assert.Single(info.ExtractionSummary).Value);
        Assert.Equal("pdf_warning", Assert.Single(info.Errors).Code);
        var entire = await database.Store.GetDocumentInfoAsync(project, committed.DocumentId, null, Token);
        Assert.Equal(2, entire!.PassageCount);
        Assert.Equal(2, entire.Errors.Count);
    }

    [Fact]
    public async Task LexicalBranchesShareSnapshotAndBudgetDuringWritesToAnotherProject()
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await database.CreateProjectAsync("Snapshot reader", Token);
        var (other, otherFolder) = await database.CreateProjectAsync("Concurrent writer", Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "snapshot.txt");
        await File.WriteAllTextAsync(path, "source", Token);
        var pending = await database.ObserveAndLeaseAsync(project, folder, path, false, Token);
        var content = Guid.NewGuid();
        var passages = Enumerable.Range(0, 40).Select(index => new PassageDraft(Guid.NewGuid(), content,
            index, $"shared evidence {index}", "shared evidence", new SourceLocation(LocationKind.Document),
            ExtractionMethod.NativeText, null, null, "shared evidence")).ToArray();
        await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length,
            new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero), "", false,
            nodes: [new ContentNodeDraft(content, null, 0, "snapshot.txt", "text/plain", "root", 0)],
            passages: passages, cancellationToken: Token);
        var generation = await database.Store.GetSearchGenerationAsync(project, Token);
        var query = StructuredSearchQuery.BuildFtsQuery([new SearchClause("term", "shared")], 1);
        var writing = WriteOtherProjectAsync();
        do
        {
            var snapshot = await database.Store.LoadKeywordBranchesAsync(project, query, query, 5, null,
                new SearchFieldWeights(), SearchScope.Passage, Token);
            Assert.Equal(generation, snapshot.SearchGeneration);
            Assert.Equal(5, snapshot.MainCandidates.Count);
            Assert.True(snapshot.MainLimitReached);
            Assert.True(snapshot.OptionalLimitReached);
            Assert.Equal(snapshot.MainCandidates.Select(item => (item.PassageId, item.KeywordScore)),
                snapshot.OptionalCandidates.Select(item => (item.PassageId, item.KeywordScore)));
        } while (!writing.IsCompleted);
        await writing;

        async Task WriteOtherProjectAsync()
        {
            for (var index = 0; index < 10; index++)
            {
                var otherPath = Path.Combine(database.Paths.SourceDirectory, $"other-{index}.txt");
                await File.WriteAllTextAsync(otherPath, "shared evidence from another project", Token);
                var otherPending = await database.ObserveAndLeaseAsync(other, otherFolder, otherPath, false, Token);
                await database.CommitAsync(otherPending.Job, otherPending.Sha256, otherPending.File.Length,
                    new DateTimeOffset(otherPending.File.LastWriteTimeUtc, TimeSpan.Zero), "shared evidence from another project",
                    false, cancellationToken: Token);
            }
        }
    }
}
