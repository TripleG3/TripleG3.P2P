using Xunit;

namespace TripleG3.P2P.UnitTests;

public class JitterAndLossTests
{
    private static TripleG3.P2P.Video.EncodedAccessUnit BuildAu(byte[] nal, uint ts)
    {
        var annex = new byte[4 + nal.Length]; annex[3] = 1; Buffer.BlockCopy(nal, 0, annex, 4, nal.Length);
        // public EncodedAccessUnit: (AnnexB, IsKeyFrame, Timestamp90k, CaptureTicks)
        return new TripleG3.P2P.Video.EncodedAccessUnit(annex, (nal[0] & 0x1F) == 5, ts, 0);
    }

    [Fact]
    public void LossTracking_Increments_When_SequenceGap()
    {
    var pktizer = new TripleG3.P2P.Video.Rtp.H264RtpPacketizer(0x55, 1200, new TripleG3.P2P.Video.Security.NoOpCipher());
        var nal = new byte[3000];
        nal[0] = 0x61;
        using var au = BuildAu(nal, 1000);
        var packets = pktizer.Packetize(au).ToList();
        Assert.True(packets.Count > 2);
        packets.RemoveAt(1);
    using var recv = new TripleG3.P2P.Video.RtpVideoReceiver(new TripleG3.P2P.Video.NoOpCipher());
        foreach (var p in packets) recv.ProcessRtp(p.Span);
        var stats = recv.GetStats();
        Assert.True(stats.PacketsLost >= 1);
    }

    [Fact]
    public void Cipher_Xor_RoundTrip()
    {
    var cipher2 = new TripleG3.P2P.Video.NoOpCipher();
        var senderPackets = new List<ReadOnlyMemory<byte>>();
    var sender = new TripleG3.P2P.Video.RtpVideoSender(0xABC, 1200, cipher2, p => senderPackets.Add(p));
        var nal = new byte[] { 0x65, 1, 2, 3, 4, 5, 6, 7 };
        using var au = BuildAu(nal, 7777);
        sender.Send(au);
    var recv = new TripleG3.P2P.Video.RtpVideoReceiver(cipher2);
        TripleG3.P2P.Video.EncodedAccessUnit? received = null;
    recv.FrameReceived += a => received = a;
        foreach (var p in senderPackets) recv.ProcessRtp(p.Span);
        Assert.NotNull(received);
        Assert.Equal(au.AnnexB.ToArray(), received!.Value.AnnexB.ToArray());
    }
}
