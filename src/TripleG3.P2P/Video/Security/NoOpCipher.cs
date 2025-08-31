namespace TripleG3.P2P.Video.Security;

public sealed class NoOpCipher : IVideoPayloadCipher
{
    public ReadOnlySpan<byte> Encrypt(RtpPacketMetadata meta, ReadOnlySpan<byte> payload, Span<byte> output)
    { payload.CopyTo(output); return output[..payload.Length]; }
    public ReadOnlySpan<byte> Decrypt(RtpPacketMetadata meta, ReadOnlySpan<byte> payload, Span<byte> output)
    { payload.CopyTo(output); return output[..payload.Length]; }
}
