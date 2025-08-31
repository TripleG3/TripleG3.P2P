using System.Buffers.Binary;

namespace TripleG3.P2P.Video.Rtp;

/// <summary>Minimal RTP packet representation sufficient for H264 video.</summary>
public ref struct RtpPacket
{
    public const int HeaderLength = 12; // without extensions/CSRC

    public byte VersionPaddingExtensionCsrc; // V(2) P X CC(4)
    public byte MarkerPayloadType;           // M(1) PT(7)
    public ushort SequenceNumber;
    public uint Timestamp;
    public uint Ssrc;
    public ReadOnlySpan<byte> Payload;

    public bool Marker => (MarkerPayloadType & 0x80) != 0;
    public int PayloadType => MarkerPayloadType & 0x7F;

    public static bool TryParse(ReadOnlySpan<byte> buffer, out RtpPacket packet)
    {
        packet = default;
        if (buffer.Length < HeaderLength) return false;
        var v = buffer[0];
        if ((v >> 6) != 2) return false; // only RTP v2
        int cc = v & 0x0F;
        int headerLen = HeaderLength + cc * 4;
        if (buffer.Length < headerLen) return false;
        packet.VersionPaddingExtensionCsrc = v;
        packet.MarkerPayloadType = buffer[1];
        packet.SequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2,2));
        packet.Timestamp = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4,4));
        packet.Ssrc = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8,4));
        packet.Payload = buffer.Slice(headerLen);
        return true;
    }

    public static int WriteHeader(Span<byte> dest, bool marker, byte payloadType, ushort seq, uint ts, uint ssrc)
    {
        if (dest.Length < HeaderLength) throw new ArgumentException("dest too small");
        dest[0] = 0x80; // V=2, P=0, X=0, CC=0
        dest[1] = (byte)(payloadType & 0x7F); if (marker) dest[1] |= 0x80;
        BinaryPrimitives.WriteUInt16BigEndian(dest.Slice(2,2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(dest.Slice(4,4), ts);
        BinaryPrimitives.WriteUInt32BigEndian(dest.Slice(8,4), ssrc);
        return HeaderLength;
    }
}
