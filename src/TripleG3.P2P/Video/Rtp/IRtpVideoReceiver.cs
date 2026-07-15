using TripleG3.P2P.Video.Stats;

namespace TripleG3.P2P.Video.Rtp;

public interface IRtpVideoReceiver : IStatsCollector
{
    event Action<EncodedAccessUnit> AccessUnitReceived;

    void ProcessRtp(ReadOnlySpan<byte> datagram);
}