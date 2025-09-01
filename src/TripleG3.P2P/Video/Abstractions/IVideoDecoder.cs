using System.Threading.Tasks;
using TripleG3.P2P.Video;

namespace TripleG3.P2P.Video.Abstractions
{
    public interface IVideoDecoder
    {
        /// <summary>
        /// Decode an EncodedAccessUnit. Implementations may be no-op if the consumer handles Annex B.
        /// </summary>
    Task DecodeAsync(TripleG3.P2P.Video.EncodedAccessUnit au);
    }
}
