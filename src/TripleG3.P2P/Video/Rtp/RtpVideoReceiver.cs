using System.Diagnostics;
using TripleG3.P2P.Video.Security;
using Microsoft.Extensions.Logging;
using TripleG3.P2P.Video.Stats;

namespace TripleG3.P2P.Video.Rtp;

public interface IRtpVideoReceiver : IStatsCollector
{
    event Action<EncodedAccessUnit> AccessUnitReceived;
    void ProcessRtp(ReadOnlySpan<byte> datagram);
}

public sealed class RtpVideoReceiver : IRtpVideoReceiver
{
    private readonly H264RtpDepacketizer _depacketizer;
    private readonly RtpReorderBuffer _reorder = new(64);
    private readonly VideoStreamStats _stats = new();
    public event Action<EncodedAccessUnit>? AccessUnitReceived;

    private readonly ILogger<RtpVideoReceiver>? _log;

    private ushort? _lastSeq;
    private ushort? _baseSeq; // first sequence observed
    private uint _cycles; // sequence number wrap cycles
    private uint _highestExtSeq; // highest extended sequence received
    private uint _prevExpectedExt;
    private uint _prevReceived;
    private byte _lastFractionLost;
    private uint _lastTransit; // for jitter calc
    private uint _clockRate = 90000; // H264 assumed
    public RtpVideoReceiver(Video.Security.IVideoPayloadCipher cipher, ILogger<RtpVideoReceiver>? log = null)
    { _log = log; _depacketizer = new H264RtpDepacketizer(cipher); }

    public void ProcessRtp(ReadOnlySpan<byte> datagram)
    {
    _stats.PacketsReceived++; _stats.BytesReceived += (uint)datagram.Length;
    try { _log?.LogDebug("LegacyRtpVideoReceiver.ProcessRtp len={Len} packetsReceived={Packets}", datagram.Length, _stats.PacketsReceived); } catch { }
        if (!RtpPacket.TryParse(datagram, out var pkt)) return;
        HandleSeqAndJitter(pkt);
        // Store for reordering before depacketization
        _reorder.Add(pkt.SequenceNumber, datagram.ToArray());
        foreach (var raw in _reorder.PopReady())
        {
                if (_depacketizer.TryProcessPacket(raw, out var au))
                {
                    try { _log?.LogDebug("LegacyRtpVideoReceiver invoking AccessUnitReceived ts={Ts} isKey={IsKey}", au.Timestamp90k, au.IsKeyFrame); } catch { }
                    AccessUnitReceived?.Invoke(au);
                }
        }
    }

    private void HandleSeqAndJitter(RtpPacket pkt)
    {
        if (_lastSeq.HasValue)
        {
            ushort expected = (ushort)(_lastSeq.Value + 1);
            if (pkt.SequenceNumber != expected)
            {
                int diff = (ushort)(pkt.SequenceNumber - expected);
                if (diff < 0) diff += 65536;
                if (diff > 0) _stats.PacketsLost += (uint)diff;
            }
            // Detect wrap
            if (pkt.SequenceNumber < _lastSeq.Value && _lastSeq.Value > 60000 && pkt.SequenceNumber < 5000)
                _cycles += 1;
        }
        _lastSeq = pkt.SequenceNumber;
        if (!_baseSeq.HasValue) _baseSeq = pkt.SequenceNumber;

        _highestExtSeq = (_cycles << 16) | _lastSeq.Value;

        uint arrivalRtpUnits = (uint)(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency * _clockRate);
        int transit = unchecked((int)(arrivalRtpUnits - pkt.Timestamp));
        int d = transit - (int)_lastTransit;
        if (_lastTransit != 0)
        {
            if (d < 0) d = -d;
            _stats.Jitter += (d - _stats.Jitter) / 16.0;
        }
        _lastTransit = (uint)transit;
    }

    // RTCP handling
    private uint _lastSrCompact;
    private DateTime _lastSrArrival;
    public void ProcessRtcp(ReadOnlySpan<byte> packet)
    {
        if (RtcpPackets.TryParse(packet, out var parsed) && parsed.Type == RtcpPacketType.SenderReport)
        {
            _lastSrCompact = RtcpUtil.ToCompact(parsed.NtpSec, parsed.NtpFrac);
            _lastSrArrival = DateTime.UtcNow;
        }
    }

    public byte[]? CreateReceiverReport(uint reporterSsrc)
    {
        if (_lastSrCompact == 0) return null;
        uint dlsr = (uint)((DateTime.UtcNow - _lastSrArrival).TotalSeconds * 65536.0);
        // Fraction lost computation per RFC3550
        if (_baseSeq.HasValue)
        {
            uint baseExt = _baseSeq.Value; // cycles for base assumed 0
            uint expectedExt = _highestExtSeq - baseExt + 1;
            uint expectedInterval = expectedExt - _prevExpectedExt;
            uint receivedInterval = _stats.PacketsReceived - _prevReceived;
            int lostInterval = (int)(expectedInterval - receivedInterval);
            if (lostInterval < 0) lostInterval = 0;
            byte fraction = 0;
            if (expectedInterval != 0)
            {
                fraction = (byte)((lostInterval * 256) / expectedInterval);
            }
            _lastFractionLost = fraction;
            _prevExpectedExt = expectedExt;
            _prevReceived = _stats.PacketsReceived;
        }
        var block = new RtcpReportBlock(
            reporterSsrc,
            _lastFractionLost,
            (int)_stats.PacketsLost,
            _lastSeq ?? 0,
            _stats.Jitter,
            _lastSrCompact,
            dlsr
        );
        return RtcpPackets.BuildReceiverReport(reporterSsrc, block);
    }

    public VideoStreamStats GetStats() => _stats;
}
