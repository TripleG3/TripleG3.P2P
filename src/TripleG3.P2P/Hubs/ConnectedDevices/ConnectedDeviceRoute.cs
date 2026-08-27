namespace TripleG3.P2P.Hubs;

public sealed record ConnectedDeviceRoute<TConnectionRoute>(
    Guid HubId,
    Guid DeviceId,
    Guid ConnectionId,
    long MembershipRevision,
    TConnectionRoute Route,
    CancellationToken RevocationToken);
