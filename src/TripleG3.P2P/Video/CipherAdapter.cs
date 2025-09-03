namespace TripleG3.P2P.Video;

/// <summary>Adapter to bridge stable IVideoPayloadCipher to legacy Security.IVideoPayloadCipher.</summary>
internal sealed class CipherAdapter(IVideoPayloadCipher inner) : Video.Security.IVideoPayloadCipher
{
    public ReadOnlySpan<byte> Encrypt(Video.Security.RtpPacketMetadata meta, ReadOnlySpan<byte> payload, Span<byte> output)
    {
        // Copy payload into mutable buffer then call inner in-place.
        payload.CopyTo(output);
        var len = inner.Encrypt(output[..payload.Length]);
        return output[..len];
    }
    public ReadOnlySpan<byte> Decrypt(Video.Security.RtpPacketMetadata meta, ReadOnlySpan<byte> payload, Span<byte> output)
    {
        payload.CopyTo(output);
        var len = inner.Decrypt(output[..payload.Length]);
        return output[..len];
    }
}