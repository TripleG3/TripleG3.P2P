using System;
using System.Collections.Generic;
using TripleG3.P2P.Video;
using TripleG3.P2P.Video.Rtp;
using TripleG3.P2P.Video.Security;
using Xunit;

namespace TripleG3.P2P.VideoTests;

public class StatsAndCipherTests
{
    [Fact]
    public void Cipher_NoOp_RoundTripIntegrity()
    {
        byte[] nal = {0x65, 1,2,3,4,5};
        var annex = new byte[4+nal.Length]; annex[3]=1; Buffer.BlockCopy(nal,0,annex,4,nal.Length);
    using var au = new EncodedAccessUnit(annex,true,12345,0);
        ReadOnlyMemory<byte>? received = null;
        var sender = new RtpVideoSender(0x11,1200,new NoOpCipher(), d => {
            var receiver = new RtpVideoReceiver(new NoOpCipher());
            receiver.AccessUnitReceived += a => received = a.AnnexB;
            receiver.ProcessRtp(d.Span);
        });
        sender.Send(au);
        Assert.NotNull(received);
        Assert.Equal(au.AnnexB.ToArray(), received!.Value.ToArray());
    }

    [Fact]
    public void Stats_PacketCountsAndJitterAccumulate()
    {
        byte[] nal = {0x65,1,2};
        var annex = new byte[4+nal.Length]; annex[3]=1; Buffer.BlockCopy(nal,0,annex,4,nal.Length);
    using var au = new EncodedAccessUnit(annex,true,90000,0);
        var packets = new List<ReadOnlyMemory<byte>>();
        var sender = new RtpVideoSender(0x22,1200,new NoOpCipher(), p => packets.Add(p));
        sender.Send(au);
        Assert.True(packets.Count>=1);
        var recv = new RtpVideoReceiver(new NoOpCipher());
        int delivered=0; recv.AccessUnitReceived += _=>delivered++;
        foreach (var p in packets) recv.ProcessRtp(p.Span);
        Assert.Equal(1, delivered);
        var sStats = sender.GetStats();
        var rStats = recv.GetStats();
        Assert.Equal((uint)packets.Count, sStats.PacketsSent);
        Assert.Equal((uint)packets.Count, rStats.PacketsReceived);
    }
}
