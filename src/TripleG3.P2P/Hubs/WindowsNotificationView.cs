namespace TripleG3.P2P.Hubs;

public sealed record WindowsNotificationView(
    string Title,
    string Body,
    string? AttributionText,
    string? HeroImageUri,
    string? LaunchUri,
    string? Tag,
    string? Group,
    NotificationPriority Priority,
    bool IsSilent,
    IReadOnlyList<NotificationAction> Actions);