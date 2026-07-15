using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PacketCipher = TripleG3.P2P.Video.Security.IVideoPayloadCipher;
using RtpPacketMetadata = TripleG3.P2P.Video.Security.RtpPacketMetadata;

namespace TripleG3.P2P.Video.Rtp;

/// <summary>Reassembles bounded H.264 Annex-B access units from RTP single-NAL and FU-A packets.</summary>
public sealed class H264RtpDepacketizer
{
    private const int DefaultMaximumFrames = 32;
    private const int DefaultMaximumFrameBytes = 5 * 1024 * 1024;
    private const int DefaultMaximumNalBytes = 2 * 1024 * 1024;

    private readonly PacketCipher _cipher;
    private readonly ILogger<H264RtpDepacketizer> _logger;
    private readonly Dictionary<uint, H264FrameAssembly> _frames = [];
    private readonly object _gate = new();
    private readonly int _maximumFrames;
    private readonly int _maximumFrameBytes;
    private readonly int _maximumNalBytes;
    private readonly TimeSpan _maximumAssemblyAge;

    public H264RtpDepacketizer(PacketCipher cipher, ILogger<H264RtpDepacketizer>? logger = null)
        : this(
            cipher,
            DefaultMaximumFrames,
            DefaultMaximumFrameBytes,
            DefaultMaximumNalBytes,
            TimeSpan.FromMilliseconds(500),
            logger)
    {
    }

    internal H264RtpDepacketizer(
        PacketCipher cipher,
        int maximumFrames,
        int maximumFrameBytes,
        int maximumNalBytes,
        TimeSpan maximumAssemblyAge,
        ILogger<H264RtpDepacketizer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        if (maximumFrames <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFrames));
        if (maximumFrameBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
        if (maximumNalBytes <= 0 || maximumNalBytes > maximumFrameBytes) throw new ArgumentOutOfRangeException(nameof(maximumNalBytes));
        if (maximumAssemblyAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAssemblyAge));
        _cipher = cipher;
        _maximumFrames = maximumFrames;
        _maximumFrameBytes = maximumFrameBytes;
        _maximumNalBytes = maximumNalBytes;
        _maximumAssemblyAge = maximumAssemblyAge;
        _logger = logger ?? NullLogger<H264RtpDepacketizer>.Instance;
    }

    public bool TryProcessPacket(ReadOnlySpan<byte> datagram, out EncodedAccessUnit accessUnit)
    {
        lock (_gate)
        {
            return TryProcessPacketCore(datagram, out accessUnit);
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            foreach (var frame in _frames.Values) frame.ReleaseAll();
            _frames.Clear();
        }
    }

    private bool TryProcessPacketCore(ReadOnlySpan<byte> datagram, out EncodedAccessUnit accessUnit)
    {
        accessUnit = default;
        EvictExpiredFrames();
        if (!RtpPacket.TryParse(datagram, out var packet) || packet.Payload.IsEmpty) return false;

        byte[]? rentedDecryptionBuffer = null;
        Span<byte> decryptionBuffer = packet.Payload.Length <= 2048
            ? stackalloc byte[packet.Payload.Length]
            : (rentedDecryptionBuffer = ArrayPool<byte>.Shared.Rent(packet.Payload.Length)).AsSpan(0, packet.Payload.Length);
        try
        {
            var metadata = new RtpPacketMetadata(packet.Timestamp, packet.SequenceNumber, packet.Ssrc, packet.Marker);
            var payload = _cipher.Decrypt(metadata, packet.Payload, decryptionBuffer);
            if (payload.Length > decryptionBuffer.Length) return false;

            var frame = GetOrCreateFrame(packet.Timestamp);
            if (!frame.AcceptSequence(packet.SequenceNumber)) frame.Invalid = true;
            if (!ProcessNalFragment(frame, payload)) frame.Invalid = true;
            if (!packet.Marker) return false;

            _frames.Remove(packet.Timestamp);
            try
            {
                if (frame.Invalid || frame.HasCurrentFu || frame.NalCount == 0) return false;
                var pooledFrame = new ArrayPoolFrame(frame.AnnexBLength);
                frame.CopyAnnexBTo(pooledFrame.Memory.Span);
                accessUnit = new EncodedAccessUnit(
                    pooledFrame,
                    frame.AnnexBLength,
                    frame.IsKeyFrame,
                    packet.Timestamp,
                    0);
                return true;
            }
            finally
            {
                frame.ReleaseAll();
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            _logger.LogWarning(exception, "Rejected malformed or undecryptable H.264 RTP packet.");
            return false;
        }
        finally
        {
            if (rentedDecryptionBuffer is not null) ArrayPool<byte>.Shared.Return(rentedDecryptionBuffer);
        }
    }

    private H264FrameAssembly GetOrCreateFrame(uint timestamp)
    {
        if (_frames.TryGetValue(timestamp, out var existing)) return existing;
        while (_frames.Count >= _maximumFrames) EvictOldestFrame();
        var frame = new H264FrameAssembly();
        _frames.Add(timestamp, frame);
        return frame;
    }

    private bool ProcessNalFragment(H264FrameAssembly frame, ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || frame.Invalid) return false;
        var nalHeader = payload[0];
        var nalType = (byte)(nalHeader & 0x1F);
        if (nalType != 28)
        {
            return frame.AddSingleNal(payload, _maximumNalBytes, _maximumFrameBytes);
        }

        if (payload.Length < 2) return false;
        var fuHeader = payload[1];
        var start = (fuHeader & 0x80) != 0;
        var end = (fuHeader & 0x40) != 0;
        var originalType = (byte)(fuHeader & 0x1F);
        if (start)
        {
            var reconstructedHeader = (byte)((nalHeader & 0xE0) | originalType);
            if (!frame.StartFu(reconstructedHeader, _maximumNalBytes, _maximumFrameBytes)) return false;
        }
        else if (!frame.HasCurrentFu)
        {
            return false;
        }

        if (!frame.AppendFuPayload(payload[2..], _maximumNalBytes, _maximumFrameBytes)) return false;
        return !end || frame.CompleteFu(originalType);
    }

    private void EvictExpiredFrames()
    {
        var now = Stopwatch.GetTimestamp();
        foreach (var item in _frames
                     .Where(item => Stopwatch.GetElapsedTime(item.Value.CreatedTimestamp, now) > _maximumAssemblyAge)
                     .ToArray())
        {
            item.Value.ReleaseAll();
            _frames.Remove(item.Key);
        }
    }

    private void EvictOldestFrame()
    {
        var oldest = _frames.MinBy(item => item.Value.CreatedTimestamp);
        oldest.Value.ReleaseAll();
        _frames.Remove(oldest.Key);
    }
}