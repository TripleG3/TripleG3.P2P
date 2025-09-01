using System;
using System.Buffers;

namespace TripleG3.P2P.Video
{
    public static class EncodedAccessUnitFactory
    {
        public static EncodedAccessUnit FromAnnexB(ReadOnlyMemory<byte> annexB, long captureTicks, bool isKeyFrame, int width, int height, TripleG3.P2P.Video.Primitives.CodecKind codec)
        {
            var arr = ArrayPool<byte>.Shared.Rent(annexB.Length);
            annexB.Span.CopyTo(new Span<byte>(arr, 0, annexB.Length));
            var pooled = new ArrayPoolFrame(annexB.Length);
            pooled.Memory.Span.CopyTo(new Span<byte>(arr, 0, annexB.Length));
            return new EncodedAccessUnit(new ReadOnlyMemory<byte>(arr, 0, annexB.Length), isKeyFrame, 0, captureTicks, pooled);
        }
    }
}
