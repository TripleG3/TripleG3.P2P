namespace TripleG3.P2P.Video.Abstractions
{
    [Obsolete("Use TripleG3.P2P.Video.IVideoEncoder. This duplicate abstraction will be removed in 2.0.", false)]
    public interface IVideoEncoder
    {
        /// <summary>
        /// Encode a pre-encoded Annex B payload passthrough. Implementations should return an EncodedAccessUnit.
        /// </summary>
    Task<TripleG3.P2P.Video.EncodedAccessUnit> EncodeAsync(ReadOnlyMemory<byte> annexB, long timestampTicks, bool isKeyframe, int width, int height, CancellationToken ct = default);
    }
}
