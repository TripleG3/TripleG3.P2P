using System.Collections.Concurrent;
using System.Net;
using TripleG3.P2P.Core;
using TripleG3.P2P.Hubs;
using Xunit;

namespace TripleG3.P2P.IntegrationTests;

public sealed class NotificationsHubIntegrationTests
{
    private static int _portSeed = 30000;

    public static TheoryData<string> Transports => new()
    {
        "udp",
        "tcp"
    };

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Device_Receives_Full_And_Platform_Notification_Over_Transport(string transport)
    {
        var createBus = GetBusFactory(transport);
        var hub = new NotificationsHub(Guid.NewGuid());
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var device = hub.RegisterDevice(deviceId, userId, NotificationPlatform.Android, "en-US");
        var received = new ConcurrentQueue<NotificationWireDelivery>();
        var receiver = createBus();
        var sender = createBus();
        var port = NextPort();
        receiver.SubscribeTo<NotificationWireDelivery>(received.Enqueue);

        try
        {
            await receiver.StartListeningAsync(new ProtocolConfiguration
            {
                LocalAddress = IPAddress.Loopback,
                LocalPort = port,
                SerializationProtocol = SerializationProtocol.LengthPrefixed
            });
            await sender.StartListeningAsync(new ProtocolConfiguration
            {
                LocalAddress = IPAddress.Loopback,
                LocalPort = 0,
                OutboundEndPoints = [new IPEndPoint(IPAddress.Loopback, port)],
                SerializationProtocol = SerializationProtocol.LengthPrefixed
            });
            var dispatch = hub.Route(
                new NotificationRequest(
                    "Match ready",
                    "Open the game.",
                    Category: "game",
                    Data:
                    [
                        new NotificationDataEntry("androidChannelId", "matches"),
                        new NotificationDataEntry("androidSmallIcon", "ic_match")
                    ]),
                NotificationRecipient.ForDevices(device.DeviceId));
            var delivery = Assert.Single(dispatch.Deliveries);

            await sender.SendAsync(delivery.ToWireDelivery());
            await WaitForAsync(() => received.Count == 1);

            var wire = Assert.Single(received);
            Assert.Equal(deviceId, wire.DeviceId);
            Assert.Equal("Match ready", wire.ReadNotification().Title);
            var android = wire.ReadPlatformView<AndroidNotificationView>();
            Assert.Equal("matches", android.ChannelId);
            Assert.Equal("ic_match", android.SmallIcon);
        }
        finally
        {
            await sender.CloseConnectionAsync();
            await receiver.CloseConnectionAsync();
        }
    }

    private static Func<ISerialBus> GetBusFactory(string transport)
        => transport switch
        {
            "udp" => SerialBusFactory.CreateUdp,
            "tcp" => SerialBusFactory.CreateTcp,
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };

    private static int NextPort()
    {
        var port = Interlocked.Increment(ref _portSeed);
        if (port <= 60000) return port;
        Interlocked.Exchange(ref _portSeed, 30000);
        return Interlocked.Increment(ref _portSeed);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMilliseconds)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        throw new TimeoutException("Expected notification delivery did not complete before timeout.");
    }
}