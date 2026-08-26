using TripleG3.P2P.Attributes;

namespace TripleG3.P2P.Hubs;

public sealed record NotificationDataEntry(
    [property: P2PProperty(1)] string Key,
    [property: P2PProperty(2)] string Value);