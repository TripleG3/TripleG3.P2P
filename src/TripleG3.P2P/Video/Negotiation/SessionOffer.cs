namespace TripleG3.P2P.Video.Negotiation;

public sealed record SessionOffer(string Codec, string ProfileLevelId, int Width, int Height, int Bitrate, string SpropParameterSets);
public sealed record SessionAnswer(bool Accepted, string Codec, string ProfileLevelId, string SpropParameterSets);
