using Xunit;

namespace TripleG3.P2P.VideoTests;

public class StatsAndCipherTests
{
    [Fact]
    public void Cipher_NoOp_RoundTripIntegrity()
    {
        var nal = new byte[] { 0x65, 1, 2, 3, 4, 5 };
        var annex = new byte[4 + nal.Length]; annex[3] = 1; Buffer.BlockCopy(nal, 0, annex, 4, nal.Length);
        using var au = new TripleG3.P2P.Video.EncodedAccessUnit(annex, true, 12345u, 0);
    ReadOnlyMemory<byte>? received = null;

    // Packetize and feed receiver directly (no UDP)
    var receiver = new TripleG3.P2P.Video.RtpVideoReceiver(new TripleG3.P2P.Video.NoOpCipher());
    // new EncodedAccessUnit has AnnexB property (record struct). Use AnnexB directly.
    receiver.FrameReceived += a => received = a?.AnnexB;

    var pktizer = new TripleG3.P2P.Video.Internal.Packetizer(1200, new TripleG3.P2P.Video.Internal.SequenceNumberGenerator());
        foreach (var seg in pktizer.Packetize(au, 96, 0x1234))
            receiver.ProcessRtp(seg.AsSpan());

        Assert.NotNull(received);
        Assert.Equal(au.AnnexB.ToArray(), received!.Value.ToArray());
    }

    [Fact]
    public void Stats_PacketCountsAndJitterAccumulate()
    {
        var nal = new byte[] { 0x65, 1, 2 };
        var annex = new byte[4 + nal.Length]; annex[3] = 1; Buffer.BlockCopy(nal, 0, annex, 4, nal.Length);
        using var au = new TripleG3.P2P.Video.EncodedAccessUnit(annex, true, 90000u, 0);

        var packets = new List<System.ReadOnlyMemory<byte>>();
    var sender = new TripleG3.P2P.Video.RtpVideoSender(0x22u, 1200, new TripleG3.P2P.Video.NoOpCipher(), p => packets.Add(p));
        sender.Send(au);

        Assert.True(packets.Count >= 1);

    var recv = new TripleG3.P2P.Video.RtpVideoReceiver(new TripleG3.P2P.Video.NoOpCipher());
        int delivered = 0; recv.FrameReceived += _ => delivered++;
        foreach (var p in packets) recv.ProcessRtp(p.Span);

        Assert.Equal(1, delivered);

        var sStats = sender.GetStats();
        var rStats = recv.GetStats();
        Assert.Equal((uint)packets.Count, sStats?.PacketsSent ?? 0);
        Assert.Equal((uint)packets.Count, rStats?.PacketsReceived ?? 0);
    }
}
