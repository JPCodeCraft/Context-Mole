using System.Buffers.Binary;
using System.Text;

using OpenMcdf;

namespace ContextMole.Tests;

internal enum OutlookRecipientKind { To = 1, Cc = 2, Bcc = 3 }

internal sealed record OutlookTestRecipient(string DisplayName, string Email,
    OutlookRecipientKind Kind = OutlookRecipientKind.To);

internal sealed record OutlookTestAttachment(string FileName, byte[] Data);

internal sealed record OutlookTestMessage(
    string Subject,
    string SenderName,
    string SenderEmail,
    IReadOnlyList<OutlookTestRecipient> Recipients,
    string Body,
    string? Rtf = null,
    IReadOnlyList<OutlookTestAttachment>? Attachments = null,
    IReadOnlyList<OutlookTestMessage>? EmbeddedMessages = null,
    DateTimeOffset? SentOn = null);

/// <summary>
/// Writes small Outlook .msg files (MS-OXMSG compound files) for tests. Every string is stored as PT_UNICODE, as
/// current Outlook does, so MsgReader never needs a legacy code page for the properties themselves; the optional
/// RTF body is stored uncompressed ("MELA"), which still makes MsgReader parse it when the message opens.
/// </summary>
internal static class OutlookTestFiles
{
    private const ushort PtLong = 0x0003;
    private const ushort PtSystemTime = 0x0040;
    private const ushort PtUnicode = 0x001F;
    private const ushort PtBinary = 0x0102;
    private const ushort PtObject = 0x000D;
    private const int AttachByValue = 1;
    private const int AttachEmbeddedMessage = 5;

    public static byte[] Create(OutlookTestMessage message)
    {
        using var output = new MemoryStream();
        using (var root = RootStorage.Create(output, OpenMcdf.Version.V3, StorageModeFlags.LeaveOpen))
            WriteMessage(root, message, embedded: false);
        return output.ToArray();
    }

    private static void WriteMessage(OpenMcdf.Storage storage, OutlookTestMessage message, bool embedded)
    {
        var properties = new List<(uint Tag, long Value)>();
        WriteString(storage, 0x001A, "IPM.Note");
        WriteString(storage, 0x0037, message.Subject);
        WriteString(storage, 0x0C1A, message.SenderName);
        WriteString(storage, 0x0C1E, "SMTP");
        WriteString(storage, 0x0C1F, message.SenderEmail);
        WriteString(storage, 0x1000, message.Body);
        if (message.Rtf is not null)
            WriteBinary(storage, 0x1009, UncompressedRtf(message.Rtf));
        if (message.SentOn is { } sentOn)
            properties.Add((Tag(0x0039, PtSystemTime), sentOn.ToFileTime()));

        for (var index = 0; index < message.Recipients.Count; index++)
        {
            var recipient = message.Recipients[index];
            var child = storage.CreateStorage($"__recip_version1.0_#{index:X8}");
            WriteString(child, 0x3001, recipient.DisplayName);
            WriteString(child, 0x3002, "SMTP");
            WriteString(child, 0x3003, recipient.Email);
            WriteString(child, 0x39FE, recipient.Email);
            WriteProperties(child, new byte[8], [(Tag(0x0C15, PtLong), (int)recipient.Kind)]);
        }

        var attachmentIndex = 0;
        foreach (var attachment in message.Attachments ?? [])
        {
            var child = storage.CreateStorage($"__attach_version1.0_#{attachmentIndex++:X8}");
            WriteString(child, 0x3704, attachment.FileName);
            WriteString(child, 0x3707, attachment.FileName);
            WriteBinary(child, 0x3701, attachment.Data);
            WriteProperties(child, new byte[8], [(Tag(0x3705, PtLong), AttachByValue)]);
        }
        foreach (var embeddedMessage in message.EmbeddedMessages ?? [])
        {
            var child = storage.CreateStorage($"__attach_version1.0_#{attachmentIndex++:X8}");
            WriteString(child, 0x3001, embeddedMessage.Subject);
            WriteProperties(child, new byte[8], [(Tag(0x3705, PtLong), AttachEmbeddedMessage)]);
            WriteMessage(child.CreateStorage($"__substg1.0_3701{PtObject:X4}"), embeddedMessage, embedded: true);
        }

        // Top-level and embedded property streams differ only in header length (32 and 24 bytes): reserved, next
        // recipient ID, next attachment ID, recipient count, attachment count, and 8 more reserved bytes at the top.
        var header = new byte[embedded ? 24 : 32];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), message.Recipients.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), attachmentIndex);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), message.Recipients.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), attachmentIndex);
        WriteProperties(storage, header, properties);
    }

    private static uint Tag(ushort id, ushort type) => ((uint)id << 16) | type;

    private static void WriteString(OpenMcdf.Storage storage, ushort id, string value) =>
        WriteStream(storage, $"__substg1.0_{id:X4}{PtUnicode:X4}", Encoding.Unicode.GetBytes(value + "\0"));

    private static void WriteBinary(OpenMcdf.Storage storage, ushort id, byte[] value) =>
        WriteStream(storage, $"__substg1.0_{id:X4}{PtBinary:X4}", value);

    private static void WriteProperties(OpenMcdf.Storage storage, byte[] header, IReadOnlyList<(uint Tag, long Value)> properties)
    {
        var bytes = new byte[header.Length + properties.Count * 16];
        header.CopyTo(bytes, 0);
        for (var index = 0; index < properties.Count; index++)
        {
            var entry = bytes.AsSpan(header.Length + index * 16, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, properties[index].Tag);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 0x6); // readable and writable
            BinaryPrimitives.WriteInt64LittleEndian(entry[8..], properties[index].Value);
        }
        WriteStream(storage, "__properties_version1.0", bytes);
    }

    private static void WriteStream(OpenMcdf.Storage storage, string name, byte[] bytes)
    {
        using var stream = storage.CreateStream(name);
        stream.Write(bytes);
    }

    // MS-OXRTFCP: compressed size (raw size + 12), raw size, "MELA" (stored uncompressed), CRC (unused for MELA).
    private static byte[] UncompressedRtf(string rtf)
    {
        var raw = Encoding.ASCII.GetBytes(rtf);
        var bytes = new byte[16 + raw.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, raw.Length + 12);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), raw.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 0x414C454D);
        raw.CopyTo(bytes, 16);
        return bytes;
    }
}
