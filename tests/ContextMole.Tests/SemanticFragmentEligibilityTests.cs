using System.Collections.Concurrent;
using System.Text.Json;

using ContextMole.Core;
using ContextMole.Indexing;
using ContextMole.Infrastructure;
using ContextMole.Search;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class SemanticFragmentEligibilityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string OldPreparation = "layout-v4/spans-v3/body-context-v3";

    [Theory]
    [InlineData("•")]
    [InlineData("◦")]
    [InlineData("▪")]
    [InlineData("‣")]
    [InlineData("⁃")]
    public void OnlyASeparatedMarkerCanBorrowItsRightHandItemsMeaning(string marker)
    {
        var sections = new[] { Block(marker), Block("Approved", 2, x: 0.13) };
        Assert.Equal(new[] { 0 }, SemanticEvidenceEligibility.FindSeparatedListMarkers(sections));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([sections[0]]));
        Assert.Equal(marker, sections[0].Text);
        Assert.Equal(LexicalText.Canonicalize(marker), LexicalText.Canonicalize(sections[0].Text));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("3.5")]
    [InlineData("3,5%")]
    [InlineData("0")]
    [InlineData("Yes")]
    [InlineData("No")]
    [InlineData("Male")]
    [InlineData("Female")]
    [InlineData("=")]
    [InlineData("==")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("£")]
    [InlineData("·")]
    [InlineData("Copyright")]
    [InlineData("1. Approved")]
    [InlineData("1.")]
    [InlineData("23)")]
    [InlineData("(3)")]
    [InlineData("a.")]
    [InlineData("B)")]
    [InlineData("(c)")]
    public void ShortFactsNumbersOperatorsAndCompleteListItemsRemainEligible(string text)
    {
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers(
            [Block(text), Block("Useful neighboring text", 2, x: 0.13)]));
    }

    [Fact]
    public void GeometryLogicalSectionsHeadingsTablesAndUncertainLayoutsAreConservative()
    {
        var marker = Block("•");
        var body = Block("Approved", 2, x: 0.13);
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker, body with { IsBoilerplate = true }]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker, body with { SectionKey = "other" }]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker, body with
            { Location = body.Location with { Page = 2 } }]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker,
            Block("Approved", 2, x: 0.6)]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker,
            Block("Approved", 2, x: 0.13, y: 0.3)]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker with { Heading = "•" }, body]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker with
            { Location = marker.Location with { StructurePath = "page[1]/table[1]/block[1]" } }, body]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker with
            { Location = marker.Location with { Region = null } }, body]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker with
            { Location = marker.Location with { LayoutWarning = "rotated_layout: unverified" } }, body]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker, body with
            { Location = body.Location with { LayoutWarning = "layout_fallback: unverified" } }]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker with
            { Location = marker.Location with { Kind = LocationKind.Slide } }, body]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker, body with { Method = ExtractionMethod.Ocr }]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker, Block("42", 2, x: 0.13)]));
        foreach (var page in new int?[] { null, 0, -1 })
            Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker with
                { Location = marker.Location with { Page = page } }, body with
                { Location = body.Location with { Page = page } }]));
        foreach (var frame in new int?[] { null, 0, -1 })
            Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker with
                { Location = marker.Location with { Kind = LocationKind.ImageFrame, ImageFrame = frame } }, body with
                { Location = body.Location with { Kind = LocationKind.ImageFrame, ImageFrame = frame } }]));
        foreach (var region in new[]
        {
            new SourceRegion(-0.01, 0.2, 0.015, 0.012), new SourceRegion(0.1, -0.01, 0.015, 0.012),
            new SourceRegion(0.1, 0.2, 0, 0.012), new SourceRegion(0.1, 0.2, 0.015, 0),
            new SourceRegion(0.1, 0.2, double.NaN, 0.012), new SourceRegion(0.1, 0.2, double.PositiveInfinity, 0.012),
            new SourceRegion(0.1, 0.2, 0.95, 0.012), new SourceRegion(0.1, 0.2, 0.015, 0.9)
        })
            Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker with
                { Location = marker.Location with { Region = region } }, body]));
    }

    [Theory]
    [InlineData("·")]
    [InlineData("•")]
    [InlineData("1.")]
    [InlineData("a.")]
    public void NearbyLeftContextPreservesOperatorsDecimalLiteralsAndIdentifiers(string text)
    {
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers(
            [Block("2", x: 0.07), Block(text, 2), Block("velocity", 3, x: 0.13)]));
    }

    [Fact]
    public void AnExactDocumentTitleIsUsefulEvidenceEvenWhenItLooksLikeAListLabel()
    {
        var sections = new[] { Block("•"), Block("Approved", 2, x: 0.13) };
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers(sections, "•"));
        Assert.Equal(new[] { 0 }, SemanticEvidenceEligibility.FindSeparatedListMarkers(sections, "Other title"));
    }

    [Theory]
    [InlineData("1.", "kg")]
    [InlineData("a.", "Length")]
    [InlineData("(3)", "sin x")]
    [InlineData("(a)", "velocity")]
    [InlineData("23)", "velocity")]
    [InlineData("B)", "Length")]
    public void PrefixValuesMembersFactorsAndAmbiguousLabelsRemainEligible(string prefix, string operand)
    {
        foreach (var method in new[] { ExtractionMethod.NativeText, ExtractionMethod.Ocr })
        foreach (var x in new[] { 0.115, 0.13 })
            Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers(
                [Block(prefix, method: method), Block(operand, 2, x: x, method: method)]));
    }

    [Fact]
    public void MixedNativeAndOcrLeftOperandsAlsoVetoSuppression()
    {
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers(
            [Block("force", x: 0.07), Block("•", 2, method: ExtractionMethod.Ocr),
             Block("velocity", 3, x: 0.13, method: ExtractionMethod.Ocr)]));
    }

    [Fact]
    public void LowConfidenceOcrCannotSupplyPositiveMarkerEvidenceButCanVetoIt()
    {
        var marker = Block("•", method: ExtractionMethod.Ocr);
        var body = Block("Approved", 2, x: 0.13, method: ExtractionMethod.Ocr);
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker with { OcrConfidence = 49 }, body]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker, body with { OcrConfidence = 49 }]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker with { OcrConfidence = null }, body]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker, body with { OcrConfidence = null }]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers(
            [Block("force", x: 0.07, method: ExtractionMethod.Ocr) with { OcrConfidence = 20 }, marker, body]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers(
            [Block("force", x: 0.07, method: ExtractionMethod.Ocr) with { OcrConfidence = null }, marker, body]));
    }

    [Fact]
    public void WarnedLeftContextCanVetoSuppressionButCannotSupplyPositiveEvidence()
    {
        var marker = Block("•");
        var body = Block("Approved", 2, x: 0.13);
        var left = Block("force", 3, x: 0.07);
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([left with
            { Location = left.Location with { LayoutWarning = "native_text_replaced: unverified" } }, marker, body]));
        Assert.Empty(SemanticEvidenceEligibility.FindSeparatedListMarkers([marker, body with
            { Location = body.Location with { LayoutWarning = "native_text_replaced: unverified" } }]));
    }

    [Theory]
    [InlineData(ExtractionMethod.NativeText, LocationKind.Page)]
    [InlineData(ExtractionMethod.Ocr, LocationKind.Page)]
    [InlineData(ExtractionMethod.Ocr, LocationKind.ImageFrame)]
    public void SeveralMarkersCanReferToOneMultilineBodyBlock(ExtractionMethod method, LocationKind kind)
    {
        var first = Block("•", method: method, kind: kind);
        var second = Block("•", 2, y: 0.24, method: method, kind: kind);
        var body = Block("Approved\nRate: 3.5%", 3, x: 0.13, height: 0.055, method: method, kind: kind);
        Assert.Equal(new[] { 0, 1 }, SemanticEvidenceEligibility.FindSeparatedListMarkers([first, second, body]).Order());
    }

    [Theory]
    [InlineData(ExtractionMethod.NativeText, false)]
    [InlineData(ExtractionMethod.Ocr, false)]
    [InlineData(ExtractionMethod.NativeText, true)]
    [InlineData(ExtractionMethod.Ocr, true)]
    public async Task IndexSearchAndReadKeepEveryLiteralPassageWhileOnlyMarkersLoseVectors(ExtractionMethod method,
        bool titleMatchesLabel)
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "authored-evidence.txt");
        await File.WriteAllTextAsync(path, "Authored fixture source bytes must stay unchanged.", Token);
        var sourceHash = await StorageTestDatabase.HashAsync(path, Token);
        var sections = new[]
        {
            Block("1.", method: method), Block("Approved", 2, x: 0.13, method: method),
            Block("•", 3, y: 0.25, method: method), Block("Rate: 3.5%", 4, x: 0.13, y: 0.25, method: method),
            Block("42", 5, y: 0.3, method: method), Block("=", 6, y: 0.35, method: method),
            Block("Copyright", 7, y: 0.4, method: method),
            Block("3.", 8, y: 0.45, method: method) with
                { Location = new SourceLocation(LocationKind.Page, Page: 1, StructurePath: "page[1]/table[8]",
                    Region: new SourceRegion(0.1, 0.45, 0.015, 0.012)) },
            Block("2.", 9, y: 0.5, method: method) with { Heading = "2." },
            Block("4.", 10, y: 0.55, method: method),
            Block("a.", 11, y: 0.6, method: method), Block("Yes", 12, x: 0.13, y: 0.6, method: method)
        };
        var (project, _) = await database.CreateProjectAsync("Semantic fragment evidence", Token);
        var embeddings = new RecordingEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new FixtureExtractor(sections, titleMatchesLabel ? "•" : "Highly relevant title context cannot create body evidence"),
            embeddings, new IndexingActivityTracker(),
            new EmbeddingPolicyRefreshTracker(), budget, NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(Token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while ((await database.Store.ListProjectsAsync(timeout.Token)).Single(item => item.Id == project)
                   is not { IndexedCount: 1, PendingCount: 0 })
                await Task.Delay(20, timeout.Token);

            var stored = new List<(Guid Id, string Text, string Body, string Semantic, bool Eligible, int Offset)>();
            await using (var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath}"))
            {
                await connection.OpenAsync(Token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT id,display_text,body_text,search_text,semantic_eligible,section_offset FROM passages ORDER BY ordinal;";
                await using var reader = await command.ExecuteReaderAsync(Token);
                while (await reader.ReadAsync(Token))
                    stored.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4) == 1, reader.GetInt32(5)));
                await reader.DisposeAsync();
                command.CommandText = "SELECT display_text FROM sections;";
                Assert.Equal(string.Join('\n', sections.Select(section => section.Text)),
                    Assert.IsType<string>(await command.ExecuteScalarAsync(Token)));
            }
            Assert.Equal(sections.Select(section => section.Text), stored.Select(row => row.Text));
            var expectedOffset = 0;
            foreach (var row in stored)
            {
                Assert.Equal(expectedOffset, row.Offset);
                expectedOffset += row.Text.Length + 1;
            }
            Assert.Equal(sections.Select(section => LexicalText.Canonicalize(section.Text)), stored.Select(row => row.Body));
            Assert.Equal(titleMatchesLabel ? Array.Empty<string>() : new[] { "•" },
                stored.Where(row => !row.Eligible).Select(row => row.Text));
            Assert.All(stored.Where(row => !row.Eligible), row => Assert.Empty(row.Semantic));
            var expectedVectors = sections.Length - (titleMatchesLabel ? 0 : 1);
            Assert.Equal(expectedVectors, embeddings.Inputs.Count);
            Assert.Contains(embeddings.Inputs, input => input.StartsWith("Approved", StringComparison.Ordinal));
            Assert.Contains(embeddings.Inputs, input => input.StartsWith("42", StringComparison.Ordinal));
            var metadata = await database.Store.LoadVectorSnapshotMetadataAsync(project, embeddings.Policy!, Token);
            Assert.True(metadata.IsComplete);
            Assert.Equal(expectedVectors, metadata.EntryCount);

            var reads = await database.Store.ReadPassagesAsync(project, stored.Select(row => row.Id).ToArray(), 0, 0, Token);
            Assert.Equal(stored.Select(row => row.Text), reads.Select(read => read.Text));
            foreach (var read in reads)
            {
                var section = await database.Store.ReadSectionAsync(project, read.SectionId!.Value,
                    await database.Store.GetSearchGenerationAsync(project, Token), cancellationToken: Token);
                Assert.Contains(section.Passages, passage => passage.PassageId == read.PassageId && passage.Text == read.Text);
                Assert.Contains(section.Passages, passage => passage.Text == "Approved");
                Assert.Contains(section.Passages, passage => passage.Text == "1.");
                Assert.Contains(section.Passages, passage => passage.Text == "a.");
                Assert.Contains(section.Passages, passage => passage.Text == "Yes");
            }
            var search = new HybridSearchService(database.Store, embeddings, new FlatVectorIndexFactory(),
                new VectorIndexCache(), budget);
            var response = await search.SearchAsync(new SearchRequest(project, SearchMode.Keyword,
                Clauses: [new SearchClause("ordinal", "1", SearchClauseOccur.Must, Fields: [SearchField.Body])]), Token);
            var preview = Assert.Single(Assert.Single(response.Results).Previews);
            Assert.Equal("1.", preview.Excerpt);
            Assert.Equal(stored[0].Id, preview.PassageId);
            Assert.Equal(sourceHash, await StorageTestDatabase.HashAsync(path, Token));
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task V3EligibilityCannotBeRelabeledAsV4ByAnEmbeddingOnlyRefresh()
    {
        Assert.Equal("layout-v5/spans-v3/body-context-v4", IndexPreparation.Version);
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "v3-evidence.txt");
        await File.WriteAllTextAsync(path, "1.\nApproved", Token);
        var (project, folder) = await database.CreateProjectAsync("Fragment eligibility migration", Token);
        var first = await database.ObserveAndLeaseAsync(project, folder, path, false, Token);
        var modified = new DateTimeOffset(first.File.LastWriteTimeUtc, TimeSpan.Zero);
        var committed = await database.CommitAsync(first.Job, first.Sha256, first.File.Length, modified,
            "1.\nApproved", cancellationToken: Token);
        var oldPolicy = StorageTestDatabase.TestEmbeddingPolicy with { PreparationVersion = OldPreparation };
        await using (var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath}"))
        {
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE document_revisions SET preparation_version=$old,embedding_policy_json=$policy WHERE id=$revision;
                UPDATE embeddings SET policy_key=$key WHERE revision_id=$revision;
                """;
            command.Parameters.AddWithValue("$old", OldPreparation);
            command.Parameters.AddWithValue("$policy", JsonSerializer.Serialize(oldPolicy));
            command.Parameters.AddWithValue("$key", oldPolicy.Key);
            command.Parameters.AddWithValue("$revision", committed.RevisionId.ToString());
            await command.ExecuteNonQueryAsync(Token);
        }
        await database.Writer.RequestEmbeddingRefreshAsync(project, StorageTestDatabase.TestEmbeddingPolicy, false, Token);
        var admitted = Assert.IsType<IndexJobLease>(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
        Assert.Equal(IndexJobKind.Reindex, admitted.Kind);
        await database.Writer.FailJobAsync(admitted, "source_unavailable", "Authored repair failure", false, Token);
        var current = await database.Store.LoadVectorSnapshotMetadataAsync(project, StorageTestDatabase.TestEmbeddingPolicy, Token);
        Assert.False(current.IsComplete);
        Assert.Equal(0, current.EntryCount);
        Assert.Single((await database.Store.LoadVectorSnapshotAsync(project, oldPolicy, Token)).Entries);
        Assert.Equal("1.\nApproved", Assert.Single(await database.Store.ReadPassagesAsync(project,
            [committed.PassageId], 0, 0, Token)).Text);
        var observed = await database.Writer.ObserveFileAsync(new FileObservation(project, folder, path,
            first.File.Length, modified), Token);
        Assert.False(observed.Queued); // Discovery must not automatically reopen the failed same-preparation repair.
        await database.Writer.RequestReindexAsync(project, Token);
        var upgrade = Assert.IsType<IndexJobLease>(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
        Assert.Equal(IndexJobKind.Reindex, upgrade.Kind);
        await database.Writer.FailJobAsync(upgrade, "source_unavailable", "Authored migration failure", false, Token);
        await database.Writer.RequestEmbeddingRefreshAsync(project, StorageTestDatabase.TestEmbeddingPolicy, true, Token);
        var retried = Assert.IsType<IndexJobLease>(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
        Assert.Equal(IndexJobKind.Reindex, retried.Kind);
        await database.Writer.FailJobAsync(retried, "source_unavailable", "Authored repair failure", false, Token);
        Assert.Equal("1.\nApproved", Assert.Single(await database.Store.ReadPassagesAsync(project,
            [committed.PassageId], 0, 0, Token)).Text);
    }

    [Fact]
    public async Task SuccessfulV3ToV4ReextractionKeepsAllCanonicalIdsBodiesLocationsAndOffsets()
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "successful-upgrade.txt");
        await File.WriteAllTextAsync(path, "Unchanged source for an authored preparation upgrade.", Token);
        var (project, folder) = await database.CreateProjectAsync("Successful eligibility migration", Token);
        var first = await database.ObserveAndLeaseAsync(project, folder, path, false, Token);
        var sections = new[] { Block("•"), Block("Approved", 2, x: 0.13),
            Block("1.", 3, y: 0.3), Block("kg", 4, x: 0.13, y: 0.3) };
        var extractor = new FixtureExtractor(sections, "Preparation evidence");
        var embeddings = new RecordingEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            extractor, embeddings, new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(),
            budget, NullLogger<IndexingCoordinator>.Instance);
        var root = (await extractor.ExtractAsync(new ExtractionRequest(path), Token)).Root;
        var flatten = typeof(IndexingCoordinator).GetMethod("FlattenAndChunk",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var draft = ((List<ContentNodeDraft> Nodes, List<PassageDraft> Passages, List<SectionDraft> Sections))
            flatten.Invoke(coordinator, [root, path, first.Job.DocumentId])!;
        // Model the preceding policy's sole behavior difference: every nonempty body received
        // context and a vector. Canonical construction is shared and must survive the upgrade.
        var oldPassages = draft.Passages.Select(passage => passage with
        {
            SemanticEligible = true, Embedding = StorageTestDatabase.TestVector(),
            SearchText = passage.SearchText.Length > 0 ? passage.SearchText : SemanticTextPreparation.Compose(
                SemanticTextPreparation.CleanBody(passage.DisplayText),
                [("Title", root.Title), ("Content", root.Name), ("Filename", Path.GetFileName(path))], embeddings.CountTokens)
        }).ToArray();
        var committed = await database.CommitAsync(first.Job, first.Sha256, first.File.Length,
            new DateTimeOffset(first.File.LastWriteTimeUtc, TimeSpan.Zero), "unused",
            nodes: draft.Nodes, passages: oldPassages, sections: draft.Sections, cancellationToken: Token);
        var oldPolicy = embeddings.Policy! with { PreparationVersion = OldPreparation };
        await using (var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath}"))
        {
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE document_revisions SET preparation_version=$old,embedding_policy_json=$policy WHERE id=$revision;
                UPDATE embeddings SET policy_key=$key WHERE revision_id=$revision;
                """;
            command.Parameters.AddWithValue("$old", OldPreparation);
            command.Parameters.AddWithValue("$policy", JsonSerializer.Serialize(oldPolicy));
            command.Parameters.AddWithValue("$key", oldPolicy.Key);
            command.Parameters.AddWithValue("$revision", committed.RevisionId.ToString());
            await command.ExecuteNonQueryAsync(Token);
        }
        var before = await CanonicalSnapshot(database);
        Assert.Equal(sections.Length, (await database.Store.LoadVectorSnapshotAsync(project, oldPolicy, Token)).Entries.Count);
        await coordinator.StartAsync(Token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            VectorSnapshotMetadata metadata;
            do
            {
                metadata = await database.Store.LoadVectorSnapshotMetadataAsync(project, embeddings.Policy!, timeout.Token);
                if (!metadata.IsComplete) await Task.Delay(20, timeout.Token);
            } while (!metadata.IsComplete);
            Assert.Equal(sections.Length - 1, metadata.EntryCount);
            Assert.Equal(before, await CanonicalSnapshot(database));
            Assert.Equal(first.Sha256, await StorageTestDatabase.HashAsync(path, Token));
            var reads = await database.Store.ReadPassagesAsync(project, oldPassages.Select(passage => passage.Id).ToArray(), 0, 0, Token);
            Assert.Equal(sections.Select(section => section.Text), reads.Select(read => read.Text));
            var sectionRead = await database.Store.ReadSectionAsync(project, reads[0].SectionId!.Value,
                metadata.SearchGeneration, cancellationToken: Token);
            Assert.Contains(sectionRead.Passages, passage => passage.Text == "•");
            Assert.Contains(sectionRead.Passages, passage => passage.Text == "Approved");
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    private static async Task<string[]> CanonicalSnapshot(StorageTestDatabase database)
    {
        await using var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath}");
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id,p.content_id,p.display_text,p.body_text,p.section_id,p.section_offset,p.location_json
            FROM passages p JOIN documents d ON d.active_revision_id=p.revision_id ORDER BY p.ordinal;
            """;
        await using var reader = await command.ExecuteReaderAsync(Token);
        var rows = new List<string>();
        while (await reader.ReadAsync(Token))
            rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray()));
        return rows.ToArray();
    }

    private static ExtractedSection Block(string text, int ordinal = 1, double x = 0.1, double y = 0.2,
        double height = 0.012, ExtractionMethod method = ExtractionMethod.NativeText,
        LocationKind kind = LocationKind.Page) =>
        new(text, new SourceLocation(kind, Page: 1, StructurePath: $"page[1]/block[{ordinal}]",
            ImageFrame: kind == LocationKind.ImageFrame ? 1 : null, Region: new SourceRegion(x, y, 0.015, height)),
            method, OcrConfidence: method == ExtractionMethod.Ocr ? 90 : null, SectionKey: "logical-section");

    private sealed class FixtureExtractor(IReadOnlyList<ExtractedSection> sections, string title) : IDocumentExtractor
    {
        public IReadOnlyCollection<string> Extensions => [".txt"];
        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ExtractionResult(new ExtractedNode(Path.GetFileName(request.SourcePath), "text/plain", "root",
                sections, [], Title: title), []));
    }

    private sealed class RecordingEmbeddings : IEmbeddingGenerator
    {
        public ConcurrentBag<string> Inputs { get; } = [];
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public EmbeddingPolicy? Policy => StorageTestDatabase.TestEmbeddingPolicy;
        public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken)
        {
            foreach (var passage in passages) Inputs.Add(passage);
            return Task.FromResult(new EmbeddingBatch(passages.Select(_ => StorageTestDatabase.TestVector()).ToArray(), Policy!));
        }
        public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult(new QueryEmbedding(StorageTestDatabase.TestVector(), Policy!));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
