using Microsoft.Extensions.Logging;
using TripleG3.P2P.Video.Stats;
using PacketCipher = TripleG3.P2P.Video.Security.IVideoPayloadCipher;

namespace TripleG3.P2P.Video.Rtp;

[Obsolete("Use TripleG3.P2P.Video.RtpVideoReceiver. This compatibility type will be removed in 2.0.", false)]
public sealed class RtpVideoReceiver : IRtpVideoReceiver
{
    private readonly RtpVideoReceiverEngine _engine;

    public RtpVideoReceiver(PacketCipher cipher, ILogger<RtpVideoReceiver>? logger = null)
    {
        _engine = new RtpVideoReceiverEngine(cipher);
    }

    public event Action<EncodedAccessUnit>? AccessUnitReceived
    {
        add => _engine.AccessUnitReceived += value;
        remove => _engine.AccessUnitReceived -= value;
    }

    public void ProcessRtp(ReadOnlySpan<byte> datagram) => _engine.ProcessRtp(datagram);

    public void ProcessRtcp(ReadOnlySpan<byte> packet) => _engine.ProcessRtcp(packet);

    public byte[]? CreateReceiverReport(uint reporterSsrc) => _engine.CreateReceiverReport(reporterSsrc);

    public VideoStreamStats GetStats() => _engine.GetStats();
}