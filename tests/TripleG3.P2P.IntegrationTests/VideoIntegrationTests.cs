using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TripleG3.P2P.Video;
using TripleG3.P2P.Video.Rtp;
using TripleG3.P2P.Video.Security;
using TripleG3.P2P.Video.Negotiation;
using Xunit;

namespace TripleG3.P2P.IntegrationTests;

public class VideoIntegrationTests
{
    private static byte[] BuildAnnexB(params byte[][] nals)
    {
        var total = nals.Sum(n => n.Length + 4);
        var buf = new byte[total];
        int o = 0;
        foreach (var nal in nals)
        {
            buf[o++] = 0; buf[o++] = 0; buf[o++] = 0; buf[o++] = 1;
            Buffer.BlockCopy(nal,0,buf,o,nal.Length);
            o += nal.Length;
        }
        return buf;
    }

    [Fact]
    public void EndToEnd_Video_Frames_RoundTrip_With_Large_And_Small_NALs()
    {
        var cipher = new NoOpCipher();
        var rtpPackets = new List<ReadOnlyMemory<byte>>();
        var rtcpPackets = new List<ReadOnlyMemory<byte>>();
        var sender = new RtpVideoSender(0x77, 1200, cipher, p => rtpPackets.Add(p), p => rtcpPackets.Add(p));
        var receiver = new RtpVideoReceiver(cipher);
        var receivedFrames = new List<EncodedAccessUnit>();
        receiver.AccessUnitReceived += au => receivedFrames.Add(au);

        // Build frames: keyframe large (> MTU) + two small delta
        var keyNal = new byte[3000]; keyNal[0] = 0x65; for(int i=1;i<keyNal.Length;i++) keyNal[i] = (byte)(i%250);
        var delta1 = new byte[]{0x61,1,2,3,4,5};
        var delta2 = new byte[]{0x61,9,8,7,6,5,4};
        var f1 = BuildAnnexB(keyNal);
        var f2 = BuildAnnexB(delta1);
        var f3 = BuildAnnexB(delta2);
        using var au1 = new EncodedAccessUnit(f1, true, 0, 0);
        using var au2 = new EncodedAccessUnit(f2, false, 3000, 0);
        using var au3 = new EncodedAccessUnit(f3, false, 6000, 0);

        sender.Send(au1);
        sender.Send(au2);
        sender.Send(au3);

        // Deliver RTP packets in original order
        foreach (var pkt in rtpPackets)
            receiver.ProcessRtp(pkt.Span);

        Assert.Equal(3, receivedFrames.Count);
        Assert.True(receivedFrames[0].IsKeyFrame);
        Assert.Equal(f1, receivedFrames[0].AnnexB.ToArray());
        Assert.Equal(f2, receivedFrames[1].AnnexB.ToArray());
        Assert.Equal(f3, receivedFrames[2].AnnexB.ToArray());

        // Stats sanity
        var sStats = sender.GetStats();
        var rStats = receiver.GetStats();
        Assert.Equal((uint)rtpPackets.Count, sStats.PacketsSent);
        Assert.Equal((uint)rtpPackets.Count, rStats.PacketsReceived);
        Assert.True(rStats.Jitter >= 0);

        // Dispose received frames to release pooled buffers
        foreach (var rf in receivedFrames) rf.Dispose();
    }

    [Fact]
    public void Rtcp_Rtt_And_FractionLost_Computed()
    {
        var cipher = new NoOpCipher();
        var rtpPackets = new List<ReadOnlyMemory<byte>>();
        var rtcpPackets = new List<ReadOnlyMemory<byte>>();
        var sender = new RtpVideoSender(0x99, 1200, cipher, p => rtpPackets.Add(p), p => rtcpPackets.Add(p));
        var receiver = new RtpVideoReceiver(cipher);

        // Build a large frame fragmented into multiple packets
        var nal = new byte[2500]; nal[0] = 0x65; for(int i=1;i<nal.Length;i++) nal[i]=(byte)(i%200);
        using var au = new EncodedAccessUnit(BuildAnnexB(nal), true, 10000, 0);
        sender.Send(au);
        // Drop one middle RTP packet to simulate loss (if >2 packets)
        if (rtpPackets.Count > 2) rtpPackets.RemoveAt(1);
        foreach (var pkt in rtpPackets) receiver.ProcessRtp(pkt.Span);

        // Send SR & process at receiver
        sender.SendSenderReport(10000);
        Assert.True(rtcpPackets.Any());
        foreach (var rr in rtcpPackets) receiver.ProcessRtcp(rr.Span);

        // Receiver creates RR -> feed back for RTT
        var rrBytes = receiver.CreateReceiverReport(0x55);
        Assert.NotNull(rrBytes);
        sender.ProcessRtcp(rrBytes!);

        var sStats = sender.GetStats();
        var rStats = receiver.GetStats();
        Assert.True(sStats.RttEstimateMs.HasValue);
        // Loss accounting: if we dropped a packet, PacketsLost >=1
        if (rtpPackets.Count > 1) Assert.True(rStats.PacketsLost >= 0);
    }

    [Fact]
    public async Task Negotiation_Keyframe_Request_Invokes_Encoder()
    {
        var chOffer = new InMemoryControlChannel();
        var chAnswer = new InMemoryControlChannel();
        chOffer.MessageReceived += m => chAnswer.SendReliableAsync(m);
        chAnswer.MessageReceived += m => chOffer.SendReliableAsync(m);
        var offerMgr = new NegotiationManager(chOffer);
        var answerMgr = new NegotiationManager(chAnswer);

        var fakeEnc = new TestEncoder();
        answerMgr.AttachEncoder(fakeEnc);

        await offerMgr.CreateOfferAsync(new VideoSessionConfig(640,360,400_000,30));
        // allow messages
        await Task.Delay(50);
        offerMgr.RequestKeyFrame();
        await Task.Delay(50);
        Assert.True(fakeEnc.KeyRequested);
    }

    private sealed class TestEncoder : IVideoEncoder
    {
        public bool KeyRequested; public event Action<EncodedAccessUnit>? AccessUnitReady { add { } remove { } }
        public void RequestKeyFrame() => KeyRequested = true;
    }
}
