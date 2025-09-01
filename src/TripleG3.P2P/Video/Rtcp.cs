using System;

namespace TripleG3.P2P.Video;

/// <summary>RTCP utility helpers.</summary>
public static class Rtcp
{
    /// <summary>Heuristic: true if packet looks like RTCP (PT 200..204) with V=2.</summary>
    public static bool IsRtcpPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 2) return false;
        // Version bits 2 (0x80 mask top two bits == 0x80)
        if ((packet[0] & 0xC0) != 0x80) return false;
        byte pt = packet[1];
        return pt is >= 200 and <= 204; // SR=200, RR=201, SDES=202, BYE=203, APP=204
    }
}