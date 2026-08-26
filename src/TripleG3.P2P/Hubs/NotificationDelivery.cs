namespace TripleG3.P2P.Hubs;

public sealed record NotificationDelivery(
    Guid DeliveryId,
    Guid HubId,
    long Revision,
    Guid DeviceId,
    Guid UserId,
    NotificationMessage Notification,
    NotificationPlatformView PlatformView,
    DateTimeOffset RoutedAt);