namespace TripleG3.P2P.Video.Stats;

public sealed class VideoStreamStats
{
    public uint PacketsSent { get; internal set; }
    public uint BytesSent { get; internal set; }
    public uint PacketsReceived { get; internal set; }
    public uint BytesReceived { get; internal set; }
    public uint PacketsLost { get; internal set; }
    public double Jitter { get; internal set; } // RFC3550
    public double? RttEstimateMs { get; internal set; }
    public override string ToString() => $"sent={PacketsSent}({BytesSent}B) recv={PacketsReceived}({BytesReceived}B) lost={PacketsLost} jitter={Jitter:F2} rtt={RttEstimateMs?.ToString("F1")??"-"}ms";
}

public interface IStatsCollector { VideoStreamStats GetStats(); }
