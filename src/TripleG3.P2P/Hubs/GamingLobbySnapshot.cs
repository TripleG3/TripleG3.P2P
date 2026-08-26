namespace TripleG3.P2P.Hubs;

public sealed record GamingLobbySnapshot(
    Guid LobbyId,
    long Revision,
    IReadOnlyList<GamingLobbyMember> Members,
    IReadOnlyList<GamingLobbyTeam> Teams,
    IReadOnlyList<HubChatMessage> Messages,
    IReadOnlyList<HubNotification> Notifications,
    GamingLobbyAudioPolicy AudioPolicy)
{
    public int MemberCount => Members.Count;
}