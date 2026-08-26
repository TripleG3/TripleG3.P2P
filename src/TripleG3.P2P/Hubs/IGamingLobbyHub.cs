namespace TripleG3.P2P.Hubs;

public interface IGamingLobbyHub
{
    GamingLobbySnapshot Snapshot { get; }

    event EventHandler<HubStateChangedEventArgs<GamingLobbySnapshot>>? StateChanged;

    GamingLobbySnapshot AddMember(Guid requesterMemberId, Guid memberId, string username);

    GamingLobbySnapshot RemoveMember(Guid requesterMemberId, Guid memberId);

    GamingLobbySnapshot PromoteMember(Guid requesterMemberId, Guid memberId);

    GamingLobbySnapshot DemoteMember(Guid requesterMemberId, Guid memberId);

    GamingLobbySnapshot Leave(Guid memberId);

    GamingLobbySnapshot AddTeam(Guid requesterMemberId, Guid teamId, string name);

    GamingLobbySnapshot RemoveTeam(Guid requesterMemberId, Guid teamId);

    GamingLobbySnapshot AssignMemberToTeam(Guid requesterMemberId, Guid memberId, Guid teamId);

    GamingLobbySnapshot UnassignMemberFromTeam(Guid requesterMemberId, Guid memberId);

    GamingLobbySnapshot SetAudioPolicy(Guid requesterMemberId, GamingLobbyAudioPolicy policy);

    HubDispatch SendChat(Guid senderMemberId, HubAudience audience, Guid teamId, string text);

    HubAudioRoute GetAudioRoute(Guid senderMemberId, HubAudience audience, Guid teamId);

    bool IsAudioRouteCurrent(HubAudioRoute route);

    IReadOnlyList<HubChatMessage> GetMessagesForMember(Guid memberId);
}