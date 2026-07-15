using TripleG3.P2P.Security;

namespace TripleG3.P2P.Video;

internal sealed class CoreCipherAdapter(ICipher inner) : Video.Security.IVideoPayloadCipher
{
    public int OverheadBytes => inner.OverheadBytes;

    public ReadOnlySpan<byte> Encrypt(
        Video.Security.RtpPacketMetadata meta,
        ReadOnlySpan<byte> payload,
        Span<byte> output)
    {
        var length = inner.Encrypt(payload, output);
        return output[..length];
    }

    public ReadOnlySpan<byte> Decrypt(
        Video.Security.RtpPacketMetadata meta,
        ReadOnlySpan<byte> payload,
        Span<byte> output)
    {
        var length = inner.Decrypt(payload, output);
        return output[..length];
    }
}