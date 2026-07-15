namespace TripleG3.P2P.Video;

/// <summary>Adapter to bridge stable IVideoPayloadCipher to legacy Security.IVideoPayloadCipher.</summary>
internal sealed class CipherAdapter(IVideoPayloadCipher inner) : Video.Security.IVideoPayloadCipher
{
    public int OverheadBytes => inner.OverheadBytes;

    public ReadOnlySpan<byte> Encrypt(Video.Security.RtpPacketMetadata meta, ReadOnlySpan<byte> payload, Span<byte> output)
    {
        var len = inner.Encrypt(payload, output);
        return output[..len];
    }
    public ReadOnlySpan<byte> Decrypt(Video.Security.RtpPacketMetadata meta, ReadOnlySpan<byte> payload, Span<byte> output)
    {
        var len = inner.Decrypt(payload, output);
        return output[..len];
    }
}