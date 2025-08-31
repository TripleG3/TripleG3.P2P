namespace TripleG3.P2P.Video;

public interface IVideoDecoder
{
    /// <summary>Feed an encoded access unit for decode/display.</summary>
    void Submit(EncodedAccessUnit unit);
}
