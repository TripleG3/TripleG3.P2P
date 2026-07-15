using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TripleG3.P2P.Video.Stats;
using PacketCipher = TripleG3.P2P.Video.Security.IVideoPayloadCipher;

namespace TripleG3.P2P.Video.Rtp;

internal sealed class RtpVideoReceiverEngine
{
    private readonly H264RtpDepacketizer _depacketizer;
    private readonly RtpReorderBuffer _reorder;
    private readonly VideoStreamStats _stats = new();
    private readonly ILogger<RtpVideoReceiverEngine> _logger;
    private readonly byte _payloadType;
    private readonly uint? _expectedSsrc;
    private uint? _activeSsrc;
    private ushort? _highestSequence;
    private uint _sequenceCycles;
    private uint? _baseExtendedSequence;
    private uint _highestExtendedSequence;
    private uint _previousExpected;
    private uint _previousReceived;
    private uint _uniquePacketsReceived;
    private int? _lastTransit;
    private uint _lastSenderReportCompact;
    private DateTime _lastSenderReportArrival;

    public RtpVideoReceiverEngine(PacketCipher cipher, ILogger<RtpVideoReceiverEngine>? logger = null)
        : this(cipher, 96, null, TimeSpan.FromMilliseconds(250), logger)
    {
    }

    internal RtpVideoReceiverEngine(
        PacketCipher cipher,
        byte payloadType,
        uint? expectedSsrc,
        TimeSpan maximumReorderDelay,
        ILogger<RtpVideoReceiverEngine>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        if (payloadType > 127) throw new ArgumentOutOfRangeException(nameof(payloadType));
        _logger = logger ?? NullLogger<RtpVideoReceiverEngine>.Instance;
        _payloadType = payloadType;
        _expectedSsrc = expectedSsrc;
        _depacketizer = new H264RtpDepacketizer(cipher);
        _reorder = new RtpReorderBuffer(64, maximumReorderDelay);
    }

    public event Action<EncodedAccessUnit>? AccessUnitReceived;

    public void ProcessRtp(ReadOnlySpan<byte> datagram)
    {
        if (!RtpPacket.TryParse(datagram, out var packet) || packet.PayloadType != _payloadType) return;
        var expectedSource = _expectedSsrc ?? _activeSsrc;
        if (expectedSource.HasValue && packet.Ssrc != expectedSource.Value) return;
        _activeSsrc ??= packet.Ssrc;

        var rawPacket = datagram.ToArray();
        if (!_reorder.Add(packet.SequenceNumber, rawPacket, packet.Marker)) return;
        _stats.PacketsReceived++;
        _stats.BytesReceived += (uint)datagram.Length;
        _uniquePacketsReceived++;
        TrackSequenceAndJitter(packet);

        var readyPackets = _reorder.PopReady(out var skippedPackets);
        _stats.PacketsLost += (uint)skippedPackets;
        if (skippedPackets > 0) _depacketizer.Reset();
        foreach (var readyPacket in readyPackets)
        {
            if (!_depacketizer.TryProcessPacket(readyPacket, out var accessUnit)) continue;
            try
            {
                AccessUnitReceived?.Invoke(accessUnit);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "An RTP access-unit subscriber failed.");
                accessUnit.Dispose();
            }
        }
    }

    public void ProcessRtcp(ReadOnlySpan<byte> packet)
    {
        if (RtcpPackets.TryParse(packet, out var parsed) && parsed.Type == RtcpPacketType.SenderReport)
        {
            _lastSenderReportCompact = RtcpUtil.ToCompact(parsed.NtpSec, parsed.NtpFrac);
            _lastSenderReportArrival = DateTime.UtcNow;
        }
    }

    public byte[]? CreateReceiverReport(uint reporterSsrc)
    {
        if (_lastSenderReportCompact == 0 || !_baseExtendedSequence.HasValue) return null;
        var delaySinceLastReport = (uint)((DateTime.UtcNow - _lastSenderReportArrival).TotalSeconds * 65536.0);
        var expected = _highestExtendedSequence - _baseExtendedSequence.Value + 1;
        var expectedInterval = expected - _previousExpected;
        var receivedInterval = _uniquePacketsReceived - _previousReceived;
        var lostInterval = Math.Max(0, (long)expectedInterval - receivedInterval);
        var fractionLost = expectedInterval == 0
            ? (byte)0
            : (byte)Math.Min(255, lostInterval * 256 / expectedInterval);
        _previousExpected = expected;
        _previousReceived = _uniquePacketsReceived;

        var block = new RtcpReportBlock(
            _activeSsrc ?? 0,
            fractionLost,
            (int)Math.Min(int.MaxValue, _stats.PacketsLost),
            _highestExtendedSequence,
            _stats.Jitter,
            _lastSenderReportCompact,
            delaySinceLastReport);
        return RtcpPackets.BuildReceiverReport(reporterSsrc, block);
    }

    public VideoStreamStats GetStats() => _stats;

    private void TrackSequenceAndJitter(RtpPacket packet)
    {
        if (!_highestSequence.HasValue)
        {
            _highestSequence = packet.SequenceNumber;
            _highestExtendedSequence = packet.SequenceNumber;
            _baseExtendedSequence = _highestExtendedSequence;
        }
        else
        {
            var delta = unchecked((short)(packet.SequenceNumber - _highestSequence.Value));
            if (delta > 0)
            {
                if (packet.SequenceNumber < _highestSequence.Value) _sequenceCycles++;
                _highestSequence = packet.SequenceNumber;
                _highestExtendedSequence = (_sequenceCycles << 16) | packet.SequenceNumber;
            }
        }

        var arrivalRtpUnits = (uint)(Stopwatch.GetTimestamp() * (90000.0 / Stopwatch.Frequency));
        var transit = unchecked((int)(arrivalRtpUnits - packet.Timestamp));
        if (_lastTransit.HasValue)
        {
            var difference = Math.Abs(transit - _lastTransit.Value);
            _stats.Jitter += (difference - _stats.Jitter) / 16.0;
        }

        _lastTransit = transit;
    }
}