using System.Diagnostics;
using System.Globalization;
using System.Text;

using ContextMole.Core;
using ContextMole.Documents;
using ContextMole.Infrastructure;
using ContextMole.Storage;

namespace ContextMole.Tests;

public sealed class OutlookMessageTests
{
    private const string FreshProcessVariable = "CONTEXTMOLE_TESTS_FRESH_PROCESS";
    private const string AttachmentText = "Signed contract evidence from the attachment.";
    private const string NestedAttachmentText = "Minutes evidence inside the forwarded message.";

    // An RTF body makes MsgReader build its RTF font table as soon as the message opens.
    private const string RtfBody = """{\rtf1\ansi\ansicpg1252\fromhtml1 \deff0{\fonttbl{\f0\fswiss\fcharset0 Arial;}}{\*\htmltag1 <html>}{\*\htmltag50 <body>}\htmlrtf Contract approved.\htmlrtf0 {\*\htmltag58 </body>}{\*\htmltag27 </html>}}""";

    private static readonly DateTimeOffset SentOn = new(2026, 11, 3, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task MsgHeadersNameTheSenderAndEveryRecipient()
    {
        using var directory = new OutlookTestDirectory();
        var path = directory.Write("figures.msg", OutlookTestFiles.Create(new OutlookTestMessage(
            "Quarterly figures", "Ana Souza", "ana@example.test",
            [
                new("Bruno Lima", "bruno@example.test"),
                new("Doe, Jane", "jane@example.test"),
                new("Carla Reis", "carla@example.test", OutlookRecipientKind.Cc),
                new("audit@example.test", "audit@example.test", OutlookRecipientKind.Bcc)
            ],
            "The figures are attached.", SentOn: SentOn)));

        var result = await ExtractAsync(path);

        Assert.Empty(result.Errors);
        var headers = Assert.Single(result.Root.Sections, section => section.SectionKey == "email:headers");
        Assert.Equal(
            "From: Ana Souza <ana@example.test>\n" +
            "To: Bruno Lima <bruno@example.test>; Doe, Jane <jane@example.test>\n" +
            "Cc: Carla Reis <carla@example.test>\n" +
            "Bcc: audit@example.test\n" +
            $"Date: {SentOn.ToLocalTime().ToString("O", CultureInfo.InvariantCulture)}\n" +
            "Subject: Quarterly figures",
            headers.Text);
        Assert.Contains(result.Root.Sections, section =>
            section.SectionKey == "email:body" && section.Text.Contains("The figures are attached.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForwardedMsgHeadersNameTheirOwnSenderAndRecipients()
    {
        using var directory = new OutlookTestDirectory();
        var path = directory.Write("forward.msg", OutlookTestFiles.Create(WithForwardedMinutes()));

        var result = await ExtractAsync(path);

        Assert.Empty(result.Errors);
        Assert.Equal("From: Ana Souza <ana@example.test>\nTo: Bruno Lima <bruno@example.test>\nCc: \n" +
            $"Date: {SentOn.ToLocalTime().ToString("O", CultureInfo.InvariantCulture)}\nSubject: Contract approved",
            Assert.Single(result.Root.Sections, section => section.SectionKey == "email:headers").Text);
        var forwarded = Assert.Single(result.Root.Attachments, node => node.Name == "Forwarded minutes.msg");
        Assert.Equal("application/vnd.ms-outlook", forwarded.MimeType);
        Assert.Equal("From: Carla Reis <carla@example.test>\nTo: Ana Souza <ana@example.test>\n" +
            "Cc: Bruno Lima <bruno@example.test>\nDate: \nSubject: Forwarded minutes",
            Assert.Single(forwarded.Sections, section => section.SectionKey == "email:headers").Text);
        Assert.Contains(NestedAttachmentText, RecursiveText(forwarded));
        Assert.DoesNotContain("MsgReader.", RecursiveText(result.Root));
    }

    [Fact]
    public void OutlookMailboxesReadLikeMailClientAddresses()
    {
        Assert.Equal("Ana Souza <ana@example.test>", DocumentExtractionRegistry.OutlookMailbox(" Ana Souza ", "ana@example.test"));
        Assert.Equal("ana@example.test", DocumentExtractionRegistry.OutlookMailbox("ANA@example.test", "ana@example.test"));
        Assert.Equal("ana@example.test", DocumentExtractionRegistry.OutlookMailbox(null, "ana@example.test"));
        Assert.Equal("Ana Souza", DocumentExtractionRegistry.OutlookMailbox("Ana Souza", ""));
        Assert.Equal(string.Empty, DocumentExtractionRegistry.OutlookMailbox(null, null));
    }

    /// <summary>
    /// The broker serves materialize_content in its own process and never builds the extractor, which is where the
    /// legacy code pages used to be registered. A test inside the shared test process cannot see that, because other
    /// tests have already registered them, so this one runs itself again in a fresh process.
    /// </summary>
    [Fact]
    public async Task MsgAttachmentsMaterializeInAProcessThatNeverRanTheExtractor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        if (Environment.GetEnvironmentVariable(FreshProcessVariable) != "1")
        {
            var (exitCode, output) = await RunInFreshProcessAsync(
                $"{typeof(OutlookMessageTests).FullName}.{nameof(MsgAttachmentsMaterializeInAProcessThatNeverRanTheExtractor)}",
                cancellationToken);
            Assert.True(exitCode == 0, $"The fresh-process run exited with {exitCode}:\n{output}");
            return;
        }

        Assert.Throws<NotSupportedException>(() => Encoding.GetEncoding(1252));
        await MaterializeMsgAttachmentsAsync(cancellationToken);
    }

    private static async Task MaterializeMsgAttachmentsAsync(CancellationToken cancellationToken)
    {
        await using var database = await StorageTestDatabase.CreateAsync(cancellationToken);
        var (projectId, folderId) = await database.CreateProjectAsync("Outlook materialization", cancellationToken);
        var source = Path.Combine(database.Paths.SourceDirectory, "contract.msg");
        await File.WriteAllBytesAsync(source, OutlookTestFiles.Create(WithForwardedMinutes()), cancellationToken);

        // The index records what the app's extractor produced; the broker only reads it.
        var pending = await database.ObserveAndLeaseAsync(projectId, folderId, source, false, cancellationToken);
        var modified = new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero);
        var rootId = Guid.CreateVersion7();
        var attachmentId = Guid.CreateVersion7();
        var forwardedId = Guid.CreateVersion7();
        var nestedAttachmentId = Guid.CreateVersion7();
        ContentNodeDraft[] nodes =
        [
            new(rootId, null, 0, "contract.msg", "application/vnd.ms-outlook", "root", 0),
            new(attachmentId, rootId, 0, "contract.txt", "text/plain", "email-attachment", 1),
            new(forwardedId, rootId, 1, "Forwarded minutes.msg", "application/vnd.ms-outlook", "email-attachment", 1),
            new(nestedAttachmentId, forwardedId, 0, "minutes.txt", "text/plain", "email-attachment", 2)
        ];
        PassageDraft[] passages =
        [
            new(Guid.CreateVersion7(), rootId, 0, "Contract approved.", "Contract approved.",
                new SourceLocation(LocationKind.EmailPart, EmailPart: "body"), ExtractionMethod.Email, null, null),
            new(Guid.CreateVersion7(), attachmentId, 0, AttachmentText, AttachmentText,
                new SourceLocation(LocationKind.Document), ExtractionMethod.NativeText, null, null),
            new(Guid.CreateVersion7(), nestedAttachmentId, 0, NestedAttachmentText, NestedAttachmentText,
                new SourceLocation(LocationKind.Document), ExtractionMethod.NativeText, null, null)
        ];
        await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length, modified, "unused",
            includeVector: false, nodes: nodes, passages: passages, cancellationToken: cancellationToken);

        using var budget = new GlobalCpuBudget(new StorageFixedCpuSettings());
        var materializer = new ContentMaterializationService(database.Store, database.Paths, budget);

        var attachment = await materializer.MaterializeAsync(projectId, attachmentId, cancellationToken);
        Assert.True(attachment.Temporary);
        Assert.Equal(["contract.txt"], attachment.AttachmentChain);
        Assert.Equal(AttachmentText, await File.ReadAllTextAsync(attachment.LocalPath, cancellationToken));

        var nested = await materializer.MaterializeAsync(projectId, nestedAttachmentId, cancellationToken);
        Assert.Equal(["Forwarded minutes.msg", "minutes.txt"], nested.AttachmentChain);
        Assert.Equal(NestedAttachmentText, await File.ReadAllTextAsync(nested.LocalPath, cancellationToken));
    }

    private static OutlookTestMessage WithForwardedMinutes() => new(
        "Contract approved", "Ana Souza", "ana@example.test", [new("Bruno Lima", "bruno@example.test")],
        "Contract approved.", RtfBody,
        Attachments: [new("contract.txt", Encoding.UTF8.GetBytes(AttachmentText))],
        EmbeddedMessages:
        [
            new("Forwarded minutes", "Carla Reis", "carla@example.test",
                [new("Ana Souza", "ana@example.test"), new("Bruno Lima", "bruno@example.test", OutlookRecipientKind.Cc)],
                "Minutes are attached.", Attachments: [new("minutes.txt", Encoding.UTF8.GetBytes(NestedAttachmentText))])
        ],
        SentOn: SentOn);

    private static async Task<(int ExitCode, string Output)> RunInFreshProcessAsync(string testMethod,
        CancellationToken cancellationToken)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("The test host path is unknown.");
        var start = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        // Under "dotnet exec" the host is dotnet itself; under the apphost it is the test executable.
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(typeof(OutlookMessageTests).Assembly.Location);
        }
        start.ArgumentList.Add("--filter-method");
        start.ArgumentList.Add(testMethod);
        start.Environment[FreshProcessVariable] = "1";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The fresh test process did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await standardOutput + await standardError);
    }

    private static Task<ExtractionResult> ExtractAsync(string path) =>
        new DocumentExtractionRegistry(new OutlookNoOcr())
            .ExtractAsync(new ExtractionRequest(path), TestContext.Current.CancellationToken);

    private static string RecursiveText(ExtractedNode node) =>
        string.Join('\n', node.Sections.Select(section => section.Text)
            .Concat(node.Attachments.Select(RecursiveText)));
}

