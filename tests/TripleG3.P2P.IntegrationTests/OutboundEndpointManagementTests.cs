using System.Net;
using TripleG3.P2P.Attributes;
using TripleG3.P2P.Core;
using Xunit;

namespace TripleG3.P2P.IntegrationTests;

public sealed class OutboundEndpointManagementTests
{
    private static int _portSeed = 45000;

    [Fact]
    public Task Udp_Endpoints_Can_Be_Added_And_Removed_While_Listening()
        => VerifyEndpointManagementAsync(SerialBusFactory.CreateUdp);

    [Fact]
    public Task Tcp_Endpoints_Can_Be_Added_And_Removed_While_Listening()
        => VerifyEndpointManagementAsync(SerialBusFactory.CreateTcp);

    private static async Task VerifyEndpointManagementAsync(Func<ISerialBus> createBus)
    {
        var basePort = NextPortBlock();
        var sender = createBus();
        var firstReceiver = createBus();
        var secondReceiver = createBus();
        var firstReceived = 0;
        var secondReceived = 0;
        firstReceiver.SubscribeTo<LobbyMessage>(_ => Interlocked.Increment(ref firstReceived));
        secondReceiver.SubscribeTo<LobbyMessage>(_ => Interlocked.Increment(ref secondReceived));

        try
        {
            await firstReceiver.StartListeningAsync(CreateConfiguration(basePort + 1));
            await secondReceiver.StartListeningAsync(CreateConfiguration(basePort + 2));
            await sender.StartListeningAsync(CreateConfiguration(basePort));

            var endpointBus = Assert.IsAssignableFrom<IOutboundEndpointSerialBus>(sender);
            var firstEndpoint = new IPEndPoint(IPAddress.Loopback, basePort + 1);
            var secondEndpoint = new IPEndPoint(IPAddress.Loopback, basePort + 2);
            Assert.Empty(endpointBus.OutboundEndPoints);
            Assert.True(endpointBus.AddOutboundEndPoint(firstEndpoint));
            Assert.True(endpointBus.AddOutboundEndPoint(secondEndpoint));
            Assert.False(endpointBus.AddOutboundEndPoint(secondEndpoint));

            await sender.SendAsync(new LobbyMessage("joined"));
            await WaitForAsync(() => Volatile.Read(ref firstReceived) == 1 && Volatile.Read(ref secondReceived) == 1);

            Assert.True(endpointBus.RemoveOutboundEndPoint(secondEndpoint));
            Assert.False(endpointBus.RemoveOutboundEndPoint(secondEndpoint));

            await sender.SendAsync(new LobbyMessage("left"));
            await WaitForAsync(() => Volatile.Read(ref firstReceived) == 2);
            await Task.Delay(150);
            Assert.Equal(1, Volatile.Read(ref secondReceived));
        }
        finally
        {
            await sender.CloseConnectionAsync();
            await firstReceiver.CloseConnectionAsync();
            await secondReceiver.CloseConnectionAsync();
        }
    }

    private static ProtocolConfiguration CreateConfiguration(int localPort)
        => new()
        {
            LocalAddress = IPAddress.Loopback,
            LocalPort = localPort,
            SerializationProtocol = SerializationProtocol.LengthPrefixed
        };

    private static int NextPortBlock()
    {
        var port = Interlocked.Add(ref _portSeed, 10);
        if (port <= 60000) return port;

        Interlocked.Exchange(ref _portSeed, 45000);
        return Interlocked.Add(ref _portSeed, 10);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4000)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMilliseconds)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.True(condition(), "Condition not met before timeout.");
    }

    [P2PMessage("LobbyMessage")]
    public sealed record LobbyMessage([property: P2PProperty(1)] string Text);
}