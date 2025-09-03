using System.Buffers;
// ...existing code...

namespace TripleG3.P2P.Video.Internal
{
    internal sealed class Packetizer(int mtu, SequenceNumberGenerator seq)
    {
        private readonly SequenceNumberGenerator _seq = seq;

        // Simple RTP header builder (no CSRC, no extensions). Returns a list of packets (each as byte[] rented).
        public IEnumerable<ArraySegment<byte>> Packetize(TripleG3.P2P.Video.EncodedAccessUnit au, int payloadType, uint ssrc)
        {
            // convert timestamp ticks -> 90kHz clock
            uint ts = au.Timestamp90k;
            var nalUnits = SplitAnnexB(au.AnnexB);
            for (int ni = 0; ni < nalUnits.Count; ni++)
            {
                var nal = nalUnits[ni];
                var nalSpan = nal.AsSpan();
                bool isLastNalOfAu = (ni + 1) == nalUnits.Count;
                if (nalSpan.Length + 12 <= mtu)
                {
                    // single NAL RTP packet
                    var pkt = ArrayPool<byte>.Shared.Rent(nalSpan.Length + 12);
                    int offset = 0;
                    // RTP header
                    pkt[offset++] = 0x80; // V=2
                    // set marker if this NAL is the last of the AU
                    pkt[offset++] = (byte)((isLastNalOfAu ? 0x80 : 0x00) | (payloadType & 0x7F)); // M + payload
                    // seq
                    var seq = _seq.Next();
                    pkt[offset++] = (byte)(seq >> 8);
                    pkt[offset++] = (byte)(seq & 0xFF);
                    // ts
                    pkt[offset++] = (byte)(ts >> 24);
                    pkt[offset++] = (byte)((ts >> 16) & 0xFF);
                    pkt[offset++] = (byte)((ts >> 8) & 0xFF);
                    pkt[offset++] = (byte)(ts & 0xFF);
                    // ssrc
                    pkt[offset++] = (byte)(ssrc >> 24);
                    pkt[offset++] = (byte)((ssrc >> 16) & 0xFF);
                    pkt[offset++] = (byte)((ssrc >> 8) & 0xFF);
                    pkt[offset++] = (byte)(ssrc & 0xFF);

                    // copy bytes without creating a Span that escapes across yield
                    Buffer.BlockCopy(nal, 0, pkt, offset, nal.Length);
                    yield return new ArraySegment<byte>(pkt, 0, offset + nalSpan.Length);
                }
                else
                {
                    // FU-A fragmentation
                    int headerSize = 2; // FU indicator + FU header
                    int payloadPerPacket = mtu - 12 - headerSize;
                    int remaining = nalSpan.Length - 1; // skip original NAL header in FU payload
                    byte nalHeader = nalSpan[0];
                    int pos = 1;
                    bool first = true;
                    while (remaining > 0)
                    {
                        int take = Math.Min(payloadPerPacket, remaining);
                        var pkt = ArrayPool<byte>.Shared.Rent(12 + headerSize + take);
                        int offset = 0;
                        pkt[offset++] = 0x80;
                        bool last = (remaining - take) == 0 && !first ? true : (remaining - take) == 0;
                        // marker set only on the last fragment of the NAL when this is the last NAL of the AU
                        bool marker = last && isLastNalOfAu;
                        pkt[offset++] = (byte)((marker ? 0x80 : 0x00) | (payloadType & 0x7F));
                        var seq = _seq.Next();
                        pkt[offset++] = (byte)(seq >> 8);
                        pkt[offset++] = (byte)(seq & 0xFF);
                        pkt[offset++] = (byte)(ts >> 24);
                        pkt[offset++] = (byte)((ts >> 16) & 0xFF);
                        pkt[offset++] = (byte)((ts >> 8) & 0xFF);
                        pkt[offset++] = (byte)(ts & 0xFF);
                        pkt[offset++] = (byte)(ssrc >> 24);
                        pkt[offset++] = (byte)((ssrc >> 16) & 0xFF);
                        pkt[offset++] = (byte)((ssrc >> 8) & 0xFF);
                        pkt[offset++] = (byte)(ssrc & 0xFF);

                        // FU-A headers
                        byte fuIndicator = (byte)((nalHeader & 0xE0) | 28);
                        byte fuHeader = 0;
                        if (first) fuHeader |= 0x80; // start
                        if ((remaining - take) == 0) fuHeader |= 0x40; // end
                        fuHeader |= (byte)(nalHeader & 0x1F);

                        pkt[offset++] = fuIndicator;
                        pkt[offset++] = fuHeader;

                        Buffer.BlockCopy(nal, pos, pkt, offset, take);
                        yield return new ArraySegment<byte>(pkt, 0, offset + take);

                        pos += take;
                        remaining -= take;
                        first = false;
                    }
                }
            }
        }

        private static List<byte[]> SplitAnnexB(ReadOnlyMemory<byte> data)
        {
            var list = new List<byte[]>();
            var span = data.Span;
            int i = 0;
            while (i < span.Length)
            {
                // find start code
                int start = -1;
                for (int j = i; j + 3 < span.Length; j++)
                {
                    if (span[j] == 0 && span[j + 1] == 0 && span[j + 2] == 1)
                    {
                        start = j + 3; break;
                    }
                    if (j + 4 < span.Length && span[j] == 0 && span[j + 1] == 0 && span[j + 2] == 0 && span[j + 3] == 1)
                    {
                        start = j + 4; break;
                    }
                }
                if (start == -1) break;
                // find next start
                int next = -1;
                for (int j = start; j + 2 < span.Length; j++)
                {
                    if (span[j] == 0 && span[j + 1] == 0 && span[j + 2] == 1) { next = j; break; }
                    if (j + 3 < span.Length && span[j] == 0 && span[j + 1] == 0 && span[j + 2] == 0 && span[j + 3] == 1) { next = j; break; }
                }
                if (next == -1) next = span.Length;
                int len = next - start;
                var arr = new byte[len];
                span.Slice(start, len).CopyTo(arr);
                list.Add(arr);
                i = next;
            }
            return list;
        }
    }
}