file sealed class OutlookNoOcr : IOcrEngine
{
    public bool IsAvailable => false;
    public string UnavailableReason => "OCR must not be reached by Outlook message fixtures.";
    public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException(UnavailableReason));
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
        Task.FromException<OcrResult>(new InvalidOperationException(UnavailableReason));
}

file sealed class OutlookTestDirectory : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ContextMole.Tests", Guid.NewGuid().ToString("N"));

    public OutlookTestDirectory() => Directory.CreateDirectory(_root);

    public string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

[Collection(nameof(SqliteIntegrationCollection))]
public sealed class LegacyOutlookHeaderRepairTests
{
    private const string BrokenHeader = "From: MsgReader.Outlook.Storage+Sender\nTo: MsgReader.Outlook.Storage+Recipient\n" +
        "Date: 2026-11-03T14:30:00.0000000+00:00\nSubject: Contract approved";

    [Fact]
    public async Task StartupQueuesReextractionOnlyForDocumentsWithBrokenOutlookHeaders()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await StorageTestDatabase.CreateAsync(token);
        var (projectId, folderId) = await database.CreateProjectAsync("Outlook header repair", token);
        var broken = await CommitAsync(database, projectId, folderId, "broken.msg",
            ("broken.msg", "application/vnd.ms-outlook", "headers", BrokenHeader), token);
        var archived = await CommitAsync(database, projectId, folderId, "mail.zip",
            ("old.msg", "application/octet-stream", "headers", BrokenHeader), token);
        var repaired = await CommitAsync(database, projectId, folderId, "fixed.msg",
            ("fixed.msg", "application/vnd.ms-outlook", "headers",
                "From: Ana Souza <ana@example.test>\nTo: Bruno Lima <bruno@example.test>\nCc: \nDate: \nSubject: Contract"), token);
        var quoted = await CommitAsync(database, projectId, folderId, "bug-report.msg",
            ("bug-report.msg", "application/vnd.ms-outlook", "body", BrokenHeader), token);
        var notes = await CommitAsync(database, projectId, folderId, "notes.txt",
            ("notes.txt", "text/plain", "headers", BrokenHeader), token);
        Assert.Empty(await OpenJobsAsync(database, token));

