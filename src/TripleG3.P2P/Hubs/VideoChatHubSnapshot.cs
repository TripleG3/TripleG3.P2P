namespace TripleG3.P2P.Hubs;

public sealed record VideoChatHubSnapshot(
    Guid HubId,
    long Revision,
    long RoutingRevision,
    IReadOnlyList<VideoChatMember> Members,
    IReadOnlyList<HubChatMessage> Messages,
    IReadOnlyList<HubNotification> Notifications)
{
    public int MemberCount => Members.Count;
}