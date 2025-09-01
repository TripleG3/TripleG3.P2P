using System;
using System.Buffers;

namespace TripleG3.P2P.Video
{
    public static class EncodedAccessUnitFactory
    {
        public static EncodedAccessUnit FromAnnexB(ReadOnlyMemory<byte> annexB, long captureTicks, bool isKeyFrame, int width, int height, TripleG3.P2P.Video.Primitives.CodecKind codec)
        {
            var pooled = new ArrayPoolFrame(annexB.Length);
            annexB.Span.CopyTo(pooled.Memory.Span.Slice(0, annexB.Length));
            uint rtpTs = (uint)((captureTicks * 90000) / TimeSpan.TicksPerSecond);
            return new EncodedAccessUnit(pooled, annexB.Length, isKeyFrame, rtpTs, captureTicks);
        }
    }
}
