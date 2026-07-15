namespace TripleG3.P2P.Video.Rtp;

internal sealed class RtpSequenceNumberGenerator
{
    private int _value;

    public RtpSequenceNumberGenerator(ushort initial = 0)
    {
        _value = initial;
    }

    public ushort Next() => unchecked((ushort)Interlocked.Increment(ref _value));
}
