using ContextMole.Core;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ContextMole.Storage;

public sealed partial class DatabaseWriterService
{
    /// <summary>
    /// Text that v0.5.0 and earlier wrote at the start of every Outlook .msg header block, in place of the sender
    /// (and recipients): the type name of MsgReader's sender object. No real header starts with it.
    /// </summary>
    internal const string LegacyOutlookHeaderPrefix = "From: MsgReader.Outlook.Storage+";

    /// <summary>
    /// Queues a source-backed reindex of only the documents whose active evidence still has a broken Outlook header
    /// (looking only at .msg content nodes, so the startup check stays cheap on large indexes),
    /// so the fix reaches existing indexes without bumping the preparation version, which would re-extract and
    /// re-OCR every document. A re-extracted header no longer matches, so the next startup finds nothing. Documents
    /// with open work are left to it, and one whose repair already failed under this preparation is not reopened,
    /// matching how preparation upgrades treat failures.
    /// </summary>
    internal static async Task<int> QueueLegacyOutlookHeaderRepairsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        var documents = new List<(Guid ProjectId, Guid DocumentId)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT DISTINCT d.project_id,d.id
                FROM documents d
                JOIN content_nodes c ON c.revision_id=d.active_revision_id
                JOIN passages p ON p.content_id=c.id
                WHERE d.tombstoned=0
                  AND (c.mime_type='application/vnd.ms-outlook' OR c.name LIKE '%.msg')
                  AND p.email_part='headers'
                  AND substr(p.display_text,1,length($prefix))=$prefix
                  AND NOT EXISTS(SELECT 1 FROM index_jobs j WHERE j.document_id=d.id
                    AND (j.state IN ('queued','retry_wait','running') OR
                         (j.state='failed' AND j.kind=$reindex AND j.target_policy_key=$preparation)));
                """;
            select.Parameters.AddWithValue("$prefix", LegacyOutlookHeaderPrefix);
            select.Parameters.AddWithValue("$reindex", (int)IndexJobKind.Reindex);
            select.Parameters.AddWithValue("$preparation", "preparation:" + IndexPreparation.Version);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                documents.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1))));
        }
        if (documents.Count == 0)
            return 0;

        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var (projectId, documentId) in documents)
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE documents SET observation_epoch=observation_epoch+1,updated_utc=$now WHERE id=$id;",
                [new("$now", now), new("$id", documentId.ToString())], cancellationToken).ConfigureAwait(false);
            long epoch;
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT observation_epoch FROM documents WHERE id=$id;";
                read.Parameters.AddWithValue("$id", documentId.ToString());
                epoch = Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            }
            await UpsertOpenJobAsync(connection, transaction, projectId, documentId, epoch, IndexJobKind.Reindex, now,
                cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return documents.Count;
    }

    private async Task RepairLegacyEvidenceForMaintenanceAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            var queued = await QueueLegacyOutlookHeaderRepairsAsync(connection, cancellationToken).ConfigureAwait(false);
            if (queued > 0)
                _logger.LogInformation("Queued {Count} documents with broken Outlook email headers for re-extraction", queued);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            // Like history pruning, a repair must never stop the writer; it is retried at the next startup.
            _logger.LogWarning(exception, "Documents with broken Outlook email headers could not be queued for re-extraction");
        }
    }
}
