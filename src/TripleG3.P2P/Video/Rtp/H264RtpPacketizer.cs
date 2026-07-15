using PacketCipher = TripleG3.P2P.Video.Security.IVideoPayloadCipher;
using RtpPacketMetadata = TripleG3.P2P.Video.Security.RtpPacketMetadata;

namespace TripleG3.P2P.Video.Rtp;

/// <summary>Converts H.264 Annex-B access units into RTP single-NAL or FU-A packets.</summary>
public sealed class H264RtpPacketizer
{
    private readonly uint _ssrc;
    private readonly int _mtu;
    private readonly byte _payloadType;
    private readonly PacketCipher _cipher;
    private readonly RtpSequenceNumberGenerator _sequenceNumbers = new();

    public H264RtpPacketizer(uint ssrc, int mtu, PacketCipher cipher)
        : this(ssrc, mtu, 96, cipher)
    {
    }

    public H264RtpPacketizer(uint ssrc, int mtu, byte payloadType, PacketCipher cipher)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        if (payloadType > 127) throw new ArgumentOutOfRangeException(nameof(payloadType));
        if (cipher.OverheadBytes < 0) throw new ArgumentOutOfRangeException(nameof(cipher), "Cipher overhead cannot be negative.");
        if (mtu <= RtpPacket.HeaderLength + 2 + cipher.OverheadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(mtu), "MTU is too small for an RTP FU-A packet and cipher overhead.");
        }

        _ssrc = ssrc;
        _mtu = mtu;
        _payloadType = payloadType;
        _cipher = cipher;
    }

    public IEnumerable<ReadOnlyMemory<byte>> Packetize(EncodedAccessUnit accessUnit)
    {
        var nalUnits = AnnexBHelper.EnumerateNalUnits(accessUnit.AnnexB);
        for (var index = 0; index < nalUnits.Count; index++)
        {
            var isLastNal = index == nalUnits.Count - 1;
            foreach (var packet in PacketizeNal(nalUnits[index].ToArray(), isLastNal, accessUnit.RtpTimestamp90k))
            {
                yield return packet;
            }
        }
    }

    private IEnumerable<ReadOnlyMemory<byte>> PacketizeNal(byte[] nal, bool isLastNalOfAccessUnit, uint timestamp)
    {
        if (nal.Length == 0) yield break;
        var maximumPlaintextPayload = _mtu - RtpPacket.HeaderLength - _cipher.OverheadBytes;
        if (nal.Length <= maximumPlaintextPayload)
        {
            yield return BuildPacket(nal, isLastNalOfAccessUnit, timestamp);
            yield break;
        }

        var fragmentCapacity = maximumPlaintextPayload - 2;
        var nalHeader = nal[0];
        var offset = 1;
        var remaining = nal.Length - 1;
        var first = true;
        while (remaining > 0)
        {
            var fragmentLength = Math.Min(fragmentCapacity, remaining);
            var last = fragmentLength == remaining;
            var plaintextPayload = new byte[fragmentLength + 2];
            plaintextPayload[0] = (byte)((nalHeader & 0xE0) | 28);
            plaintextPayload[1] = (byte)(nalHeader & 0x1F);
            if (first) plaintextPayload[1] |= 0x80;
            if (last) plaintextPayload[1] |= 0x40;
            Buffer.BlockCopy(nal, offset, plaintextPayload, 2, fragmentLength);

            yield return BuildPacket(plaintextPayload, last && isLastNalOfAccessUnit, timestamp);
            first = false;
            offset += fragmentLength;
            remaining -= fragmentLength;
        }
    }

    private ReadOnlyMemory<byte> BuildPacket(byte[] plaintextPayload, bool marker, uint timestamp)
    {
        var sequenceNumber = _sequenceNumbers.Next();
        var packet = new byte[RtpPacket.HeaderLength + plaintextPayload.Length + _cipher.OverheadBytes];
        RtpPacket.WriteHeader(packet, marker, _payloadType, sequenceNumber, timestamp, _ssrc);
        var output = packet.AsSpan(RtpPacket.HeaderLength);
        var metadata = new RtpPacketMetadata(timestamp, sequenceNumber, _ssrc, marker);
        var encrypted = _cipher.Encrypt(metadata, plaintextPayload, output);
        if (encrypted.Length > output.Length)
        {
            throw new InvalidDataException("Cipher returned more bytes than the packet output buffer can hold.");
        }

        if (!encrypted.Overlaps(output, out var offset) || offset != 0)
        {
            encrypted.CopyTo(output);
        }

        return packet.AsMemory(0, RtpPacket.HeaderLength + encrypted.Length);
    }
}