using System.Buffers;
using TripleG3.P2P.Video.Internal;
using TripleG3.P2P.Video;
using TripleG3.P2P.Video.Primitives;
using Xunit;

namespace TripleG3.P2P.VideoTests
{
    public class RtpVideoApiTests
    {
        [Fact]
        public void Packetizer_FragmentsLargeNal_To_FuaSequence()
        {
            var seq = new SequenceNumberGenerator(0);
            var packetizer = new Packetizer(120, seq);
            // create a fake AnnexB with one large NAL (no start codes repeated for simplicity)
            var nal = new byte[500];
            nal[0] = 0x65; // IDR
            var annex = new byte[4 + nal.Length];
            annex[0] = 0; annex[1] = 0; annex[2] = 0; annex[3] = 1;
            Array.Copy(nal, 0, annex, 4, nal.Length);
            var ticks = DateTime.UtcNow.Ticks;
            using var au = new TripleG3.P2P.Video.EncodedAccessUnit(annex, true, (uint)((ticks * 90000) / TimeSpan.TicksPerSecond), ticks);
            var pkts = packetizer.Packetize(au, 96, 0x1234).ToList();
            Assert.True(pkts.Count > 1);
            // all packets non-null
            Assert.All(pkts, p => Assert.NotNull(p.Array));
            // return buffers
            foreach (var s in pkts) ArrayPool<byte>.Shared.Return(s.Array!);
        }

        [Fact]
        public void Depacketizer_Reassembles_Fragments()
        {
            var seq = new SequenceNumberGenerator(0);
            var packetizer = new Packetizer(120, seq);
            var dep = new Depacketizer();
            var nal = new byte[300]; nal[0] = 0x65;
            var annex = new byte[4 + nal.Length]; annex[0]=0; annex[1]=0; annex[2]=0; annex[3]=1; Array.Copy(nal,0,annex,4,nal.Length);
            var ticks2 = DateTime.UtcNow.Ticks;
            using var au = new TripleG3.P2P.Video.EncodedAccessUnit(annex, true, (uint)((ticks2 * 90000) / TimeSpan.TicksPerSecond), ticks2);
            var pkts = packetizer.Packetize(au, 96, 0x1234).ToList();
            TripleG3.P2P.Video.EncodedAccessUnit? outAu = null;
            foreach (var p in pkts)
            {
                // simulate span
                var span = new ReadOnlySpan<byte>(p.Array, 0, p.Count);
                if (dep.AddPacket(span, out var maybe)) outAu = maybe;
                ArrayPool<byte>.Shared.Return(p.Array!);
            }
            Assert.NotNull(outAu);
            outAu?.Dispose();
        }

        [Fact]
        public void Timestamp_Mapping_OneSecond_Equals_90000()
        {
            var ticks = TimeSpan.TicksPerSecond;
            var au = EncodedAccessUnitFactory.FromAnnexB(new ReadOnlyMemory<byte>(new byte[]{0,0,0,1,0x65}), ticks, true, 0,0, CodecKind.H264);
            // internal mapping in Packetizer is private; replicate formula
            uint ts = au.RtpTimestamp90k;
            Assert.Equal(90000u, ts);
            au.Dispose();
        }

        [Fact]
        public void OfferAnswer_RoundTrip_Json()
        {
            var offer = new VideoOffer { Codec = CodecKind.H264, Ssrc = 1, PayloadType = 96, Width = 1280, Height = 720 };
            var json = offer.ToJson();
            var parsed = VideoOffer.FromJson(json);
            Assert.Equal(offer.Codec, parsed.Codec);

            var answer = new VideoAnswer { Accepted = true, Codec = CodecKind.H264, Ssrc = 1, PayloadType = 96 };
            var aj = answer.ToJson();
            var parsedA = VideoAnswer.FromJson(aj);
            Assert.True(parsedA.Accepted);
        }
    }
}
