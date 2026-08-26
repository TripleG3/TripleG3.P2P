namespace TripleG3.P2P.Hubs;

public sealed record NotificationMessage(
    Guid NotificationId,
    string Title,
    string Body,
    string? Subtitle,
    string? ImageUri,
    string? Sound,
    string? Category,
    string? ThreadId,
    string? Tag,
    int? Badge,
    NotificationPriority Priority,
    bool IsSilent,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<NotificationDataEntry> Data,
    IReadOnlyList<NotificationAction> Actions);