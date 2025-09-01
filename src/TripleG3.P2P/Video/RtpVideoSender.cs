using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TripleG3.P2P.Video.Abstractions;
using TripleG3.P2P.Video.Internal;
using TripleG3.P2P.Video.Primitives;
using TripleG3.P2P.Security;
using TripleG3.P2P.Video.Security;

namespace TripleG3.P2P.Video
{
    /// <summary>
    /// High-level RTP video sender that packetizes Annex-B access units and sends RTP over UDP.
    /// </summary>
    public sealed class RtpVideoSender : IDisposable
    {
        private readonly RtpVideoSenderConfig _config;
        private readonly ICipher? _cipher;
        private readonly ILogger<RtpVideoSender>? _log;
    private readonly UdpClient _udp;
    private readonly Packetizer _packetizer;
    private readonly Action<ReadOnlyMemory<byte>>? _datagramOutCompat;
    private readonly Action<ReadOnlyMemory<byte>>? _rtcpOutCompat;
    // If constructed via compatibility ctor, hold legacy implementation to delegate rich legacy API surface
    private TripleG3.P2P.Video.Rtp.RtpVideoSender? _legacyImpl;
        private readonly SequenceNumberGenerator _seq = new SequenceNumberGenerator();

    public event Action<RtpVideoSenderStats>? StatsAvailable;
        private readonly System.Threading.Timer? _statsTimer;
        private long _packetsSent;
        private long _bytesSent;
        private long _ausSent;

    public RtpVideoSender(RtpVideoSenderConfig config, ICipher? cipher = null, ILogger<RtpVideoSender>? log = null)
        {
            _config = config;
            _cipher = cipher;
            _log = log;
            _udp = new UdpClient();
            _udp.Connect(config.RemoteIp, config.RemotePort);
            _packetizer = new Packetizer(config.Mtu, _seq);
            // Start periodic stats timer (2s default)
            int intervalMs = Math.Max(500, (int)TimeSpan.FromSeconds(2).TotalMilliseconds);
            _statsTimer = new System.Threading.Timer(_ => EmitStats(), null, intervalMs, intervalMs);
        }

        /// <summary>
        /// Stable minimal API constructor: provide SSRC, MTU, cipher, and RTP (and optional RTCP) output delegates.
        /// </summary>
        public RtpVideoSender(uint ssrc, int mtu, IVideoPayloadCipher cipher, Action<ReadOnlyMemory<byte>> datagramOut, Action<ReadOnlyMemory<byte>>? rtcpOut = null)
            : this(new RtpVideoSenderConfig { Ssrc = ssrc, Mtu = mtu }, null, null)
        {
            _datagramOutCompat = datagramOut;
            _rtcpOutCompat = rtcpOut;
            // legacy impl for richer behavior (stats / RTCP) if available
            try
            {
                var legacyCipher = new CipherAdapter(cipher);
                if (rtcpOut is null)
                    _legacyImpl = new TripleG3.P2P.Video.Rtp.RtpVideoSender(ssrc, mtu, (Video.Security.IVideoPayloadCipher)legacyCipher, datagramOut);
                else
                    _legacyImpl = new TripleG3.P2P.Video.Rtp.RtpVideoSender(ssrc, mtu, (Video.Security.IVideoPayloadCipher)legacyCipher, datagramOut, rtcpOut);
            }
            catch { }
        }

        // Compatibility Send method: wrap SendAsync for existing tests using Send(EncodedAccessUnit)
        public void Send(EncodedAccessUnit au)
        {
            // Fire-and-forget the async send to keep behavior simple for tests.
            if (_legacyImpl != null)
            {
                _legacyImpl.Send(au);
                return;
            }
            _ = SendAsync(au);
        }

        // Legacy compatibility surface
        public void SendSenderReport(uint rtpTimestamp)
        {
            _legacyImpl?.SendSenderReport(rtpTimestamp);
        }

        public void ProcessRtcp(ReadOnlySpan<byte> packet)
        {
            _legacyImpl?.ProcessRtcp(packet);
        }

        /// <summary>Optional simple stats (packet/frame counters).</summary>
        public RtpVideoSenderStats GetStats()
        {
            if (_legacyImpl != null)
            {
                var s = _legacyImpl.GetStats();
                if (s != null) return new RtpVideoSenderStats { PacketsSent = s.PacketsSent, BytesSent = s.BytesSent, AUsSent = (uint)Interlocked.Read(ref _ausSent) };
            }
            return new RtpVideoSenderStats
            {
                PacketsSent = (uint)Interlocked.Read(ref _packetsSent),
                BytesSent = (uint)Interlocked.Read(ref _bytesSent),
                AUsSent = (uint)Interlocked.Read(ref _ausSent)
            };
        }

        public async Task<bool> SendAsync(EncodedAccessUnit au, CancellationToken ct = default)
        {
            try
            {
                foreach (var segment in _packetizer.Packetize(au, _config.PayloadType, _config.Ssrc))
                {
                    var arr = segment.Array!;
                    if (_datagramOutCompat != null)
                    {
                        _datagramOutCompat(new ReadOnlyMemory<byte>(arr, 0, segment.Count));
                    }
                    else
                    {
                        await _udp.SendAsync(arr, segment.Count);
                    }
                    System.Threading.Interlocked.Add(ref _packetsSent, 1);
                    System.Threading.Interlocked.Add(ref _bytesSent, segment.Count);
                    System.Threading.Interlocked.Add(ref _ausSent, 1);
                    ArrayPool<byte>.Shared.Return(arr);
                }
                return true;
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "SendAsync failed");
                return false;
            }
        }

        public void Dispose()
        {
            _statsTimer?.Dispose();
            _udp.Dispose();
        }

    private void EmitStats()
        {
            try
            {
        if (StatsAvailable == null) return;
        StatsAvailable?.Invoke(GetStats());
                // reset counters for next interval
                Interlocked.Exchange(ref _bytesSent, 0);
                Interlocked.Exchange(ref _packetsSent, 0);
                Interlocked.Exchange(ref _ausSent, 0);
            }
            catch { }
        }
    }
}
