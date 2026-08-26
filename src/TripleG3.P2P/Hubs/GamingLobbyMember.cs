namespace TripleG3.P2P.Hubs;

public sealed record GamingLobbyMember(
    Guid MemberId,
    string Username,
    HubMemberRole Role,
    Guid TeamId,
    DateTimeOffset JoinedAt);