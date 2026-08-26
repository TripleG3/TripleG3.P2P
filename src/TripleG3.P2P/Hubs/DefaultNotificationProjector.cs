namespace TripleG3.P2P.Hubs;

public sealed class DefaultNotificationProjector : INotificationProjector
{
    public NotificationPlatformView Project(NotificationMessage notification, NotificationDevice device)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(device);
        var data = notification.Data.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

        return device.Platform switch
        {
            NotificationPlatform.Windows => NotificationPlatformView.ForWindows(new WindowsNotificationView(
                notification.Title,
                notification.Body,
                notification.Subtitle,
                notification.ImageUri,
                GetValue(data, "launchUri"),
                notification.Tag,
                notification.ThreadId,
                notification.Priority,
                notification.IsSilent,
                notification.Actions)),
            NotificationPlatform.Android => NotificationPlatformView.ForAndroid(new AndroidNotificationView(
                notification.Title,
                notification.Body,
                GetValue(data, "androidChannelId") ?? notification.Category,
                GetValue(data, "androidSmallIcon"),
                notification.ImageUri,
                notification.IsSilent ? null : notification.Sound,
                notification.Tag,
                notification.Priority,
                notification.IsSilent,
                notification.Actions)),
            NotificationPlatform.Ios => NotificationPlatformView.ForIos(new IosNotificationView(
                notification.Title,
                notification.Subtitle,
                notification.Body,
                notification.IsSilent ? null : notification.Sound,
                notification.Badge,
                notification.Category,
                notification.ThreadId,
                notification.IsSilent,
                !string.IsNullOrWhiteSpace(notification.ImageUri),
                notification.Actions)),
            NotificationPlatform.Generic => NotificationPlatformView.Generic,
            _ => throw new ArgumentOutOfRangeException(nameof(device))
        };
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> data, string key)
        => data.TryGetValue(key, out var value) ? value : null;
}