namespace TripleG3.P2P.Hubs;

public sealed record AndroidNotificationView(
    string Title,
    string Body,
    string? ChannelId,
    string? SmallIcon,
    string? LargeIconUri,
    string? Sound,
    string? CollapseKey,
    NotificationPriority Priority,
    bool IsSilent,
    IReadOnlyList<NotificationAction> Actions);