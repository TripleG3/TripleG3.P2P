using System.Net;
using TripleG3.P2P.Audio;
using TripleG3.P2P.Attributes;
using TripleG3.P2P.Core;
using TripleG3.P2P.Hubs;
using Xunit;

namespace TripleG3.P2P.IntegrationTests;

public sealed class GamingLobbyHubIntegrationTests
{
    public static TheoryData<string> Transports => new()
    {
        "udp",
        "tcp"
    };

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task All_And_Team_Chat_Use_Exact_Hub_Recipients_Over_Transport(string transport)
    {
        var createBus = GetBusFactory(transport);
        await using var harness = new HubTransportTestHarness(createBus);
        var host = Guid.NewGuid();
        var redPlayer = Guid.NewGuid();
        var bluePlayer = Guid.NewGuid();
        var red = Guid.NewGuid();
        var blue = Guid.NewGuid();
        var lobby = BuildLobby(host, redPlayer, bluePlayer, red, blue);
        var hostSession = await harness.AddMemberAsync(host);
        var redSession = await harness.AddMemberAsync(redPlayer);
        var blueSession = await harness.AddMemberAsync(bluePlayer);

        await harness.PublishAsync(lobby.SendChat(host, HubAudience.All, Guid.Empty, "all"));
        await redSession.WaitForMessageCountAsync(1);
        await blueSession.WaitForMessageCountAsync(1);

        await harness.PublishAsync(lobby.SendChat(redPlayer, HubAudience.Team, red, "red"));
        await hostSession.WaitForMessageCountAsync(1);
        await Task.Delay(150);

        Assert.Equal(["all"], blueSession.Messages.Select(message => message.Text));
        Assert.Equal(["red"], hostSession.Messages.Select(message => message.Text));
        Assert.Equal(["all"], redSession.Messages.Select(message => message.Text));
        Assert.DoesNotContain(lobby.Snapshot.Messages, message => message.Audience == HubAudience.Team);
        Assert.Contains(lobby.GetMessagesForMember(redPlayer), message => message.Text == "red");
        Assert.DoesNotContain(lobby.GetMessagesForMember(bluePlayer), message => message.Text == "red");
    }

