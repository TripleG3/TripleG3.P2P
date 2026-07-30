using System.Net;
using TripleG3.P2P.Audio;
using Xunit;

namespace TripleG3.P2P.IntegrationTests;

public sealed class RtpAudioIntegrationTests
{
    [Fact]
    public async Task Sender_delivers_an_opus_frame_to_receiver()
    {
        var receiverPort = GetFreePort();
        var received = new TaskCompletionSource<ReceivedAudioFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new RtpAudioConfig
        {
            LocalPort = receiverPort,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, receiverPort),
            Ssrc = 42
        };
        await using var receiver = new RtpAudioReceiver(config);
        await using var sender = new RtpAudioSender(config);
        receiver.AudioFrameReceived += frame => received.TrySetResult(frame);

        await receiver.StartAsync();
        await sender.SendAsync(new byte[] { 1, 2, 3 }, new AudioFrameMetadata(960));
        var frame = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([1, 2, 3], frame.OpusFrame.ToArray());
        Assert.Equal(960u, frame.Timestamp);
        Assert.False(frame.IsGap);
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}