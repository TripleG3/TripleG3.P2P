using Microsoft.Extensions.Logging;
namespace TripleG3.P2P.Video.Security;

/// <summary>NOT SECURE. Simple XOR for testing pipeline.</summary>
public sealed class XorTestCipher(byte key = 0x5A, ILogger<XorTestCipher>? log = null) : IVideoPayloadCipher
{
    public ReadOnlySpan<byte> Encrypt(RtpPacketMetadata meta, ReadOnlySpan<byte> payload, Span<byte> output)
    { for (int i=0;i<payload.Length;i++) output[i] = (byte)(payload[i]^key); return output[..payload.Length]; }
    public ReadOnlySpan<byte> Decrypt(RtpPacketMetadata meta, ReadOnlySpan<byte> payload, Span<byte> output)
    {
        // small debug log to help tests — will show if decrypt is invoked
        try
        {
            var sb = new System.Text.StringBuilder();
            int s = Math.Min(6, payload.Length);
            for (int i = 0; i < s; i++) sb.Append(payload[i].ToString() + ",");
            log?.LogDebug("XOR.Decrypt ts={Timestamp} seq={Seq} ssrc={Ssrc} sample={Sample}", meta.Timestamp, meta.SequenceNumber, meta.Ssrc, sb.ToString());
        }
        catch { }
        for (int i=0;i<payload.Length;i++) output[i] = (byte)(payload[i]^key);
        return output[..payload.Length];
    }
}
