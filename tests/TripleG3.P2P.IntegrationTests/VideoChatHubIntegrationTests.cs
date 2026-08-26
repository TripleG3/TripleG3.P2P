using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using TripleG3.P2P.Audio;
using TripleG3.P2P.Attributes;
using TripleG3.P2P.Core;
using TripleG3.P2P.Hubs;
using TripleG3.P2P.Video;
using TripleG3.P2P.Video.Primitives;
using Xunit;
using VideoAccessUnit = TripleG3.P2P.Video.EncodedAccessUnit;

namespace TripleG3.P2P.IntegrationTests;

public sealed class VideoChatHubIntegrationTests
{
    public static TheoryData<string> Transports => new()
    {
        "udp",
        "tcp"
    };

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Text_And_Custom_Signaling_Route_To_Exact_Members_Over_Transport(string transport)
    {
        await using var harness = new HubTransportTestHarness(GetBusFactory(transport));
        var hub = new VideoChatHub(Guid.NewGuid());
        var sender = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var departed = Guid.NewGuid();
        var senderSession = await harness.AddMemberAsync(sender);
        var recipientSession = await harness.AddMemberAsync(recipient);
        var departedSession = await harness.AddMemberAsync(departed);
        var senderSignals = senderSession.Subscribe<MediaSignal>();
        var recipientSignals = recipientSession.Subscribe<MediaSignal>();
        var departedSignals = departedSession.Subscribe<MediaSignal>();
        hub.Join(sender, "Sender");
        hub.Join(recipient, "Recipient");
        hub.Join(departed, "Departed");

        await harness.PublishAsync(hub.SendMessage(sender, "hello"));
        await recipientSession.WaitForMessageCountAsync(1);
        await departedSession.WaitForMessageCountAsync(1);
        hub.Leave(departed);
        await harness.PublishAsync(hub.RouteMessage(sender, new MediaSignal("camera-switched")));
        await WaitForAsync(() => recipientSignals.Count == 1);
        await Task.Delay(150);

        Assert.Empty(senderSession.Messages);
        Assert.Empty(senderSignals);
        Assert.Single(departedSession.Messages);
        Assert.Empty(departedSignals);
        Assert.Equal("camera-switched", Assert.Single(recipientSignals).Kind);
        Assert.Single(hub.Snapshot.Messages);
    }

    [Fact]
    public async Task Audio_And_Video_Routes_Deliver_Synchronized_Frames_To_The_Same_Recipients()
    {
        var senderId = Guid.NewGuid();
        var firstReceiverId = Guid.NewGuid();
        var secondReceiverId = Guid.NewGuid();
        var hub = new VideoChatHub(Guid.NewGuid());
        hub.Join(senderId, "Sender");
        hub.Join(firstReceiverId, "FirstReceiver");
        hub.Join(secondReceiverId, "SecondReceiver");
        hub.SetMicrophoneEnabled(senderId, true);
        hub.SetCameraEnabled(senderId, true);
        var route = hub.GetMediaRoute(senderId, VideoChatMediaKind.AudioAndVideo);
        Assert.Equal(new[] { firstReceiverId, secondReceiverId }.Order(), route.RecipientMemberIds.Order());
        Assert.True(hub.IsRouteCurrent(route));

        var receivers = new Dictionary<Guid, MediaReceiverSession>();
        foreach (var recipientId in route.RecipientMemberIds)
        {
            receivers[recipientId] = await MediaReceiverSession.StartAsync(GetFreeUdpPort(), GetFreeUdpPort());
        }

        var captureOrigin = Stopwatch.GetTimestamp();
        var captureTimestamp = captureOrigin + Stopwatch.Frequency / 2;
        var clock = new RtpMediaClock(captureOrigin, Stopwatch.Frequency, 10_000, 20_000);
        var timestamps = clock.Map(captureTimestamp);
        try
        {
            foreach (var recipientId in route.RecipientMemberIds)
            {
                var receiver = receivers[recipientId];
                await receiver.SendAsync(timestamps, captureTimestamp);
            }
            await WaitForAsync(() => receivers.Values.All(receiver => receiver.AudioFrames.Count == 1 && receiver.VideoFrames.Count == 1));

            foreach (var receiver in receivers.Values)
            {
                Assert.Equal(timestamps.AudioTimestamp48k, Assert.Single(receiver.AudioFrames).Timestamp);
                Assert.Equal(timestamps.VideoTimestamp90k, Assert.Single(receiver.VideoFrames).RtpTimestamp90k);
            }
        }
        finally
        {
            foreach (var receiver in receivers.Values)
            {
                await receiver.DisposeAsync();
            }
        }

        hub.SetCameraEnabled(senderId, false);
        Assert.True(route.RevocationToken.IsCancellationRequested);
        Assert.False(hub.IsRouteCurrent(route));
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

        throw new TimeoutException("Expected video chat delivery did not complete before timeout.");
    }

