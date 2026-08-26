using TripleG3.P2P.Attributes;

namespace TripleG3.P2P.Hubs;

[P2PMessage("HubNotification")]
public sealed record HubNotification(
    [property: P2PProperty(1)] Guid NotificationId,
    [property: P2PProperty(2)] HubNotificationKind Kind,
    [property: P2PProperty(3)] Guid ActorMemberId,
    [property: P2PProperty(4)] Guid SubjectMemberId,
    [property: P2PProperty(5)] Guid TeamId,
    [property: P2PProperty(6)] string Text,
    [property: P2PProperty(7)] int MemberCount,
    [property: P2PProperty(8)] DateTimeOffset CreatedAt);