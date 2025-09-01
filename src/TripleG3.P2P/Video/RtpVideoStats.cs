namespace TripleG3.P2P.Video;

/// <summary>Lightweight sender stats.</summary>
public sealed class RtpVideoSenderStats
{
    public uint PacketsSent { get; internal set; }
    public uint BytesSent { get; internal set; }
    public uint AUsSent { get; internal set; }
}

/// <summary>Lightweight receiver stats.</summary>
public sealed class RtpVideoReceiverStats
{
    public uint PacketsReceived { get; internal set; }
    public uint BytesReceived { get; internal set; }
    public uint PacketsLost { get; internal set; }
}