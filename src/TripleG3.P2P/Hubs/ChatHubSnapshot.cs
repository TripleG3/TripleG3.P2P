namespace TripleG3.P2P.Hubs;

public sealed record ChatHubSnapshot(
    Guid HubId,
    long Revision,
    IReadOnlyList<HubMember> Members,
    IReadOnlyList<HubChatMessage> Messages,
    IReadOnlyList<HubNotification> Notifications)
{
    public int MemberCount => Members.Count;
}