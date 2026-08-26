using TripleG3.P2P.Attributes;

namespace TripleG3.P2P.Hubs;

public sealed record NotificationAction(
    [property: P2PProperty(1)] string ActionId,
    [property: P2PProperty(2)] string Title,
    [property: P2PProperty(3)] string? Uri);