namespace TripleG3.P2P.Video.Rtp;

internal sealed class RtpSequenceNumberGenerator(ushort initial = 0)
{
    public ushort Next() => unchecked(++initial);
}
