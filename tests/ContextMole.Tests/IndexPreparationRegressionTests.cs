using System.Collections.Concurrent;
using System.Text;
using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Indexing;
using ContextMole.Infrastructure;
using ContextMole.Search;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class IndexPreparationRegressionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void SemanticNoiseCleanupPreservesEvidenceAndConservativelyDeduplicatesContext()
    {
        const string body = "Contract evidence\nThis email and any attachments are confidential.\nPlease delete it.\n\nAdditional evidence\n--\nSignature contact";
        var semantic = SemanticTextPreparation.CleanBody(body, emailBody: true);
        Assert.Contains("Contract evidence", semantic);
        Assert.Contains("Additional evidence", semantic);
        Assert.DoesNotContain("confidential", semantic);
        Assert.DoesNotContain("Signature", semantic);
        Assert.Equal(body, TextNormalization.ForDisplay(body));
        var composed = SemanticTextPreparation.Compose(semantic,
            [("Content", "report.pdf"), ("Filename", "report.pdf"), ("Title", new string('T', 300))], text => 1 + text.Length);
        Assert.Equal(1, composed.Split("report.pdf").Length - 1);
        Assert.True(composed.Length - semantic.Length <= 64);
    }

    [Fact]
    public void GeneralDocumentsRetainSeparatorsAndQuotedEmailPolicyExamples()
    {
        const string body = "Configuration reference\n--\nThe vault rotation key is required.\n" +
            "This email and any attachments are confidential.\nThis is the sample legal footer to configure.\n\nFinal evidence";
        var semantic = SemanticTextPreparation.CleanBody(body);
        Assert.Contains("vault rotation key", semantic);
        Assert.Contains("sample legal footer", semantic);
        Assert.Contains("Final evidence", semantic);
        Assert.Null(SemanticTextPreparation.SignatureStart(body));
        Assert.NotNull(SemanticTextPreparation.SignatureStart(body, emailBody: true));
        Assert.DoesNotContain("vault rotation key", SemanticTextPreparation.CleanBody(body, emailBody: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignatureCleanupUsesEmailBodyContextAndRetainsLiteralEvidence(bool email)
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var path = Path.Combine(database.Paths.SourceDirectory, email ? "message.eml" : "reference.txt");
        const string body = "The deployment runbook is approved.\n-- \n" +
            "recovery_marker requires verifying the backup checksum before restoring the database.";
        var text = email
            ? "From: sender@example.org\r\nTo: reader@example.org\r\nSubject: Deployment\r\n" +
                "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n" + body
            : body;
        await File.WriteAllTextAsync(path, text, Token);
        var (project, _) = await database.CreateProjectAsync("Content preservation", Token);
        var embeddings = new RecordingEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new DocumentExtractionRegistry(new StorageNoOcr()), embeddings, new IndexingActivityTracker(),
            new EmbeddingPolicyRefreshTracker(), budget, NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(Token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while ((await database.Store.ListProjectsAsync(timeout.Token)).Single(item => item.Id == project)
                   is not { IndexedCount: 1, PendingCount: 0 })
                await Task.Delay(20, timeout.Token);
            Assert.Contains(embeddings.Inputs, input => input.Contains("deployment runbook", StringComparison.Ordinal));
            Assert.Equal(!email, embeddings.Inputs.Any(input => input.Contains("recovery_marker", StringComparison.Ordinal)));
            var search = new HybridSearchService(database.Store, embeddings, new FlatVectorIndexFactory(),
                new VectorIndexCache(), budget);
            var response = await search.SearchAsync(new SearchRequest(project, SearchMode.Keyword,
                Clauses: [new SearchClause("marker", "recovery_marker", SearchClauseOccur.Must,
                    Fields: [SearchField.Body])]), Token);
            var preview = Assert.Single(Assert.Single(response.Results).Previews);
            var read = Assert.Single(await database.Store.ReadPassagesAsync(project, [preview.PassageId], 0, 0,
                response.SearchGeneration, Token));
            Assert.Equal(preview.Excerpt, read.Text.Substring(preview.ExcerptStart, preview.ExcerptLength));
            Assert.Contains("recovery_marker", read.Text);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DiscretionaryWrapSurvivesExtractionIndexSearchAndRead()
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var path = Path.Combine(database.Paths.SourceDirectory, "recovery.txt");
        const string body = "The inter\u00ad\nnational recovery procedure requires approval.\n" +
            "The separate re\u00ad\n\nentry paragraphs must remain separate.";
        await File.WriteAllTextAsync(path, body, Token);
        var sourceHash = await StorageTestDatabase.HashAsync(path, Token);
        var (project, _) = await database.CreateProjectAsync("Discretionary wrap", Token);
        var embeddings = new RecordingEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new DocumentExtractionRegistry(new StorageNoOcr()), embeddings, new IndexingActivityTracker(),
            new EmbeddingPolicyRefreshTracker(), budget, NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(Token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while ((await database.Store.ListProjectsAsync(timeout.Token)).Single(item => item.Id == project)
                   is not { IndexedCount: 1, PendingCount: 0 })
                await Task.Delay(20, timeout.Token);
            Assert.Contains(embeddings.Inputs, input => input.Contains("international recovery", StringComparison.Ordinal));
            var search = new HybridSearchService(database.Store, embeddings, new FlatVectorIndexFactory(),
                new VectorIndexCache(), budget);
            var response = await search.SearchAsync(new SearchRequest(project, SearchMode.Keyword,
                Clauses: [new SearchClause("word", "international", SearchClauseOccur.Must,
                    Fields: [SearchField.Body])]), Token);
            var preview = Assert.Single(Assert.Single(response.Results).Previews);
            var read = Assert.Single(await database.Store.ReadPassagesAsync(project, [preview.PassageId], 0, 0,
                response.SearchGeneration, Token));
            Assert.Contains("international recovery", read.Text);
            Assert.Equal(preview.Excerpt, read.Text.Substring(preview.ExcerptStart, preview.ExcerptLength));
            var nonmatch = await search.SearchAsync(new SearchRequest(project, SearchMode.Keyword,
                Clauses: [new SearchClause("word", "reentry", SearchClauseOccur.Must,
                    Fields: [SearchField.Body])]), Token);
            Assert.Empty(nonmatch.Results);
            Assert.Equal(sourceHash, await StorageTestDatabase.HashAsync(path, Token));
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractionIndexSearchAndReadPreserveEvidenceWithCompleteSemanticBudget(bool pdf)
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var path = Path.Combine(database.Paths.SourceDirectory, pdf ? "contract.pdf" : "contract.html");
        if (pdf)
        {
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            var page = builder.AddPage(600, 800);
            page.AddText("Contrato principal", 20, new PdfPoint(50, 740), font);
            page.AddText("Evidence alpha with under_score and cafe.", 12, new PdfPoint(50, 700), font);
            page.AddText("The tail_marker identifies the required clause.", 12, new PdfPoint(50, 660), font);
            await File.WriteAllBytesAsync(path, builder.Build(), Token);
        }
        else
        {
            var text = new StringBuilder("<html><head><title>").Append(new string('T', 300))
                .Append("</title></head><body><h1>Contract</h1><p>");
            for (var index = 0; index < 300; index++) text.Append("Neutral evidence ").Append(index).Append(".<br>");
            text.Append("The tail_marker identifies the required clause.</p><table><tr><th>Vendor</th><th>Amount</th></tr><tr><td>Ada</td><td>42</td></tr></table><footer>Contact footer</footer></body></html>");
            await File.WriteAllTextAsync(path, text.ToString(), Token);
        }
        var original = await StorageTestDatabase.HashAsync(path, Token);
        var (project, _) = await database.CreateProjectAsync("Evidence pipeline", Token);
        var embeddings = new RecordingEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new DocumentExtractionRegistry(new StorageNoOcr()), embeddings, new IndexingActivityTracker(),
            new EmbeddingPolicyRefreshTracker(), budget, NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(Token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while ((await database.Store.ListProjectsAsync(timeout.Token)).Single(item => item.Id == project)
                   is not { IndexedCount: 1, PendingCount: 0 })
                await Task.Delay(20, timeout.Token);
            var search = new HybridSearchService(database.Store, embeddings, new FlatVectorIndexFactory(),
                new VectorIndexCache(), budget);
            var response = await search.SearchAsync(new SearchRequest(project, SearchMode.Keyword,
                Clauses: [new SearchClause("tail", "tail_marker", SearchClauseOccur.Must, Fields: [SearchField.Body])]), Token);
            var preview = Assert.Single(Assert.Single(response.Results).Previews);
            var read = Assert.Single(await database.Store.ReadPassagesAsync(project, [preview.PassageId], 0, 0,
                response.SearchGeneration, Token));
            Assert.Equal(preview.Excerpt, read.Text.Substring(preview.ExcerptStart, preview.ExcerptLength));
            Assert.Contains("tail_marker", preview.Excerpt);
            Assert.Equal(read.SectionId, preview.SectionId);
            if (pdf)
            {
                Assert.Equal(1, preview.Location.Page);
                Assert.NotNull(preview.Location.Region);
            }
            else
            {
                var section = await database.Store.ReadSectionAsync(project, preview.SectionId!.Value,
                    response.SearchGeneration, 50, cancellationToken: Token);
                Assert.Contains(section.Passages, item => item.Text.Contains('\n'));
                Assert.Contains(section.Passages, item => item.Text.Contains("Vendor\tAmount"));
                var paragraphChunks = section.Passages.Where(item => item.Text.Contains("Neutral evidence")).ToArray();
                Assert.True(paragraphChunks.Length > 1);
                var overlap = Enumerable.Range(1, Math.Min(paragraphChunks[0].Text.Length, paragraphChunks[1].Text.Length))
                    .Where(length => paragraphChunks[0].Text.EndsWith(paragraphChunks[1].Text[..length], StringComparison.Ordinal))
                    .DefaultIfEmpty(0).Max();
                Assert.True(overlap >= 20, "Long title context must not consume the overlap that protects body phrases.");
                Assert.DoesNotContain(embeddings.Inputs, input => input.StartsWith("Contact footer", StringComparison.Ordinal));
            }
            Assert.NotEmpty(embeddings.Inputs);
            Assert.All(embeddings.Inputs, input =>
            {
                Assert.True(embeddings.CountTokens(input) <= 512, "The full embedding input must fit without truncation.");
                Assert.DoesNotContain(database.Paths.SourceDirectory, input);
            });
            Assert.Contains(embeddings.Inputs, input => input.Contains("tail_marker", StringComparison.Ordinal));
            Assert.True((await database.Store.LoadVectorSnapshotMetadataAsync(project, embeddings.Policy!, Token)).IsComplete,
                "Intentionally excluded boilerplate must not make semantic coverage appear incomplete.");
            Assert.Equal(original, await StorageTestDatabase.HashAsync(path, Token));
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractedTableKeepsEmptyEdgeCellsThroughIndexSearchAndRead(bool pdf)
    {
        await using var database = await StorageTestDatabase.CreateAsync(Token);
        var path = Path.Combine(database.Paths.SourceDirectory, pdf ? "edge-cells.pdf" : "edge-cells.html");
        const string expected = "\tAccount\tNotes\t\n\tAda\tApproved\t";
        if (pdf)
        {
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            var page = builder.AddPage(600, 700);
            foreach (var x in new[] { 40, 100, 240, 390, 450 })
                page.DrawLine(new PdfPoint(x, 500), new PdfPoint(x, 600), 1);
            foreach (var y in new[] { 500, 550, 600 })
                page.DrawLine(new PdfPoint(40, y), new PdfPoint(450, y), 1);
            page.AddText("Account", 10, new PdfPoint(110, 570), font);
            page.AddText("Notes", 10, new PdfPoint(260, 570), font);
            page.AddText("Ada", 10, new PdfPoint(110, 520), font);
            page.AddText("Approved", 10, new PdfPoint(260, 520), font);
            page.AddText("This source provides reliable native evidence about account approval and review status for the financial operations team.",
                8, new PdfPoint(40, 400), font);
            await File.WriteAllBytesAsync(path, builder.Build(), Token);
        }
        else
            await File.WriteAllTextAsync(path, "<table><tr><th></th><th>Account</th><th>Notes</th><th></th></tr>" +
                "<tr><td></td><td>Ada</td><td>Approved</td><td></td></tr></table>", Token);
        var (project, _) = await database.CreateProjectAsync("Preserved table", Token);
        var embeddings = new RecordingEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new DocumentExtractionRegistry(new StorageNoOcr()), embeddings, new IndexingActivityTracker(),
            new EmbeddingPolicyRefreshTracker(), budget, NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(Token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while ((await database.Store.ListProjectsAsync(timeout.Token)).Single(item => item.Id == project)
                   is not { IndexedCount: 1, PendingCount: 0 })
                await Task.Delay(20, timeout.Token);
            var search = new HybridSearchService(database.Store, embeddings, new FlatVectorIndexFactory(),
                new VectorIndexCache(), budget);
            var response = await search.SearchAsync(new SearchRequest(project, SearchMode.Keyword,
                Clauses: [new SearchClause("account", "Ada", SearchClauseOccur.Must, Fields: [SearchField.Body])]), Token);
            var preview = Assert.Single(Assert.Single(response.Results).Previews);
            var read = Assert.Single(await database.Store.ReadPassagesAsync(project, [preview.PassageId], 0, 0,
                response.SearchGeneration, Token));
            Assert.Equal(expected, read.Text);
            Assert.All(read.Text.Split('\n'), row => Assert.Equal(4, row.Split('\t').Length));
            Assert.Equal(preview.Excerpt, read.Text.Substring(preview.ExcerptStart, preview.ExcerptLength));
            Assert.Contains(embeddings.Inputs, input => input.Contains("Table headers: Account | Notes", StringComparison.Ordinal));
            var section = await database.Store.ReadSectionAsync(project, preview.SectionId!.Value,
                response.SearchGeneration, 50, cancellationToken: Token);
            Assert.Contains(section.Passages, passage => passage.Text == expected && passage.SectionOffset == read.SectionOffset);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RecordingEmbeddings : IEmbeddingGenerator
    {
        public ConcurrentBag<string> Inputs { get; } = [];
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public EmbeddingPolicy? Policy => StorageTestDatabase.TestEmbeddingPolicy;
        public int CountTokens(string text) => 1 + text.Length;
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
