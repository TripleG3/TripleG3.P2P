namespace TripleG3.P2P.Video.Primitives
{
    public sealed class RtpVideoSenderStats
    {
        public DateTimeOffset Timestamp { get; set; }
        public double BitrateKbps { get; set; }
        public double FrameRate { get; set; }
        public uint PacketsSent { get; set; }
        public uint AUsSent { get; set; }
        public uint NacksReceived { get; set; }
        public double RetransmitPercent { get; set; }
    }
}
