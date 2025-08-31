namespace TripleG3.P2P.Video;

public interface IVideoEncoder
{
    /// <summary>Raised when a complete access unit is ready to send.</summary>
    event Action<EncodedAccessUnit> AccessUnitReady;

    /// <summary>Request an IDR (key) frame at earliest opportunity.</summary>
    void RequestKeyFrame();
}
