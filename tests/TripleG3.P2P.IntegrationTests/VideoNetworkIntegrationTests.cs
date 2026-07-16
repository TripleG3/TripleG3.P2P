using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using TripleG3.P2P.Video;
using TripleG3.P2P.Video.Abstractions;
using TripleG3.P2P.Video.Primitives;
using Xunit;
using VideoAccessUnit = TripleG3.P2P.Video.EncodedAccessUnit;

namespace TripleG3.P2P.IntegrationTests;

public sealed class VideoNetworkIntegrationTests
{
    [Fact]
    public async Task Receiver_Start_Stop_And_Restart_Is_Idempotent()
    {
        await using var receiver = new RtpVideoReceiver(new RtpVideoReceiverConfig
        {
            LocalAddress = IPAddress.Loopback,
            LocalPort = GetAvailableUdpPort()
        });

        await receiver.StartAsync();
        await receiver.StartAsync();
        await receiver.StopAsync();
        await receiver.StopAsync();
        await receiver.StartAsync();
        await receiver.StopAsync();
    }

    [Fact]
    public async Task Receiver_Synchronous_Dispose_Can_Race_Stop_And_Prevents_Restart()
    {
        var receiver = new RtpVideoReceiver(new RtpVideoReceiverConfig
        {
            LocalAddress = IPAddress.Loopback,
            LocalPort = GetAvailableUdpPort()
        });
        await receiver.StartAsync();

        var stopTask = receiver.StopAsync();
        receiver.Dispose();
        await stopTask;

        await Assert.ThrowsAsync<ObjectDisposedException>(() => receiver.StartAsync());
    }

    [Fact]
    public async Task DependencyInjection_Resolves_And_Transfers_Frame_Over_Udp()
    {
        var receiverPort = GetAvailableUdpPort();
        var services = new ServiceCollection();
        services.AddTripleG3P2PVideo(options =>
        {
            options.SenderConfiguration = new RtpVideoSenderConfig
            {
                RemoteIp = IPAddress.Loopback.ToString(),
                RemotePort = receiverPort,
                Ssrc = 0x9876,
                PayloadType = 96,
                Mtu = 300
            };
            options.ReceiverConfiguration = new RtpVideoReceiverConfig
            {
                LocalAddress = IPAddress.Loopback,
                LocalPort = receiverPort,
                ExpectedSsrc = 0x9876,
                PayloadType = 96
            };
        });
        using var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<IRtpVideoSender>();
        var receiver = provider.GetRequiredService<IRtpVideoReceiver>();
        var completion = new TaskCompletionSource<VideoAccessUnit>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FrameReceived += accessUnit =>
        {
            if (accessUnit.HasValue) completion.TrySetResult(accessUnit.Value);
        };
        await receiver.StartAsync();
        var annexB = BuildAnnexB(CreateNal(600, 0x65));
        using var source = new VideoAccessUnit(annexB, true, 90000, 0);

        Assert.True(await sender.SendAsync(source));
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(annexB, result.AnnexB.ToArray());
        result.Dispose();
        await receiver.StopAsync();
    }

    private static byte[] CreateNal(int length, byte header)
    {
        var nal = new byte[length];
        nal[0] = header;
        for (var index = 1; index < nal.Length; index++) nal[index] = (byte)(index % 251);
        return nal;
    }

    private static byte[] BuildAnnexB(byte[] nal)
    {
        var annexB = new byte[nal.Length + 4];
        annexB[3] = 1;
        Buffer.BlockCopy(nal, 0, annexB, 4, nal.Length);
        return annexB;
    }

    private static int GetAvailableUdpPort()
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }
}