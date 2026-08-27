namespace TripleG3.P2P.Hubs;

public sealed record LiveStreamDescriptor<TStreamDescriptor>(
    Guid StreamId,
    string Kind,
    LiveStreamDirection Direction,
    TStreamDescriptor Descriptor);
