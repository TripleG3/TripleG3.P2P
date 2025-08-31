using System.Buffers;
using TripleG3.P2P.Video.Rtp;
using TripleG3.P2P.Video.Security;

namespace TripleG3.P2P.Video.Rtp;

/// <summary>Converts H264 Annex B access units into RTP packets (RFC 6184, single NAL & FU-A only).</summary>
public sealed class H264RtpPacketizer
{
    private readonly RtpSequenceNumberGenerator _seq = new();
    private readonly uint _ssrc;
    private readonly int _mtu;
    private readonly IVideoPayloadCipher _cipher;
    private const byte PayloadType = 96; // dynamic

    public H264RtpPacketizer(uint ssrc, int mtu, IVideoPayloadCipher cipher)
    {
        _ssrc = ssrc; _mtu = mtu; _cipher = cipher;
    }

    public IEnumerable<ReadOnlyMemory<byte>> Packetize(EncodedAccessUnit au)
    {
        // Enumerate NAL units as slices referencing the original Annex B buffer (no per-NAL allocation)
        var nalUnits = AnnexBHelper.EnumerateNalUnits(au.AnnexB);
        int total = nalUnits.Count;
        // Materialize each NAL slice to an array once (still fewer allocations than previous per-fragment copies)
        for (int i = 0; i < total; i++)
        {
            bool isLastNal = (i + 1) == total;
            var slice = nalUnits[i];
            var nalArray = slice.ToArray();
        foreach (var packet in PacketizeNal(nalArray, isLastNal, au.Timestamp90k))
                yield return packet;
        }
    }

    private IEnumerable<ReadOnlyMemory<byte>> PacketizeNal(byte[] nal, bool isLastNalOfAu, uint timestamp)
    {
        // Single NAL if fits
        int maxPayload = _mtu - RtpPacket.HeaderLength;
        if (nal.Length <= maxPayload)
        {
            var rent = ArrayPool<byte>.Shared.Rent(RtpPacket.HeaderLength + nal.Length);
            var span = rent.AsSpan();
            var seq = _seq.Next();
            RtpPacket.WriteHeader(span, marker: isLastNalOfAu, payloadType: PayloadType, seq, timestamp, _ssrc);
            var payloadDest = span.Slice(RtpPacket.HeaderLength, nal.Length);
            new ReadOnlySpan<byte>(nal).CopyTo(payloadDest);
            // Encrypt (output into same buffer)
            var meta = new RtpPacketMetadata(timestamp, seq, _ssrc, isLastNalOfAu);
            _cipher.Encrypt(meta, payloadDest, payloadDest);
            yield return new Memory<byte>(rent, 0, RtpPacket.HeaderLength + nal.Length);
            yield break;
        }

        // FU-A fragmentation
    byte nalHeader = nal[0];
        byte forbidden = (byte)(nalHeader & 0x80);
        byte nri = (byte)(nalHeader & 0x60);
        byte type = (byte)(nalHeader & 0x1F);
        int offset = 1; // skip original header already captured in FU header
        int payloadRemaining = nal.Length - 1;
        bool first = true;
        while (payloadRemaining > 0)
        {
            int fragmentPayload = Math.Min(maxPayload - 2, payloadRemaining); // 2 bytes FU-A headers
            var rent = ArrayPool<byte>.Shared.Rent(RtpPacket.HeaderLength + 2 + fragmentPayload);
            var span = rent.AsSpan();
            bool lastFragment = payloadRemaining - fragmentPayload == 0;
            bool marker = lastFragment && isLastNalOfAu;
            var seq = _seq.Next();
            RtpPacket.WriteHeader(span, marker, PayloadType, seq, timestamp, _ssrc);
            // FU indicator
            span[RtpPacket.HeaderLength] = (byte)(forbidden | nri | 28); // FU-A type 28
            // FU header
            byte fuHeader = (byte)(type & 0x1F);
            if (first) fuHeader |= 0x80; // S
            if (lastFragment) fuHeader |= 0x40; // E
            span[RtpPacket.HeaderLength + 1] = fuHeader;
            var fragSpan = span.Slice(RtpPacket.HeaderLength + 2, fragmentPayload);
            new ReadOnlySpan<byte>(nal, offset, fragmentPayload).CopyTo(fragSpan);
            var meta = new RtpPacketMetadata(timestamp, seq, _ssrc, marker);
            _cipher.Encrypt(meta, fragSpan, fragSpan);
            yield return new Memory<byte>(rent, 0, RtpPacket.HeaderLength + 2 + fragmentPayload);
            offset += fragmentPayload;
            payloadRemaining -= fragmentPayload;
            first = false;
        }
    }
}

internal static class AnnexBHelper
{
    /// <summary>
    /// Enumerate NAL units (without start codes) as ReadOnlyMemory slices referencing the original buffer.
    /// No allocations are performed for the NAL payloads themselves.
    /// </summary>
    public static List<ReadOnlyMemory<byte>> EnumerateNalUnits(ReadOnlyMemory<byte> memory)
    {
        var span = memory.Span;
        var list = new List<ReadOnlyMemory<byte>>();
        int i = 0; int start = -1;
        while (i < span.Length)
        {
            if (IsStartCode(span, i, out int scLen))
            {
                if (start != -1)
                {
                    int len = i - start;
                    list.Add(memory.Slice(start, len));
                }
                i += scLen; start = i; continue;
            }
            i++;
        }
        if (start != -1 && start < span.Length)
        {
            int len = span.Length - start;
            list.Add(memory.Slice(start, len));
        }
        return list;
    }

    private static bool IsStartCode(ReadOnlySpan<byte> data, int index, out int length)
    {
        length = 0;
        if (index + 3 < data.Length && data[index] == 0 && data[index+1]==0 && data[index+2]==1)
        { length = 3; return true; }
        if (index + 4 < data.Length && data[index]==0 && data[index+1]==0 && data[index+2]==0 && data[index+3]==1)
        { length = 4; return true; }
        return false;
    }
}