    [P2PMessage("MediaSignal")]
    public sealed record MediaSignal([property: P2PProperty(1)] string Kind);

    private sealed class MediaReceiverSession : IAsyncDisposable
    {
        private readonly RtpAudioReceiver _audioReceiver;
        private readonly RtpVideoReceiver _videoReceiver;
        private readonly int _audioPort;
        private readonly int _videoPort;

        private MediaReceiverSession(RtpAudioReceiver audioReceiver, RtpVideoReceiver videoReceiver, int audioPort, int videoPort)
        {
            _audioReceiver = audioReceiver;
            _videoReceiver = videoReceiver;
            _audioPort = audioPort;
            _videoPort = videoPort;
            _audioReceiver.AudioFrameReceived += AudioFrames.Enqueue;
            _videoReceiver.FrameReceived += frame =>
            {
                if (frame is { } value) VideoFrames.Enqueue(value);
            };
        }

        public ConcurrentQueue<ReceivedAudioFrame> AudioFrames { get; } = new();

        public ConcurrentQueue<VideoAccessUnit> VideoFrames { get; } = new();

        public static async Task<MediaReceiverSession> StartAsync(int audioPort, int videoPort)
        {
            var audioReceiver = new RtpAudioReceiver(new RtpAudioConfig
            {
                LocalAddress = IPAddress.Loopback,
                LocalPort = audioPort,
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, audioPort),
                Ssrc = 0xA001u
            });
            var videoReceiver = new RtpVideoReceiver(new RtpVideoReceiverConfig
            {
                LocalAddress = IPAddress.Loopback,
                LocalPort = videoPort,
                ExpectedSsrc = 0xB001u
            });
            var session = new MediaReceiverSession(audioReceiver, videoReceiver, audioPort, videoPort);
            await audioReceiver.StartAsync();
            await videoReceiver.StartAsync();
            return session;
        }

        public async Task SendAsync(RtpMediaTimestamps timestamps, long captureTimestamp)
        {
            await using var audioSender = new RtpAudioSender(new RtpAudioConfig
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, _audioPort),
                Ssrc = 0xA001u
            });
            using var videoSender = new RtpVideoSender(new RtpVideoSenderConfig
            {
                RemoteIp = IPAddress.Loopback.ToString(),
                RemotePort = _videoPort,
                Ssrc = 0xB001u
            });
            await audioSender.SendAsync(new byte[] { 0xF8, 0xFF, 0xFE }, new AudioFrameMetadata(timestamps.AudioTimestamp48k));
            using var accessUnit = new VideoAccessUnit(
                new byte[] { 0, 0, 0, 1, 0x65, 1, 2, 3 },
                true,
                timestamps.VideoTimestamp90k,
                captureTimestamp);
            Assert.True(await videoSender.SendAsync(accessUnit));
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var frame in VideoFrames) frame.Dispose();
            await _audioReceiver.DisposeAsync();
            await _videoReceiver.DisposeAsync();
        }
    }
}