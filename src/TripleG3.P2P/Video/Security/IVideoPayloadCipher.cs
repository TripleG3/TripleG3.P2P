namespace TripleG3.P2P.Video.Security;

public readonly record struct RtpPacketMetadata(uint Timestamp, ushort SequenceNumber, uint Ssrc, bool Marker);

public interface IVideoPayloadCipher
{
    ReadOnlySpan<byte> Encrypt(RtpPacketMetadata meta, ReadOnlySpan<byte> payload, Span<byte> output);
    ReadOnlySpan<byte> Decrypt(RtpPacketMetadata meta, ReadOnlySpan<byte> payload, Span<byte> output);
}
