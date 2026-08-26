namespace TripleG3.P2P.Hubs;

public sealed record IosNotificationView(
    string Title,
    string? Subtitle,
    string Body,
    string? Sound,
    int? Badge,
    string? Category,
    string? ThreadId,
    bool IsContentAvailable,
    bool IsMutableContent,
    IReadOnlyList<NotificationAction> Actions);