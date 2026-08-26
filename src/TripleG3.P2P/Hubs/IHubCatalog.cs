namespace TripleG3.P2P.Hubs;

public interface IHubCatalog
{
    IReadOnlyCollection<Guid> ChatHubIds { get; }

    IReadOnlyCollection<Guid> HostedChatHubIds { get; }

    IReadOnlyCollection<Guid> GamingLobbyIds { get; }

    IReadOnlyCollection<Guid> NotificationsHubIds { get; }

    IReadOnlyCollection<Guid> VideoChatHubIds { get; }

    ChatHub CreateChatHub(Guid hubId, HubOptions? options = null);

    HostedChatHub CreateHostedChatHub(
        Guid hubId,
        Guid initialHostMemberId,
        string initialHostUsername,
        HubOptions? options = null);

    GamingLobbyHub CreateGamingLobby(
        Guid lobbyId,
        Guid initialHostMemberId,
        string initialHostUsername,
        HubOptions? options = null);

    NotificationsHub CreateNotificationsHub(Guid hubId, NotificationsHubOptions? options = null);

    VideoChatHub CreateVideoChatHub(Guid hubId, HubOptions? options = null);

    bool TryGetChatHub(Guid hubId, out ChatHub? hub);

    bool TryGetHostedChatHub(Guid hubId, out HostedChatHub? hub);

    bool TryGetGamingLobby(Guid lobbyId, out GamingLobbyHub? hub);

    bool TryGetNotificationsHub(Guid hubId, out NotificationsHub? hub);

    bool TryGetVideoChatHub(Guid hubId, out VideoChatHub? hub);

    bool RemoveChatHub(Guid hubId, ChatHub expectedHub);

    bool RemoveHostedChatHub(Guid hubId, HostedChatHub expectedHub);

    bool RemoveGamingLobby(Guid lobbyId, GamingLobbyHub expectedLobby);

    bool RemoveNotificationsHub(Guid hubId, NotificationsHub expectedHub);

    bool RemoveVideoChatHub(Guid hubId, VideoChatHub expectedHub);
}