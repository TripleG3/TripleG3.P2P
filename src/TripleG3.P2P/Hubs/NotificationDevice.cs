namespace TripleG3.P2P.Hubs;

public sealed record NotificationDevice(
    Guid DeviceId,
    Guid UserId,
    NotificationPlatform Platform,
    string Locale,
    string? TimeZoneId,
    DateTimeOffset RegisteredAt);