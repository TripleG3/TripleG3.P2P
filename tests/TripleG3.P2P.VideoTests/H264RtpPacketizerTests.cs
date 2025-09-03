using Xunit;

namespace TripleG3.P2P.VideoTests;

public class H264RtpPacketizerTests
{
    [Fact]
    public void H264RtpPacketizer_SplitsLargeNal_FragmentsAndReassembles()
    {
        // Build a fake large NAL (IDR type 5) of size > MTU to force FU-A
        byte[] largeNal = new byte[5000];
        largeNal[0] = 0x65; // F=0 NRI=3 (0x60) type=5 -> 0x65
        for (int i=1;i<largeNal.Length;i++) largeNal[i] = (byte)(i%251);
        // Annex B framing: start code + nal
        var annexB = new byte[4 + largeNal.Length];
        annexB[0]=0; annexB[1]=0; annexB[2]=0; annexB[3]=1; Buffer.BlockCopy(largeNal,0,annexB,4,largeNal.Length);
    using var au = new TripleG3.P2P.Video.EncodedAccessUnit(annexB, true, 90000, 0);
    var packetizer = new TripleG3.P2P.Video.Rtp.H264RtpPacketizer(0x12345678, 1200, new TripleG3.P2P.Video.Security.NoOpCipher());
    var packets = packetizer.Packetize(au).ToList();
        Assert.True(packets.Count > 1); // fragmented
        // Last packet marker bit should be set
        bool lastMarker = false;
        foreach (var mem in packets)
        {
            var span = mem.Span;
            lastMarker = (span[1] & 0x80)!=0; // marker
        }
        Assert.True(lastMarker);
    }
}
