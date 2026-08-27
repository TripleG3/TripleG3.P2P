namespace TripleG3.P2P.Hubs;

public sealed record DeviceMembershipChange(
    Guid ChangeId,
    DeviceMembershipChangeKind Kind,
    Guid DeviceId,
    Guid ConnectionId,
    Guid PreviousConnectionId,
    long MembershipRevision,
    DateTimeOffset CreatedAt);
