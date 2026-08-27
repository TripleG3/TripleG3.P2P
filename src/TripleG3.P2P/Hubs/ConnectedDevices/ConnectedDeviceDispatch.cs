namespace TripleG3.P2P.Hubs;

public sealed record ConnectedDeviceDispatch<TMessage, TConnectionRoute>(
    Guid DispatchId,
    Guid HubId,
    DeviceConnection Sender,
    TMessage Message,
    IReadOnlyList<ConnectedDeviceRoute<TConnectionRoute>> Recipients,
    DateTimeOffset CreatedAt);
