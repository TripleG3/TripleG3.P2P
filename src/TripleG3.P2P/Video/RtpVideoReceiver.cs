using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TripleG3.P2P.Video.Abstractions;
using TripleG3.P2P.Video.Internal;
using TripleG3.P2P.Video.Primitives;

namespace TripleG3.P2P.Video
{
    /// <summary>
    /// High-level RTP video receiver that listens on UDP, depacketizes RTP and raises frames.
    /// </summary>
    public sealed class RtpVideoReceiver : IRtpVideoReceiver
    {
        private readonly RtpVideoReceiverConfig _config;
    private readonly UdpClient _udp;
    private CancellationTokenSource? _cts;
    private readonly Depacketizer _depacketizer = new Depacketizer();
    private ILogger<RtpVideoReceiver>? _log;
    private Task? _recvTask;
    // legacy impl for compatibility with tests using Video.Rtp types
    private TripleG3.P2P.Video.Rtp.RtpVideoReceiver? _legacyImpl;

    public event Action<EncodedAccessUnit?>? FrameReceived;
        public event Action? KeyframeNeeded;

        public RtpVideoReceiver(RtpVideoReceiverConfig config, ILogger<RtpVideoReceiver>? log = null)
        {
            _config = config;
            _log = log;
            _udp = new UdpClient(config.LocalPort);
            _cts = new CancellationTokenSource();
            _recvTask = Task.Run(() => ReceiveLoop(_cts.Token));
        }

        /// <summary>
        /// Stable minimal API constructor: provide only a cipher (currently unused/no-op).
        /// </summary>
        public RtpVideoReceiver(IVideoPayloadCipher cipher)
            : this(new RtpVideoReceiverConfig(), null)
        {
            try
            {
                var legacyCipher = new CipherAdapter(cipher);
                _legacyImpl = new TripleG3.P2P.Video.Rtp.RtpVideoReceiver((Video.Security.IVideoPayloadCipher)legacyCipher);
                _legacyImpl.AccessUnitReceived += au => { FrameReceived?.Invoke(au); AccessUnitReceived?.Invoke(au); };
            }
            catch { }
        }

        /// <summary>Stable event exposing decoded access units.</summary>
        public event Action<EncodedAccessUnit>? AccessUnitReceived;

        public Task StartAsync(CancellationToken ct = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = ReceiveLoop(_cts.Token);
            return Task.CompletedTask;
        }

        private async Task ReceiveLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var res = await _udp.ReceiveAsync(ct);
                    // if legacy impl is present, delegate to it so its cipher/reorder logic runs
                    if (_legacyImpl != null)
                    {
                        _log?.LogDebug("RtpVideoReceiver.ReceiveLoop delegating to legacy impl len={Len}", res.Buffer.Length);
                        _legacyImpl.ProcessRtp(res.Buffer);
                        continue;
                    }

                    if (_depacketizer.AddPacket(res.Buffer, out var au))
                    {
                        if (au is null) continue;
                        FrameReceived?.Invoke(au);
                        // caller must dispose
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }

        public Task StopAsync()
        {
            _cts?.Cancel();
            return Task.CompletedTask;
        }

        // Compatibility surface delegating to legacy impl when present
        public void ProcessRtcp(ReadOnlySpan<byte> packet)
        {
            _legacyImpl?.ProcessRtcp(packet);
        }

        public byte[]? CreateReceiverReport(uint reporterSsrc)
        {
            return _legacyImpl?.CreateReceiverReport(reporterSsrc);
        }

        public RtpVideoReceiverStats GetStats()
        {
            var s = _legacyImpl?.GetStats();
            if (s != null) return new RtpVideoReceiverStats { PacketsReceived = s.PacketsReceived, BytesReceived = s.BytesReceived, PacketsLost = s.PacketsLost };
            return new RtpVideoReceiverStats();
        }

        public void RequestKeyframe()
        {
            KeyframeNeeded?.Invoke();
        }

        public void Dispose()
        {
            _udp.Dispose();
            _cts?.Cancel();
        }

        // Compatibility method: accept ReadOnlySpan<byte> datagram like the legacy API
        public void ProcessRtp(ReadOnlySpan<byte> datagram)
        {
            var arr = datagram.ToArray();
            // if we have a legacy implementation (compat ctor), let it handle the packet
            if (_legacyImpl != null)
            {
                _log?.LogDebug("RtpVideoReceiver.ProcessRtp delegating len={Len}", datagram.Length);
                _legacyImpl.ProcessRtp(datagram);
                return;
            }

            if (_depacketizer.AddPacket(arr, out var au))
            {
                if (au is { } frame)
                {
                    FrameReceived?.Invoke(frame);
                    AccessUnitReceived?.Invoke(frame);
                }
            }
        }
    }
}
