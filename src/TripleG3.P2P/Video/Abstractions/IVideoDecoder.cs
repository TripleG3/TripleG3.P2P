namespace TripleG3.P2P.Video.Abstractions
{
    [Obsolete("Use TripleG3.P2P.Video.IVideoDecoder. This duplicate abstraction will be removed in 2.0.", false)]
    public interface IVideoDecoder
    {
        /// <summary>
        /// Decode an EncodedAccessUnit. Implementations may be no-op if the consumer handles Annex B.
        /// </summary>
    Task DecodeAsync(TripleG3.P2P.Video.EncodedAccessUnit au);
    }
}
