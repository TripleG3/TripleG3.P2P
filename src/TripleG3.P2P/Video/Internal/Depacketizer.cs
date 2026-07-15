using TripleG3.P2P.Video.Rtp;

namespace TripleG3.P2P.Video.Internal;

internal sealed class Depacketizer
{
    private readonly H264RtpDepacketizer _inner = new(new Video.Security.NoOpCipher());

    public void Reset() => _inner.Reset();

    public bool AddPacket(ReadOnlySpan<byte> packet, out EncodedAccessUnit? accessUnit)
    {
        if (_inner.TryProcessPacket(packet, out var completed))
        {
            accessUnit = completed;
            return true;
        }

        accessUnit = null;
        return false;
    }
}