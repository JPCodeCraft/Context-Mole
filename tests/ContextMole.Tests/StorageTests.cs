using System.Text;

using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Infrastructure;
using ContextMole.Search;
using ContextMole.Storage;

using Microsoft.Data.Sqlite;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class StorageTests
{
    [Fact]
    public async Task ProjectFoldersCannotContainOrSitInsideApplicationData()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var parent = Directory.GetParent(database.Paths.DataDirectory)!.FullName;

        var ancestor = await Assert.ThrowsAsync<ContextMoleException>(() =>
            database.Writer.CreateProjectAsync(new CreateProjectRequest("Unsafe ancestor", [parent]),
                cancellationToken));
        Assert.Equal("unsafe_folder", ancestor.Code);

        var descendant = Path.Combine(database.Paths.DataDirectory, "nested-source");
        Directory.CreateDirectory(descendant);
        var child = await Assert.ThrowsAsync<ContextMoleException>(() =>
            database.Writer.CreateProjectAsync(new CreateProjectRequest("Unsafe child", [descendant]),
                cancellationToken));
        Assert.Equal("unsafe_folder", child.Code);
    }

    [Fact]
    public async Task MultiColumnFtsHonorsAgentFieldWeightOverrides()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Weighted metadata", cancellationToken);

        var bodySource = Path.Combine(database.Paths.SourceDirectory, "body-only.txt");
        await File.WriteAllTextAsync(bodySource, "needle appears in the body", cancellationToken);
        var bodyPending = await database.ObserveAndLeaseAsync(projectId, folderId, bodySource, false, cancellationToken);
        var bodyDocument = await database.CommitAsync(bodyPending.Job, bodyPending.Sha256, bodyPending.File.Length,
            new DateTimeOffset(bodyPending.File.LastWriteTimeUtc, TimeSpan.Zero), "needle appears in the body",
            includeVector: false, cancellationToken: cancellationToken);

        var nameSource = Path.Combine(database.Paths.SourceDirectory, "needle-report.txt");
        await File.WriteAllTextAsync(nameSource, "unrelated content", cancellationToken);
        var namePending = await database.ObserveAndLeaseAsync(projectId, folderId, nameSource, false, cancellationToken);
        var nameDocument = await database.CommitAsync(namePending.Job, namePending.Sha256, namePending.File.Length,
            new DateTimeOffset(namePending.File.LastWriteTimeUtc, TimeSpan.Zero), "unrelated content",
            includeVector: false, cancellationToken: cancellationToken);

        var query = StructuredSearchQuery.BuildFtsQuery([new SearchClause("needle", "needle")], 1);
        var defaults = await database.Store.KeywordSearchAsync(projectId, query, 10, null,
            new SearchFieldWeights(), cancellationToken);
        Assert.Equal(nameDocument.DocumentId, defaults.Candidates[0].DocumentId);

        var bodyOnly = await database.Store.KeywordSearchAsync(projectId, query, 10, null,
            new SearchFieldWeights(Body: 10, Title: 0, Heading: 0, Filename: 0, Path: 0, ContentName: 0,
                Sheet: 0, EmailSubject: 0), cancellationToken);
        Assert.Equal(bodyDocument.DocumentId, bodyOnly.Candidates[0].DocumentId);
    }

    [Fact]
    public async Task KeywordAndSemanticDateFiltersCompareTheSameInstantAcrossOffsets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Offset filters", cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "offset.txt");
        await File.WriteAllTextAsync(source, "offset evidence", cancellationToken);
        var pending = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var modified = new DateTimeOffset(2026, 1, 1, 0, 30, 0, TimeSpan.Zero);
        var document = await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length, modified,
            "offset evidence", includeVector: false, cancellationToken: cancellationToken);
        var upperBoundWithOffset = new DateTimeOffset(2025, 12, 31, 22, 0, 0, TimeSpan.FromHours(-3));
        var filters = new SearchFilters(ModifiedToUtc: upperBoundWithOffset);
        var query = StructuredSearchQuery.BuildFtsQuery([new SearchClause("offset", "offset")], 1);

        var keyword = await database.Store.KeywordSearchAsync(projectId, query, 10, filters, cancellationToken);
        Assert.Equal(document.PassageId, Assert.Single(keyword.Candidates).PassageId);

        var vectorEntry = new VectorEntry(document.PassageId, document.DocumentId, document.ContentId,
            source, ".txt", modified, false, StorageTestDatabase.TestVector());
        var semantic = new FlatVectorIndex(new VectorSnapshot(1, StorageTestDatabase.TestEmbeddingPolicy,
            [vectorEntry])).Search(StorageTestDatabase.TestVector(), 10, filters);
        Assert.Equal(document.PassageId, Assert.Single(semantic).PassageId);
    }

    [Fact]
    public async Task VersionSixDerivedIndexIsDiscardedAndRebuiltWithoutTouchingSources()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new StorageTestPaths();
        var source = Path.Combine(paths.SourceDirectory, "legacy.txt");
        await File.WriteAllTextAsync(source, "legacy evidence remains", cancellationToken);
        var projectId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var passageId = Guid.NewGuid();

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString()))
        {
            await connection.OpenAsync(cancellationToken);
            var assembly = typeof(SqliteSearchStore).Assembly;
            await using (var migrations = connection.CreateCommand())
            {
                migrations.CommandText = "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);";
                await migrations.ExecuteNonQueryAsync(cancellationToken);
            }
            for (var version = 1; version <= 6; version++)
            {
                var marker = $".Migrations.{version:000}_";
                var resource = assembly.GetManifestResourceNames().Single(name => name.Contains(marker, StringComparison.Ordinal));
                await using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                await using var migration = connection.CreateCommand();
                migration.CommandText = await reader.ReadToEndAsync(cancellationToken);
                await migration.ExecuteNonQueryAsync(cancellationToken);
                await using var record = connection.CreateCommand();
                record.CommandText = "INSERT INTO schema_migrations(version,applied_utc) VALUES($version,$now);";
                record.Parameters.AddWithValue("$version", version);
                record.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await record.ExecuteNonQueryAsync(cancellationToken);
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO projects(id,name,name_key,state,search_generation,created_utc,updated_utc)
                  VALUES($project,'Legacy','LEGACY',0,1,$now,$now);
                INSERT INTO project_folders(id,project_id,path,path_key,created_utc)
                  VALUES($folder,$project,$folder_path,$folder_path,$now);
                INSERT INTO documents(id,project_id,folder_id,path,path_key,file_name,extension,size,modified_utc,
                  observation_epoch,tombstoned,available,created_utc,updated_utc)
                  VALUES($document,$project,$folder,$path,$path,'legacy.txt','.txt',23,$now,1,0,1,$now,$now);
                INSERT INTO document_revisions(id,document_id,sha256,status,embedding_policy_json,created_utc,activated_utc)
                  VALUES($revision,$document,'legacy-sha','active','{}',$now,$now);
                UPDATE documents SET active_revision_id=$revision,sha256='legacy-sha' WHERE id=$document;
                INSERT INTO content_nodes(id,revision_id,ordinal,name,relationship,depth,status)
                  VALUES($content,$revision,0,'legacy.txt','root',0,'indexed');
                INSERT INTO passages(id,revision_id,content_id,ordinal,display_text,search_text,location_kind,
                  extraction_method) VALUES($passage,$revision,$content,0,'legacy evidence remains',
                  'legacy evidence remains',0,0);
                INSERT INTO passages_fts(rowid,search_text)
                  SELECT rowid,search_text FROM passages WHERE id=$passage;
                INSERT INTO embeddings(passage_rowid,passage_id,revision_id,vector,policy_key)
                  SELECT rowid,$passage,$revision,$vector,'legacy-policy' FROM passages WHERE id=$passage;
                """;
            seed.Parameters.AddWithValue("$project", projectId.ToString());
            seed.Parameters.AddWithValue("$folder", folderId.ToString());
            seed.Parameters.AddWithValue("$document", documentId.ToString());
            seed.Parameters.AddWithValue("$revision", revisionId.ToString());
            seed.Parameters.AddWithValue("$content", contentId.ToString());
            seed.Parameters.AddWithValue("$passage", passageId.ToString());
            seed.Parameters.AddWithValue("$folder_path", paths.SourceDirectory);
            seed.Parameters.AddWithValue("$path", source);
            seed.Parameters.AddWithValue("$now", now);
            seed.Parameters.AddWithValue("$vector", new byte[1536]);
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        var writer = new DatabaseWriterService(paths);
        await writer.StartAsync(cancellationToken);
        await writer.Ready.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        try
        {
            var store = new SqliteSearchStore(paths);
            var query = StructuredSearchQuery.BuildFtsQuery([new SearchClause("legacy", "legacy")], 1);
            Assert.Empty((await store.KeywordSearchAsync(projectId, query, 10, null,
                cancellationToken)).Candidates);
            Assert.True(File.Exists(source));
            Assert.Equal("legacy evidence remains", await File.ReadAllTextAsync(source, cancellationToken));
            var project = Assert.Single(await store.ListProjectsAsync(cancellationToken));
            Assert.Equal(1, project.PendingCount);
            Assert.Equal(0, (await store.LoadVectorSnapshotMetadataAsync(projectId, cancellationToken)).EntryCount);

            await using var verify = new SqliteConnection($"Data Source={paths.DatabasePath};Mode=ReadOnly");
            await verify.OpenAsync(cancellationToken);
            await using var schema = verify.CreateCommand();
            schema.CommandText = "SELECT MAX(version) FROM schema_migrations;";
            Assert.Equal(7L, Convert.ToInt64(await schema.ExecuteScalarAsync(cancellationToken)));
            await using var derivedRows = verify.CreateCommand();
            derivedRows.CommandText = "SELECT (SELECT COUNT(*) FROM document_revisions),(SELECT COUNT(*) FROM passages),(SELECT COUNT(*) FROM embeddings);";
            await using var derivedReader = await derivedRows.ExecuteReaderAsync(cancellationToken);
            Assert.True(await derivedReader.ReadAsync(cancellationToken));
            Assert.Equal(0, derivedReader.GetInt32(0));
            Assert.Equal(0, derivedReader.GetInt32(1));
            Assert.Equal(0, derivedReader.GetInt32(2));
            await using var job = verify.CreateCommand();
            job.CommandText = "SELECT kind,state FROM index_jobs WHERE document_id=$document;";
            job.Parameters.AddWithValue("$document", documentId.ToString());
            await using var jobReader = await job.ExecuteReaderAsync(cancellationToken);
            Assert.True(await jobReader.ReadAsync(cancellationToken));
            Assert.Equal((int)IndexJobKind.Reindex, jobReader.GetInt32(0));
            Assert.Equal("queued", jobReader.GetString(1));
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
            writer.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CommittedRevisionIsSearchableAndRenamePreservesDocumentIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Lifecycle", cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "contract.txt");
        await File.WriteAllTextAsync(source, "Contrato café with exact provenance.", cancellationToken);

        var pending = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var modified = new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero);
        var committed = await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length, modified,
            "Contrato café with exact provenance.", cancellationToken: cancellationToken);

        var project = (await database.Store.ListProjectsAsync(cancellationToken)).Single(item => item.Id == projectId);
        Assert.Equal(1, project.IndexedCount);
        Assert.Equal(0, project.PendingCount);
        Assert.True(project.SearchGeneration > 0);

        var keyword = await database.Store.KeywordSearchAsync(projectId,
            TextNormalization.QuoteFtsTerms("contrato café"), 10, null, cancellationToken);
        var match = Assert.Single(keyword.Candidates);
        Assert.Equal(committed.DocumentId, match.DocumentId);
        Assert.Equal(Path.GetFullPath(source), match.SourcePath);

        var info = await database.Store.GetDocumentInfoAsync(projectId, committed.DocumentId, null, cancellationToken);
        Assert.NotNull(info);
        Assert.True(info.Searchable);
        Assert.Equal(pending.Sha256, info.Sha256);
        Assert.Equal(1, info.PassageCount);
        var resolved = await database.Store.ResolveLocalFileAsync(projectId, committed.DocumentId, null,
            cancellationToken);
        Assert.Equal(Path.GetFullPath(source), resolved?.SourcePath);

        var renamed = Path.Combine(database.Paths.SourceDirectory, "renamed-contract.txt");
        File.Move(source, renamed);
        await database.Writer.HandleRenamedAsync(projectId, folderId, source, renamed, cancellationToken);
        var afterRename = await database.Store.GetDocumentInfoAsync(projectId, committed.DocumentId, null,
            cancellationToken);
        Assert.Equal(Path.GetFullPath(renamed), afterRename?.SourcePath);
        Assert.Equal(committed.DocumentId,
            Assert.Single((await database.Store.KeywordSearchAsync(projectId,
                TextNormalization.QuoteFtsTerms("contrato"), 10, null, cancellationToken)).Candidates).DocumentId);

        File.Delete(renamed);
        await database.Writer.HandleDeletedAsync(projectId, folderId, renamed, cancellationToken);
        project = (await database.Store.ListProjectsAsync(cancellationToken)).Single(item => item.Id == projectId);
        Assert.Equal(0, project.DocumentCount);
        Assert.Empty((await database.Store.KeywordSearchAsync(projectId,
            TextNormalization.QuoteFtsTerms("contrato"), 10, null, cancellationToken)).Candidates);
    }

    [Fact]
    public async Task RenameQueuesFullReindexAndAtomicallyReusesStableIdsWithFreshSemanticMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Rename semantic refresh", cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "old-name.txt");
        await File.WriteAllTextAsync(source, "rename metadata evidence", cancellationToken);

        var pending = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var contentId = Guid.NewGuid();
        var passageId = Guid.NewGuid();
        var initialNodes = new ContentNodeDraft[]
        {
            new(contentId, null, 0, "old-name.txt", "text/plain", "root", 0)
        };
        var initialPassages = new PassageDraft[]
        {
            new(passageId, contentId, 0, "rename metadata evidence",
                $"Title: old-name.txt\nPath: {source}\nBody: rename metadata evidence",
                new SourceLocation(LocationKind.Document), ExtractionMethod.NativeText, null,
                StorageTestDatabase.TestVector(), "rename metadata evidence", "old-name.txt",
                FileName: "old-name.txt", SourcePath: source, ContentName: "old-name.txt")
        };
        var first = await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length,
            new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero), "unused", nodes: initialNodes,
            passages: initialPassages, cancellationToken: cancellationToken);

        var renamed = Path.Combine(database.Paths.SourceDirectory, "new-name.txt");
        File.Move(source, renamed);
        await database.Writer.HandleRenamedAsync(projectId, folderId, source, renamed, cancellationToken);
        var reindex = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(reindex);
        Assert.Equal(IndexJobKind.Reindex, reindex.Kind);
        Assert.Equal(first.DocumentId, reindex.DocumentId);

        var renamedFile = new FileInfo(renamed);
        var renamedPath = Path.GetFullPath(renamed);
        var replacementNodes = new ContentNodeDraft[]
        {
            new(contentId, null, 0, "new-name.txt", "text/plain", "root", 0)
        };
        var replacementPassages = new PassageDraft[]
        {
            new(passageId, contentId, 0, "rename metadata evidence",
                $"Title: new-name.txt\nPath: {renamedPath}\nBody: rename metadata evidence",
                new SourceLocation(LocationKind.Document), ExtractionMethod.NativeText, null,
                StorageTestDatabase.TestVector(), "rename metadata evidence", "new-name.txt",
                FileName: "new-name.txt", SourcePath: renamedPath, ContentName: "new-name.txt")
        };
        var second = await database.CommitAsync(reindex, await StorageTestDatabase.HashAsync(renamed,
                cancellationToken), renamedFile.Length,
            new DateTimeOffset(renamedFile.LastWriteTimeUtc, TimeSpan.Zero), "unused", nodes: replacementNodes,
            passages: replacementPassages, cancellationToken: cancellationToken);

        Assert.NotEqual(first.RevisionId, second.RevisionId);
        Assert.Equal(first.ContentId, second.ContentId);
        Assert.Equal(first.PassageId, second.PassageId);
        await using var connection = new SqliteConnection($"Data Source={database.Paths.DatabasePath};Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.search_text,p.filename,p.path,
              (SELECT COUNT(*) FROM embeddings e WHERE e.passage_id=p.id)
            FROM passages p JOIN documents d ON d.active_revision_id=p.revision_id
            WHERE p.id=$passage;
            """;
        command.Parameters.AddWithValue("$passage", passageId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Contains("new-name.txt", reader.GetString(0));
        Assert.DoesNotContain("old-name.txt", reader.GetString(0));
        Assert.Equal("new-name.txt", reader.GetString(1));
        Assert.Equal(renamedPath, reader.GetString(2));
        Assert.Equal(1, reader.GetInt32(3));
    }

    [Fact]
    public async Task HashCorrelatedRenameFallbackPreservesDocumentIdAndQueuesMetadataReindex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Hash rename fallback", cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "fallback-old.txt");
        await File.WriteAllTextAsync(source, "hash correlated rename", cancellationToken);
        var initial = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var committed = await database.CommitAsync(initial.Job, initial.Sha256, initial.File.Length,
            new DateTimeOffset(initial.File.LastWriteTimeUtc, TimeSpan.Zero), "hash correlated rename",
            cancellationToken: cancellationToken);

        var renamed = Path.Combine(database.Paths.SourceDirectory, "fallback-new.txt");
        File.Move(source, renamed);
        var discovered = await database.ObserveAndLeaseAsync(projectId, folderId, renamed, false,
            cancellationToken);
        Assert.NotEqual(committed.DocumentId, discovered.Job.DocumentId);
        var begin = await database.Writer.BeginRevisionAsync(discovered.Job, discovered.Sha256,
            discovered.File.Length, new DateTimeOffset(discovered.File.LastWriteTimeUtc, TimeSpan.Zero),
            cancellationToken);
        Assert.False(begin.ShouldExtract);
        Assert.Contains("full metadata reindex", begin.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var replacement = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(replacement);
        Assert.Equal(IndexJobKind.Reindex, replacement.Kind);
        Assert.Equal(committed.DocumentId, replacement.DocumentId);
        var documents = await database.Store.ListDocumentsAsync(new DocumentListRequest(projectId, Limit: 10),
            cancellationToken);
        Assert.Equal(committed.DocumentId, Assert.Single(documents.Documents).DocumentId);
        Assert.Equal(Path.GetFullPath(renamed), documents.Documents[0].SourcePath);
        await database.Writer.FailJobAsync(replacement, "cleanup", "Deliberate cleanup", retryable: false,
            cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task ExistingOfflineFolderCanBeKeptWhileEditingProject()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, _) = await database.CreateProjectAsync("Offline edit", cancellationToken);
        var offlineDirectory = Path.Combine(Path.GetDirectoryName(database.Paths.SourceDirectory)!, "source-offline");
        Directory.Move(database.Paths.SourceDirectory, offlineDirectory);

        await database.Writer.UpdateProjectAsync(new UpdateProjectRequest(
            projectId, "Renamed while offline", [database.Paths.SourceDirectory]), cancellationToken);

        var updated = (await database.Store.ListProjectsAsync(cancellationToken)).Single(project => project.Id == projectId);
        Assert.Equal("Renamed while offline", updated.Name);
        Assert.Equal(Path.GetFullPath(database.Paths.SourceDirectory), Assert.Single(updated.Folders).Path);

        var newUnavailableFolder = Path.Combine(Path.GetDirectoryName(database.Paths.SourceDirectory)!, "not-present");
        var exception = await Assert.ThrowsAsync<ContextMoleException>(() =>
            database.Writer.UpdateProjectAsync(new UpdateProjectRequest(
                projectId, "Still offline", [database.Paths.SourceDirectory, newUnavailableFolder]), cancellationToken));
        Assert.Equal("folder_unavailable", exception.Code);
    }

    [Fact]
    public async Task SupersededJobCannotActivateAStaleRevision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Supersession", cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "changing.txt");
        await File.WriteAllTextAsync(source, "First observation", cancellationToken);
        var pending = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var modified = new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero);

        var firstRevision = await database.Writer.BeginRevisionAsync(pending.Job, pending.Sha256,
            pending.File.Length, modified, cancellationToken);
        Assert.True(firstRevision.ShouldExtract);
        Assert.NotNull(firstRevision.RevisionId);

        var newer = await database.Writer.ObserveFileAsync(new FileObservation(projectId, folderId, source,
            pending.File.Length, modified, Force: true), cancellationToken);
        Assert.True(newer.ObservationEpoch > pending.Observation.ObservationEpoch);
        Assert.Null(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken));

        var staleCommit = await database.Writer.CommitRevisionAsync(new IndexCommitRequest(pending.Job.JobId,
            projectId, pending.Observation.DocumentId, firstRevision.RevisionId!.Value,
            pending.Job.ExpectedObservationEpoch, pending.Sha256, pending.File.Length, modified, [], [], null, []),
            cancellationToken);
        Assert.False(staleCommit);

        var replacement = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(replacement);
        Assert.Equal(pending.Observation.DocumentId, replacement.DocumentId);
        await database.CommitAsync(replacement, pending.Sha256, pending.File.Length, modified,
            "The replacement revision is active.", includeVector: false, cancellationToken: cancellationToken);

        var project = (await database.Store.ListProjectsAsync(cancellationToken)).Single(item => item.Id == projectId);
        Assert.Equal(1, project.IndexedCount);
        Assert.Equal(0, project.PendingCount);
        Assert.Equal(0, project.ErrorCount);
    }

    [Fact]
    public async Task EmbeddingRefreshReusesActivePassagesWithoutReplacingTheirRevision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("WAL and migration", cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "reader-writer.txt");
        await File.WriteAllTextAsync(source, "first revision", cancellationToken);
        var pending = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var modified = new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero);
        await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length, modified, "first revision",
            cancellationToken: cancellationToken);

        var before = (await database.Store.ListProjectsAsync(cancellationToken)).Single(item => item.Id == projectId);
        await database.Writer.RequestReindexAsync(projectId, cancellationToken);
        var replacement = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(replacement);

        CommittedTestDocument second;
        await using (var reader = database.Store.StreamVectorEntriesAsync(projectId, before.SearchGeneration, null,
                         cancellationToken)
                         .GetAsyncEnumerator(cancellationToken))
        {
            Assert.True(await reader.MoveNextAsync());
            second = await database.CommitAsync(replacement, pending.Sha256, pending.File.Length, modified, "second revision",
                cancellationToken: cancellationToken);
        }

        var targetPolicy = StorageTestDatabase.TestEmbeddingPolicy with { ModelId = "tests-small", Revision = "2" };
        var generationBeforeMigration = (await database.Store.ListProjectsAsync(cancellationToken))
            .Single(item => item.Id == projectId).SearchGeneration;
        await database.Writer.RequestEmbeddingRefreshAsync(projectId, targetPolicy, retryFailed: false,
            cancellationToken);
        var retained = await database.Store.LoadVectorSnapshotMetadataAsync(projectId, cancellationToken);
        Assert.Equal(1, retained.EntryCount);
        Assert.True(retained.IsComplete);
        Assert.Equal(StorageTestDatabase.TestEmbeddingPolicy.Key, retained.Policy?.Key);
        Assert.Equal(generationBeforeMigration, retained.SearchGeneration);
        Assert.Single((await database.Store.KeywordSearchAsync(projectId,
            TextNormalization.QuoteFtsTerms("second"), 10, null, cancellationToken)).Candidates);

        var migration = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(migration);
        Assert.Equal(IndexJobKind.EmbeddingRefresh, migration.Kind);
        var migrationSource = await database.Writer.LoadEmbeddingRefreshSourceAsync(migration, cancellationToken);
        Assert.NotNull(migrationSource);
        Assert.Equal(second.RevisionId, migrationSource.RevisionId);
        Assert.Equal("second revision", Assert.Single(migrationSource.Passages).SearchText);

        await database.Writer.FailJobAsync(migration, "embedding_refresh_failed", "Migration failed",
            retryable: false,
            cancellationToken: cancellationToken);
        var afterFailure = await database.Store.LoadVectorSnapshotMetadataAsync(projectId, cancellationToken);
        Assert.Equal(1, afterFailure.EntryCount);
        Assert.Equal(StorageTestDatabase.TestEmbeddingPolicy.Key, afterFailure.Policy?.Key);
        await database.Writer.RequestEmbeddingRefreshAsync(projectId, targetPolicy, retryFailed: false,
            cancellationToken);
        Assert.Null(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken));
        var terminalError = Assert.Single(await database.Store.ListProjectErrorsAsync(projectId, 10,
            cancellationToken));
        Assert.Equal("embedding_refresh_failed", terminalError.Code);
        Assert.Equal(1, terminalError.Attempt);

        await database.Writer.RequestEmbeddingRefreshAsync(projectId, targetPolicy, retryFailed: true,
            cancellationToken);
        var repair = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(repair);
        Assert.Equal(IndexJobKind.EmbeddingRefresh, repair.Kind);
        Assert.NotEqual(migration.JobId, repair.JobId);
        await database.Writer.FailJobAsync(repair, "embedding_refresh_failed", "Explicit model repair failed",
            retryable: false, cancellationToken: cancellationToken);

        Assert.Equal(1, await database.Writer.RetryFailedFilesAsync(projectId, cancellationToken));
        Assert.Equal(0, await database.Writer.RetryFailedFilesAsync(projectId, cancellationToken));
        var explicitRetry = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(explicitRetry);
        Assert.Equal(IndexJobKind.EmbeddingRefresh, explicitRetry.Kind);
        Assert.Equal(repair.ExpectedObservationEpoch, explicitRetry.ExpectedObservationEpoch);
        var repairSource = await database.Writer.LoadEmbeddingRefreshSourceAsync(explicitRetry, cancellationToken);
        Assert.NotNull(repairSource);

        File.Delete(source);
        var refreshed = await database.Writer.CommitEmbeddingRefreshAsync(new EmbeddingRefreshCommitRequest(
            explicitRetry.JobId, projectId, explicitRetry.DocumentId, repairSource.RevisionId,
            explicitRetry.ExpectedObservationEpoch,
            repairSource.Passages.Select((passage, index) =>
                new PassageEmbedding(passage.PassageId, StorageTestDatabase.TestVector(index + 1))).ToArray(),
            targetPolicy), cancellationToken);
        Assert.True(refreshed);

        var document = await database.Store.GetDocumentInfoAsync(projectId, second.DocumentId, null,
            cancellationToken);
        Assert.Equal(second.RevisionId, document?.ActiveRevisionId);
        var passage = Assert.Single(await database.Store.ReadPassagesAsync(projectId, [second.PassageId], 0, 0,
            cancellationToken));
        Assert.Equal(second.PassageId, passage.PassageId);
        Assert.Equal(second.ContentId, passage.ContentId);
        Assert.Equal("second revision", passage.Text);
        Assert.Equal(ExtractionMethod.NativeText, passage.ExtractionMethod);
        var completed = await database.Store.LoadVectorSnapshotMetadataAsync(projectId, cancellationToken);
        Assert.True(completed.IsComplete);
        Assert.Equal(targetPolicy.Key, completed.Policy?.Key);
        Assert.Equal(1, completed.EntryCount);
        Assert.Single((await database.Store.KeywordSearchAsync(projectId,
            TextNormalization.QuoteFtsTerms("second"), 10, null, cancellationToken)).Candidates);
    }

    [Fact]
    public async Task FailedEmbeddingPolicyDoesNotBlockAReplacementPolicy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Embedding policy fencing", cancellationToken);
        var source = await WriteAsync("policy.txt", "policy passage", database.Paths.SourceDirectory,
            cancellationToken);
        var pending = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var modified = new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero);
        await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length, modified, "policy passage",
            cancellationToken: cancellationToken);

        var failedPolicy = StorageTestDatabase.TestEmbeddingPolicy with { ModelId = "failed-policy" };
        await database.Writer.RequestEmbeddingRefreshAsync(projectId, failedPolicy, retryFailed: false,
            cancellationToken);
        var failedJob = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(failedJob);
        await database.Writer.FailJobAsync(failedJob, "embedding_refresh_failed", "Failed policy",
            retryable: false, cancellationToken: cancellationToken);

        await database.Writer.RequestEmbeddingRefreshAsync(projectId, failedPolicy, retryFailed: false,
            cancellationToken);
        Assert.Null(await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken));

        var replacementPolicy = failedPolicy with { ModelId = "replacement-policy" };
        await database.Writer.RequestEmbeddingRefreshAsync(projectId, replacementPolicy, retryFailed: false,
            cancellationToken);
        var replacementJob = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(replacementJob);
        Assert.Equal(IndexJobKind.EmbeddingRefresh, replacementJob.Kind);
        await database.Writer.FailJobAsync(replacementJob, "cleanup", "Deliberate cleanup", retryable: false,
            cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task FullReindexSupersedesRunningEmbeddingRefreshAtTheSameEpoch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Embedding priority", cancellationToken);
        var source = await WriteAsync("priority.txt", "persisted source", database.Paths.SourceDirectory,
            cancellationToken);
        var pending = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var modified = new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero);
        await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length, modified, "persisted source",
            cancellationToken: cancellationToken);

        var targetPolicy = StorageTestDatabase.TestEmbeddingPolicy with { ModelId = "priority-target" };
        await database.Writer.RequestEmbeddingRefreshAsync(projectId, targetPolicy, retryFailed: false,
            cancellationToken);
        var embeddingJob = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(embeddingJob);
        Assert.Equal(IndexJobKind.EmbeddingRefresh, embeddingJob.Kind);
        var persisted = await database.Writer.LoadEmbeddingRefreshSourceAsync(embeddingJob, cancellationToken);
        Assert.NotNull(persisted);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var upgrade = connection.CreateCommand();
            upgrade.CommandText = "UPDATE index_jobs SET kind=$kind WHERE id=$job;";
            upgrade.Parameters.AddWithValue("$kind", (int)IndexJobKind.Reindex);
            upgrade.Parameters.AddWithValue("$job", embeddingJob.JobId.ToString());
            Assert.Equal(1, await upgrade.ExecuteNonQueryAsync(cancellationToken));
        }

        var staleCommit = await database.Writer.CommitEmbeddingRefreshAsync(new EmbeddingRefreshCommitRequest(
            embeddingJob.JobId, projectId, embeddingJob.DocumentId, persisted.RevisionId,
            embeddingJob.ExpectedObservationEpoch,
            persisted.Passages.Select(passage =>
                new PassageEmbedding(passage.PassageId, StorageTestDatabase.TestVector())).ToArray(), targetPolicy),
            cancellationToken);
        Assert.False(staleCommit);

        await database.Writer.RequestEmbeddingRefreshAsync(projectId, targetPolicy, retryFailed: false,
            cancellationToken);
        var fullJob = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(fullJob);
        Assert.Equal(IndexJobKind.Reindex, fullJob.Kind);
        Assert.Equal(embeddingJob.ExpectedObservationEpoch, fullJob.ExpectedObservationEpoch);
        await database.Writer.FailJobAsync(fullJob, "cleanup", "Deliberate cleanup", retryable: false,
            cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task InventoryReportsStatusesAndRetryQueuesOnlyDocumentsWithErrors()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Inventory", cancellationToken);
        var alphaPath = await WriteAsync("alpha.txt", "alpha", database.Paths.SourceDirectory, cancellationToken);
        var bravoPath = await WriteAsync("Bravo.eml", "bravo", database.Paths.SourceDirectory, cancellationToken);
        var charliePath = await WriteAsync("charlie.md", "charlie", database.Paths.SourceDirectory,
            cancellationToken);

        var alpha = await database.ObserveAndLeaseAsync(projectId, folderId, alphaPath, false, cancellationToken);
        var alphaModified = new DateTimeOffset(alpha.File.LastWriteTimeUtc, TimeSpan.Zero);
        var rootId = Guid.CreateVersion7();
        var attachmentId = Guid.CreateVersion7();
        var nodes = new ContentNodeDraft[]
        {
            new(rootId, null, 0, "alpha.txt", "text/plain", "root", 0),
            new(attachmentId, rootId, 0, "attachment.pdf", "application/pdf", "attachment", 1)
        };
        var passages = new PassageDraft[]
        {
            new(Guid.CreateVersion7(), rootId, 0, "alpha root", "alpha root",
                new SourceLocation(LocationKind.Document), ExtractionMethod.NativeText, null, null),
            new(Guid.CreateVersion7(), attachmentId, 0, "alpha attachment", "alpha attachment",
                new SourceLocation(LocationKind.EmailPart, EmailPart: "attachment:0"), ExtractionMethod.Attachment,
                null, null)
        };
        await database.CommitAsync(alpha.Job, alpha.Sha256, alpha.File.Length, alphaModified, "unused",
            includeVector: false, nodes: nodes, passages: passages, cancellationToken: cancellationToken);

        var bravo = await database.ObserveAndLeaseAsync(projectId, folderId, bravoPath, false, cancellationToken);
        await database.Writer.FailJobAsync(bravo.Job, "extraction_failed", "Broken container", retryable: false,
            cancellationToken: cancellationToken);
        var charlie = await database.ObserveAndLeaseAsync(projectId, folderId, charliePath, false, cancellationToken);

        var inventory = await database.Store.ListDocumentsAsync(new DocumentListRequest(projectId), cancellationToken);
        Assert.Equal(DocumentInventoryStatus.Indexed,
            inventory.Documents.Single(item => item.DocumentId == alpha.Observation.DocumentId).Status);
        Assert.Equal(DocumentInventoryStatus.Error,
            inventory.Documents.Single(item => item.DocumentId == bravo.Observation.DocumentId).Status);
        Assert.Equal(DocumentInventoryStatus.Processing,
            inventory.Documents.Single(item => item.DocumentId == charlie.Observation.DocumentId).Status);
        var indexed = inventory.Documents.Single(item => item.DocumentId == alpha.Observation.DocumentId);
        Assert.Equal(2, indexed.ContentCount);
        Assert.Equal(1, indexed.AttachmentCount);
        Assert.Equal(2, indexed.ExtractedPassageCount);

        var counts = (await database.Store.ListProjectFileTypeCountsAsync(projectId, cancellationToken))
            .ToDictionary(item => item.Extension, item => item.Count, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, counts[".txt"]);
        Assert.Equal(1, counts[".eml"]);
        Assert.Equal(1, counts[".md"]);
        var firstPage = await database.Store.ListDocumentsAsync(new DocumentListRequest(projectId, Limit: 2),
            cancellationToken);
        Assert.Equal(["alpha.txt", "Bravo.eml"], firstPage.Documents.Select(item => item.FileName).ToArray());
        Assert.NotNull(firstPage.NextCursor);
        var secondPage = await database.Store.ListDocumentsAsync(new DocumentListRequest(projectId, Limit: 2,
            Cursor: firstPage.NextCursor), cancellationToken);
        Assert.Equal(["charlie.md"], secondPage.Documents.Select(item => item.FileName).ToArray());
        Assert.Equal(bravo.Observation.DocumentId,
            Assert.Single((await database.Store.ListDocumentsAsync(new DocumentListRequest(projectId,
                DocumentStatusFilter.Error), cancellationToken)).Documents).DocumentId);

        Assert.Equal(1, await database.Writer.RetryFailedFilesAsync(projectId, cancellationToken));
        Assert.Equal(0, await database.Writer.RetryFailedFilesAsync(projectId, cancellationToken));
        var retry = await database.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), cancellationToken);
        Assert.NotNull(retry);
        Assert.Equal(bravo.Observation.DocumentId, retry.DocumentId);
        Assert.Equal(IndexJobKind.Reindex, retry.Kind);
        var bravoModified = new DateTimeOffset(bravo.File.LastWriteTimeUtc, TimeSpan.Zero);
        await database.CommitAsync(retry, bravo.Sha256, bravo.File.Length, bravoModified, "bravo recovered",
            includeVector: false, cancellationToken: cancellationToken);
        Assert.Empty(await database.Store.ListProjectErrorsAsync(projectId, 25, cancellationToken));

        await database.Writer.FailJobAsync(charlie.Job, "cleanup", "Deliberate cleanup", retryable: false,
            cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task MaterializationExtractsIndexedAttachmentAndRejectsChangedSource()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Materialization", cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "message.eml");
        const string attachmentText = "Materialized attachment evidence.";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(attachmentText));
        var eml = $$"""
            From: source@example.test
            To: reader@example.test
            Subject: Materialization
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="test-boundary"

            --test-boundary
            Content-Type: text/plain; charset=utf-8

            Root body.
            --test-boundary
            Content-Type: text/plain; name="evidence.txt"
            Content-Disposition: attachment; filename="evidence.txt"
            Content-Transfer-Encoding: base64

            {{encoded}}
            --test-boundary--
            """
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(source, eml, new UTF8Encoding(false), cancellationToken);
        var pending = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var modified = new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero);
        var rootId = Guid.CreateVersion7();
        var attachmentId = Guid.CreateVersion7();
        var nodes = new ContentNodeDraft[]
        {
            new(rootId, null, 0, "message.eml", "message/rfc822", "root", 0),
            new(attachmentId, rootId, 0, "evidence.txt", "text/plain", "email-attachment", 1)
        };
        var passages = new PassageDraft[]
        {
            new(Guid.CreateVersion7(), rootId, 0, "Root body.", "Root body.",
                new SourceLocation(LocationKind.EmailPart, EmailPart: "body"), ExtractionMethod.Email, null, null),
            new(Guid.CreateVersion7(), attachmentId, 0, attachmentText, attachmentText,
                new SourceLocation(LocationKind.EmailPart, EmailPart: "attachment:0"), ExtractionMethod.Attachment,
                null, null)
        };
        await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length, modified, "unused",
            includeVector: false, nodes: nodes, passages: passages, cancellationToken: cancellationToken);

        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        var materializer = new ContentMaterializationService(database.Store, database.Paths, budget);
        var root = await materializer.MaterializeAsync(projectId, rootId, cancellationToken);
        Assert.False(root.Temporary);
        Assert.Equal(Path.GetFullPath(source), root.LocalPath);
        var attachment = await materializer.MaterializeAsync(projectId, attachmentId, cancellationToken);
        Assert.True(attachment.Temporary);
        Assert.Equal(["evidence.txt"], attachment.AttachmentChain);
        Assert.Equal(attachmentText, await File.ReadAllTextAsync(attachment.LocalPath, cancellationToken));
        AssertPathIsWithin(database.Paths.TempDirectory, attachment.LocalPath);

        await File.AppendAllTextAsync(source, "changed", cancellationToken);
        var exception = await Assert.ThrowsAsync<ContextMoleException>(() =>
            materializer.MaterializeAsync(projectId, rootId, cancellationToken));
        Assert.Equal("source_changed", exception.Code);
    }

    private static async Task<string> WriteAsync(string name, string text, string directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, text, cancellationToken);
        return path;
    }

    private static void AssertPathIsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        Assert.False(Path.IsPathRooted(relative));
        Assert.NotEqual("..", relative);
        Assert.False(relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        Assert.False(relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
