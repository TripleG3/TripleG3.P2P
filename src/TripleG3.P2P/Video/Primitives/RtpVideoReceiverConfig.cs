using System;

namespace TripleG3.P2P.Video.Primitives
{
    public sealed class RtpVideoReceiverConfig
    {
        public int LocalPort { get; set; }
        public int PayloadType { get; set; } = 96;
        public uint? ExpectedSsrc { get; set; }
        public CodecKind Codec { get; set; } = CodecKind.H264;
        public TimeSpan JitterBufferMax { get; set; } = TimeSpan.FromMilliseconds(250);
    }
}
