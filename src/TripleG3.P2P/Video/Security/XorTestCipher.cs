using Microsoft.Extensions.Logging;
namespace TripleG3.P2P.Video.Security;

/// <summary>NOT SECURE. Simple XOR for testing pipeline.</summary>
public sealed class XorTestCipher(byte key = 0x5A, ILogger<XorTestCipher>? log = null) : IVideoPayloadCipher
{
    public ReadOnlySpan<byte> Encrypt(RtpPacketMetadata meta, ReadOnlySpan<byte> payload, Span<byte> output)
    { for (int i=0;i<payload.Length;i++) output[i] = (byte)(payload[i]^key); return output[..payload.Length]; }
    public ReadOnlySpan<byte> Decrypt(RtpPacketMetadata meta, ReadOnlySpan<byte> payload, Span<byte> output)
    {
        var sampleLength = Math.Min(6, payload.Length);
        log?.LogDebug(
            "XOR decrypt ts={Timestamp} seq={SequenceNumber} ssrc={Ssrc} sample={Sample}",
            meta.Timestamp,
            meta.SequenceNumber,
            meta.Ssrc,
            Convert.ToHexString(payload[..sampleLength]));
        for (int i=0;i<payload.Length;i++) output[i] = (byte)(payload[i]^key);
        return output[..payload.Length];
    }
}
