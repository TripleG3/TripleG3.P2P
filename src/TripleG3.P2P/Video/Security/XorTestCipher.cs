namespace TripleG3.P2P.Video.Security;

/// <summary>NOT SECURE. Simple XOR for testing pipeline.</summary>
public sealed class XorTestCipher : IVideoPayloadCipher
{
    private readonly byte _key;
    public XorTestCipher(byte key = 0x5A) => _key = key;
    public ReadOnlySpan<byte> Encrypt(RtpPacketMetadata meta, ReadOnlySpan<byte> payload, Span<byte> output)
    { for (int i=0;i<payload.Length;i++) output[i] = (byte)(payload[i]^_key); return output[..payload.Length]; }
    public ReadOnlySpan<byte> Decrypt(RtpPacketMetadata meta, ReadOnlySpan<byte> payload, Span<byte> output)
    { for (int i=0;i<payload.Length;i++) output[i] = (byte)(payload[i]^_key); return output[..payload.Length]; }
}
