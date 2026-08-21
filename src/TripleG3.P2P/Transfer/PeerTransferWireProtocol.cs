using System.Buffers.Binary;
using System.Text.Json;

namespace TripleG3.P2P.Transfer;

internal enum PeerTransferFrameKind : byte
{
    Hello = 1,
    HelloAcknowledgement = 2,
    TransferOpen = 3,
    TransferOpenAcknowledgement = 4,
    Data = 5,
    SenderCompleted = 6,
    TransferCompleted = 7,
    TransferFailed = 8,
    Cancel = 9,
    CancelAcknowledgement = 10,
    Query = 11,
    Status = 12,
    Ping = 13,
    Pong = 14,
    SessionClose = 15,
    SessionCloseAcknowledgement = 16
}

internal readonly record struct PeerTransferFrame(
    PeerTransferFrameKind Kind,
    Guid SessionId,
    Guid TransferId,
    ReadOnlyMemory<byte> Payload);

internal sealed record PeerTransferHelloAcknowledgement(
    bool Accepted,
    string Reason,
    string SenderDeviceId,
    string ReceiverDeviceId);

internal sealed record PeerTransferOpenPayload(PeerTransferDescriptor Descriptor);

internal sealed record PeerTransferOpenAcknowledgement(bool Accepted, string Reason);

internal sealed record PeerTransferFailurePayload(string Reason);

internal sealed record PeerTransferCancellationPayload(Guid RequestId, string Reason);

internal sealed record PeerTransferCancellationAcknowledgement(
    Guid RequestId,
    PeerTransferState TerminalState,
    string Reason);

internal sealed record PeerTransferStatusPayload(PeerTransferSnapshot Snapshot);

internal static class PeerTransferWireProtocol
{
    private const uint Magic = 0x33535450;
    private const byte Version = 1;
    private const int HeaderLength = 4 + 1 + 1 + 16 + 16 + 4;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask WriteAsync(
        Stream stream,
        PeerTransferFrame frame,
        int maximumFrameBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (frame.Payload.Length > maximumFrameBytes)
        {
            throw new InvalidDataException("The peer transfer frame exceeds the configured maximum size.");
        }

        byte[] header = new byte[HeaderLength];
        WriteHeader(header, frame.Kind, frame.SessionId, frame.TransferId, frame.Payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!frame.Payload.IsEmpty)
        {
            await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<PeerTransferFrame> ReadAsync(
        Stream stream,
        int maximumFrameBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[HeaderLength];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4)) != Magic || header[4] != Version)
        {
            throw new InvalidDataException("Unsupported peer transfer session protocol.");
        }

        PeerTransferFrameKind kind = (PeerTransferFrameKind)header[5];
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidDataException("Unknown peer transfer frame kind.");
        }

        Guid sessionId = new(header.AsSpan(6, 16));
        Guid transferId = new(header.AsSpan(22, 16));
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(38, 4));
        if (payloadLength is < 0 or > 16 * 1024 * 1024 || payloadLength > maximumFrameBytes)
        {
            throw new InvalidDataException("The peer transfer frame length is invalid.");
        }

        byte[] payload = payloadLength == 0 ? [] : new byte[payloadLength];
        if (payloadLength > 0)
        {
            await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }

        return new PeerTransferFrame(kind, sessionId, transferId, payload);
    }

    public static byte[] SerializeControl<T>(T value, int maximumControlPayloadBytes)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length > maximumControlPayloadBytes)
        {
            throw new InvalidDataException("The peer transfer control payload exceeds the configured maximum size.");
        }

        return payload;
    }

    public static T DeserializeControl<T>(ReadOnlyMemory<byte> payload, int maximumControlPayloadBytes)
    {
        if (payload.Length > maximumControlPayloadBytes)
        {
            throw new InvalidDataException("The peer transfer control payload exceeds the configured maximum size.");
        }

        return JsonSerializer.Deserialize<T>(payload.Span, JsonOptions)
            ?? throw new InvalidDataException("The peer transfer control payload is invalid.");
    }

    public static byte[] CreateAuthenticationData(
        PeerTransferFrameKind kind,
        Guid sessionId,
        Guid transferId,
        int protectedPayloadLength)
    {
        byte[] header = new byte[HeaderLength];
        WriteHeader(header, kind, sessionId, transferId, protectedPayloadLength);
        return header;
    }

    private static void WriteHeader(
        Span<byte> header,
        PeerTransferFrameKind kind,
        Guid sessionId,
        Guid transferId,
        int payloadLength)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], Magic);
        header[4] = Version;
        header[5] = (byte)kind;
        sessionId.TryWriteBytes(header.Slice(6, 16));
        transferId.TryWriteBytes(header.Slice(22, 16));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(38, 4), payloadLength);
    }

    private static async ValueTask ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The peer transfer session closed unexpectedly.");
            }

            offset += count;
        }
    }
}