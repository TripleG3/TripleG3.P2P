namespace TripleG3.P2P.Hubs;

public sealed record ConnectedDeviceHubSnapshot<TDeviceDescriptor, TConnectionRoute, TStreamDescriptor>
{
    public ConnectedDeviceHubSnapshot(
        Guid hubId,
        long revision,
        long membershipRevision,
        IEnumerable<ConnectedDevice<TDeviceDescriptor, TConnectionRoute>> devices,
        IEnumerable<LiveSessionSnapshot<TStreamDescriptor>> sessions,
        IEnumerable<DeviceMembershipChange> membershipHistory)
    {
        HubId = hubId;
        Revision = revision;
        MembershipRevision = membershipRevision;
        Devices = Array.AsReadOnly(devices.ToArray());
        Sessions = Array.AsReadOnly(sessions.ToArray());
        MembershipHistory = Array.AsReadOnly(membershipHistory.ToArray());
    }

    public Guid HubId { get; }

    public long Revision { get; }

    public long MembershipRevision { get; }

    public IReadOnlyList<ConnectedDevice<TDeviceDescriptor, TConnectionRoute>> Devices { get; }

    public IReadOnlyList<LiveSessionSnapshot<TStreamDescriptor>> Sessions { get; }

    public IReadOnlyList<DeviceMembershipChange> MembershipHistory { get; }

    public int ConnectedDeviceCount => Devices.Count;
}