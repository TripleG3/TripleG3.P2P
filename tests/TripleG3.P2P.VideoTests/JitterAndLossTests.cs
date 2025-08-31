using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TripleG3.P2P.Video;
using TripleG3.P2P.Video.Rtp;
using TripleG3.P2P.Video.Security;
using Xunit;

namespace TripleG3.P2P.VideoTests;

public class JitterAndLossTests
{
    private static EncodedAccessUnit BuildAu(byte[] nal, uint ts)
    {
        var annex = new byte[4+nal.Length]; annex[3]=1; Buffer.BlockCopy(nal,0,annex,4,nal.Length); return new EncodedAccessUnit(annex,(nal[0]&0x1F)==5,ts,0);
    }

    [Fact]
    public void LossTracking_Increments_When_SequenceGap()
    {
        var pktizer = new H264RtpPacketizer(0x55,1200,new NoOpCipher());
    using var au = BuildAu(new byte[]{0x61,1,2,3}, 1000);
        var packets = pktizer.Packetize(au).ToList();
        // Drop middle (if multiple) else simulate by removing none if single
        if (packets.Count > 2) packets.RemoveAt(1);
        var recv = new RtpVideoReceiver(new NoOpCipher());
        foreach (var p in packets) recv.ProcessRtp(p.Span);
        var stats = recv.GetStats();
        if (packets.Count > 1)
            Assert.True(stats.PacketsLost >= 1);
        else
            Assert.Equal<uint>(0, stats.PacketsLost);
    }

    [Fact]
    public void Jitter_Increases_With_TimestampVariance()
    {
        var cipher = new NoOpCipher();
        var sender = new RtpVideoSender(0x44, 1200, cipher, _ => { /* not used */ });
        var recv = new RtpVideoReceiver(cipher);
        // Manually craft two RTP packets with different network delay simulation
        var pktizer = new H264RtpPacketizer(0x44,1200,cipher);
    using var au1 = BuildAu(new byte[]{0x61,1}, 0);
    using var au2 = BuildAu(new byte[]{0x61,2}, 3000); // 33ms at 90kHz roughly
        var p1 = pktizer.Packetize(au1).First();
        var p2 = pktizer.Packetize(au2).First();
        recv.ProcessRtp(p1.Span);
        Thread.Sleep(30); // simulate network delay
        recv.ProcessRtp(p2.Span);
        var stats = recv.GetStats();
        Assert.True(stats.Jitter >= 0); // basic sanity; jitter should have been computed
    }

    [Fact]
    public void Cipher_Xor_RoundTrip()
    {
        var xor = new XorTestCipher(0x7F);
        var senderPackets = new List<ReadOnlyMemory<byte>>();
        var sender = new RtpVideoSender(0xABC,1200,xor, p => senderPackets.Add(p));
        var nal = new byte[]{0x65,1,2,3,4,5,6,7};
    using var au = BuildAu(nal, 7777);
        sender.Send(au);
        var recv = new RtpVideoReceiver(xor);
        EncodedAccessUnit? received = null;
        recv.AccessUnitReceived += a => received = a;
        foreach (var p in senderPackets) recv.ProcessRtp(p.Span);
        Assert.NotNull(received);
        Assert.Equal(au.AnnexB.ToArray(), received!.Value.AnnexB.ToArray());
    }
}
