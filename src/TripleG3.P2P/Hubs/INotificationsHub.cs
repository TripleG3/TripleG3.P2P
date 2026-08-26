namespace TripleG3.P2P.Hubs;

public interface INotificationsHub
{
    NotificationsHubSnapshot Snapshot { get; }

    event EventHandler<HubStateChangedEventArgs<NotificationsHubSnapshot>>? StateChanged;

    NotificationDevice RegisterDevice(
        Guid deviceId,
        Guid userId,
        NotificationPlatform platform,
        string locale,
        string? timeZoneId = null);

    bool UnregisterDevice(Guid deviceId);

    NotificationDispatch Route(NotificationRequest request, NotificationRecipient recipient);

    NotificationDispatch Route(NotificationMessage notification, NotificationRecipient recipient);
}