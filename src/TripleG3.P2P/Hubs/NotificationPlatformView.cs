namespace TripleG3.P2P.Hubs;

public sealed record NotificationPlatformView(
    NotificationPlatform Platform,
    WindowsNotificationView? Windows,
    AndroidNotificationView? Android,
    IosNotificationView? Ios)
{
    public static NotificationPlatformView ForWindows(WindowsNotificationView view)
        => new(NotificationPlatform.Windows, view, null, null);

    public static NotificationPlatformView ForAndroid(AndroidNotificationView view)
        => new(NotificationPlatform.Android, null, view, null);

    public static NotificationPlatformView ForIos(IosNotificationView view)
        => new(NotificationPlatform.Ios, null, null, view);

    public static NotificationPlatformView Generic { get; } = new(NotificationPlatform.Generic, null, null, null);
}