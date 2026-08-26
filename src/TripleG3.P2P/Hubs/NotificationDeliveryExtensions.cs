using System.Text.Json;

namespace TripleG3.P2P.Hubs;

public static class NotificationDeliveryExtensions
{
    public static NotificationWireDelivery ToWireDelivery(
        this NotificationDelivery delivery,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        object platformView = delivery.PlatformView.Platform switch
        {
            NotificationPlatform.Windows => delivery.PlatformView.Windows
                ?? throw new InvalidOperationException("The Windows notification projection is missing."),
            NotificationPlatform.Android => delivery.PlatformView.Android
                ?? throw new InvalidOperationException("The Android notification projection is missing."),
            NotificationPlatform.Ios => delivery.PlatformView.Ios
                ?? throw new InvalidOperationException("The iOS notification projection is missing."),
            NotificationPlatform.Generic => delivery.Notification,
            _ => throw new ArgumentOutOfRangeException(nameof(delivery))
        };
        return new NotificationWireDelivery(
            delivery.DeliveryId,
            delivery.HubId,
            delivery.Revision,
            delivery.DeviceId,
            delivery.UserId,
            delivery.PlatformView.Platform,
            JsonSerializer.Serialize(delivery.Notification, options),
            JsonSerializer.Serialize(platformView, platformView.GetType(), options),
            delivery.RoutedAt);
    }
}