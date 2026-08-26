namespace TripleG3.P2P.Hubs;

public sealed record HubAudioRoute(
    Guid LobbyId,
    Guid SenderMemberId,
    HubAudience Audience,
    Guid TeamId,
    IReadOnlyList<Guid> RecipientMemberIds,
    long Revision);