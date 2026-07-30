using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TripleG3.P2P.Core;

namespace TripleG3.P2P.Audio;

/// <summary>Bounded and restartable RTP receiver for initial Opus audio support.</summary>
public sealed class RtpAudioReceiver : IRtpAudioReceiver
{
    private readonly RtpAudioConfig _config;
    private readonly ILogger<RtpAudioReceiver> _logger;
    private readonly object _lifecycleGate = new();
    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _dispatchTask;
    private Channel<ReceivedAudioFrame>? _frames;
    private ushort? _lastSequenceNumber;
    private int _disposed;

    public RtpAudioReceiver(RtpAudioConfig config, ILogger<RtpAudioReceiver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        RtpAudioConfiguration.Validate(config, requireLocalPort: true);
        _config = config;
        _logger = logger ?? NullLogger<RtpAudioReceiver>.Instance;
    }

    public event Action<ReceivedAudioFrame>? AudioFrameReceived;

    public event EventHandler<P2PDiagnosticEventArgs>? Diagnostic;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_lifecycleGate)
        {
            if (_receiveTask is { IsCompleted: false }) return Task.CompletedTask;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _frames = Channel.CreateBounded<ReceivedAudioFrame>(new BoundedChannelOptions(_config.MaximumQueuedFrames)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
            _client = new UdpClient(new IPEndPoint(_config.LocalAddress, _config.LocalPort));
            _receiveTask = ReceiveLoopAsync(_client, _frames.Writer, _cts.Token);
            _dispatchTask = DispatchLoopAsync(_frames.Reader, _cts.Token);
        }

        Report(P2PDiagnosticKind.ListenerStarted);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        UdpClient? client;
        Task? receiveTask;
        Task? dispatchTask;
        lock (_lifecycleGate)
        {
            cts = _cts;
            client = _client;
            receiveTask = _receiveTask;
            dispatchTask = _dispatchTask;
            _cts = null;
            _client = null;
            _receiveTask = null;
            _dispatchTask = null;
            _frames = null;
            _lastSequenceNumber = null;
        }

        cts?.Cancel();
        client?.Dispose();
        if (receiveTask is not null) await IgnoreCancellationAsync(receiveTask).ConfigureAwait(false);
        if (dispatchTask is not null) await IgnoreCancellationAsync(dispatchTask).ConfigureAwait(false);
        cts?.Dispose();
        Report(P2PDiagnosticKind.ListenerStopped);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task ReceiveLoopAsync(UdpClient client, ChannelWriter<ReceivedAudioFrame> writer, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var datagram = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!await IsAuthorizedAsync(datagram.RemoteEndPoint, cancellationToken).ConfigureAwait(false))
                {
                    Report(P2PDiagnosticKind.AuthorizationRejected, datagram.RemoteEndPoint);
                    continue;
                }

                if (!TryReadFrame(datagram.Buffer, out var frame)) continue;
                writer.TryWrite(frame);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException exception) { _logger.LogWarning(exception, "RTP audio receive failed."); }
        finally { writer.TryComplete(); }
    }

    private async Task DispatchLoopAsync(ChannelReader<ReceivedAudioFrame> reader, CancellationToken cancellationToken)
    {
        await foreach (var frame in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var handler in AudioFrameReceived?.GetInvocationList().Cast<Action<ReceivedAudioFrame>>() ?? [])
            {
                try { handler(frame); }
                catch (Exception exception) { _logger.LogWarning(exception, "An RTP audio subscriber failed."); }
            }
        }
    }

    private bool TryReadFrame(byte[] packet, out ReceivedAudioFrame frame)
    {
        frame = default!;
        if (packet.Length <= 12 || packet[0] >> 6 != 2 || (packet[0] & 0x0F) != 0 || (packet[1] & 0x7F) != _config.PayloadType) return false;
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
        var timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4, 4));
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
        if (_config.Ssrc != 0 && ssrc != _config.Ssrc) return false;
        var isGap = _lastSequenceNumber is { } previous && unchecked((ushort)(previous + 1)) != sequence;
        _lastSequenceNumber = sequence;
        frame = new ReceivedAudioFrame(packet.AsMemory(12).ToArray(), timestamp, sequence, isGap);
        return true;
    }

    private async ValueTask<bool> IsAuthorizedAsync(IPEndPoint peer, CancellationToken cancellationToken)
        => _config.PeerAuthorizer is null || await _config.PeerAuthorizer.AuthorizeAsync(
            new PeerAuthorizationContext(_config.SessionId, _config.SenderDeviceId, peer, P2PResourceKind.RtpAudio), cancellationToken).ConfigureAwait(false);

    private void Report(P2PDiagnosticKind kind, IPEndPoint? peer = null)
        => Diagnostic?.Invoke(this, new P2PDiagnosticEventArgs(kind, peer, _config.SessionId));

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}

internal static class RtpAudioConfiguration
{
    public static void Validate(RtpAudioConfig config, bool requireLocalPort)
    {
        if (requireLocalPort && config.LocalPort is <= 0 or > IPEndPoint.MaxPort) throw new ArgumentOutOfRangeException(nameof(config.LocalPort));
        if (!requireLocalPort && config.LocalPort is < 0 or > IPEndPoint.MaxPort) throw new ArgumentOutOfRangeException(nameof(config.LocalPort));
        if (config.RemoteEndPoint.Port is <= 0 or > IPEndPoint.MaxPort) throw new ArgumentOutOfRangeException(nameof(config.RemoteEndPoint));
        if (config.PayloadType is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(config.PayloadType));
        if (config.SampleRate != 48_000 || config.Channels != 1 || config.PacketDuration != TimeSpan.FromMilliseconds(20)) throw new NotSupportedException("Only 48 kHz mono Opus frames at 20 ms are supported.");
        if (config.MaximumQueuedFrames <= 0) throw new ArgumentOutOfRangeException(nameof(config.MaximumQueuedFrames));
    }
}