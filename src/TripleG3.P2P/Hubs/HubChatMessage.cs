using TripleG3.P2P.Attributes;

namespace TripleG3.P2P.Hubs;

[P2PMessage("HubChatMessage")]
public sealed record HubChatMessage(
    [property: P2PProperty(1)] Guid HubId,
    [property: P2PProperty(2)] long Revision,
    [property: P2PProperty(3)] Guid MessageId,
    [property: P2PProperty(4)] Guid SenderMemberId,
    [property: P2PProperty(5)] string Username,
    [property: P2PProperty(6)] HubAudience Audience,
    [property: P2PProperty(7)] Guid TeamId,
    [property: P2PProperty(8)] string Text,
    [property: P2PProperty(9)] DateTimeOffset SentAt);