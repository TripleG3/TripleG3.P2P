namespace TripleG3.P2P.Hubs;

public sealed record HubDispatch(
    HubChatMessage Message,
    IReadOnlyList<Guid> RecipientMemberIds,
    long Revision);