using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TripleG3.P2P.Security;
using TripleG3.P2P.Video.Primitives;
using TripleG3.P2P.Video.Rtp;
using PrimitiveSenderStats = TripleG3.P2P.Video.Primitives.RtpVideoSenderStats;

namespace TripleG3.P2P.Video;

/// <summary>Canonical high-level RTP video sender.</summary>
public sealed class RtpVideoSender : Abstractions.IRtpVideoSender
{
    private readonly H264RtpPacketizer _packetizer;
    private readonly UdpClient? _udpClient;
    private readonly Action<ReadOnlyMemory<byte>>? _datagramOutput;
    private readonly Action<ReadOnlyMemory<byte>>? _rtcpOutput;
    private readonly ILogger<RtpVideoSender> _logger;
    private readonly uint _ssrc;
    private readonly Timer _statsTimer;
    private long _packetsSent;
    private long _bytesSent;
    private long _accessUnitsSent;
    private long _lastStatsBytes;
    private long _lastStatsAccessUnits;
    private int _disposed;

    public RtpVideoSender(
        RtpVideoSenderConfig config,
        ICipher? cipher = null,
        ILogger<RtpVideoSender>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ValidateConfiguration(config);
        _logger = logger ?? NullLogger<RtpVideoSender>.Instance;
        _ssrc = config.Ssrc;
        var packetCipher = new CoreCipherAdapter(cipher ?? new TripleG3.P2P.Security.NoOpCipher());
        _packetizer = new H264RtpPacketizer(config.Ssrc, config.Mtu, checked((byte)config.PayloadType), packetCipher);
        _udpClient = new UdpClient();
        _udpClient.Connect(config.RemoteIp, config.RemotePort);
        _statsTimer = new Timer(_ => EmitStats(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public RtpVideoSender(
        uint ssrc,
        int mtu,
        IVideoPayloadCipher cipher,
        Action<ReadOnlyMemory<byte>> datagramOut,
        Action<ReadOnlyMemory<byte>>? rtcpOut = null)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        ArgumentNullException.ThrowIfNull(datagramOut);
        _logger = NullLogger<RtpVideoSender>.Instance;
        _ssrc = ssrc;
        _packetizer = new H264RtpPacketizer(ssrc, mtu, new CipherAdapter(cipher));
        _datagramOutput = datagramOut;
        _rtcpOutput = rtcpOut;
        _statsTimer = new Timer(_ => EmitStats(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public event Action<PrimitiveSenderStats>? StatsAvailable;

    public void Send(EncodedAccessUnit accessUnit)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_datagramOutput is null)
        {
            throw new InvalidOperationException("Use SendAsync for a network-backed RTP sender.");
        }

        SendToCallback(accessUnit);
    }

    public async Task<bool> SendAsync(EncodedAccessUnit accessUnit, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();
        try
        {
            if (_datagramOutput is not null)
            {
                SendToCallback(accessUnit);
                return true;
            }

            var udpClient = _udpClient ?? throw new InvalidOperationException("No RTP output is configured.");
            foreach (var packet in _packetizer.Packetize(accessUnit))
            {
                ct.ThrowIfCancellationRequested();
                await udpClient.SendAsync(packet, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _packetsSent);
                Interlocked.Add(ref _bytesSent, packet.Length);
            }

            Interlocked.Increment(ref _accessUnitsSent);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SocketException or IOException or InvalidDataException or NotSupportedException)
        {
            _logger.LogError(exception, "RTP access-unit send failed.");
            return false;
        }
    }

    public void SendSenderReport(uint rtpTimestamp)
    {
        if (_rtcpOutput is null) return;
        var report = RtcpPackets.BuildSenderReport(
            _ssrc,
            rtpTimestamp,
            (uint)Interlocked.Read(ref _packetsSent),
            (uint)Interlocked.Read(ref _bytesSent),
            out _);
        _rtcpOutput(report);
    }

    public void ProcessRtcp(ReadOnlySpan<byte> packet)
    {
        if (!RtcpPackets.TryParse(packet, out var parsed) || parsed.Type != RtcpPacketType.ReceiverReport) return;
        _logger.LogDebug("Received RTCP receiver report from SSRC {Ssrc}.", parsed.Ssrc);
    }

    public RtpVideoSenderStats GetStats()
        => new()
        {
            PacketsSent = (uint)Interlocked.Read(ref _packetsSent),
            BytesSent = (uint)Interlocked.Read(ref _bytesSent),
            AUsSent = (uint)Interlocked.Read(ref _accessUnitsSent)
        };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _statsTimer.Dispose();
        _udpClient?.Dispose();
    }

    private void SendToCallback(EncodedAccessUnit accessUnit)
    {
        foreach (var packet in _packetizer.Packetize(accessUnit))
        {
            _datagramOutput!(packet);
            Interlocked.Increment(ref _packetsSent);
            Interlocked.Add(ref _bytesSent, packet.Length);
        }

        Interlocked.Increment(ref _accessUnitsSent);
    }

    private void EmitStats()
    {
        var handler = StatsAvailable;
        if (handler is null) return;
        var totalBytes = Interlocked.Read(ref _bytesSent);
        var totalAccessUnits = Interlocked.Read(ref _accessUnitsSent);
        var intervalBytes = totalBytes - Interlocked.Exchange(ref _lastStatsBytes, totalBytes);
        var intervalAccessUnits = totalAccessUnits - Interlocked.Exchange(ref _lastStatsAccessUnits, totalAccessUnits);
        try
        {
            handler(new PrimitiveSenderStats
            {
                Timestamp = DateTimeOffset.UtcNow,
                BitrateKbps = intervalBytes * 8 / 2000.0,
                FrameRate = intervalAccessUnits / 2.0,
                PacketsSent = (uint)Interlocked.Read(ref _packetsSent),
                AUsSent = (uint)totalAccessUnits
            });
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "An RTP sender stats subscriber failed.");
        }
    }

    private static void ValidateConfiguration(RtpVideoSenderConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.RemoteIp)) throw new ArgumentException("RemoteIp is required.", nameof(config));
        if (config.RemotePort is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(config.RemotePort));
        if (config.PayloadType is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(config.PayloadType));
        if (config.Mtu <= RtpPacket.HeaderLength + 2) throw new ArgumentOutOfRangeException(nameof(config.Mtu));
        if (config.Codec != CodecKind.H264) throw new NotSupportedException("Only H.264 RTP packetization is currently supported.");
    }
}