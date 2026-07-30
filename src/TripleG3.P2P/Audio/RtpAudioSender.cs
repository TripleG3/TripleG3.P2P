using System.Buffers.Binary;
using System.Net.Sockets;

namespace TripleG3.P2P.Audio;

/// <summary>Network-backed RTP sender for initial Opus audio support.</summary>
public sealed class RtpAudioSender : IRtpAudioSender
{
    private readonly RtpAudioConfig _config;
    private readonly UdpClient _client;
    private ushort _sequenceNumber;
    private int _disposed;

    public RtpAudioSender(RtpAudioConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        RtpAudioConfiguration.Validate(config, requireLocalPort: false);
        _config = config;
        _client = new UdpClient(config.RemoteEndPoint.AddressFamily);
        _client.Connect(config.RemoteEndPoint);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> opusFrame, AudioFrameMetadata metadata, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (opusFrame.IsEmpty) throw new ArgumentException("An Opus frame is required.", nameof(opusFrame));
        var packet = new byte[12 + opusFrame.Length];
        packet[0] = 0x80;
        packet[1] = (byte)(_config.PayloadType | (metadata.Marker ? 0x80 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), unchecked(++_sequenceNumber));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), metadata.Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), _config.Ssrc);
        opusFrame.CopyTo(packet.AsMemory(12));
        await _client.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _client.Dispose();
        return ValueTask.CompletedTask;
    }
}