        await database.Writer.StopAsync(token);
        using (var restarted = new DatabaseWriterService(database.Paths))
        {
            await restarted.StartAsync(token);
            var jobs = await OpenJobsAsync(database, token);
            Assert.Equal(new[] { broken, archived }.Order(), jobs.Keys.Order());
            Assert.All(jobs.Values, kind => Assert.Equal(IndexJobKind.Reindex, kind));
            Assert.DoesNotContain(repaired, jobs.Keys);
            Assert.DoesNotContain(quoted, jobs.Keys);
            Assert.DoesNotContain(notes, jobs.Keys);

            // A repair that fails is left alone, the way failed preparation upgrades are.
            var lease = await restarted.LeaseNextJobAsync(TimeSpan.FromMinutes(1), token)
                ?? throw new InvalidOperationException("The repair job was not leased.");
            await restarted.FailJobAsync(lease, "unreadable", "Source could not be read.", retryable: false, token);
            await restarted.StopAsync(token);
        }

        using var again = new DatabaseWriterService(database.Paths);
        await again.StartAsync(token);
        Assert.Single(await OpenJobsAsync(database, token));
        await again.StopAsync(token);
    }

    private static async Task<Guid> CommitAsync(StorageTestDatabase database, Guid projectId, Guid folderId,
        string fileName, (string Name, string MimeType, string EmailPart, string Text) header, CancellationToken token)
    {
        var path = Path.Combine(database.Paths.SourceDirectory, fileName);
        await File.WriteAllTextAsync(path, $"source bytes for {fileName}", token);
        var pending = await database.ObserveAndLeaseAsync(projectId, folderId, path, false, token);
        var modified = new DateTimeOffset(pending.File.LastWriteTimeUtc, TimeSpan.Zero);
        var rootId = Guid.CreateVersion7();
        var nested = header.Name != fileName;
        var headerNodeId = nested ? Guid.CreateVersion7() : rootId;
        List<ContentNodeDraft> nodes =
            [new(rootId, null, 0, fileName, nested ? "application/zip" : header.MimeType, "root", 0)];
        if (nested)
            nodes.Add(new(headerNodeId, rootId, 0, header.Name, header.MimeType, "archive-entry", 1));
        PassageDraft[] passages =
        [
            new(Guid.CreateVersion7(), headerNodeId, 0, header.Text, TextNormalization.ForSearch(header.Text),
                new SourceLocation(LocationKind.EmailPart, EmailPart: header.EmailPart), ExtractionMethod.Email, null, null)
        ];
        await database.CommitAsync(pending.Job, pending.Sha256, pending.File.Length, modified, "unused",
            includeVector: false, nodes: nodes, passages: passages, cancellationToken: token);
        return pending.Job.DocumentId;
    }

    private static async Task<Dictionary<Guid, IndexJobKind>> OpenJobsAsync(StorageTestDatabase database,
        CancellationToken token)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.Paths.DatabasePath}");
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document_id,kind FROM index_jobs WHERE state IN ('queued','retry_wait','running');";
        var jobs = new Dictionary<Guid, IndexJobKind>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            jobs.Add(Guid.Parse(reader.GetString(0)), (IndexJobKind)reader.GetInt32(1));
        return jobs;
    }
}
