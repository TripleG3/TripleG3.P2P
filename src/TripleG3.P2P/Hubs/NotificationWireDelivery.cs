using System.Text.Json;
using TripleG3.P2P.Attributes;

namespace TripleG3.P2P.Hubs;

[P2PMessage("NotificationDelivery")]
public sealed record NotificationWireDelivery(
    [property: P2PProperty(1)] Guid DeliveryId,
    [property: P2PProperty(2)] Guid HubId,
    [property: P2PProperty(3)] long Revision,
    [property: P2PProperty(4)] Guid DeviceId,
    [property: P2PProperty(5)] Guid UserId,
    [property: P2PProperty(6)] NotificationPlatform Platform,
    [property: P2PProperty(7)] string NotificationJson,
    [property: P2PProperty(8)] string PlatformViewJson,
    [property: P2PProperty(9)] DateTimeOffset RoutedAt)
{
    public NotificationMessage ReadNotification(JsonSerializerOptions? options = null)
        => JsonSerializer.Deserialize<NotificationMessage>(NotificationJson, options)
            ?? throw new InvalidDataException("The full notification payload is invalid.");

    public TView ReadPlatformView<TView>(JsonSerializerOptions? options = null)
    {
        var expectedType = Platform switch
        {
            NotificationPlatform.Windows => typeof(WindowsNotificationView),
            NotificationPlatform.Android => typeof(AndroidNotificationView),
            NotificationPlatform.Ios => typeof(IosNotificationView),
            NotificationPlatform.Generic => typeof(NotificationMessage),
            _ => throw new InvalidDataException("The notification platform is invalid.")
        };
        if (typeof(TView) != expectedType)
        {
            throw new InvalidOperationException($"Platform {Platform} requires {expectedType.Name}.");
        }
        return JsonSerializer.Deserialize<TView>(PlatformViewJson, options)
            ?? throw new InvalidDataException("The platform notification payload is invalid.");
    }
}