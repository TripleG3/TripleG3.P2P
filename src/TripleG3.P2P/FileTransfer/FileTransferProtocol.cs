using System.Buffers.Binary;
using System.Text;

namespace TripleG3.P2P.FileTransfer;

internal static class FileTransferProtocol
{
    private const uint Magic = 0x33504654;
    private const byte Version = 1;

    public enum MessageKind : byte
    {
        PushOffer = 1,
        PullRequest = 2,
        Accepted = 3,
        Rejected = 4,
        Data = 5,
        Completed = 6,
        Cancelled = 7
    }

    public static async ValueTask WriteHeaderAsync(Stream stream, MessageKind kind, Guid id, string fileName, long length, string sha256, CancellationToken cancellationToken)
    {
        var nameBytes = Encoding.UTF8.GetBytes(fileName);
        var hashBytes = Encoding.ASCII.GetBytes(sha256);
        if (nameBytes.Length > ushort.MaxValue || hashBytes.Length > byte.MaxValue) throw new InvalidDataException("File transfer metadata is too large.");
        var buffer = new byte[4 + 1 + 1 + 16 + 2 + nameBytes.Length + 8 + 1 + hashBytes.Length];
        var offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), Magic); offset += 4;
        buffer[offset++] = Version;
        buffer[offset++] = (byte)kind;
        id.TryWriteBytes(buffer.AsSpan(offset, 16)); offset += 16;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)nameBytes.Length); offset += 2;
        nameBytes.CopyTo(buffer.AsSpan(offset)); offset += nameBytes.Length;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), length); offset += 8;
        buffer[offset++] = (byte)hashBytes.Length;
        hashBytes.CopyTo(buffer.AsSpan(offset));
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<(MessageKind Kind, Guid Id, string FileName, long Length, string Sha256)> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var fixedPart = new byte[4 + 1 + 1 + 16 + 2];
        await ReadExactAsync(stream, fixedPart, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(0, 4)) != Magic || fixedPart[4] != Version) throw new InvalidDataException("Unsupported file transfer protocol.");
        var kind = (MessageKind)fixedPart[5];
        if (!Enum.IsDefined(kind)) throw new InvalidDataException("Unknown file transfer message.");
        var id = new Guid(fixedPart.AsSpan(6, 16));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(22, 2));
        var nameBytes = new byte[nameLength];
        await ReadExactAsync(stream, nameBytes, cancellationToken).ConfigureAwait(false);
        var variablePart = new byte[8 + 1];
        await ReadExactAsync(stream, variablePart, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt64LittleEndian(variablePart.AsSpan(0, 8));
        if (length < 0) throw new InvalidDataException("Negative file length.");
        var hashLength = variablePart[8];
        var hashBytes = new byte[hashLength];
        await ReadExactAsync(stream, hashBytes, cancellationToken).ConfigureAwait(false);
        return (kind, id, Encoding.UTF8.GetString(nameBytes), length, Encoding.ASCII.GetString(hashBytes));
    }

    public static async ValueTask WriteDecisionAsync(Stream stream, bool accepted, string? reason, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(reason ?? string.Empty);
        if (bytes.Length > ushort.MaxValue) throw new InvalidDataException("Decision reason is too long.");
        var buffer = new byte[1 + 2 + bytes.Length];
        buffer[0] = accepted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(1, 2), (ushort)bytes.Length);
        bytes.CopyTo(buffer.AsSpan(3));
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<(bool Accepted, string? Reason)> ReadDecisionAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[3];
        await ReadExactAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(1, 2));
        var bytes = new byte[length];
        await ReadExactAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        return (prefix[0] == 1, bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes));
    }

    public static async ValueTask ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
    }
}
