using System;
using System.Buffers;
using System.Collections.Generic;
// ...existing code...

namespace TripleG3.P2P.Video.Internal
{
    internal sealed class Depacketizer
    {
        private readonly List<byte[]> _fragments = new List<byte[]>();
        private int _expectedSeq = -1;

        public void Reset()
        {
            foreach (var b in _fragments) ArrayPool<byte>.Shared.Return(b);
            _fragments.Clear();
            _expectedSeq = -1;
        }

        public bool AddPacket(ReadOnlySpan<byte> pkt, out TripleG3.P2P.Video.EncodedAccessUnit? au)
        {
            au = null;
            if (pkt.Length < 12) return false;
            int offset = 0;
            // skip rtp header
            byte v = pkt[offset++];
            byte pt = pkt[offset++];
            int seq = (pkt[offset++] << 8) | pkt[offset++];
            uint ts = (uint)((pkt[offset++] << 24) | (pkt[offset++] << 16) | (pkt[offset++] << 8) | pkt[offset++]);
            uint ssrc = (uint)((pkt[offset++] << 24) | (pkt[offset++] << 16) | (pkt[offset++] << 8) | pkt[offset++]);

            var payload = pkt.Slice(offset);
            // check FU-A
            if (payload.Length >= 2 && (payload[0] & 0x1F) == 28)
            {
                byte fuHeader = payload[1];
                bool start = (fuHeader & 0x80) != 0;
                bool end = (fuHeader & 0x40) != 0;

                if (start)
                {
                    Reset();
                    // reconstruct nal header
                    byte originalNal = (byte)((payload[0] & 0xE0) | (payload[1] & 0x1F));
                    var buf = ArrayPool<byte>.Shared.Rent(payload.Length - 2 + 1);
                    buf[0] = originalNal;
                    payload.Slice(2).CopyTo(new Span<byte>(buf, 1, payload.Length - 2));
                    _fragments.Add(buf);
                    _expectedSeq = seq + 1;
                    if (end)
                    {
                        au = Assemble(ts);
                        return true;
                    }
                    return false;
                }
                else
                {
                    if (_expectedSeq != -1 && seq != _expectedSeq)
                    {
                        // gap
                        Reset();
                        return false;
                    }
                    var buf = ArrayPool<byte>.Shared.Rent(payload.Length - 2);
                    payload.Slice(2).CopyTo(new Span<byte>(buf, 0, payload.Length - 2));
                    _fragments.Add(buf);
                    _expectedSeq = seq + 1;
                    if ((payload[1] & 0x40) != 0)
                    {
                        au = Assemble(ts);
                        return true;
                    }
                    return false;
                }
            }
            else
            {
                // Single-NAL packet
                var buf = ArrayPool<byte>.Shared.Rent(payload.Length + 4);
                // prepend 0x000001
                buf[0] = 0; buf[1] = 0; buf[2] = 0; buf[3] = 1;
                payload.CopyTo(new Span<byte>(buf, 4, payload.Length));
                au = new TripleG3.P2P.Video.EncodedAccessUnit(new TripleG3.P2P.Video.ArrayPoolFrame(payload.Length + 4), payload.Length + 4, false, ts, (long)ts * TimeSpan.TicksPerSecond / 90000);
                return true;
            }
        }

        private EncodedAccessUnit Assemble(uint ts)
        {
            // compute total
            int total = 0;
            foreach (var b in _fragments) total += b.Length;
            var outBuf = ArrayPool<byte>.Shared.Rent(total + 4);
            outBuf[0] = 0; outBuf[1] = 0; outBuf[2] = 0; outBuf[3] = 1;
            int pos = 4;
            foreach (var b in _fragments)
            {
                b.CopyTo(new Span<byte>(outBuf, pos, b.Length));
                pos += b.Length;
                ArrayPool<byte>.Shared.Return(b);
            }
            _fragments.Clear();
            var au = new TripleG3.P2P.Video.EncodedAccessUnit(new TripleG3.P2P.Video.ArrayPoolFrame(pos), pos, false, ts, (long)ts * TimeSpan.TicksPerSecond / 90000);
            return au;
        }
    }
}
