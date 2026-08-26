namespace TripleG3.P2P.Hubs;

[Flags]
public enum VideoChatMediaKind
{
    None = 0,
    Audio = 1,
    Video = 2,
    AudioAndVideo = Audio | Video
}