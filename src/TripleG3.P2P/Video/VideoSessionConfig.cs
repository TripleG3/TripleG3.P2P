namespace TripleG3.P2P.Video;

public sealed record VideoSessionConfig(int Width, int Height, int Bitrate, int Fps, string Codec = "H264", bool LowLatency = true, int MTU = 1200);
