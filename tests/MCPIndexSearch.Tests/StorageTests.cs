using System.Text;

using MCPIndexSearch.Core;
using MCPIndexSearch.Documents;
using MCPIndexSearch.Infrastructure;

using Microsoft.Data.Sqlite;

namespace MCPIndexSearch.Tests;

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class StorageTests
{
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
        var cleared = await database.Store.LoadVectorSnapshotMetadataAsync(projectId, cancellationToken);
        Assert.Equal(0, cleared.EntryCount);
        Assert.False(cleared.IsComplete);
        Assert.True(cleared.SearchGeneration > generationBeforeMigration);
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
        Assert.Equal(0, (await database.Store.LoadVectorSnapshotMetadataAsync(projectId, cancellationToken)).EntryCount);
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
            """.Replace("\n", "\r\n", StringComparison.Ordinal);
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
        var exception = await Assert.ThrowsAsync<McpIndexException>(() =>
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