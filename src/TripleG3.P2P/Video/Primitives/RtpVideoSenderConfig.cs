namespace TripleG3.P2P.Video.Primitives
{
    public sealed class RtpVideoSenderConfig
    {
        public string RemoteIp { get; set; } = "127.0.0.1";
        public int RemotePort { get; set; }
        public int PayloadType { get; set; } = 96;
        public uint Ssrc { get; set; }
        public int Mtu { get; set; } = 1200;
        public int KeyframeIntervalSeconds { get; set; } = 2;
        public CodecKind Codec { get; set; } = CodecKind.H264;
    }
}
