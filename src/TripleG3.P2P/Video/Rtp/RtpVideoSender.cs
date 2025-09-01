using TripleG3.P2P.Video.Security;
using TripleG3.P2P.Video.Stats;

namespace TripleG3.P2P.Video.Rtp;

public interface IRtpVideoSender : IStatsCollector
{
    void Send(EncodedAccessUnit au);
}

public sealed class RtpVideoSender : IRtpVideoSender
{
    private readonly H264RtpPacketizer _packetizer;
    private readonly Action<ReadOnlyMemory<byte>> _datagramOut;
    private readonly Action<ReadOnlyMemory<byte>>? _rtcpOut;
    private readonly VideoStreamStats _stats = new();
    private uint _lastSrCompact;
    private DateTime _lastRrReceivedAt;
    private uint _ssrc;
    public RtpVideoSender(uint ssrc, int mtu, Video.Security.IVideoPayloadCipher cipher, Action<ReadOnlyMemory<byte>> datagramOut)
    { _ssrc = ssrc; _packetizer = new H264RtpPacketizer(ssrc, mtu, cipher); _datagramOut = datagramOut; }

    public RtpVideoSender(uint ssrc, int mtu, Video.Security.IVideoPayloadCipher cipher, Action<ReadOnlyMemory<byte>> datagramOut, Action<ReadOnlyMemory<byte>> rtcpOut)
    { _ssrc = ssrc; _packetizer = new H264RtpPacketizer(ssrc, mtu, cipher); _datagramOut = datagramOut; _rtcpOut = rtcpOut; }

    public void Send(EncodedAccessUnit au)
    {
        foreach (var packet in _packetizer.Packetize(au))
        {
            _stats.PacketsSent++; _stats.BytesSent += (uint)packet.Length;
            _datagramOut(packet);
        }
    }

    public void SendSenderReport(uint rtpTimestamp)
    {
        if (_rtcpOut == null) return;
        var sr = RtcpPackets.BuildSenderReport(_ssrc, rtpTimestamp, _stats.PacketsSent, _stats.BytesSent, out var compact);
        _lastSrCompact = compact;
        _rtcpOut(sr);
    }

    public void ProcessRtcp(ReadOnlySpan<byte> packet)
    {
        if (RtcpPackets.TryParse(packet, out var parsed) && parsed.Type == RtcpPacketType.ReceiverReport)
        {
            // RTT: A - LSR - DLSR (all in 1/65536s units)
            if (parsed.Timing.Lsr != 0 && parsed.Timing.Dlsr != 0)
            {
                var (sec, frac) = RtcpUtil.NowNtp();
                uint nowCompact = RtcpUtil.ToCompact(sec, frac);
                uint rttUnits = nowCompact - parsed.Timing.Lsr - parsed.Timing.Dlsr;
                double rttMs = rttUnits * 1000.0 / 65536.0;
                _stats.RttEstimateMs = rttMs;
            }
            _lastRrReceivedAt = DateTime.UtcNow;
        }
    }

    public VideoStreamStats GetStats() => _stats;
}
