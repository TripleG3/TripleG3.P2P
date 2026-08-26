namespace TripleG3.P2P.Hubs;

public sealed record HubDispatchMetadata(
    Guid HubId,
    long Revision,
    Guid SenderMemberId,
    HubAudience Audience,
    Guid TeamId,
    DateTimeOffset CreatedAt);