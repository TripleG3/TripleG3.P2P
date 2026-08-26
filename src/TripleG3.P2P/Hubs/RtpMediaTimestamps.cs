namespace TripleG3.P2P.Hubs;

public readonly record struct RtpMediaTimestamps(
    uint AudioTimestamp48k,
    uint VideoTimestamp90k);