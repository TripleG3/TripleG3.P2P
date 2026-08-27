namespace TripleG3.P2P.Hubs;

public sealed record ConnectedDevice<TDeviceDescriptor, TConnectionRoute>(
    Guid DeviceId,
    Guid ConnectionId,
    TDeviceDescriptor Descriptor,
    TConnectionRoute Route,
    DateTimeOffset ConnectedAt);
