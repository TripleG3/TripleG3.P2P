namespace TripleG3.P2P.Hubs;

public sealed record NotificationRequest(
    string Title,
    string Body,
    string? Subtitle = null,
    string? ImageUri = null,
    string? Sound = null,
    string? Category = null,
    string? ThreadId = null,
    string? Tag = null,
    int? Badge = null,
    NotificationPriority Priority = NotificationPriority.Normal,
    bool IsSilent = false,
    DateTimeOffset? ExpiresAt = null,
    IReadOnlyList<NotificationDataEntry>? Data = null,
    IReadOnlyList<NotificationAction>? Actions = null);