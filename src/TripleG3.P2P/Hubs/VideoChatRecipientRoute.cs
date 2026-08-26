namespace TripleG3.P2P.Hubs;

public sealed record VideoChatRecipientRoute(
    Guid HubId,
    Guid SenderMemberId,
    VideoChatMediaKind MediaKind,
    IReadOnlyList<Guid> RecipientMemberIds,
    long RoutingRevision,
    CancellationToken RevocationToken);