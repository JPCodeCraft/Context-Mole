using System.Text.Json;

using ContextMole.Core;
using ContextMole.Infrastructure;
using ContextMole.Search;
using ContextMole.Storage;

using Microsoft.Data.Sqlite;

namespace ContextMole.Tests;

public sealed partial class MigrationRegressionTests
{
    private const string LegacyPreparation = "layout-v2/spans-v2/body-context-v2";
    private const string LegacyText = "Café inter-\nruption checksum remains literal evidence.";
    private const string LegacySearchText = "older normalized searchable text is retained verbatim";
    private const string RebuiltText = "New source-backed rebuilt recovery evidence.";
    private static EmbeddingPolicy Current97Policy =>
        GraniteEmbeddingModels.Get(EmbeddingModelChoice.Granite97M).CreatePolicy(false);

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public async Task LegacyStagingRecoveryDoesNotLeaveOrphanFtsOrExposeSupersededEvidence(int version)
    {
        using var paths = new MigrationTestPaths();
        var legacy = await SeedLegacyEvidenceAsync(paths, version, false, true, true);
        var staging = Guid.NewGuid();
        var superseded = Guid.NewGuid();
        await using (var connection = await OpenAsync(paths))
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO document_revisions(id,document_id,sha256,status,created_utc)
                  VALUES($staging,$document,'staging-sha','staging',$now),
                        ($superseded,$document,'superseded-sha','superseded',$now);
                INSERT INTO content_nodes(id,revision_id,ordinal,name,relationship,depth,status)
                  VALUES($staging,$staging,0,'staging.txt','root',0,'indexed'),
                        ($superseded,$superseded,0,'superseded.txt','root',0,'indexed');
                INSERT INTO passages(id,revision_id,content_id,ordinal,display_text,search_text,location_kind,extraction_method)
                  VALUES($staging,$staging,$staging,0,'staging_only_marker','staging_only_marker',0,0),
                        ($superseded,$superseded,$superseded,0,'superseded_only_marker','superseded_only_marker',0,0);
                """ + (version == 7 ? """
                UPDATE passages SET body_text=display_text WHERE revision_id IN ($staging,$superseded);
                INSERT INTO passages_fts(rowid,body_text) SELECT rowid,body_text FROM passages WHERE revision_id IN ($staging,$superseded);
                """ : "INSERT INTO passages_fts(rowid,search_text) SELECT rowid,search_text FROM passages WHERE revision_id IN ($staging,$superseded);");
            seed.Parameters.AddWithValue("$staging", staging.ToString());
            seed.Parameters.AddWithValue("$superseded", superseded.ToString());
            seed.Parameters.AddWithValue("$document", legacy.DocumentId.ToString());
            seed.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await seed.ExecuteNonQueryAsync(Token);
        }
        using var writer = new DatabaseWriterService(paths);
        await writer.StartAsync(Token);
        var store = new SqliteSearchStore(paths);
        await AssertRetainedLegacyEvidenceAsync(paths, store, legacy, version);
        Assert.Equal(0L, await ScalarAsync(paths, "SELECT COUNT(*) FROM document_revisions WHERE status='staging';"));
        Assert.Equal(1L, await ScalarAsync(paths, "SELECT COUNT(*) FROM document_revisions WHERE status='superseded';"));
        Assert.Equal(3L, await ScalarAsync(paths, "SELECT COUNT(*) FROM passages;"));
        Assert.Equal(3L, await ScalarAsync(paths, "SELECT COUNT(*) FROM sections;"));
        Assert.Equal(0L, await ScalarAsync(paths, "SELECT COUNT(*) FROM passages_fts WHERE passages_fts MATCH 'staging_only_marker OR superseded_only_marker';"));
        Assert.Equal(0L, await ScalarAsync(paths, "SELECT COUNT(*) FROM sections_fts WHERE sections_fts MATCH 'staging_only_marker OR superseded_only_marker';"));
        var supersededRead = Assert.Single(await store.ReadPassagesAsync(legacy.ProjectId, [superseded], 0, 0, Token));
        Assert.Equal("stale_passage", supersededRead.ErrorCode);
        var generation = await store.GetSearchGenerationAsync(legacy.ProjectId, Token);
        var error = await Assert.ThrowsAsync<ContextMoleException>(() => store.ReadSectionAsync(legacy.ProjectId,
            superseded, generation, cancellationToken: Token));
        Assert.Equal("section_not_found", error.Code);
        await writer.StopAsync(Token);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public async Task LegacyPreparationRemainsExcludedEvenIfOldVectorsClaimCurrent97Policy(int version)
    {
        using var paths = new MigrationTestPaths();
        var legacy = await SeedLegacyEvidenceAsync(paths, version, false, true, true);
        legacy = legacy with { Policy = Current97Policy };
        await using (var connection = await OpenAsync(paths))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE document_revisions SET embedding_policy_json=$policy; UPDATE embeddings SET policy_key=$key;";
            command.Parameters.AddWithValue("$policy", legacy.PolicyJson);
            command.Parameters.AddWithValue("$key", legacy.Policy.Key);
            await command.ExecuteNonQueryAsync(Token);
        }
        using var writer = new DatabaseWriterService(paths);
        await writer.StartAsync(Token);
        await AssertRetainedLegacyEvidenceAsync(paths, new SqliteSearchStore(paths), legacy, version);
        await writer.RequestEmbeddingRefreshAsync(legacy.ProjectId, Current97Policy, true, Token);
        Assert.Equal(0L, await ScalarAsync(paths, "SELECT COUNT(*) FROM index_jobs WHERE kind=2;"));
        await writer.StopAsync(Token);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public async Task UnavailableRootReconciliationRetainsLegacyActiveEvidence(int version)
    {
        using var paths = new MigrationTestPaths();
        var legacy = await SeedLegacyEvidenceAsync(paths, version, false, false, true);
        Directory.Move(paths.SourceDirectory, paths.SourceDirectory + "-offline");
        using var writer = new DatabaseWriterService(paths);
        await writer.StartAsync(Token);
        await writer.CompleteReconciliationAsync(legacy.ProjectId, legacy.FolderId, "unavailable-scan", Token);
        await AssertRetainedLegacyEvidenceAsync(paths, new SqliteSearchStore(paths), legacy, version);
        var job = await LeaseLegacyUpgradeAsync(writer);
        await writer.FailJobAsync(job, "folder_unavailable", "Source root temporarily unavailable", false, Token);
        await AssertRetainedLegacyEvidenceAsync(paths, new SqliteSearchStore(paths), legacy, version, expectedQueued: false);
        Assert.Equal(RebuiltText, await File.ReadAllTextAsync(Path.Combine(paths.SourceDirectory + "-offline", "legacy.txt"), Token));
        await writer.StopAsync(Token);
    }

    [Theory]
    [InlineData(6, true, true, true)]
    [InlineData(7, true, true, true)]
    [InlineData(6, false, false, true)]
    [InlineData(7, false, false, true)]
    [InlineData(6, false, false, false)]
    [InlineData(7, false, false, false)]
    public async Task LegacyEvidenceRemainsReadableForPausedUnavailableAndMissingSources(
        int version, bool paused, bool available, bool sourceExists)
    {
        using var paths = new MigrationTestPaths();
        var legacy = await SeedLegacyEvidenceAsync(paths, version, paused, available, sourceExists);
        using (var writer = new DatabaseWriterService(paths))
        {
            await writer.StartAsync(Token);
            var store = new SqliteSearchStore(paths);
            await AssertRetainedLegacyEvidenceAsync(paths, store, legacy, version);
            var summary = Assert.Single(await store.ListProjectsAsync(Token));
            Assert.Equal(paused ? ProjectState.Paused : ProjectState.Active, summary.State);
            Assert.Equal(1, summary.PendingCount);
            Assert.Equal(1, summary.IndexedCount);
            if (paused) Assert.Null(await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
            else
            {
                await writer.RequestEmbeddingRefreshAsync(legacy.ProjectId, Current97Policy, false, Token);
                Assert.Equal(1L, await ScalarAsync(paths, "SELECT COUNT(*) FROM index_jobs WHERE state='queued';"));
                Assert.Equal(0L, await ScalarAsync(paths, "SELECT COUNT(*) FROM index_jobs WHERE kind=2;"));
            }
            await writer.StopAsync(Token);
        }
        var generation = await ScalarAsync(paths, "SELECT search_generation FROM projects;");
        var epoch = await ScalarAsync(paths, "SELECT observation_epoch FROM documents;");
        using (var repeated = new DatabaseWriterService(paths))
        {
            await repeated.StartAsync(Token);
            await AssertRetainedLegacyEvidenceAsync(paths, new SqliteSearchStore(paths), legacy, version);
            await repeated.StopAsync(Token);
        }
        Assert.Equal(generation, await ScalarAsync(paths, "SELECT search_generation FROM projects;"));
        Assert.Equal(epoch, await ScalarAsync(paths, "SELECT observation_epoch FROM documents;"));
        Assert.Equal(1L, await ScalarAsync(paths, "SELECT COUNT(*) FROM index_jobs;"));
        Assert.Equal(sourceExists, File.Exists(legacy.SourcePath));
        if (sourceExists) Assert.Equal(RebuiltText, await File.ReadAllTextAsync(legacy.SourcePath, Token));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public async Task LegacyMigrationFailureRollsBackEvidenceAndCanRetryWithoutDuplication(int version)
    {
        using var paths = new MigrationTestPaths();
        var legacy = await SeedLegacyEvidenceAsync(paths, version, false, true, true);
        // Fail after the backfilled FTS/sections were created, exercising transactional DDL rollback.
        await ExecuteAsync(paths, """
            CREATE TRIGGER fail_legacy_upgrade BEFORE UPDATE ON index_jobs
            BEGIN SELECT RAISE(ABORT,'intentional legacy upgrade failure'); END;
            """);
        using (var failed = new DatabaseWriterService(paths))
        {
            var error = await Assert.ThrowsAsync<ContextMoleException>(() => failed.StartAsync(Token));
            Assert.Equal("migration_failed", error.Code);
            Assert.Contains($"migration {version + 1}", error.Message);
        }
        Assert.Equal(version, await ScalarAsync(paths, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(1L, await ScalarAsync(paths, "SELECT COUNT(*) FROM document_revisions WHERE status='active';"));
        Assert.Equal(2L, await ScalarAsync(paths, "SELECT COUNT(*) FROM passages;"));
        Assert.Equal(2L, await ScalarAsync(paths, "SELECT COUNT(*) FROM embeddings;"));
        Assert.Equal(0L, await ScalarAsync(paths, "SELECT COUNT(*) FROM pragma_table_info('passages') WHERE name='section_id';"));
        Assert.Equal(0L, await ScalarAsync(paths, "SELECT COUNT(*) FROM sqlite_schema WHERE name='sections';"));
        Assert.Equal(11L, await ScalarAsync(paths, "SELECT search_generation FROM projects;"));
        Assert.Equal(5L, await ScalarAsync(paths, "SELECT observation_epoch FROM documents;"));
        Assert.Equal(1L, await ScalarAsync(paths,
            version == 6 ? "SELECT COUNT(*) FROM passages_fts WHERE passages_fts MATCH 'older';" :
                           "SELECT COUNT(*) FROM passages_fts WHERE passages_fts MATCH 'checksum';"));
        await ExecuteAsync(paths, "DROP TRIGGER fail_legacy_upgrade;");
        using (var retry = new DatabaseWriterService(paths))
        {
            await retry.StartAsync(Token);
            await AssertRetainedLegacyEvidenceAsync(paths, new SqliteSearchStore(paths), legacy, version);
            await retry.StopAsync(Token);
        }
        Assert.Equal(RebuiltText, await File.ReadAllTextAsync(legacy.SourcePath, Token));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public async Task LegacyRebuildRetainsEvidenceThroughFailedCommitAndInterruptedRetry(int version)
    {
        using var paths = new MigrationTestPaths();
        var legacy = await SeedLegacyEvidenceAsync(paths, version, false, true, true);
        IndexCommitRequest interrupted;
        using (var writer = new DatabaseWriterService(paths))
        {
            await writer.StartAsync(Token);
            var store = new SqliteSearchStore(paths);
            var job = await LeaseLegacyUpgradeAsync(writer);
            Assert.Equal(IndexJobKind.Reindex, job.Kind);
            var begin = await writer.BeginRevisionAsync(job, legacy.SourceSha, legacy.SourceSize, legacy.ModifiedUtc, Token);
            Assert.True(begin.ShouldExtract); // The hash is unchanged, but legacy preparation cannot skip extraction.
            var request = await BuildSourceBackedCommitAsync(legacy, job, begin.RevisionId!.Value);
            var duplicate = request.ContentNodes[0];
            var failedCommit = request with { ContentNodes = [duplicate, duplicate] };
            await Assert.ThrowsAsync<SqliteException>(() => writer.CommitRevisionAsync(failedCommit, Token));
            await AssertRetainedLegacyEvidenceAsync(paths, store, legacy, version);
            await writer.FailJobAsync(job, "source_unavailable", "Synthetic interrupted rebuild", false, Token);
            await AssertRetainedLegacyEvidenceAsync(paths, store, legacy, version, expectedQueued: false);
            await writer.RequestEmbeddingRefreshAsync(legacy.ProjectId, Current97Policy, false, Token);
            Assert.Null(await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
            await writer.RequestEmbeddingRefreshAsync(legacy.ProjectId, Current97Policy, true, Token);
            var admitted = await LeaseLegacyUpgradeAsync(writer);
            Assert.Equal(IndexJobKind.Reindex, admitted.Kind);
            await writer.FailJobAsync(admitted, "source_unavailable", "Synthetic explicit repair retry", false, Token);
            await AssertRetainedLegacyEvidenceAsync(paths, store, legacy, version, expectedQueued: false);
            await writer.RequestReindexAsync(legacy.ProjectId, Token);
            var retry = await LeaseLegacyUpgradeAsync(writer);
            var staging = await writer.BeginRevisionAsync(retry, legacy.SourceSha, legacy.SourceSize, legacy.ModifiedUtc, Token);
            Assert.True(staging.ShouldExtract);
            interrupted = await BuildSourceBackedCommitAsync(legacy, retry, staging.RevisionId!.Value);
            await AssertRetainedLegacyEvidenceAsync(paths, store, legacy, version);
            await writer.StopAsync(Token);
        }
        // Startup must discard only staging revisions and requeue the lease, retaining active old evidence.
        using (var restarted = new DatabaseWriterService(paths))
        {
            await restarted.StartAsync(Token);
            var store = new SqliteSearchStore(paths);
            Assert.Equal(0L, await ScalarAsync(paths, "SELECT COUNT(*) FROM document_revisions WHERE status='staging';"));
            await AssertRetainedLegacyEvidenceAsync(paths, store, legacy, version);
            Assert.False(await restarted.CommitRevisionAsync(interrupted, Token));
            var job = await LeaseLegacyUpgradeAsync(restarted);
            var begin = await restarted.BeginRevisionAsync(job, legacy.SourceSha, legacy.SourceSize, legacy.ModifiedUtc, Token);
            Assert.True(begin.ShouldExtract);
            var request = await BuildSourceBackedCommitAsync(legacy, job, begin.RevisionId!.Value);
            Assert.True(await restarted.CommitRevisionAsync(request, Token));
            var fresh = Assert.Single(await store.ReadPassagesAsync(legacy.ProjectId, [request.Passages[0].Id], 0, 0, Token));
            Assert.Equal(RebuiltText, fresh.Text);
            Assert.Equal(1L, await ScalarAsync(paths, "SELECT COUNT(*) FROM document_revisions;"));
            Assert.Equal(1L, await ScalarAsync(paths, "SELECT COUNT(*) FROM passages_fts;"));
            Assert.Equal(1L, await ScalarAsync(paths, "SELECT COUNT(*) FROM sections_fts;"));
            Assert.Equal(0L, await ScalarAsync(paths, "SELECT COUNT(*) FROM sections WHERE kind='legacy-passage';"));
            Assert.Equal(0L, await ScalarAsync(paths, "SELECT COUNT(*) FROM document_revisions WHERE preparation_version<>'" + IndexPreparation.Version + "';"));
            var old = Assert.Single(await store.ReadPassagesAsync(legacy.ProjectId, [legacy.PassageId], 0, 0, Token));
            Assert.Equal("stale_passage", old.ErrorCode);
            var metadata = await store.LoadVectorSnapshotMetadataAsync(legacy.ProjectId, Current97Policy, Token);
            Assert.True(metadata.IsComplete);
            Assert.Equal(1, metadata.EntryCount);
            Assert.Single((await store.LoadVectorSnapshotAsync(legacy.ProjectId, Current97Policy, Token)).Entries);
            Assert.Empty((await store.KeywordSearchAsync(legacy.ProjectId, TextNormalization.QuoteFtsTerms("checksum"), 10, null, Token)).Candidates);
            await AssertEvidenceIntegrityAsync(paths);
            await restarted.StopAsync(Token);
        }
        Assert.Equal(legacy.SourceSha, await StorageTestDatabase.HashAsync(legacy.SourcePath, Token));
    }

    private static async Task<IndexCommitRequest> BuildSourceBackedCommitAsync(LegacyEvidenceFixture legacy,
        IndexJobLease job, Guid revisionId)
    {
        var text = await File.ReadAllTextAsync(legacy.SourcePath, Token);
        var content = Guid.NewGuid();
        var passage = Guid.NewGuid();
        return new IndexCommitRequest(job.JobId, legacy.ProjectId, legacy.DocumentId, revisionId,
            job.ExpectedObservationEpoch, legacy.SourceSha, legacy.SourceSize, legacy.ModifiedUtc,
            [new ContentNodeDraft(content, null, 0, Path.GetFileName(legacy.SourcePath), "text/plain", "root", 0)],
            [new PassageDraft(passage, content, 0, text, TextNormalization.ForSearch(text),
                new SourceLocation(LocationKind.Document), ExtractionMethod.NativeText, null,
                StorageTestDatabase.TestVector(), LexicalText.Canonicalize(text),
                FileName: Path.GetFileName(legacy.SourcePath), SourcePath: legacy.SourcePath,
                ContentName: Path.GetFileName(legacy.SourcePath))], Current97Policy, []);
    }

    private static async Task<IndexJobLease> LeaseLegacyUpgradeAsync(DatabaseWriterService writer)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            if (await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), timeout.Token) is { } job) return job;
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task AssertRetainedLegacyEvidenceAsync(MigrationTestPaths paths, SqliteSearchStore store,
        LegacyEvidenceFixture legacy, int version, bool expectedQueued = true)
    {
        var generation = await store.GetSearchGenerationAsync(legacy.ProjectId, Token);
        var read = Assert.Single(await store.ReadPassagesAsync(legacy.ProjectId, [legacy.PassageId], 0, 0, generation, Token));
        Assert.Equal(LegacyText, read.Text);
        Assert.Null(read.ErrorCode);
        Assert.Equal(legacy.DocumentId, read.DocumentId);
        Assert.Equal(legacy.ContentId, read.ContentId);
        Assert.Equal(legacy.PassageId, read.SectionId);
        Assert.Equal(0, read.SectionOffset);
        Assert.Equal(new SourceLocation(LocationKind.Page, Page: 9, StructurePath: "Legacy / heading"), read.Location);
        Assert.Null(read.Location.Region);
        var sibling = Assert.Single(await store.ReadPassagesAsync(legacy.ProjectId, [legacy.AttachmentPassageId], 0, 0, Token));
        Assert.Equal(new SourceLocation(LocationKind.Sheet, Sheet: "Résumé", CellRange: "C7:D8", Slide: 2,
            StructurePath: "Legacy table", EmailPart: "body", ImageFrame: 3), sibling.Location);
        Assert.Equal(ExtractionMethod.Ocr, sibling.ExtractionMethod);
        Assert.Equal(0.875, sibling.OcrConfidence);
        Assert.Equal(["child.txt"], sibling.AttachmentChain);
        foreach (var scope in new[] { SearchScope.Passage, SearchScope.Section })
        {
            var query = TextNormalization.QuoteFtsTerms("cafe interruption checksum");
            var branch = await store.LoadKeywordBranchesAsync(legacy.ProjectId, query, null, 10, null,
                new SearchFieldWeights(), scope, Token);
            var candidate = Assert.Single(branch.MainCandidates);
            Assert.Equal(legacy.PassageId, candidate.PassageId);
            Assert.Equal(LegacyText, candidate.DisplayText);
            Assert.Equal("legacy.txt", candidate.FileName);
            Assert.Equal(legacy.SourcePath, candidate.SourcePath);
            Assert.Equal("legacy.txt", candidate.ContentName);
            if (scope == SearchScope.Section)
            {
                Assert.Equal(LegacyText, candidate.SectionText);
                Assert.NotNull(candidate.SectionPassages);
                Assert.Equal(legacy.PassageId, Assert.Single(candidate.SectionPassages).PassageId);
            }
            using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
            var search = new HybridSearchService(store, new StorageUnavailableEmbeddings(), new FlatVectorIndexFactory(),
                new VectorIndexCache(), budget);
            var result = await search.SearchAsync(new SearchRequest(legacy.ProjectId, SearchMode.Keyword,
                Clauses: [new SearchClause("checksum", "checksum", Fields: [SearchField.Body])], Scope: scope), Token);
            var preview = Assert.Single(Assert.Single(result.Results).Previews);
            Assert.Equal(legacy.PassageId, preview.PassageId);
            Assert.Equal(LegacyText.Substring(preview.ExcerptStart, preview.ExcerptLength), preview.Excerpt);
            Assert.All(preview.MatchSpans, span => Assert.Equal("checksum", LegacyText.Substring(span.Start, span.Length)));
        }
        var section = await store.ReadSectionAsync(legacy.ProjectId, legacy.PassageId, generation, cancellationToken: Token);
        Assert.Equal("legacy-passage", section.Kind);
        Assert.Empty(section.HeadingPath);
        Assert.Equal(read.Location, section.Location);
        Assert.Equal(LegacyText, Assert.Single(section.Passages).Text);
        var current = await store.LoadVectorSnapshotMetadataAsync(legacy.ProjectId, Current97Policy, Token);
        Assert.False(current.IsComplete);
        Assert.Equal(0, current.EntryCount);
        Assert.Equal(1, current.ExcludedDocumentCount);
        Assert.Equal(expectedQueued ? 1 : 0, current.RepairQueuedDocumentCount);
        Assert.Empty((await store.LoadVectorSnapshotAsync(legacy.ProjectId, Current97Policy, Token)).Entries);
        await foreach (var _ in store.StreamVectorEntriesAsync(legacy.ProjectId, generation, Current97Policy, null, Token))
            Assert.Fail("Legacy vectors cannot enter the current 97M stream.");
        Assert.Equal(2L, await ScalarAsync(paths, "SELECT COUNT(*) FROM embeddings;"));
        Assert.Equal(1L, await ScalarAsync(paths, "SELECT COUNT(*) FROM documents WHERE active_revision_id IS NOT NULL AND sha256 IS NOT NULL;"));
        await using (var connection = await OpenAsync(paths))
        {
            await using var retained = connection.CreateCommand();
            retained.CommandText = "SELECT r.id,r.preparation_version,r.embedding_policy_json,p.search_text,e.policy_key FROM document_revisions r JOIN passages p ON p.revision_id=r.id JOIN embeddings e ON e.passage_rowid=p.rowid WHERE p.id=$passage;";
            retained.Parameters.AddWithValue("$passage", legacy.PassageId.ToString());
            await using var reader = await retained.ExecuteReaderAsync(Token);
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal(legacy.RevisionId.ToString(), reader.GetString(0));
            Assert.Equal(LegacyPreparation, reader.GetString(1));
            Assert.Equal(legacy.PolicyJson, reader.GetString(2));
            Assert.Equal(LegacySearchText, reader.GetString(3));
            Assert.Equal(legacy.Policy.Key, reader.GetString(4));
        }
        if (version == 7)
            Assert.Single((await store.KeywordSearchAsync(legacy.ProjectId, "title : retainedtitle", 10, null, Token)).Candidates);
        await AssertEvidenceIntegrityAsync(paths);
    }

    private static async Task AssertEvidenceIntegrityAsync(MigrationTestPaths paths)
    {
        Assert.Equal(0L, await ScalarAsync(paths, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        await ExecuteAsync(paths, "INSERT INTO passages_fts(passages_fts) VALUES('integrity-check'); INSERT INTO sections_fts(sections_fts) VALUES('integrity-check');");
        Assert.Equal(await ScalarAsync(paths, "SELECT COUNT(*) FROM passages p JOIN document_revisions r ON r.id=p.revision_id AND r.status='active' JOIN documents d ON d.id=r.document_id AND d.active_revision_id=r.id AND d.tombstoned=0;"), await ScalarAsync(paths, "SELECT COUNT(*) FROM passages_fts;"));
        Assert.Equal(await ScalarAsync(paths, "SELECT COUNT(*) FROM sections s JOIN document_revisions r ON r.id=s.revision_id AND r.status='active' JOIN documents d ON d.id=r.document_id AND d.active_revision_id=r.id AND d.tombstoned=0;"), await ScalarAsync(paths, "SELECT COUNT(*) FROM sections_fts;"));
    }

    private static async Task<LegacyEvidenceFixture> SeedLegacyEvidenceAsync(MigrationTestPaths paths, int version,
        bool paused, bool available, bool sourceExists)
    {
        await CreateLegacyAsync(paths, version);
        var source = Path.Combine(paths.SourceDirectory, "legacy.txt");
        if (sourceExists) await File.WriteAllTextAsync(source, RebuiltText, Token);
        var modified = sourceExists ? new DateTimeOffset(File.GetLastWriteTimeUtc(source), TimeSpan.Zero) : DateTimeOffset.UtcNow;
        var sha = sourceExists ? await StorageTestDatabase.HashAsync(source, Token) : "retained-unavailable-source-sha";
        var fixture = new LegacyEvidenceFixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), source,
            sha, RebuiltText.Length, modified,
            new EmbeddingPolicy("ibm-granite/granite-embedding-311m-multilingual", "legacy", "old-model", "old-tokenizer",
                "fp32", 768, 384, "cls", "l2", LegacyPreparation));
        await using var connection = await OpenAsync(paths);
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO projects(id,name,name_key,state,search_generation,created_utc,updated_utc)
              VALUES($project,'Legacy evidence','LEGACY EVIDENCE',$state,11,$now,$now);
            INSERT INTO project_folders(id,project_id,path,path_key,created_utc)
              VALUES($folder,$project,$folder_path,$folder_path,$now);
            INSERT INTO documents(id,project_id,folder_id,path,path_key,file_name,extension,size,modified_utc,
              observation_epoch,tombstoned,available,created_utc,updated_utc)
              VALUES($document,$project,$folder,$path,$path,'legacy.txt','.txt',$size,$modified,5,0,$available,$now,$now);
            INSERT INTO document_revisions(id,document_id,sha256,status,embedding_policy_json,created_utc,activated_utc)
              VALUES($revision,$document,$sha,'active',$policy,$now,$now);
            UPDATE documents SET active_revision_id=$revision,sha256=$sha WHERE id=$document;
            INSERT INTO content_nodes(id,revision_id,ordinal,name,mime_type,relationship,depth,status)
              VALUES($content,$revision,0,'legacy.txt','text/plain','root',0,'indexed');
            INSERT INTO content_nodes(id,revision_id,parent_id,ordinal,name,mime_type,relationship,depth,status)
              VALUES($child,$revision,$content,0,'child.txt','text/plain','attachment',1,'indexed');
            INSERT INTO passages(id,revision_id,content_id,ordinal,display_text,search_text,location_kind,page,structure_path,extraction_method)
              VALUES($passage,$revision,$content,0,$text,$search,1,9,'Legacy / heading',0);
            INSERT INTO passages(id,revision_id,content_id,ordinal,display_text,search_text,location_kind,sheet,cell_range,
              slide,structure_path,email_part,image_frame,extraction_method,ocr_confidence)
              VALUES($attachment,$revision,$child,0,'Sibling literal evidence remains separate.','sibling literal evidence',
                2,'Résumé','C7:D8',2,'Legacy table','body',3,1,0.875);
            INSERT INTO embeddings(passage_rowid,passage_id,revision_id,vector,policy_key)
              SELECT rowid,id,revision_id,$vector,$key FROM passages;
            INSERT INTO index_jobs(id,project_id,document_id,kind,state,expected_epoch,attempt,not_before_utc,lease_until_utc,
              last_error,created_utc,updated_utc,target_policy_key)
              VALUES($job,$project,$document,2,'running',5,3,$now,$now,'old error',$now,$now,$key);
            """ + (version == 7 ? """
            UPDATE passages SET body_text=display_text,title=CASE WHEN id=$passage THEN 'RetainedTitle' ELSE 'ChildTitle' END,heading='RetainedHeading',
              filename='legacy.txt',path=$path,content_name=(SELECT name FROM content_nodes WHERE id=passages.content_id),
              email_subject='RetainedSubject';
            INSERT INTO passages_fts(rowid,body_text,title,heading,filename,path,content_name,sheet,email_subject)
              SELECT rowid,body_text,title,heading,filename,path,content_name,sheet,email_subject FROM passages;
            """ : "INSERT INTO passages_fts(rowid,search_text) SELECT rowid,search_text FROM passages;");
        foreach (var (name, value) in new (string, object)[]
        {
            ("$project", fixture.ProjectId.ToString()), ("$folder", fixture.FolderId.ToString()),
            ("$document", fixture.DocumentId.ToString()), ("$revision", fixture.RevisionId.ToString()),
            ("$content", fixture.ContentId.ToString()), ("$passage", fixture.PassageId.ToString()),
            ("$child", fixture.AttachmentContentId.ToString()), ("$attachment", fixture.AttachmentPassageId.ToString()),
            ("$job", fixture.JobId.ToString()), ("$state", paused ? 1 : 0), ("$available", available ? 1 : 0),
            ("$folder_path", paths.SourceDirectory), ("$path", source), ("$size", fixture.SourceSize),
            ("$modified", modified.ToString("O")), ("$now", DateTimeOffset.UtcNow.ToString("O")),
            ("$sha", sha), ("$policy", fixture.PolicyJson), ("$key", fixture.Policy.Key),
            ("$text", LegacyText), ("$search", LegacySearchText), ("$vector", new byte[1536])
        }) seed.Parameters.AddWithValue(name, value);
        await seed.ExecuteNonQueryAsync(Token);
        return fixture;
    }

    private sealed record LegacyEvidenceFixture(Guid ProjectId, Guid FolderId, Guid DocumentId, Guid RevisionId,
        Guid ContentId, Guid PassageId, Guid AttachmentContentId, Guid AttachmentPassageId, Guid JobId,
        string SourcePath, string SourceSha, long SourceSize, DateTimeOffset ModifiedUtc, EmbeddingPolicy Policy)
    {
        public string PolicyJson => JsonSerializer.Serialize(Policy);
    }
}
