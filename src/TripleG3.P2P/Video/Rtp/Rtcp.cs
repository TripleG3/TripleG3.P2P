using System.Buffers.Binary;

namespace TripleG3.P2P.Video.Rtp;

internal enum RtcpPacketType : byte
{
    SenderReport = 200,
    ReceiverReport = 201,
    PayloadSpecificFb = 206 // (PLI would live here for full RTCP; we already signal via control channel)
}

internal static class RtcpUtil
{
    private const uint NtpEpochOffset = 2208988800; // seconds between 1900 and 1970

    public static (uint ntpSec, uint ntpFrac) NowNtp()
    {
        var now = DateTime.UtcNow;
        ulong seconds = (ulong)(now - DateTime.UnixEpoch).TotalSeconds + NtpEpochOffset;
        double fractional = now.Subtract(DateTime.UnixEpoch.AddSeconds((long)(now - DateTime.UnixEpoch).TotalSeconds)).TotalSeconds;
        uint frac = (uint)(fractional * uint.MaxValue);
        return ((uint)seconds, frac);
    }

    public static uint ToCompact(uint ntpSec, uint ntpFrac) => (ntpSec << 16) | (ntpFrac >> 16);
}

/// <summary>Minimal RTCP (SR / RR) encoding for RTT + stats feedback.</summary>
internal static class RtcpPackets
{
    // Sender Report: V=2, P=0, RC=0, PT=200, length=6 (28 bytes)
    public static byte[] BuildSenderReport(uint ssrc, uint rtpTimestamp, uint packetCount, uint octetCount, out uint compactNtp)
    {
        var (sec, frac) = RtcpUtil.NowNtp();
        compactNtp = RtcpUtil.ToCompact(sec, frac);
        byte[] buffer = new byte[28];
        buffer[0] = 0x80; // V=2, RC=0
        buffer[1] = (byte)RtcpPacketType.SenderReport;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2,2), 6); // length (words-1)
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4,4), ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8,4), sec);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(12,4), frac);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(16,4), rtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(20,4), packetCount);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(24,4), octetCount);
        return buffer;
    }

    // Receiver Report (no report blocks -> just header + ssrc) OR 1 report block (we build 1) length=7 (32 bytes)
    public static byte[] BuildReceiverReport(uint reporterSsrc, in RtcpReportBlock block)
    {
        byte[] buffer = new byte[32];
        buffer[0] = 0x81; // V=2, RC=1
        buffer[1] = (byte)RtcpPacketType.ReceiverReport;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2,2), 7);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4,4), reporterSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8,4), block.Ssrc);
        buffer[12] = block.FractionLost;
        // cumulative lost 24-bit signed
        buffer[13] = (byte)((block.CumulativeLost >> 16) & 0xFF);
        buffer[14] = (byte)((block.CumulativeLost >> 8) & 0xFF);
        buffer[15] = (byte)(block.CumulativeLost & 0xFF);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(16,4), block.ExtendedHighestSeq);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(20,4), (uint)block.Jitter);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(24,4), block.Lsr);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(28,4), block.Dlsr);
        return buffer;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out RtcpParsed parsed)
    {
        parsed = default;
        if (data.Length < 8) return false;
        byte v = (byte)(data[0] >> 6);
        if (v != 2) return false;
        var pt = (RtcpPacketType)data[1];
        int lengthWords = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2,2));
        int totalLen = (lengthWords + 1) * 4;
        if (data.Length < totalLen) return false;
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4,4));
        if (pt == RtcpPacketType.SenderReport && data.Length >= 28)
        {
            uint sec = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8,4));
            uint frac = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12,4));
            uint rtpTs = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16,4));
            uint pc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20,4));
            uint oc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(24,4));
            parsed = new RtcpParsed(pt, ssrc, sec, frac, rtpTs, pc, oc, default);
            return true;
        }
        if (pt == RtcpPacketType.ReceiverReport && data.Length >= 32)
        {
            // We ignore most fields for now beyond timing
            uint lsr = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(24,4));
            uint dlsr = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(28,4));
            parsed = new RtcpParsed(pt, ssrc, 0,0,0,0,0, new RtcpReportTiming(lsr,dlsr));
            return true;
        }
        return false;
    }
}

internal readonly record struct RtcpReportTiming(uint Lsr, uint Dlsr);

internal readonly record struct RtcpParsed(RtcpPacketType Type, uint Ssrc, uint NtpSec, uint NtpFrac, uint RtpTimestamp, uint PacketCount, uint OctetCount, RtcpReportTiming Timing);

internal readonly record struct RtcpReportBlock(uint Ssrc, byte FractionLost, int CumulativeLost, uint ExtendedHighestSeq, double Jitter, uint Lsr, uint Dlsr);
