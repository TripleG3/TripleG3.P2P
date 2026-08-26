namespace TripleG3.P2P.Hubs;

public interface INotificationProjector
{
    NotificationPlatformView Project(NotificationMessage notification, NotificationDevice device);
}