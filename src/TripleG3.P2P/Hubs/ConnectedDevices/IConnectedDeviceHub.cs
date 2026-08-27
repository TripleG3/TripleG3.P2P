namespace TripleG3.P2P.Hubs;

public interface IConnectedDeviceHub<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor>
    : IDisposable
    where TDeviceDescriptor : notnull
    where TConnectionRoute : notnull
    where TStreamDescriptor : notnull
{
    ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> Snapshot { get; }

    event EventHandler<ConnectedDeviceHubStateChangedEventArgs<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor>>? StateChanged;

    ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> Connect(
        DeviceConnection connection,
        TDeviceDescriptor descriptor,
        TConnectionRoute route);

    ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> Leave(DeviceConnection connection);

    ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> Disconnect(DeviceConnection connection);

    IReadOnlyList<ConnectedDevice<TDeviceDescriptor, TConnectionRoute>> GetConnectedDevices();

    bool TryGetConnectedDevice(Guid deviceId, out ConnectedDevice<TDeviceDescriptor, TConnectionRoute>? device);

    bool IsConnected(DeviceConnection connection);

    bool IsRouteCurrent(ConnectedDeviceRoute<TConnectionRoute> route);

    ConnectedDeviceDispatch<TMessage, TConnectionRoute> RouteTo<TMessage>(
        DeviceConnection sender,
        Guid recipientDeviceId,
        TMessage message)
        where TMessage : notnull;

    ConnectedDeviceDispatch<TMessage, TConnectionRoute> Broadcast<TMessage>(
        DeviceConnection sender,
        TMessage message)
        where TMessage : notnull;

    ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> OfferSession(
        DeviceConnection origin,
        Guid remoteDeviceId,
        Guid sessionId,
        IEnumerable<LiveStreamDescriptor<TStreamDescriptor>> streams);

    ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> AnswerSession(
        DeviceConnection responder,
        Guid sessionId,
        LiveSessionAnswer answer,
        IEnumerable<LiveStreamDescriptor<TStreamDescriptor>> streams);

    ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> StartSession(
        DeviceConnection origin,
        Guid sessionId);

    ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> ActivateSession(
        DeviceConnection participant,
        Guid sessionId);

    ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> StopSession(
        DeviceConnection participant,
        Guid sessionId,
        string detail);

    ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> CompleteStopSession(
        DeviceConnection participant,
        Guid sessionId,
        string detail);

    ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute> FailSession(
        DeviceConnection participant,
        Guid sessionId,
        string detail);
}