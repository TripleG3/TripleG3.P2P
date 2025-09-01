using System;
using System.Buffers;
using System.Collections.Generic;

namespace TripleG3.P2P.Video.Primitives
{
    /// <summary>
    /// Immutable wrapper for an Annex B encoded access unit. Use FromAnnexB factory.
    /// Disposing returns pooled buffer.
    /// </summary>
    public sealed class EncodedAccessUnit : IDisposable
    {
        private byte[]? _owner;

        public long TimestampTicks { get; }
        public bool IsKeyframe { get; }
        public CodecKind Codec { get; }
        public int Width { get; }
        public int Height { get; }
        public ReadOnlyMemory<byte> Data { get; }
        public IReadOnlyDictionary<string, string>? Metadata { get; }

    internal EncodedAccessUnit(byte[] owner, ReadOnlyMemory<byte> data, long timestampTicks, bool isKeyframe, int width, int height, CodecKind codec, IReadOnlyDictionary<string, string>? metadata)
        {
            _owner = owner;
            Data = data;
            TimestampTicks = timestampTicks;
            IsKeyframe = isKeyframe;
            Width = width;
            Height = height;
            Codec = codec;
            Metadata = metadata;
        }

        public static EncodedAccessUnit FromAnnexB(ReadOnlyMemory<byte> data, long timestampTicks, bool isKeyframe, int width, int height, CodecKind codec, IReadOnlyDictionary<string, string>? metadata = null)
        {
            // allocate pooled buffer and copy so lifetime is explicit
            var src = data.Span;
            var arr = ArrayPool<byte>.Shared.Rent(src.Length);
            src.CopyTo(arr.AsSpan(0, src.Length));
            var mem = new ReadOnlyMemory<byte>(arr, 0, src.Length);
            return new EncodedAccessUnit(arr, mem, timestampTicks, isKeyframe, width, height, codec, metadata);
        }

        public void Dispose()
        {
            if (_owner is { })
            {
                ArrayPool<byte>.Shared.Return(_owner);
                _owner = null;
            }
        }
    }
}
