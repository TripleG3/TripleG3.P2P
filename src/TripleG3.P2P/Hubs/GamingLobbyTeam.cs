namespace TripleG3.P2P.Hubs;

public sealed record GamingLobbyTeam(
    Guid TeamId,
    string Name,
    int MemberCount);