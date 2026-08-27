namespace TripleG3.P2P.Hubs;

public sealed class ConnectedDeviceHubStateChangedEventArgs<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor>(
    ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> snapshot,
    IReadOnlyList<DeviceMembershipChange> membershipChanges,
    IReadOnlyList<ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute>> sessionDispatches) : EventArgs
{
    public ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor> Snapshot { get; } = snapshot;

    public IReadOnlyList<DeviceMembershipChange> MembershipChanges { get; } = membershipChanges;

    public IReadOnlyList<ConnectedDeviceDispatch<LiveSessionControl<TStreamDescriptor>, TConnectionRoute>> SessionDispatches { get; } = sessionDispatches;
}