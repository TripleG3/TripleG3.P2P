namespace TripleG3.P2P.Hubs;

public sealed record LiveSessionControl<TStreamDescriptor>(
    LiveSessionControlKind Kind,
    LiveSessionSnapshot<TStreamDescriptor> Session);
