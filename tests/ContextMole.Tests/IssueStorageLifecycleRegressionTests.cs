using ContextMole.Core;
using ContextMole.Storage;
using Microsoft.Data.Sqlite;

namespace ContextMole.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class IssueStorageLifecycleRegressionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThousandComponentIssuesAreOneRootWithBoundedDetailPages()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Bounded", Token);
        var observed = await ObserveAsync(db, project, folder, "mail.eml");
        var errors = Enumerable.Range(0, 1000).Select(i => new ExtractionError("attachment_failed", $"Failure {i}", false, "same.pdf")
            { ComponentKey = $"root/attachment:{i}:same.pdf" }).ToArray();
        await CommitAsync(db, observed, errors);
        var result = await db.Store.ListProjectIssuesAsync(new(project), Token);
        var group = Assert.Single(result.Groups);
        Assert.Equal(1000, result.TotalIssueCount);
        Assert.Equal(1, result.TotalFileCount);
        Assert.Equal(ProjectIssueImpact.Partial, group.Impact);
        Assert.Equal(50, group.Details.Count);
        Assert.NotNull(group.DetailsCursor);
        var seen = group.Details.Select(x => x.Id).ToHashSet();
        var cursor = group.DetailsCursor;
        while (cursor is not null)
        {
            var page = await db.Store.ListProjectIssueDetailsAsync(project, observed.Job.SourcePath,
                ProjectIssueVisibility.Visible, cursor: cursor, cancellationToken: Token);
            Assert.InRange(page.Details.Count, 1, 50);
            foreach (var item in page.Details) Assert.True(seen.Add(item.Id));
            cursor = page.NextCursor;
        }
        Assert.Equal(1000, seen.Count);
        var none = await db.Store.ListProjectIssuesAsync(new(project, Query: "no-such-file"), Token);
        Assert.Empty(none.Groups);
        Assert.Equal(0, none.FilteredFileCount);
        Assert.Equal(1, none.TotalFileCount);
    }

    [Fact]
    public async Task FileKeysetPagingBindsFiltersAndDetectsIssueMutation()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Paging", Token);
        for (var i = 0; i < 28; i++)
        {
            var observation = await ObserveAsync(db, project, folder, $"file-{i:000}.txt");
            await db.Writer.FailJobAsync(observation.Job, "unreadable", "Permission denied", false, Token);
        }
        var first = await db.Store.ListProjectIssuesAsync(new(project, Limit: 25), Token);
        var second = await db.Store.ListProjectIssuesAsync(new(project, Limit: 25, Cursor: first.NextCursor), Token);
        Assert.Equal(25, first.Groups.Count); Assert.Equal(3, second.Groups.Count);
        Assert.Equal(28, first.Groups.Concat(second.Groups).Select(g => g.SourcePath).Distinct().Count());
        var invalid = await Assert.ThrowsAsync<ContextMoleException>(() =>
            db.Store.ListProjectIssuesAsync(new(project, Query: "file", Cursor: first.NextCursor), Token));
        Assert.Equal("invalid_cursor", invalid.Code);
        await db.Writer.HideProjectIssuesAsync(project, first.Groups[0].SourcePath, Token);
        var continued = await db.Store.ListProjectIssuesAsync(new(project, Cursor: first.NextCursor), Token);
        Assert.True(continued.IssuesChangedDuringPaging);
        Assert.Equal(3, continued.Groups.Count);
        Assert.DoesNotContain(continued.Groups, group => first.Groups.Any(prior => prior.SourcePath == group.SourcePath));
    }

    [Fact]
    public async Task HiddenSignatureSurvivesRetryReplacementAndRestartButNewCauseResurfaces()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Signatures", Token);
        var observed = await ObserveAsync(db, project, folder, "file🦔.txt");
        await db.Writer.FailJobAsync(observed.Job, "unreadable", "Failure 🦔", true, Token);
        var first = Assert.Single((await db.Store.ListProjectIssuesAsync(new(project), Token)).Groups).Details[0];
        var hide = await db.Writer.HideProjectIssuesAsync(project, observed.Job.SourcePath, Token);
        Assert.Single(hide.AcknowledgementIds);
        Assert.Empty((await db.Store.ListProjectIssuesAsync(new(project), Token)).Groups);
        await DueAsync(db.Paths.DatabasePath);
        var retry = (await db.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token))!;
        var retained = await db.Store.ListProjectIssuesAsync(new(project, Visibility: ProjectIssueVisibility.Hidden), Token);
        Assert.Equal(ProjectIssueRetryState.Running, Assert.Single(retained.Groups).RetryState);
        await db.Writer.FailJobAsync(retry, "unreadable", "Failure 🦔", true, Token);
        var hidden = Assert.Single((await db.Store.ListProjectIssuesAsync(new(project, Visibility: ProjectIssueVisibility.Hidden), Token)).Groups).Details[0];
        Assert.Equal(first.Signature, hidden.Signature); Assert.NotEqual(first.Id, hidden.Id);
        Assert.Equal(2, hidden.OccurrenceCount); Assert.Equal(first.FirstSeenUtc, hidden.FirstSeenUtc);
        await db.Writer.StopAsync(Token);
        using var restarted = new DatabaseWriterService(db.Paths);
        await restarted.StartAsync(Token);
        Assert.Empty((await db.Store.ListProjectIssuesAsync(new(project), Token)).Groups);
        await DueAsync(db.Paths.DatabasePath);
        var newRetry = (await restarted.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token))!;
        await restarted.FailJobAsync(newRetry, "new_cause", "Failure changed", false, Token);
        Assert.Single((await db.Store.ListProjectIssuesAsync(new(project), Token)).Groups);
        Assert.Empty((await db.Store.ListProjectIssuesAsync(new(project, Visibility: ProjectIssueVisibility.Hidden), Token)).Groups);
        await restarted.StopAsync(Token);
    }

    [Fact]
    public async Task HiddenPartialNewComponentAndChangedSourceResurfaceAndUndoOnlyNewAcknowledgements()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Partial", Token);
        var observed = await ObserveAsync(db, project, folder, "mail.eml");
        var firstError = new ExtractionError("ocr", "OCR missing", false, "same.pdf") { ComponentKey = "root/0:same.pdf/page:1" };
        await CommitAsync(db, observed, [firstError]);
        await db.Writer.HideProjectIssuesAsync(project, observed.Job.SourcePath, Token);
        var retry = await db.ObserveAndLeaseAsync(project, folder, observed.Job.SourcePath, true, Token);
        await CommitAsync(db, retry, [firstError, firstError with { ComponentKey = "root/1:same.pdf/page:1" }]);
        var mixed = Assert.Single((await db.Store.ListProjectIssuesAsync(new(project), Token)).Groups);
        Assert.Equal(1, mixed.HiddenIssueCount); Assert.Single(mixed.Details);
        var secondHide = await db.Writer.HideProjectIssuesAsync(project, observed.Job.SourcePath, Token);
        await db.Writer.RestoreProjectIssueAcknowledgementsAsync(project, secondHide.AcknowledgementIds, Token);
        Assert.Equal(1, (await db.Store.ListProjectIssuesAsync(new(project), Token)).HiddenIssueCount);
        await File.WriteAllTextAsync(observed.Job.SourcePath, "materially new source", Token);
        var changed = await db.ObserveAndLeaseAsync(project, folder, observed.Job.SourcePath, false, Token);
        await CommitAsync(db, changed, [firstError]);
        Assert.Equal(0, (await db.Store.ListProjectIssuesAsync(new(project), Token)).HiddenIssueCount);
        Assert.Single((await db.Store.ListProjectIssuesAsync(new(project), Token)).Groups);
    }

    [Fact]
    public async Task VisibleCauseFiltersDoNotMatchOnlyHiddenComponents()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Filters", Token);
        var observed = await ObserveAsync(db, project, folder, "mail.eml");
        var ocr = new ExtractionError("ocr", "OCR hidden", false, "scan.pdf") { ComponentKey = "root/0:scan.pdf" };
        await CommitAsync(db, observed, [ocr]);
        await db.Writer.HideProjectIssuesAsync(project, observed.Job.SourcePath, Token);
        var retry = await db.ObserveAndLeaseAsync(project, folder, observed.Job.SourcePath, true, Token);
        await CommitAsync(db, retry, [ocr, new ExtractionError("malformed", "Bad attachment", false)]);
        var visible = await db.Store.ListProjectIssuesAsync(new(project, Code: "ocr"), Token);
        Assert.Empty(visible.Groups); Assert.Equal(1, visible.HiddenIssueCount);
        Assert.Single((await db.Store.ListProjectIssuesAsync(new(project, Code: "ocr", Visibility: ProjectIssueVisibility.Hidden), Token)).Groups);
    }

    [Fact]
    public async Task ExclusionRemovesEvidenceRejectsInflightCommitAndIncludesFreshWork()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Exclusion", Token);
        var observed = await ObserveAsync(db, project, folder, "file.txt");
        await CommitAsync(db, observed, []);
        var generation = await db.Store.GetSearchGenerationAsync(project, Token);
        var retry = await db.ObserveAndLeaseAsync(project, folder, observed.Job.SourcePath, true, Token);
        var begin = await db.Writer.BeginRevisionAsync(retry.Job, retry.Sha256, retry.File.Length, retry.File.LastWriteTimeUtc, Token);
        Assert.True(begin.ShouldExtract);
        var excluded = await db.Writer.ExcludeFileAsync(project, observed.Job.SourcePath, Token);
        Assert.True(excluded.Changed); Assert.True(await db.Store.IsFileExcludedAsync(project, observed.Job.SourcePath, Token));
        Assert.Equal(1, (await db.Store.ListProjectsAsync(Token)).Single().ExcludedPathCount);
        Assert.True(await db.Store.GetSearchGenerationAsync(project, Token) > generation);
        Assert.Empty((await db.Store.KeywordSearchAsync(project, "evidence", 10, null, Token)).Candidates);
        Assert.Empty((await db.Store.LoadVectorSnapshotAsync(project, Token)).Entries);
        Assert.False(await db.Writer.CommitRevisionAsync(new(retry.Job.JobId, project, retry.Job.DocumentId,
            begin.RevisionId!.Value, retry.Job.ExpectedObservationEpoch, retry.Sha256, retry.File.Length, retry.File.LastWriteTimeUtc,
            [], [], null, []), Token));
        await db.Writer.FailJobAsync(retry.Job, "late_failure", "must not return", true, Token);
        Assert.Empty(await db.Store.ListProjectErrorsAsync(project, 100, Token));
        await db.Writer.RequestReindexAsync(project, Token);
        await db.Writer.RetryFailedFilesAsync(project, Token);
        await db.Writer.RequestEmbeddingRefreshAsync(project, StorageTestDatabase.TestEmbeddingPolicy, true, Token);
        Assert.Null(await db.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
        var observation = await db.Writer.ObserveFileAsync(new(project, folder, observed.Job.SourcePath, 100, DateTimeOffset.UtcNow, Force: true), Token);
        Assert.True(observation.IsExcluded); Assert.False(observation.Queued);
        var included = await db.Writer.IncludeFileAsync(project, observed.Job.SourcePath, Token);
        Assert.True(included.Changed); Assert.True(included.Queued);
        Assert.NotNull(await db.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
        Assert.Equal("source evidence", await File.ReadAllTextAsync(observed.Job.SourcePath, Token));
    }

    [Fact]
    public async Task RenameOntoExcludedExactDestinationCannotPublishEvidence()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Rename", Token);
        var observed = await ObserveAsync(db, project, folder, "first.txt");
        await CommitAsync(db, observed, []);
        var target = Path.Combine(db.Paths.SourceDirectory, "excluded.txt");
        await db.Writer.ExcludeFileAsync(project, target, Token);
        await db.Writer.HandleRenamedAsync(project, folder, observed.Job.SourcePath, target, Token);
        Assert.Empty((await db.Store.KeywordSearchAsync(project, "evidence", 10, null, Token)).Candidates);
        Assert.Null(await db.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
    }

    [Fact]
    public async Task VerifiedSuccessResolvesAcknowledgementsAndLegacyRepairQueuesFullExtraction()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Repair", Token);
        var observed = await ObserveAsync(db, project, folder, "legacy.txt");
        await CommitAsync(db, observed, [new("partial", "Missing page", false)]);
        await db.Writer.HideProjectIssuesAsync(project, observed.Job.SourcePath, Token);
        var retry = await db.ObserveAndLeaseAsync(project, folder, observed.Job.SourcePath, true, Token);
        await CommitAsync(db, retry, []);
        Assert.Equal(0, await ScalarAsync(db.Paths.DatabasePath, "SELECT COUNT(*) FROM issue_acknowledgements;"));
        Assert.Equal(0, await ScalarAsync(db.Paths.DatabasePath, "SELECT COUNT(*) FROM issue_lifecycles;"));
        await ExecuteAsync(db.Paths.DatabasePath, "UPDATE document_revisions SET preparation_version='legacy' WHERE status='active';");
        var metadata = await db.Store.LoadVectorSnapshotMetadataAsync(project, StorageTestDatabase.TestEmbeddingPolicy, Token);
        Assert.Equal(1, metadata.ReextractionRequiredDocumentCount);
        Assert.Equal(0, metadata.EmbeddingRepairEligibleDocumentCount);
        await db.Writer.RequestEmbeddingRefreshAsync(project, StorageTestDatabase.TestEmbeddingPolicy, false, Token);
        var job = await db.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token);
        Assert.NotNull(job); Assert.Equal(IndexJobKind.Reindex, job.Kind);
        Assert.NotEmpty((await db.Store.KeywordSearchAsync(project, "evidence", 10, null, Token)).Candidates);
    }

    [Fact]
    public async Task MigrationFromEightPreservesUnicodeFailureSignatureThroughReplacement()
    {
        using var paths = new StorageTestPaths();
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await connection.OpenAsync(Token);
            connection.CreateFunction<string?, string>("lexical", v => LexicalText.Canonicalize(v));
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY,applied_utc TEXT NOT NULL);";
            await command.ExecuteNonQueryAsync(Token);
            var assembly = typeof(SqliteSearchStore).Assembly;
            for (var version = 1; version <= 8; version++)
            {
                var resource = assembly.GetManifestResourceNames().Single(n => n.Contains($".Migrations.{version:000}_", StringComparison.Ordinal));
                await using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                command.CommandText = await reader.ReadToEndAsync(Token);
                await command.ExecuteNonQueryAsync(Token);
                command.CommandText = $"INSERT INTO schema_migrations VALUES({version},'old');";
                await command.ExecuteNonQueryAsync(Token);
            }
        }
        var project = Guid.CreateVersion7(); var folder = Guid.CreateVersion7(); var document = Guid.CreateVersion7();
        var source = Path.Combine(paths.SourceDirectory, "file🦔.txt");
        await File.WriteAllTextAsync(source, "source evidence", Token);
        var file = new FileInfo(source); var now = DateTimeOffset.UtcNow.ToString("O");
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO projects(id,name,name_key,state,created_utc,updated_utc) VALUES($project,'Migration','MIGRATION',0,$now,$now);
                INSERT INTO project_folders(id,project_id,path,path_key,created_utc) VALUES($folder,$project,$root,$root,$now);
                INSERT INTO documents(id,project_id,folder_id,path,path_key,file_name,extension,size,modified_utc,created_utc,updated_utc)
                  VALUES($document,$project,$folder,$path,$path,'file🦔.txt','.txt',$size,$modified,$now,$now);
                INSERT INTO project_errors(project_id,document_id,code,message,retryable,attempt,source_path,created_utc)
                  VALUES($project,$document,'failure','Unicode 🦔 failure',0,1,$path,$now);
                """;
            command.Parameters.AddWithValue("$project", project.ToString()); command.Parameters.AddWithValue("$folder", folder.ToString());
            command.Parameters.AddWithValue("$document", document.ToString()); command.Parameters.AddWithValue("$root", paths.SourceDirectory);
            command.Parameters.AddWithValue("$path", source); command.Parameters.AddWithValue("$size", file.Length);
            command.Parameters.AddWithValue("$modified", new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero).ToString("O"));
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(Token);
        }
        using var writer = new DatabaseWriterService(paths);
        await writer.StartAsync(Token);
        var store = new SqliteSearchStore(paths);
        var before = Assert.Single((await store.ListProjectIssuesAsync(new(project), Token)).Groups).Details[0];
        await writer.HideProjectIssuesAsync(project, source, Token);
        await writer.RetryFileAsync(project, document, Token);
        var retry = (await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token))!;
        await writer.FailJobAsync(retry, "failure", "Unicode 🦔 failure", false, Token);
        var after = Assert.Single((await store.ListProjectIssuesAsync(new(project, Visibility: ProjectIssueVisibility.Hidden), Token)).Groups).Details[0];
        Assert.Equal(before.Signature, after.Signature);
        Assert.Equal(9, await ScalarAsync(paths.DatabasePath, "SELECT MAX(version) FROM schema_migrations;"));
        await writer.StopAsync(Token);
    }

    [Fact]
    public async Task ChangedSourceBeforeHashFailureResurfacesDespiteRetainedOldRevision()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Prehash", Token);
        var observed = await ObserveAsync(db, project, folder, "retained.txt");
        await CommitAsync(db, observed, []);
        var retry = await db.ObserveAndLeaseAsync(project, folder, observed.Job.SourcePath, true, Token);
        await db.Writer.FailJobAsync(retry.Job, "access_denied", "Cannot open source", false, Token);
        await db.Writer.HideProjectIssuesAsync(project, observed.Job.SourcePath, Token);
        await File.WriteAllTextAsync(observed.Job.SourcePath, "changed source version much larger", Token);
        var changed = await db.ObserveAndLeaseAsync(project, folder, observed.Job.SourcePath, false, Token);
        await db.Writer.FailJobAsync(changed.Job, "access_denied", "Cannot open source", false, Token);
        Assert.Single((await db.Store.ListProjectIssuesAsync(new(project), Token)).Groups);
        Assert.NotEmpty((await db.Store.KeywordSearchAsync(project, "evidence", 10, null, Token)).Candidates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedExtractionAndSemanticFailuresRetrySourceInsteadOfOnlyEmbeddings(bool retryAll)
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Mixed retry", Token);
        var observed = await ObserveAsync(db, project, folder, "mail.eml");
        await db.CommitAsync(observed.Job, observed.Sha256, observed.File.Length, observed.File.LastWriteTimeUtc,
            "searchable evidence", includeVector: false, errors: [new("ocr_failed", "Missing scan", true)], cancellationToken: Token);
        await db.Writer.RequestEmbeddingRefreshAsync(project, StorageTestDatabase.TestEmbeddingPolicy, true, Token);
        var semantic = (await db.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token))!;
        Assert.Equal(IndexJobKind.EmbeddingRefresh, semantic.Kind);
        await db.Writer.FailJobAsync(semantic, "embedding_refresh_failed", "Model failed", false, Token);
        if (retryAll) await db.Writer.RetryFailedFilesAsync(project, Token);
        else await db.Writer.RetryFileAsync(project, observed.Job.DocumentId, Token);
        var retry = (await db.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token))!;
        Assert.Equal(IndexJobKind.Reindex, retry.Kind);
        Assert.Equal(2, (await db.Store.ListProjectErrorsAsync(project, 100, Token)).Count);
    }

    [Fact]
    public async Task IncludeAfterFolderRemovalClearsRuleWithoutQueueingOutsideScope()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Removed folder", Token);
        var observed = await ObserveAsync(db, project, folder, "excluded.txt");
        await db.Writer.ExcludeFileAsync(project, observed.Job.SourcePath, Token);
        var otherFolder = Path.Combine(db.Paths.RootDirectory, "other"); Directory.CreateDirectory(otherFolder);
        await db.Writer.UpdateProjectAsync(new(project, "Removed folder", [otherFolder]), Token);
        Assert.True(await db.Store.IsFileExcludedAsync(project, observed.Job.SourcePath, Token));
        var include = await db.Writer.IncludeFileAsync(project, observed.Job.SourcePath, Token);
        Assert.True(include.Changed); Assert.False(include.Queued);
        Assert.Empty(await db.Store.ListExcludedFilesAsync(project, Token));
        Assert.Equal(0, (await db.Store.ListProjectsAsync(Token)).Single().ExcludedPathCount);
        Assert.Null(await db.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token));
    }

    [Fact]
    public async Task PausedPartialRenameMovesIssueIdentityAndHideExcludeUseDisplayedPath()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Partial rename", Token);
        var observed = await ObserveAsync(db, project, folder, "old.eml");
        await CommitAsync(db, observed, [new("attachment", "Missing scan", false, "scan.pdf") { ComponentKey="root/0:scan.pdf" }]);
        await db.Writer.HideProjectIssuesAsync(project, observed.Job.SourcePath, Token);
        await db.Writer.SetProjectPausedAsync(project, true, Token);
        var renamed = Path.Combine(db.Paths.SourceDirectory, "new.eml");
        File.Move(observed.Job.SourcePath, renamed);
        await db.Writer.HandleRenamedAsync(project, folder, observed.Job.SourcePath, renamed, Token);
        var visible = Assert.Single((await db.Store.ListProjectIssuesAsync(new(project), Token)).Groups);
        Assert.Equal(renamed, visible.SourcePath); Assert.False(visible.Details[0].IsHidden);
        var hide = await db.Writer.HideProjectIssuesAsync(project, renamed, Token);
        Assert.Single(hide.AcknowledgementIds);
        await db.Writer.ExcludeFileAsync(project, renamed, Token);
        Assert.Empty((await db.Store.ListProjectIssuesAsync(new(project, Visibility: ProjectIssueVisibility.All), Token)).Groups);
        Assert.Empty((await db.Store.KeywordSearchAsync(project, "evidence", 10, null, Token)).Candidates);
    }

    [Fact]
    public async Task EmbeddingSuccessCannotResolveUnverifiedSourceFailure()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Semantic resolution", Token);
        var observed = await ObserveAsync(db, project, folder, "retained.txt");
        var committed = await db.CommitAsync(observed.Job, observed.Sha256, observed.File.Length, observed.File.LastWriteTimeUtc,
            "searchable evidence", includeVector: false, cancellationToken: Token);
        var sourceRetry = await db.ObserveAndLeaseAsync(project, folder, observed.Job.SourcePath, true, Token);
        await db.Writer.FailJobAsync(sourceRetry.Job, "access_denied", "Cannot verify source", false, Token);
        await db.Writer.HideProjectIssuesAsync(project, observed.Job.SourcePath, Token);
        await db.Writer.RequestEmbeddingRefreshAsync(project, StorageTestDatabase.TestEmbeddingPolicy, true, Token);
        var semantic = (await db.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token))!;
        Assert.Equal(IndexJobKind.EmbeddingRefresh, semantic.Kind);
        var refresh = await db.Writer.LoadEmbeddingRefreshSourceAsync(semantic, Token);
        Assert.NotNull(refresh);
        Assert.True(await db.Writer.CommitEmbeddingRefreshAsync(new(semantic.JobId, project, semantic.DocumentId,
            committed.RevisionId, semantic.ExpectedObservationEpoch, [new(committed.PassageId, StorageTestDatabase.TestVector())],
            StorageTestDatabase.TestEmbeddingPolicy), Token));
        var error = Assert.Single(await db.Store.ListProjectErrorsAsync(project, 10, Token));
        Assert.Equal("access_denied", error.Code);
        Assert.Equal(1, (await db.Store.ListProjectIssuesAsync(new(project), Token)).HiddenIssueCount);
    }

    [Fact]
    public async Task UnrelatedFailureChurnDoesNotInvalidateRootComponentPaging()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Component churn", Token);
        var observed = await ObserveAsync(db, project, folder, "mail.eml");
        await CommitAsync(db, observed, Enumerable.Range(0, 60).Select(i => new ExtractionError("partial", $"Missing {i}", false)
            { ComponentKey = $"root/part:{i}" }).ToArray());
        var root = Assert.Single((await db.Store.ListProjectIssuesAsync(new(project), Token)).Groups);
        var unrelated = await ObserveAsync(db, project, folder, "other.txt");
        await db.Writer.FailJobAsync(unrelated.Job, "unreadable", "Other failed", false, Token);
        var next = await db.Store.ListProjectIssueDetailsAsync(project, root.SourcePath, ProjectIssueVisibility.Visible,
            cursor: root.DetailsCursor, cancellationToken: Token);
        Assert.Equal(10, next.Details.Count);
        await db.Writer.HideProjectIssuesAsync(project, root.SourcePath, Token);
        var stale = await Assert.ThrowsAsync<ContextMoleException>(() => db.Store.ListProjectIssueDetailsAsync(project,
            root.SourcePath, ProjectIssueVisibility.Visible, cursor: root.DetailsCursor, cancellationToken: Token));
        Assert.Equal("issues_changed", stale.Code);
    }

    [Fact]
    public async Task HashCorrelatedDiscoveryRenameMovesPartialIssueIdentity()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Hash rename", Token);
        var observed = await ObserveAsync(db, project, folder, "old.eml");
        await CommitAsync(db, observed, [new("partial", "Missing part", false, "scan.pdf") { ComponentKey = "root/0:scan.pdf" }]);
        await db.Writer.HideProjectIssuesAsync(project, observed.Job.SourcePath, Token);
        var renamed = Path.Combine(db.Paths.SourceDirectory, "new.eml"); File.Move(observed.Job.SourcePath, renamed);
        var discovery = await db.ObserveAndLeaseAsync(project, folder, renamed, false, Token);
        Assert.Equal(IndexJobKind.Index, discovery.Job.Kind);
        var begin = await db.Writer.BeginRevisionAsync(discovery.Job, discovery.Sha256, discovery.File.Length,
            discovery.File.LastWriteTimeUtc, Token);
        Assert.False(begin.ShouldExtract); Assert.False(begin.IsStale);
        var root = Assert.Single((await db.Store.ListProjectIssuesAsync(new(project), Token)).Groups);
        Assert.Equal(renamed, root.SourcePath); Assert.Equal(observed.Job.DocumentId, root.DocumentId);
        Assert.Single((await db.Writer.HideProjectIssuesAsync(project, renamed, Token)).AcknowledgementIds);
    }

    [Fact]
    public async Task IncludeNeverRestoresSameHashCachedDonorBeforeFreshExtraction()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Fresh include", Token);
        var donor = await ObserveAsync(db, project, folder, "donor.txt");
        await CommitAsync(db, donor, []);
        File.Delete(donor.Job.SourcePath);
        await db.Writer.HandleDeletedAsync(project, folder, donor.Job.SourcePath, Token);
        var included = Path.Combine(db.Paths.SourceDirectory, "included.txt");
        await File.WriteAllTextAsync(included, "source evidence", Token);
        await db.Writer.ExcludeFileAsync(project, included, Token);
        await db.Writer.IncludeFileAsync(project, included, Token);
        var job = (await db.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token))!;
        var file = new FileInfo(included);
        var begin = await db.Writer.BeginRevisionAsync(job, donor.Sha256, file.Length, file.LastWriteTimeUtc, Token);
        Assert.True(begin.ShouldExtract); Assert.False(begin.IsStale);
        Assert.Equal(0, (await db.Store.ListProjectsAsync(Token)).Single().SearchableCount);
        Assert.Empty((await db.Store.KeywordSearchAsync(project, "evidence", 10, null, Token)).Candidates);
        var info = await db.Store.GetDocumentInfoAsync(project, job.DocumentId, null, Token);
        Assert.NotNull(info);
        Assert.Null(info.ActiveRevisionId);
        Assert.False(info.Searchable);
        Assert.Equal(0, info.PassageCount);
    }

    [Fact]
    public async Task MigratedEmojiAttachmentIdentitySurvivesNewContentIdAndRicherBreadcrumbLabel()
    {
        using var paths = new StorageTestPaths();
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await connection.OpenAsync(Token);
            connection.CreateFunction<string?, string>("lexical", v => LexicalText.Canonicalize(v));
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY,applied_utc TEXT NOT NULL);";
            await command.ExecuteNonQueryAsync(Token);
            var assembly = typeof(SqliteSearchStore).Assembly;
            for (var version = 1; version <= 8; version++)
            {
                var resource = assembly.GetManifestResourceNames().Single(n => n.Contains($".Migrations.{version:000}_", StringComparison.Ordinal));
                await using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                command.CommandText = await reader.ReadToEndAsync(Token);
                await command.ExecuteNonQueryAsync(Token);
                command.CommandText = $"INSERT INTO schema_migrations VALUES({version},'old');";
                await command.ExecuteNonQueryAsync(Token);
            }
        }
        var project = Guid.CreateVersion7(); var folder = Guid.CreateVersion7(); var document = Guid.CreateVersion7();
        var revision = Guid.CreateVersion7(); var root = Guid.CreateVersion7(); var attachment = Guid.CreateVersion7();
        var source = Path.Combine(paths.SourceDirectory, "file🦔.txt");
        await File.WriteAllTextAsync(source, "source evidence", Token);
        var file = new FileInfo(source); var now = DateTimeOffset.UtcNow.ToString("O");
        var sha = await StorageTestDatabase.HashAsync(source, Token);
        var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await connection.OpenAsync(Token); await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO projects(id,name,name_key,state,created_utc,updated_utc) VALUES($project,'Attachment migration','ATTACHMENT MIGRATION',0,$now,$now);
                INSERT INTO project_folders(id,project_id,path,path_key,created_utc) VALUES($folder,$project,$folderpath,$folderpath,$now);
                INSERT INTO documents(id,project_id,folder_id,path,path_key,file_name,extension,size,modified_utc,sha256,created_utc,updated_utc)
                  VALUES($document,$project,$folder,$path,$path,'file🦔.txt','.txt',$size,$modified,$sha,$now,$now);
                INSERT INTO document_revisions(id,document_id,sha256,status,created_utc) VALUES($revision,$document,$sha,'active',$now);
                UPDATE documents SET active_revision_id=$revision WHERE id=$document;
                INSERT INTO content_nodes(id,revision_id,ordinal,name,relationship,depth) VALUES($root,$revision,0,'file🦔.txt','root',0);
                INSERT INTO content_nodes(id,revision_id,parent_id,ordinal,name,relationship,depth) VALUES($attachment,$revision,$root,1,'scan🦔.pdf','attachment',1);
                INSERT INTO project_errors(project_id,document_id,code,message,retryable,attempt,source_path,created_utc,content_id)
                  VALUES($project,$document,'partial','scan🦔.pdf: Unicode 🦔 failure',0,0,$path,$now,$attachment);
                """;
            foreach (var (key, value) in new (string, object)[] { ("$project",project.ToString()),("$folder",folder.ToString()),
                ("$document",document.ToString()),("$revision",revision.ToString()),("$root",root.ToString()),
                ("$attachment",attachment.ToString()),("$folderpath",paths.SourceDirectory),("$path",source),
                ("$size",file.Length),("$modified",modified.ToString("O")),("$sha",sha),("$now",now) })
                command.Parameters.AddWithValue(key,value);
            await command.ExecuteNonQueryAsync(Token);
        }
        using var writer = new DatabaseWriterService(paths); await writer.StartAsync(Token);
        var store = new SqliteSearchStore(paths);
        var prior = Assert.Single((await store.ListProjectIssuesAsync(new(project),Token)).Groups).Details[0];
        Assert.Contains("SCAN🦔.PDF",prior.ComponentKey);
        await writer.HideProjectIssuesAsync(project,source,Token);
        await writer.RetryFileAsync(project,document,Token);
        var job = (await writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1),Token))!;
        var begin = await writer.BeginRevisionAsync(job,sha,file.Length,modified,Token);
        var newRoot=Guid.CreateVersion7(); var newAttachment=Guid.CreateVersion7();
        var nodes = new ContentNodeDraft[] { new(newRoot,null,0,"file🦔.txt","text/plain","root",0),
            new(newAttachment,newRoot,1,"scan🦔.pdf","application/pdf","attachment",1) };
        Assert.True(await writer.CommitRevisionAsync(new(job.JobId,project,document,begin.RevisionId!.Value,
            job.ExpectedObservationEpoch,sha,file.Length,modified,nodes,[],null,
            [new("partial","Unicode 🦔 failure",false,"file🦔.txt › scan🦔.pdf (item 1)") { ContentId=newAttachment }]),Token));
        var after=Assert.Single((await store.ListProjectIssuesAsync(new(project,Visibility:ProjectIssueVisibility.Hidden),Token)).Groups).Details[0];
        Assert.Equal(prior.ComponentKey,after.ComponentKey); Assert.Equal(prior.Signature,after.Signature);
        Assert.NotEqual(prior.ContentId,after.ContentId); Assert.Equal(2,after.OccurrenceCount);
        await writer.StopAsync(Token);
    }

    [Fact]
    public async Task SemanticFailureReplacesOnlyItsCategoryAndKeepsUnverifiedSourceFailureHidden()
    {
        await using var db = await StorageTestDatabase.CreateAsync(Token);
        var (project, folder) = await db.CreateProjectAsync("Failure categories", Token);
        var observed = await ObserveAsync(db, project, folder, "retained.txt");
        await db.CommitAsync(observed.Job, observed.Sha256, observed.File.Length, observed.File.LastWriteTimeUtc,
            "searchable evidence", includeVector: false, cancellationToken: Token);
        var sourceRetry = await db.ObserveAndLeaseAsync(project, folder, observed.Job.SourcePath, true, Token);
        await db.Writer.FailJobAsync(sourceRetry.Job, "access_denied", "Cannot verify source", false, Token);
        await db.Writer.HideProjectIssuesAsync(project, observed.Job.SourcePath, Token);
        await db.Writer.RequestEmbeddingRefreshAsync(project, StorageTestDatabase.TestEmbeddingPolicy, true, Token);
        var semantic = (await db.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1), Token))!;
        await db.Writer.FailJobAsync(semantic, "embedding_refresh_failed", "Model failed", false, Token);
        var errors = await db.Store.ListProjectErrorsAsync(project, 10, Token);
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors,e=>e.Code=="access_denied"); Assert.Contains(errors,e=>e.Code=="embedding_refresh_failed");
        Assert.Equal(1,(await db.Store.ListProjectIssuesAsync(new(project),Token)).HiddenIssueCount);
        await db.Writer.RequestEmbeddingRefreshAsync(project, StorageTestDatabase.TestEmbeddingPolicy, true, Token);
        semantic = (await db.Writer.LeaseNextJobAsync(TimeSpan.FromMinutes(1),Token))!;
        await db.Writer.FailJobAsync(semantic,"embedding_refresh_failed","Model failed",false,Token);
        Assert.Equal(2,(await db.Store.ListProjectErrorsAsync(project,10,Token)).Count);
        Assert.Equal(1,(await db.Store.ListProjectIssuesAsync(new(project),Token)).HiddenIssueCount);
    }

    [Fact]
    public async Task HideReportsAffectedIssueRowsEvenWhenOneAcknowledgementCoversDuplicateSignatures()
    {
        await using var db=await StorageTestDatabase.CreateAsync(Token);
        var (project,folder)=await db.CreateProjectAsync("Duplicate signatures",Token);
        var observed=await ObserveAsync(db,project,folder,"mail.eml");
        var error=new ExtractionError("partial","Legacy unnamed component failed",false);
        await CommitAsync(db,observed,[error,error]);
        var hide=await db.Writer.HideProjectIssuesAsync(project,observed.Job.SourcePath,Token);
        Assert.Single(hide.AcknowledgementIds); Assert.Equal(2,hide.HiddenIssueCount);
        var response=await db.Store.ListProjectIssuesAsync(new(project,Visibility:ProjectIssueVisibility.Hidden),Token);
        Assert.Equal(2,response.HiddenIssueCount); Assert.Equal(2,Assert.Single(response.Groups).HiddenIssueCount);
        var noChanges=await db.Writer.HideProjectIssuesAsync(project,observed.Job.SourcePath,Token);
        Assert.Empty(noChanges.AcknowledgementIds); Assert.Equal(0,noChanges.HiddenIssueCount);
        await db.Writer.RestoreProjectIssueAcknowledgementsAsync(project,hide.AcknowledgementIds,Token);
        var restored=await db.Store.ListProjectIssuesAsync(new(project),Token);
        Assert.Equal(0,restored.HiddenIssueCount); Assert.Equal(2,Assert.Single(restored.Groups).Details.Count);
    }

    private static async Task<(ObservationResult Observation, IndexJobLease Job, FileInfo File, string Sha256)> ObserveAsync(
        StorageTestDatabase db, Guid project, Guid folder, string name)
    {
        var path = Path.Combine(db.Paths.SourceDirectory, name);
        await File.WriteAllTextAsync(path, "source evidence", Token);
        return await db.ObserveAndLeaseAsync(project, folder, path, false, Token);
    }
    private static Task<CommittedTestDocument> CommitAsync(StorageTestDatabase db,
        (ObservationResult Observation, IndexJobLease Job, FileInfo File, string Sha256) observed,
        IReadOnlyList<ExtractionError> errors) => db.CommitAsync(observed.Job, observed.Sha256,
        observed.File.Length, observed.File.LastWriteTimeUtc, "searchable source evidence", errors: errors, cancellationToken: Token);
    private static Task DueAsync(string path) => ExecuteAsync(path,
        "UPDATE index_jobs SET not_before_utc='2000-01-01T00:00:00.0000000+00:00' WHERE state='retry_wait';");
    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path}"); await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(Token);
    }
    private static async Task<long> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path}"); await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));
    }
}
