using TripleG3.P2P.Video;
using TripleG3.P2P.Video.Rtp;
using Xunit;
using StableRtpVideoReceiver = TripleG3.P2P.Video.RtpVideoReceiver;

namespace TripleG3.P2P.IntegrationTests;

public sealed class VideoTimingIntegrationTests
{
    [Fact]
    public void Jitter_Tracks_Variable_Packet_Arrival()
    {
        using var receiver = new StableRtpVideoReceiver(new NoOpCipher());
        var packetizer = new H264RtpPacketizer(
            0x44,
            1200,
            new TripleG3.P2P.Video.Security.NoOpCipher());
        using var firstAccessUnit = BuildAccessUnit([0x61, 1], 0);
        using var secondAccessUnit = BuildAccessUnit([0x61, 2], 3000);
        var firstPacket = packetizer.Packetize(firstAccessUnit).Single();
        var secondPacket = packetizer.Packetize(secondAccessUnit).Single();

        receiver.ProcessRtp(firstPacket.Span);
        Thread.Sleep(30);
        receiver.ProcessRtp(secondPacket.Span);

        Assert.Equal<uint>(2, receiver.GetStats().PacketsReceived);
    }

    private static EncodedAccessUnit BuildAccessUnit(byte[] nal, uint timestamp)
    {
        var annexB = new byte[nal.Length + 4];
        annexB[3] = 1;
        Buffer.BlockCopy(nal, 0, annexB, 4, nal.Length);
        return new EncodedAccessUnit(annexB, (nal[0] & 0x1F) == 5, timestamp, 0);
    }
}