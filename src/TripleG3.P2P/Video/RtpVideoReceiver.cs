using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TripleG3.P2P.Security;
using TripleG3.P2P.Video.Abstractions;
using TripleG3.P2P.Video.Primitives;
using PacketReceiver = TripleG3.P2P.Video.Rtp.RtpVideoReceiverEngine;

namespace TripleG3.P2P.Video;

/// <summary>Canonical high-level RTP video receiver with explicit asynchronous lifecycle.</summary>
public sealed class RtpVideoReceiver : IRtpVideoReceiver, IAsyncDisposable
{
    private readonly RtpVideoReceiverConfig _config;
    private readonly PacketReceiver _engine;
    private readonly ILogger<RtpVideoReceiver> _logger;
    private readonly object _lifecycleGate = new();
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private int _disposed;

    public RtpVideoReceiver(RtpVideoReceiverConfig config, ILogger<RtpVideoReceiver>? logger = null)
        : this(config, new TripleG3.P2P.Security.NoOpCipher(), logger)
    {
    }

    public RtpVideoReceiver(
        RtpVideoReceiverConfig config,
        ICipher cipher,
        ILogger<RtpVideoReceiver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(cipher);
        ValidateConfiguration(config, false);
        _config = config;
        _logger = logger ?? NullLogger<RtpVideoReceiver>.Instance;
        _engine = new PacketReceiver(
            new CoreCipherAdapter(cipher),
            checked((byte)config.PayloadType),
            config.ExpectedSsrc,
            config.JitterBufferMax);
        _engine.AccessUnitReceived += OnAccessUnitReceived;
    }

    public RtpVideoReceiver(IVideoPayloadCipher cipher)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        _config = new RtpVideoReceiverConfig();
        _logger = NullLogger<RtpVideoReceiver>.Instance;
        _engine = new PacketReceiver(new CipherAdapter(cipher));
        _engine.AccessUnitReceived += OnAccessUnitReceived;
    }

    public event Action<EncodedAccessUnit?>? FrameReceived;

    public event Action<EncodedAccessUnit>? AccessUnitReceived;

    public event Action? KeyframeNeeded;

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();
        ValidateConfiguration(_config, true);
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_receiveTask is { IsCompleted: false }) return Task.CompletedTask;
            _cts?.Dispose();
            _udpClient?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _udpClient = new UdpClient(new IPEndPoint(_config.LocalAddress, _config.LocalPort));
            _receiveTask = ReceiveLoopAsync(_udpClient, _cts.Token);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync() => StopCoreAsync();

    private async Task StopCoreAsync()
    {
        CancellationTokenSource? cts;
        UdpClient? udpClient;
        Task? receiveTask;
        lock (_lifecycleGate)
        {
            cts = _cts;
            udpClient = _udpClient;
            receiveTask = _receiveTask;
            _cts = null;
            _udpClient = null;
            _receiveTask = null;
        }

        cts?.Cancel();
        udpClient?.Dispose();
        if (receiveTask is not null)
        {
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts?.Dispose();
    }

    public void ProcessRtp(ReadOnlySpan<byte> datagram) => _engine.ProcessRtp(datagram);

    public void ProcessRtcp(ReadOnlySpan<byte> packet) => _engine.ProcessRtcp(packet);

    public byte[]? CreateReceiverReport(uint reporterSsrc) => _engine.CreateReceiverReport(reporterSsrc);

    public RtpVideoReceiverStats GetStats()
    {
        var stats = _engine.GetStats();
        return new RtpVideoReceiverStats
        {
            PacketsReceived = stats.PacketsReceived,
            BytesReceived = stats.BytesReceived,
            PacketsLost = stats.PacketsLost
        };
    }

    public void RequestKeyframe() => KeyframeNeeded?.Invoke();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _engine.AccessUnitReceived -= OnAccessUnitReceived;
        CancellationTokenSource? cts;
        UdpClient? udpClient;
        lock (_lifecycleGate)
        {
            cts = _cts;
            udpClient = _udpClient;
            _cts = null;
            _udpClient = null;
            _receiveTask = null;
        }

        cts?.Cancel();
        udpClient?.Dispose();
        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _engine.AccessUnitReceived -= OnAccessUnitReceived;
        await StopCoreAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task ReceiveLoopAsync(UdpClient udpClient, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                _engine.ProcessRtp(result.Buffer);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException exception)
            {
                _logger.LogWarning(exception, "RTP receive failed.");
            }
        }
    }

    private void OnAccessUnitReceived(EncodedAccessUnit accessUnit)
    {
        var delivered = false;
        if (FrameReceived is not null)
        {
            FrameReceived.Invoke(accessUnit);
            delivered = true;
        }

        if (AccessUnitReceived is not null)
        {
            AccessUnitReceived.Invoke(accessUnit);
            delivered = true;
        }

        if (!delivered) accessUnit.Dispose();
    }

    private static void ValidateConfiguration(RtpVideoReceiverConfig config, bool requireListeningPort)
    {
        if (requireListeningPort && config.LocalPort is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(config.LocalPort));
        if (!requireListeningPort && config.LocalPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(config.LocalPort));
        if (config.PayloadType is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(config.PayloadType));
        if (config.JitterBufferMax <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(config.JitterBufferMax));
        if (config.Codec != CodecKind.H264) throw new NotSupportedException("Only H.264 RTP depacketization is currently supported.");
    }
}