    [Fact]
    public async Task Team_Audio_Route_Delivers_Opus_Only_To_The_Selected_Team()
    {
        var host = Guid.NewGuid();
        var redPlayer = Guid.NewGuid();
        var bluePlayer = Guid.NewGuid();
        var red = Guid.NewGuid();
        var blue = Guid.NewGuid();
        var lobby = BuildLobby(host, redPlayer, bluePlayer, red, blue);
        var route = lobby.GetAudioRoute(redPlayer, HubAudience.Team, red);
        var sessions = new Dictionary<Guid, AudioReceiverSession>();

        try
        {
            sessions[host] = await AudioReceiverSession.StartAsync(GetFreeUdpPort());
            sessions[redPlayer] = await AudioReceiverSession.StartAsync(GetFreeUdpPort());
            sessions[bluePlayer] = await AudioReceiverSession.StartAsync(GetFreeUdpPort());
            Assert.True(lobby.IsAudioRouteCurrent(route));

            foreach (var recipientId in route.RecipientMemberIds)
            {
                var config = new RtpAudioConfig
                {
                    RemoteEndPoint = sessions[recipientId].EndPoint,
                    Ssrc = 0xBEEFu
                };
                await using var sender = new RtpAudioSender(config);
                await sender.SendAsync(new byte[] { 0xF8, 0xFF, 0xFE }, new AudioFrameMetadata(960));
            }

            await sessions[host].WaitForFrameCountAsync(1);
            await Task.Delay(150);

            Assert.Equal(1, sessions[host].FrameCount);
            Assert.Equal(0, sessions[redPlayer].FrameCount);
            Assert.Equal(0, sessions[bluePlayer].FrameCount);

            lobby.UnassignMemberFromTeam(host, redPlayer);
            Assert.False(lobby.IsAudioRouteCurrent(route));
        }
        finally
        {
            foreach (var session in sessions.Values)
            {
                await session.DisposeAsync();
            }
        }
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Custom_Team_Message_Is_Transported_Only_To_Authorized_Team_Members(string transport)
    {
        await using var harness = new HubTransportTestHarness(GetBusFactory(transport));
        var host = Guid.NewGuid();
        var redPlayer = Guid.NewGuid();
        var bluePlayer = Guid.NewGuid();
        var red = Guid.NewGuid();
        var blue = Guid.NewGuid();
        var lobby = BuildLobby(host, redPlayer, bluePlayer, red, blue);
        var hostSession = await harness.AddMemberAsync(host);
        var redSession = await harness.AddMemberAsync(redPlayer);
        var blueSession = await harness.AddMemberAsync(bluePlayer);
        var hostMessages = hostSession.Subscribe<PlayerPosition>();
        var redMessages = redSession.Subscribe<PlayerPosition>();
        var blueMessages = blueSession.Subscribe<PlayerPosition>();

        var dispatch = lobby.RouteMessage(redPlayer, HubAudience.Team, red, new PlayerPosition(10, 20));
        await harness.PublishAsync(dispatch);
        await WaitForAsync(() => hostMessages.Count == 1);
        await Task.Delay(150);

        Assert.Empty(redMessages);
        Assert.Empty(blueMessages);
        var delivered = Assert.Single(hostMessages);
        Assert.Equal(10, delivered.X);
        Assert.Equal(20, delivered.Y);
        Assert.Empty(lobby.Snapshot.Messages);
    }

    private static GamingLobbyHub BuildLobby(Guid host, Guid redPlayer, Guid bluePlayer, Guid red, Guid blue)
    {
        var lobby = new GamingLobbyHub(Guid.NewGuid(), host, "Host");
        lobby.AddMember(host, redPlayer, "RedPlayer");
        lobby.AddMember(host, bluePlayer, "BluePlayer");
        lobby.AddTeam(host, red, "Red");
        lobby.AddTeam(host, blue, "Blue");
        lobby.AssignMemberToTeam(host, host, red);
        lobby.AssignMemberToTeam(host, redPlayer, red);
        lobby.AssignMemberToTeam(host, bluePlayer, blue);
        return lobby;
    }

    private static Func<ISerialBus> GetBusFactory(string transport)
        => transport switch
        {
            "udp" => SerialBusFactory.CreateUdp,
            "tcp" => SerialBusFactory.CreateTcp,
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };

    private static int GetFreeUdpPort()
    {
        using var client = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMilliseconds)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        throw new TimeoutException("Expected custom gaming message was not delivered before timeout.");
    }

    [P2PMessage("PlayerPosition")]
    public sealed record PlayerPosition(
        [property: P2PProperty(1)] int X,
        [property: P2PProperty(2)] int Y);

    private sealed class AudioReceiverSession : IAsyncDisposable
    {
        private readonly RtpAudioReceiver _receiver;
        private int _frameCount;

        private AudioReceiverSession(RtpAudioReceiver receiver, IPEndPoint endPoint)
        {
            _receiver = receiver;
            EndPoint = endPoint;
            _receiver.AudioFrameReceived += _ => Interlocked.Increment(ref _frameCount);
        }

        public IPEndPoint EndPoint { get; }

        public int FrameCount => Volatile.Read(ref _frameCount);

        public static async Task<AudioReceiverSession> StartAsync(int port)
        {
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);
            var receiver = new RtpAudioReceiver(new RtpAudioConfig
            {
                LocalAddress = IPAddress.Loopback,
                LocalPort = port,
                RemoteEndPoint = endpoint,
                Ssrc = 0xBEEFu
            });
            var session = new AudioReceiverSession(receiver, endpoint);
            await receiver.StartAsync();
            return session;
        }

        public async Task WaitForFrameCountAsync(int expectedCount, int timeoutMilliseconds = 5000)
        {
            var started = Environment.TickCount64;
            while (Environment.TickCount64 - started < timeoutMilliseconds)
            {
                if (FrameCount >= expectedCount) return;
                await Task.Delay(25);
            }

            throw new TimeoutException("Expected RTP audio delivery did not complete before timeout.");
        }

        public async ValueTask DisposeAsync() => await _receiver.DisposeAsync();
    }
}