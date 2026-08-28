using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Indexing;
using ContextMole.Infrastructure;
using ContextMole.Search;
using ContextMole.Storage;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class IndexingPipelineTests
{
    [Fact]
    public async Task RealIndexKeepsFilenameAndExtractedTitleAsIndependentFields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var untitled = Path.Combine(database.Paths.SourceDirectory, "onlyfilename.txt");
        var titled = Path.Combine(database.Paths.SourceDirectory, "web-page.html");
        var duplicateTitle = Path.Combine(database.Paths.SourceDirectory, "duplicatedtitle.html");
        await File.WriteAllTextAsync(untitled, "plain neutral evidence", cancellationToken);
        await File.WriteAllTextAsync(titled,
            "<html><head><title>Orchid Charter</title></head><body>html neutral evidence</body></html>",
            cancellationToken);
        await File.WriteAllTextAsync(duplicateTitle,
            "<html><head><title>duplicatedtitle</title></head><body>duplicate neutral evidence</body></html>",
            cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Independent title metadata", cancellationToken);

        var embeddings = new StorageUnavailableEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new DocumentExtractionRegistry(new StorageNoOcr()), embeddings, new IndexingActivityTracker(),
            new EmbeddingPolicyRefreshTracker(), budget, NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(async () =>
                (await database.Store.ListProjectsAsync(cancellationToken)).Single(item => item.Id == projectId)
                is { IndexedCount: 3, PendingCount: 0 }, cancellationToken);

            var filenameQuery = StructuredSearchQuery.BuildFtsQuery(
                [new SearchClause("filename", "onlyfilename", Fields: [SearchField.Filename])], 1);
            var filenameMatch = Assert.Single((await database.Store.KeywordSearchAsync(projectId, filenameQuery,
                10, null, cancellationToken)).Candidates);
            Assert.Equal("onlyfilename.txt", filenameMatch.FileName);
            Assert.True(string.IsNullOrEmpty(filenameMatch.Title));

            var filenameInTitleQuery = StructuredSearchQuery.BuildFtsQuery(
                [new SearchClause("title", "onlyfilename", Fields: [SearchField.Title])], 1);
            Assert.Empty((await database.Store.KeywordSearchAsync(projectId, filenameInTitleQuery, 10, null,
                cancellationToken)).Candidates);

            var duplicateFilenameQuery = StructuredSearchQuery.BuildFtsQuery(
                [new SearchClause("filename", "duplicatedtitle", Fields: [SearchField.Filename])], 1);
            var duplicateFilenameMatch = Assert.Single((await database.Store.KeywordSearchAsync(projectId,
                duplicateFilenameQuery, 10, null, cancellationToken)).Candidates);
            Assert.True(string.IsNullOrEmpty(duplicateFilenameMatch.Title));
            var duplicateTitleQuery = StructuredSearchQuery.BuildFtsQuery(
                [new SearchClause("title", "duplicatedtitle", Fields: [SearchField.Title])], 1);
            Assert.Empty((await database.Store.KeywordSearchAsync(projectId, duplicateTitleQuery, 10, null,
                cancellationToken)).Candidates);

            var titleQuery = StructuredSearchQuery.BuildFtsQuery(
                [new SearchClause("title", "Orchid Charter", Match: SearchMatchKind.Phrase,
                    Fields: [SearchField.Title])], 1);
            var titleMatch = Assert.Single((await database.Store.KeywordSearchAsync(projectId, titleQuery, 10,
                null, new SearchFieldWeights(Body: 0, Title: 10, Heading: 0, Filename: 0, Path: 0,
                    ContentName: 0, Sheet: 0, EmailSubject: 0), cancellationToken)).Candidates);
            Assert.Equal("Orchid Charter", titleMatch.Title);
            Assert.Equal("web-page.html", titleMatch.FileName);
            Assert.DoesNotContain("Orchid Charter", titleMatch.DisplayText);
            Assert.True(titleMatch.KeywordScore > 0);

            var titleInFilenameQuery = StructuredSearchQuery.BuildFtsQuery(
                [new SearchClause("filename", "Orchid Charter", Match: SearchMatchKind.Phrase,
                    Fields: [SearchField.Filename])], 1);
            Assert.Empty((await database.Store.KeywordSearchAsync(projectId, titleInFilenameQuery, 10, null,
                cancellationToken)).Candidates);

            await using var connection = new SqliteConnection(
                $"Data Source={database.Paths.DatabasePath};Mode=ReadOnly");
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT search_text FROM passages WHERE id=$passage;";
            command.Parameters.AddWithValue("$passage", titleMatch.PassageId.ToString());
            var semanticText = Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken));
            Assert.Contains("Title: Orchid Charter", semanticText);
            Assert.DoesNotContain("Title: web-page.html", semanticText);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task LegacyAncestorRootExcludesActiveAppDataFromReconciliationWatchersAndIndexJobs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Legacy broad root", cancellationToken);
        var legacyDataSource = Path.Combine(database.Paths.DataDirectory, "legacy-private.txt");
        var ordinarySource = Path.Combine(database.Paths.RootDirectory, "ordinary-public.txt");
        await File.WriteAllTextAsync(legacyDataSource, "privatelegacysentinel", cancellationToken);
        await File.WriteAllTextAsync(ordinarySource, "ordinarypublicsentinel", cancellationToken);
        var legacyHash = await StorageTestDatabase.HashAsync(legacyDataSource, cancellationToken);
        var ordinaryHash = await StorageTestDatabase.HashAsync(ordinarySource, cancellationToken);

        // Seed the state a pre-hardening installation could contain, then widen its configured root directly.
        var legacy = await database.ObserveAndLeaseAsync(projectId, folderId, legacyDataSource, false,
            cancellationToken);
        await database.CommitAsync(legacy.Job, legacy.Sha256, legacy.File.Length,
            new DateTimeOffset(legacy.File.LastWriteTimeUtc, TimeSpan.Zero), "privatelegacysentinel",
            includeVector: false, cancellationToken: cancellationToken);
        var broadRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(database.Paths.RootDirectory));
        var broadKey = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? broadRoot.ToUpperInvariant()
            : broadRoot;
        await using (var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath}"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE project_folders SET path=$path,path_key=$key WHERE id=$folder;";
            update.Parameters.AddWithValue("$path", broadRoot);
            update.Parameters.AddWithValue("$key", broadKey);
            update.Parameters.AddWithValue("$folder", folderId.ToString());
            Assert.Equal(1, await update.ExecuteNonQueryAsync(cancellationToken));
        }

        var extractor = new RecordingTextExtractor();
        var embeddings = new StorageUnavailableEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths, extractor,
            embeddings, new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(), budget,
            NullLogger<IndexingCoordinator>.Instance);
        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(async () =>
            {
                var ordinary = await database.Store.KeywordSearchAsync(projectId,
                    TextNormalization.QuoteFtsTerms("ordinarypublicsentinel"), 10, null, cancellationToken);
                var project = (await database.Store.ListProjectsAsync(cancellationToken))
                    .Single(item => item.Id == projectId);
                return ordinary.Candidates.Count > 0 && project is { DocumentCount: 1, PendingCount: 0 };
            }, cancellationToken);

            Assert.Empty((await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("privatelegacysentinel"), 10, null, cancellationToken)).Candidates);
            Assert.DoesNotContain(extractor.Paths, path => PathsEqual(path, legacyDataSource));

            var watchedPrivate = Path.Combine(database.Paths.DataDirectory, "watched-private.txt");
            var watchedPublic = Path.Combine(database.Paths.RootDirectory, "watched-public.txt");
            await File.WriteAllTextAsync(watchedPrivate, "privatewatchersentinel", cancellationToken);
            await File.WriteAllTextAsync(watchedPublic, "publicwatchersentinel", cancellationToken);
            var watchedPrivateHash = await StorageTestDatabase.HashAsync(watchedPrivate, cancellationToken);
            var watchedPublicHash = await StorageTestDatabase.HashAsync(watchedPublic, cancellationToken);
            var privateInfo = new FileInfo(watchedPrivate);
            await database.Writer.ObserveFileAsync(new FileObservation(projectId, folderId, watchedPrivate,
                privateInfo.Length, new DateTimeOffset(privateInfo.LastWriteTimeUtc, TimeSpan.Zero), Force: true),
                cancellationToken);
            await WaitUntilAsync(async () =>
            {
                var found = await database.Store.KeywordSearchAsync(projectId,
                    TextNormalization.QuoteFtsTerms("publicwatchersentinel"), 10, null, cancellationToken);
                var current = (await database.Store.ListProjectsAsync(cancellationToken))
                    .Single(item => item.Id == projectId);
                return found.Candidates.Count > 0 && current.PendingCount == 0;
            }, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            Assert.Empty((await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("privatewatchersentinel"), 10, null, cancellationToken)).Candidates);
            Assert.DoesNotContain(extractor.Paths, path => PathsEqual(path, watchedPrivate));
            var project = (await database.Store.ListProjectsAsync(cancellationToken))
                .Single(item => item.Id == projectId);
            Assert.Equal(2, project.DocumentCount);
            Assert.Equal(legacyHash, await StorageTestDatabase.HashAsync(legacyDataSource, cancellationToken));
            Assert.Equal(ordinaryHash, await StorageTestDatabase.HashAsync(ordinarySource, cancellationToken));
            Assert.Equal(watchedPrivateHash,
                await StorageTestDatabase.HashAsync(watchedPrivate, cancellationToken));
            Assert.Equal(watchedPublicHash,
                await StorageTestDatabase.HashAsync(watchedPublic, cancellationToken));
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }

        static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left),
            Path.GetFullPath(right), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForcedReindexPreservesPublicIdsAndFocusedAccessUsesTheNewActiveRevision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "stable.txt");
        await File.WriteAllTextAsync(source, "stable identity evidence", cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Stable public IDs", cancellationToken);

        var extractor = new CountingTextExtractor();
        var embeddings = new StorageUnavailableEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths, extractor, embeddings,
            new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(), budget,
            NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(async () =>
                (await database.Store.ListProjectsAsync(cancellationToken)).Single(item => item.Id == projectId)
                is { IndexedCount: 1, PendingCount: 0 } && extractor.CallCount == 1, cancellationToken);

            var first = Assert.Single((await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("stable identity evidence"), 10, null, cancellationToken)).Candidates);
            var firstDocument = await database.Store.GetDocumentInfoAsync(projectId, first.DocumentId, null,
                cancellationToken);
            Assert.NotNull(firstDocument?.ActiveRevisionId);

            await database.Writer.RequestReindexAsync(projectId, cancellationToken);
            await WaitUntilAsync(async () =>
            {
                var project = (await database.Store.ListProjectsAsync(cancellationToken))
                    .Single(item => item.Id == projectId);
                var current = await database.Store.GetDocumentInfoAsync(projectId, first.DocumentId, null,
                    cancellationToken);
                return project.PendingCount == 0 && extractor.CallCount >= 2 &&
                       current?.ActiveRevisionId != firstDocument.ActiveRevisionId;
            }, cancellationToken);

            var second = Assert.Single((await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("stable identity evidence"), 10, null, cancellationToken)).Candidates);
            Assert.Equal(first.DocumentId, second.DocumentId);
            Assert.Equal(first.ContentId, second.ContentId);
            Assert.Equal(first.PassageId, second.PassageId);

            var focused = Assert.Single((await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("stable identity evidence"), 10,
                new SearchFilters(ContentIds: [first.ContentId]), cancellationToken)).Candidates);
            Assert.Equal(first.PassageId, focused.PassageId);
            var passage = Assert.Single(await database.Store.ReadPassagesAsync(projectId, [first.PassageId], 0, 0,
                cancellationToken));
            Assert.Equal(first.ContentId, passage.ContentId);

            var materializer = new ContentMaterializationService(database.Store, database.Paths, budget);
            var materialized = await materializer.MaterializeAsync(projectId, first.ContentId, cancellationToken);
            Assert.Equal(Path.GetFullPath(source), materialized.LocalPath);
            Assert.False(materialized.Temporary);
            Assert.NotEqual(firstDocument.ActiveRevisionId!.Value, materialized.IndexRevisionId);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task InsertingADifferentEarlierAttachmentDoesNotTransferSiblingContentIds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "container.txt");
        await File.WriteAllTextAsync(source, "root evidence", cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Stable attachment identities", cancellationToken);

        var extractor = new MutableAttachmentExtractor();
        var embeddings = new StorageUnavailableEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths, extractor, embeddings,
            new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(), budget,
            NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(async () =>
                (await database.Store.ListProjectsAsync(cancellationToken)).Single(item => item.Id == projectId)
                is { IndexedCount: 1, PendingCount: 0 } && extractor.CallCount >= 1, cancellationToken);
            var alphaBefore = Assert.Single((await database.Store.KeywordSearchAsync(projectId,
                    TextNormalization.QuoteFtsTerms("alpha unique evidence"), 10, null, cancellationToken)).Candidates,
                item => item.ContentName == "alpha.txt");
            var betaBefore = Assert.Single((await database.Store.KeywordSearchAsync(projectId,
                    TextNormalization.QuoteFtsTerms("beta unique evidence"), 10, null, cancellationToken)).Candidates,
                item => item.ContentName == "beta.txt");

            extractor.IncludePrecedingAttachment = true;
            await database.Writer.RequestReindexAsync(projectId, cancellationToken);
            await WaitUntilAsync(async () =>
                (await database.Store.ListProjectsAsync(cancellationToken)).Single(item => item.Id == projectId)
                is { PendingCount: 0 } && extractor.CallCount >= 2, cancellationToken);

            var inserted = Assert.Single((await database.Store.KeywordSearchAsync(projectId,
                    TextNormalization.QuoteFtsTerms("inserted unique evidence"), 10, null, cancellationToken)).Candidates,
                item => item.ContentName == "inserted.txt");
            var alphaAfter = Assert.Single((await database.Store.KeywordSearchAsync(projectId,
                    TextNormalization.QuoteFtsTerms("alpha unique evidence"), 10, null, cancellationToken)).Candidates,
                item => item.ContentName == "alpha.txt");
            var betaAfter = Assert.Single((await database.Store.KeywordSearchAsync(projectId,
                    TextNormalization.QuoteFtsTerms("beta unique evidence"), 10, null, cancellationToken)).Candidates,
                item => item.ContentName == "beta.txt");

            Assert.Equal(alphaBefore.ContentId, alphaAfter.ContentId);
            Assert.Equal(alphaBefore.PassageId, alphaAfter.PassageId);
            Assert.Equal(betaBefore.ContentId, betaAfter.ContentId);
            Assert.Equal(betaBefore.PassageId, betaAfter.PassageId);
            Assert.NotEqual(inserted.ContentId, alphaAfter.ContentId);
            Assert.NotEqual(inserted.ContentId, betaAfter.ContentId);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task ChunkingCarriesEachTableHeaderWithoutMergingRowsOrCrossingStructuralBoundaries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "boundaries.xlsx");
        await File.WriteAllBytesAsync(source, [1, 2, 3], cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Contextual boundaries", cancellationToken);

        var sections = new ExtractedSection[]
        {
            new("A1: Name\tB1: Amount", new SourceLocation(LocationKind.Sheet, Sheet: "People",
                CellRange: "A1:B1"), ExtractionMethod.NativeText),
            new("A2: Ada\tB2: 42", new SourceLocation(LocationKind.Sheet, Sheet: "People",
                CellRange: "A2:B2"), ExtractionMethod.NativeText),
            new("A1: Code\tB1: State", new SourceLocation(LocationKind.Sheet, Sheet: "Status",
                CellRange: "A1:B1"), ExtractionMethod.NativeText),
            new("A2: X7\tB2: Active", new SourceLocation(LocationKind.Sheet, Sheet: "Status",
                CellRange: "A2:B2"), ExtractionMethod.NativeText),
            new("First paragraph", new SourceLocation(LocationKind.Structure,
                StructurePath: "document/paragraph[1]"), ExtractionMethod.NativeText, Heading: "Alpha"),
            new("Second paragraph", new SourceLocation(LocationKind.Structure,
                StructurePath: "document/paragraph[2]"), ExtractionMethod.NativeText, Heading: "Alpha"),
            new("Third paragraph", new SourceLocation(LocationKind.Structure,
                StructurePath: "document/paragraph[3]"), ExtractionMethod.NativeText, Heading: "Beta"),
            new("Column A\tColumn B\nAda\t42", new SourceLocation(LocationKind.Structure,
                StructurePath: "document/table[1]"), ExtractionMethod.NativeText, Heading: "Beta"),
            new("Slide body", new SourceLocation(LocationKind.Slide, Slide: 1), ExtractionMethod.NativeText),
            new("Speaker notes", new SourceLocation(LocationKind.Slide, Slide: 1, StructurePath: "notes"),
                ExtractionMethod.NativeText),
            new("Header part", new SourceLocation(LocationKind.EmailPart, EmailPart: "headers"),
                ExtractionMethod.Email),
            new("Body part", new SourceLocation(LocationKind.EmailPart, EmailPart: "body"),
                ExtractionMethod.Email)
        };
        var root = new ExtractedNode("boundaries.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "root", sections,
            [new ExtractedNode("nested.txt", "text/plain", "attachment",
                [new ExtractedSection("Nested evidence", new SourceLocation(LocationKind.Document),
                    ExtractionMethod.Attachment)], [])]);
        var extractor = new FixedResultExtractor(new ExtractionResult(root, []));
        var embeddings = new StorageUnavailableEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths, extractor, embeddings,
            new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(), budget,
            NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(async () =>
                (await database.Store.ListProjectsAsync(cancellationToken)).Single(item => item.Id == projectId)
                is { IndexedCount: 1, PendingCount: 0 }, cancellationToken);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }

        var stored = new List<StoredPassage>();
        await using (var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath};Mode=ReadOnly"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT p.display_text,p.search_text,p.sheet,p.cell_range,p.structure_path,p.email_part,n.depth
                FROM passages p JOIN content_nodes n ON n.id=p.content_id
                JOIN documents d ON d.active_revision_id=p.revision_id
                WHERE d.project_id=$project ORDER BY n.depth,p.ordinal;
                """;
            command.Parameters.AddWithValue("$project", projectId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                stored.Add(new StoredPassage(reader.GetString(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6)));
        }

        var people = stored.Where(item => item.Sheet == "People").ToArray();
        Assert.Equal(2, people.Length);
        Assert.Equal(new string?[] { "A1:B1", "A2:B2" }, people.Select(item => item.CellRange).ToArray());
        Assert.All(people, item => Assert.Contains("Table headers: Name | Amount", item.SearchText));
        Assert.DoesNotContain("Table headers: Ada | 42", people[1].SearchText);
        Assert.All(people, item => Assert.DoesNotContain("Table headers:", item.DisplayText));

        var status = stored.Where(item => item.Sheet == "Status").ToArray();
        Assert.Equal(2, status.Length);
        Assert.All(status, item => Assert.Contains("Table headers: Code | State", item.SearchText));
        Assert.DoesNotContain(status, item => item.SearchText.Contains("Name | Amount", StringComparison.Ordinal));

        var alpha = Assert.Single(stored, item => item.DisplayText.Contains("First paragraph",
            StringComparison.Ordinal));
        Assert.Equal("First paragraph Second paragraph", alpha.DisplayText);
        Assert.Equal("document/paragraph[1]..document/paragraph[2]", alpha.StructurePath);
        Assert.Single(stored, item => item.DisplayText == "Third paragraph");
        var wordTable = Assert.Single(stored, item => item.StructurePath == "document/table[1]");
        Assert.Contains("Table headers: Column A | Column B", wordTable.SearchText);
        Assert.DoesNotContain("Table headers:", wordTable.DisplayText);
        Assert.Single(stored, item => item.DisplayText == "Slide body");
        Assert.Single(stored, item => item.DisplayText == "Speaker notes");
        Assert.Equal(2, stored.Count(item => item.EmailPart is not null));
        Assert.Single(stored, item => item.Depth == 1 && item.DisplayText == "Nested evidence");
    }

    [Fact]
    public async Task CoordinatorDiscoversExtractsAndIndexesAFileWithoutChangingIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "pipeline.txt");
        const string evidence = "Pipeline evidence with café and contrato.";
        await File.WriteAllTextAsync(source, evidence, cancellationToken);
        var originalHash = await StorageTestDatabase.HashAsync(source, cancellationToken);
        var originalModified = File.GetLastWriteTimeUtc(source);
        var (projectId, _) = await database.CreateProjectAsync("Pipeline", cancellationToken);

        var embeddings = new StorageUnavailableEmbeddings();
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        var activities = new IndexingActivityTracker();
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths,
            new DocumentExtractionRegistry(new StorageNoOcr()), embeddings, activities,
            new EmbeddingPolicyRefreshTracker(), budget, NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(async () =>
            {
                var project = (await database.Store.ListProjectsAsync(cancellationToken))
                    .Single(item => item.Id == projectId);
                return project is { IndexedCount: 1, PendingCount: 0 };
            }, cancellationToken);

            var result = await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("café contrato"), 10, null, cancellationToken);
            Assert.Equal(Path.GetFullPath(source), Assert.Single(result.Candidates).SourcePath);
            Assert.Equal(originalHash, await StorageTestDatabase.HashAsync(source, cancellationToken));
            Assert.Equal(originalModified, File.GetLastWriteTimeUtc(source));
            var timing = activities.GetSnapshot(projectId);
            Assert.Empty(timing.ActiveItems);
            Assert.Equal(1, timing.CompletedSampleCount);
            Assert.NotNull(timing.AverageCompletedDuration);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }
    }

    [Fact]
    public async Task CoordinatorRefreshesEmbeddingsFromStorageWithoutRunningExtraction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Stored embedding refresh", cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "stored.txt");
        await File.WriteAllTextAsync(source, "Persisted passage text.", cancellationToken);
        var pending = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var modified = new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero);
        var committed = await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length, modified,
            "Persisted passage text.", cancellationToken: cancellationToken);

        var targetPolicy = StorageTestDatabase.TestEmbeddingPolicy with { ModelId = "coordinator-target" };
        await database.Writer.RequestEmbeddingRefreshAsync(projectId, targetPolicy, retryFailed: false,
            cancellationToken);
        var extractor = new RejectingExtractor();
        var embeddings = new FixedEmbeddingGenerator(targetPolicy);
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        using var coordinator = new IndexingCoordinator(database.Writer, database.Store, database.Paths, extractor, embeddings,
            new IndexingActivityTracker(), new EmbeddingPolicyRefreshTracker(), budget,
            NullLogger<IndexingCoordinator>.Instance);

        await coordinator.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(async () =>
            {
                var project = (await database.Store.ListProjectsAsync(cancellationToken))
                    .Single(item => item.Id == projectId);
                var metadata = await database.Store.LoadVectorSnapshotMetadataAsync(projectId, cancellationToken);
                return project.PendingCount == 0 && metadata.IsComplete && metadata.Policy?.Key == targetPolicy.Key;
            }, cancellationToken);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            await embeddings.DisposeAsync();
        }

        Assert.Equal(0, extractor.CallCount);
        var document = await database.Store.GetDocumentInfoAsync(projectId, committed.DocumentId, null,
            cancellationToken);
        Assert.Equal(committed.RevisionId, document?.ActiveRevisionId);
        var passage = Assert.Single(await database.Store.ReadPassagesAsync(projectId, [committed.PassageId], 0, 0,
            cancellationToken));
        Assert.Equal("Persisted passage text.", passage.Text);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await condition())
            await Task.Delay(50, timeout.Token);
    }

    private sealed class RejectingExtractor : IDocumentExtractor
    {
        public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;
        public int CallCount { get; private set; }

        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Embedding refresh must not invoke document extraction.");
        }
    }

    private sealed class CountingTextExtractor : IDocumentExtractor
    {
        private int _callCount;
        public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;
        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var text = await File.ReadAllTextAsync(request.SourcePath, cancellationToken);
            return new ExtractionResult(new ExtractedNode(Path.GetFileName(request.SourcePath), "text/plain", "root",
                [new ExtractedSection(text, new SourceLocation(LocationKind.Document), ExtractionMethod.NativeText)], []), []);
        }
    }

    private sealed class FixedResultExtractor(ExtractionResult result) : IDocumentExtractor
    {
        public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;
        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class MutableAttachmentExtractor : IDocumentExtractor
    {
        private int _callCount;
        private int _includePreceding;
        public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;
        public int CallCount => Volatile.Read(ref _callCount);
        public bool IncludePrecedingAttachment
        {
            get => Volatile.Read(ref _includePreceding) != 0;
            set => Volatile.Write(ref _includePreceding, value ? 1 : 0);
        }

        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var attachments = new List<ExtractedNode>();
            if (IncludePrecedingAttachment) attachments.Add(Attachment("inserted.txt", "inserted unique evidence"));
            attachments.Add(Attachment("alpha.txt", "alpha unique evidence"));
            attachments.Add(Attachment("beta.txt", "beta unique evidence"));
            var root = new ExtractedNode(Path.GetFileName(request.SourcePath), "text/plain", "root",
                [new ExtractedSection("root evidence", new SourceLocation(LocationKind.Document),
                    ExtractionMethod.NativeText)], attachments);
            return Task.FromResult(new ExtractionResult(root, []));
        }

        private static ExtractedNode Attachment(string name, string text) =>
            new(name, "text/plain", "attachment",
                [new ExtractedSection(text, new SourceLocation(LocationKind.Document),
                    ExtractionMethod.Attachment)], []);
    }

    private sealed class RecordingTextExtractor : IDocumentExtractor
    {
        private readonly object _gate = new();
        private readonly List<string> _paths = [];
        public IReadOnlyCollection<string> Extensions => SupportedContent.Extensions;
        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_gate) return _paths.ToArray();
            }
        }

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request,
            CancellationToken cancellationToken)
        {
            lock (_gate) _paths.Add(Path.GetFullPath(request.SourcePath));
            var text = await File.ReadAllTextAsync(request.SourcePath, cancellationToken);
            return new ExtractionResult(new ExtractedNode(Path.GetFileName(request.SourcePath), "text/plain", "root",
                [new ExtractedSection(text, new SourceLocation(LocationKind.Document),
                    ExtractionMethod.NativeText)], []), []);
        }
    }

    private sealed record StoredPassage(string DisplayText, string SearchText, string? Sheet, string? CellRange,
        string? StructurePath, string? EmailPart, int Depth);

    private sealed class FixedEmbeddingGenerator(EmbeddingPolicy policy) : IEmbeddingGenerator
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public EmbeddingPolicy Policy { get; } = policy;
        EmbeddingPolicy? IEmbeddingGenerator.Policy => Policy;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public int CountTokens(string text) => text.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries).Length;

        public Task<EmbeddingBatch> EmbedPassagesAsync(IReadOnlyList<string> passages,
            CancellationToken cancellationToken) => Task.FromResult(new EmbeddingBatch(
            passages.Select((_, index) => StorageTestDatabase.TestVector(index % 384)).ToArray(), Policy));

        public Task<QueryEmbedding> EmbedQueryAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult(new QueryEmbedding(StorageTestDatabase.TestVector(), Policy));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class IndexingStateTests
{
    [Fact]
    public void ActivityTrackerReportsStagesAndAveragesOnlySuccessfulCompletions()
    {
        var tracker = new IndexingActivityTracker();
        var projectId = Guid.CreateVersion7();
        var first = NewJob(projectId, "first.txt");
        using (var activity = tracker.Start(first))
        {
            activity.SetStage(IndexingPipelineStage.GeneratingEmbeddings);
            var active = Assert.Single(tracker.GetSnapshot(projectId).ActiveItems);
            Assert.Equal(IndexingPipelineStage.GeneratingEmbeddings, active.Stage);
            activity.Complete(includeInAverage: true);
        }

        using (tracker.Start(NewJob(projectId, "failed.txt")))
        {
        }

        var completed = tracker.GetSnapshot(projectId);
        Assert.Empty(completed.ActiveItems);
        Assert.Equal(1, completed.CompletedSampleCount);
        Assert.NotNull(completed.AverageCompletedDuration);
        Assert.False(tracker.HasActiveItems);
    }

    [Fact]
    public async Task EmbeddingPolicyRefreshIsPerPolicyRetryableAndSerialized()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tracker = new EmbeddingPolicyRefreshTracker();
        var projectId = Guid.CreateVersion7();
        Assert.True(tracker.TryBeginRefresh(projectId, "policy-a"));
        Assert.False(tracker.TryBeginRefresh(projectId, "policy-a"));
        Assert.True(tracker.TryBeginRefresh(projectId, "policy-b"));
        tracker.CancelRefresh(projectId, "policy-b");
        Assert.True(tracker.TryBeginRefresh(projectId, "policy-b"));
        Assert.True(tracker.IsRefreshPending(projectId, "policy-b"));
        tracker.Clear();
        Assert.False(tracker.IsRefreshPending(projectId, "policy-b"));
        Assert.True(tracker.TryBeginRefresh(projectId, "policy-b"));

        var active = 0;
        var maximum = 0;
        var maximumGate = new object();
        async Task Work()
        {
            await tracker.RunExclusiveAsync(async () =>
            {
                var current = Interlocked.Increment(ref active);
                lock (maximumGate)
                    maximum = Math.Max(maximum, current);
                try
                {
                    await Task.Delay(40, cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }, cancellationToken);
        }

        await Task.WhenAll(Work(), Work(), Work());
        Assert.Equal(1, maximum);
    }

    private static IndexJobLease NewJob(Guid projectId, string path) =>
        new(Guid.CreateVersion7(), projectId, Guid.CreateVersion7(), Guid.CreateVersion7(), path, ".txt", 1,
            IndexJobKind.Index, 0);
}

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class SearchBoundaryTests
{
    [Fact]
    public async Task InvalidSearchInputIsRejectedBeforeSearchExecution()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("MCP boundary", cancellationToken);
        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        var search = new HybridSearchService(database.Store, new StorageUnavailableEmbeddings(),
            new FlatVectorIndexFactory(), new VectorIndexCache(), budget);

        var empty = await Assert.ThrowsAsync<ContextMoleException>(() => search.SearchAsync(
            new SearchRequest(projectId, SearchMode.Semantic, "   "), cancellationToken));
        Assert.Equal("invalid_request", empty.Code);
        var now = DateTimeOffset.UtcNow;
        var invalidRange = await Assert.ThrowsAsync<ContextMoleException>(() => search.SearchAsync(
            new SearchRequest(projectId, SearchMode.Keyword,
                Clauses: [new SearchClause("query", "query")],
                Filters: new SearchFilters(ModifiedFromUtc: now, ModifiedToUtc: now.AddDays(-1))),
            cancellationToken));
        Assert.Equal("invalid_filter", invalidRange.Code);
    }
}
