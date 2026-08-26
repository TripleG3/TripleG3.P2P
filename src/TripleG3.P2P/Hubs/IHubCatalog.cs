namespace TripleG3.P2P.Hubs;

public interface IHubCatalog
{
    IReadOnlyCollection<Guid> ChatHubIds { get; }

    IReadOnlyCollection<Guid> HostedChatHubIds { get; }

    IReadOnlyCollection<Guid> GamingLobbyIds { get; }

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

    bool TryGetChatHub(Guid hubId, out ChatHub? hub);

    bool TryGetHostedChatHub(Guid hubId, out HostedChatHub? hub);

    bool TryGetGamingLobby(Guid lobbyId, out GamingLobbyHub? hub);

    bool RemoveChatHub(Guid hubId, ChatHub expectedHub);

    bool RemoveHostedChatHub(Guid hubId, HostedChatHub expectedHub);

    bool RemoveGamingLobby(Guid lobbyId, GamingLobbyHub expectedLobby);
}