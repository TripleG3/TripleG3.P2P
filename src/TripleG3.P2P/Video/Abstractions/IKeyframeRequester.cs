namespace TripleG3.P2P.Video.Abstractions
{
    public interface IKeyframeRequester
    {
        event Action? KeyframeNeeded;
        void RequestKeyframe();
    }
}
